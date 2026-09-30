using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HookBench;

/// <summary>
/// The approved real-model confirmation (plan.md section 2): the user's real Claude Code login,
/// <c>claude-haiku-4-5</c>, only the Read tool, a chain of files in a scratch folder so the calls
/// are sequential, and each condition run once. Stdout is stream-json; every line is timestamped on
/// arrival, so the gap between an assistant tool_use and its tool_result isolates CLI-side tool
/// processing (hooks and Read) from model latency. The CLI debug log gives each HTTP hook's own
/// duration and the effective hook set. <c>--dry-run true</c> runs the identical procedure against
/// the fake API with a dummy key, which costs nothing, to verify hook isolation before spending usage.
/// </summary>
internal static class Real
{
    public static async Task<int> RunAsync(Options options)
    {
        var claude = options.Get("claude", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe"));
        var output = Directory.CreateDirectory(Path.GetFullPath(options.Required("out"))).FullName;
        var calls = options.GetInt("calls", 20);
        // The approved maximum tool calls per run, enforced independently of the prompt.
        var cap = options.GetInt("cap", calls);
        var conditions = options.Get("conditions", "none,http-down").Split(',');
        var dryRun = bool.Parse(options.Get("dry-run", "true"));

        var work = Directory.CreateDirectory(Path.Combine(output, "work")).FullName;
        for (var i = 1; i <= calls; i++)
        {
            File.WriteAllText(Path.Combine(work, $"f{i:D2}.txt"), i < calls
                ? $"Next file: {Path.Combine(work, $"f{i + 1:D2}.txt")}\n"
                : "This is the last file. Stop reading files and reply with the single word DONE.\n");
        }

        var prompt = $"Read the file {Path.Combine(work, "f01.txt")}. Each file names the next file to read. " +
                     "Read them one at a time, in order, with one Read call per turn, until a file tells you to stop.";
        var port = Ports.PickEligible(new HashSet<int>());
        var apiPort = Ports.PickEligible(new HashSet<int> { port });
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Console.WriteLine($"{(dryRun ? "DRY RUN (fake API, dummy key)" : "REAL RUN (your login, claude-haiku-4-5)")}; stopped-listener port {port}; {calls} files; cap {cap} tool calls per run");

        var report = new JsonObject { ["dry_run"] = dryRun, ["calls_per_run"] = calls, ["cap_per_run"] = cap };
        foreach (var condition in conditions)
        {
            var settings = Path.Combine(output, $"settings-{condition}.json");
            ClaudeRunner.WriteSettings(settings, condition switch
            {
                "none" => ClaudeRunner.NoHooks(),
                "http-down" => ClaudeRunner.HttpHooks(port, secret), // nothing listens on this port
                _ => throw new ArgumentException($"Unknown condition '{condition}'."),
            });
            var debugFile = Path.Combine(output, $"{(dryRun ? "dry" : "real")}-{condition}-debug.txt");
            File.Delete(debugFile);

            FakeAnthropicApi? api = null;
            if (dryRun)
            {
                // calls - 1 successful reads plus one failing read: the same number of tool calls as the real chain.
                api = new FakeAnthropicApi(apiPort, Path.Combine(work, "f01.txt"), Path.Combine(work, "missing.txt"), calls - 1);
                await api.StartAsync();
            }

            var (lines, stop) = await RunClaudeAsync(claude, work, prompt, settings, debugFile, dryRun ? apiPort : null, cap);
            if (api is not null)
            {
                await api.DisposeAsync();
            }

            var entry = Analyse(lines, debugFile);
            entry["harness_stop"] = stop;
            report[condition] = entry;
            Console.WriteLine($"{condition}: {entry.ToJsonString()}");
        }

        Json.Write(Path.Combine(output, $"{(dryRun ? "dry" : "real")}-report.json"), report);
        return 0;
    }

    /// <summary>
    /// Runs one condition. The cap is enforced three ways: the CLI's <c>--max-turns</c> (cap tool
    /// turns plus the final answer), the harness killing the process tree as soon as the stream shows
    /// more than <paramref name="cap"/> tool uses, and a 10-minute timeout.
    /// </summary>
    private static async Task<(List<(long Ticks, JsonNode? Json)> Lines, string? Stop)> RunClaudeAsync(string claude,
        string work, string prompt, string settings, string debugFile, int? fakeApiPort, int cap)
    {
        var start = new ProcessStartInfo(claude)
        {
            WorkingDirectory = work,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-p", prompt, "--model", "claude-haiku-4-5", "--tools", "Read", "--output-format", "stream-json",
                     "--verbose", "--no-session-persistence", "--strict-mcp-config", "--settings", settings,
                     "--setting-sources", "", "--debug-file", debugFile,
                     "--max-turns", (cap + 1).ToString(CultureInfo.InvariantCulture),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        // The parent is itself a Claude Code session: drop its variables so only the default config
        // directory and login apply.
        foreach (var key in start.Environment.Keys.ToList())
        {
            if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(key);
            }
        }

        start.Environment["DISABLE_AUTOUPDATER"] = "1";
        if (fakeApiPort is { } p)
        {
            start.Environment["ANTHROPIC_BASE_URL"] = $"http://127.0.0.1:{p}";
            start.Environment["ANTHROPIC_API_KEY"] = "bench-not-a-real-key";
        }

        var lines = new List<(long, JsonNode?)>();
        string? stop = null;
        var toolUses = 0;
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                var arrived = Clock.Now;
                JsonNode? json = null;
                try
                {
                    json = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                }

                lines.Add((arrived, json));
                if ((string?)json?["type"] == "assistant" && json!["message"]?["content"] is JsonArray content)
                {
                    toolUses += content.Count(b => (string?)b?["type"] == "tool_use");
                    if (toolUses > cap && stop is null)
                    {
                        stop = $"killed: {toolUses} tool uses exceeded the cap of {cap}";
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            stop = "killed: 10-minute timeout";
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
        var error = await stderr;
        if (process.ExitCode != 0 || error.Length > 0 || stop is not null)
        {
            Console.WriteLine($"  exit {process.ExitCode}; {stop ?? "completed"}; stderr: {(error.Length > 500 ? error[^500..] : error)}");
        }

        return (lines, stop);
    }

    private static JsonObject Analyse(List<(long Ticks, JsonNode? Json)> lines, string debugFile)
    {
        var toolUse = new Dictionary<string, long>();
        var gaps = new List<double>();
        var modelLatency = new List<double>();
        var toolsPerMessage = new List<int>();
        var toolNames = new HashSet<string>();
        var models = new HashSet<string>();
        long? lastResult = null;
        JsonNode? result = null;
        foreach (var (ticks, json) in lines)
        {
            var content = json?["message"]?["content"] as JsonArray;
            switch ((string?)json?["type"])
            {
                case "assistant":
                    if ((string?)json!["message"]?["model"] is { } model)
                    {
                        models.Add(model);
                    }

                    var uses = content?.Where(b => (string?)b?["type"] == "tool_use").ToList() ?? [];
                    if (uses.Count > 0)
                    {
                        toolsPerMessage.Add(uses.Count);
                        if (lastResult is { } previous)
                        {
                            modelLatency.Add(Clock.Ms(ticks - previous));
                        }
                    }

                    foreach (var use in uses)
                    {
                        toolUse[(string)use!["id"]!] = ticks;
                        toolNames.Add((string?)use["name"] ?? "?");
                    }

                    break;
                case "user":
                    foreach (var block in content?.Where(b => (string?)b?["type"] == "tool_result") ?? [])
                    {
                        if (toolUse.TryGetValue((string?)block!["tool_use_id"] ?? "", out var issued))
                        {
                            gaps.Add(Clock.Ms(ticks - issued));
                            lastResult = ticks;
                        }
                    }

                    break;
                case "result":
                    result = json;
                    break;
            }
        }

        var entry = new JsonObject
        {
            ["tool_calls"] = gaps.Count,
            ["tools_used"] = string.Join(",", toolNames),
            ["max_tools_per_message"] = toolsPerMessage.DefaultIfEmpty(0).Max(),
            ["models"] = string.Join(",", models),
            ["tool_use_to_result_ms"] = JsonSerializer.SerializeToNode(Stats.Summary(gaps)),
            ["result_to_next_tool_use_ms"] = JsonSerializer.SerializeToNode(Stats.Summary(modelLatency)),
            ["is_error"] = result?["is_error"]?.DeepClone(),
            ["subtype"] = result?["subtype"]?.DeepClone(),
            ["num_turns"] = result?["num_turns"]?.DeepClone(),
            ["duration_ms"] = result?["duration_ms"]?.DeepClone(),
            ["duration_api_ms"] = result?["duration_api_ms"]?.DeepClone(),
            ["total_cost_usd"] = result?["total_cost_usd"]?.DeepClone(),
        };

        // Effective hooks and per-hook durations from the CLI's own debug log (metadata lines only).
        var log = File.Exists(debugFile) ? File.ReadAllLines(debugFile) : [];
        string Last(string pattern) => log.Select(l => Regex.Match(l, pattern)).LastOrDefault(m => m.Success)?.Value ?? "(not logged)";
        entry["plugins"] = Last(@"Found \d+ plugins \([^)]*\)");
        entry["registered_plugin_hooks"] = Last(@"Registered \d+ hooks from \d+ plugins");
        entry["hook_modules"] = string.Join("; ", log.Select(l => Regex.Match(l, @"hooks module (\S+) loaded \(([^)]*)\)")).Where(m => m.Success)
            .Select(m => $"{m.Groups[1].Value} ({m.Groups[2].Value.Split(',').LastOrDefault()?.Trim()})").Distinct());
        entry["hook_registry_counts"] = string.Join(",", log.Select(l => Regex.Match(l, @"Found (\d+) total hooks in registry")).Where(m => m.Success)
            .Select(m => m.Groups[1].Value).Distinct());
        entry["skipped_http_hooks"] = log.Count(l => l.Contains("Skipping HTTP hook", StringComparison.Ordinal));
        entry["command_hook_lines"] = log.Count(l => Regex.IsMatch(l, @"Hooks?: .*(command|Executing)", RegexOptions.IgnoreCase));
        var hookDurations = new List<double>();
        DateTimeOffset? posted = null;
        foreach (var line in log)
        {
            var stamp = line.Length > 24 && DateTimeOffset.TryParse(line[..24], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTimeOffset?)null;
            if (line.Contains("Hooks: HTTP hook POST to", StringComparison.Ordinal))
            {
                posted = stamp;
            }
            else if (posted is not null && stamp is not null && line.Contains("Hooks: HTTP hook", StringComparison.Ordinal))
            {
                hookDurations.Add((stamp.Value - posted.Value).TotalMilliseconds);
                posted = null;
            }
        }

        entry["http_hook_post_to_outcome_ms"] = JsonSerializer.SerializeToNode(Stats.Summary(hookDurations));
        entry["http_hook_outcomes"] = string.Join("; ", log.Select(l => Regex.Match(l, @"Hooks: HTTP hook error: ([^0-9]*)")).Where(m => m.Success)
            .GroupBy(m => m.Groups[1].Value.Trim()).Select(g => $"{g.Key} x{g.Count()}"));
        return entry;
    }
}
