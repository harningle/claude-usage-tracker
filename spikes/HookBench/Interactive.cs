using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Tests;

namespace HookBench;

/// <summary>
/// Milestone 5 follow-ups G6, G7 and D3: the real CLI run interactively in a pseudo console, as a terminal runs it,
/// against the fake Messages API with an isolated config directory, so no account is used and no usage consumed.
/// One session takes three turns. Turn 1 reads files, the last one outside the working folder so that the CLI asks
/// for permission (answered "yes" after a pause), then stays idle long enough for the idle notification. Between turns
/// the user settings are replaced: hooks to path secret B, then no hooks, then hooks to C before <c>/exit</c>. The
/// status line is a probe that records the processes above it and sleeps, so the run shows which shell runs it and
/// whether a newer update cancels one still running. With <c>--helper</c> the status line is instead the installed
/// production helper, run with the command the tracker writes, and the report shows the lines it drew.
/// </summary>
internal static class Interactive
{
    public static async Task<int> RunAsync(Options options)
    {
        var claude = options.Get("claude", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe"));
        var layout = Layout.Create(Path.GetFullPath(options.Required("out")));
        var calls = options.GetInt("calls", 6);
        var stepDelayMs = options.GetInt("step-delay-ms", 700);
        var probeSleepMs = options.GetInt("probe-sleep-ms", 1500);
        var idleWait = TimeSpan.FromSeconds(options.GetInt("idle-wait-s", 75));
        var helper = options.Get("helper", "");
        var probes = Directory.CreateDirectory(Path.Combine(layout.Root, "probes")).FullName;
        foreach (var stale in Directory.EnumerateFiles(probes))
        {
            File.Delete(stale);
        }

        // The last read is outside the working folder, which the CLI asks permission for.
        var outside = Directory.CreateDirectory(Path.Combine(layout.Root, "outside")).FullName;
        var outsideTarget = Path.Combine(outside, "missing.txt");

        var port = Ports.PickEligible(new HashSet<int>());
        var apiPort = Ports.PickEligible(new HashSet<int> { port });
        var labels = new[] { "A", "B", "C" }.ToDictionary(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), l => l);
        var secretFor = labels.ToDictionary(p => p.Value, p => p.Key);
        var settingsPath = Path.Combine(layout.Config, "settings.json");
        var self = Environment.ProcessPath!.Replace('\\', '/');
        var statusLine = new JsonObject
        {
            ["type"] = "command",
            ["command"] = helper.Length > 0
                ? $"{Path.GetFullPath(helper).Replace('\\', '/')} statusline"
                : $"{self} probe-statusline {probes.Replace('\\', '/')} {probeSleepMs}",
            ["padding"] = 0,
        };

        void WriteSettings(string? label)
        {
            var settings = label is null ? ClaudeRunner.NoHooks() : ClaudeRunner.HttpHooks(port, secretFor[label]);
            settings["statusLine"] = statusLine.DeepClone();
            ClaudeRunner.WriteSettings(settingsPath, settings);
        }

        WriteSettings("A");
        WriteGlobalConfig(layout);

        var listener = new HttpHookListener(port, labels.Keys.ToArray());
        await listener.StartAsync();
        var started = Clock.Now;
        double Since(long ticks) => Math.Round(Clock.Ms(ticks - started), 0);

        await using var api = new FakeAnthropicApi(apiPort, layout.ReadTarget, outsideTarget, calls, _ => Task.Delay(stepDelayMs));
        await api.StartAsync();

        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => ((string)e.Key, (string)e.Value!))
            .Where(e => !e.Item1.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) && !e.Item1.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Item1, e => e.Item2, StringComparer.OrdinalIgnoreCase);
        environment["CLAUDE_CONFIG_DIR"] = layout.Config;
        environment["ANTHROPIC_BASE_URL"] = $"http://127.0.0.1:{apiPort}";
        environment["ANTHROPIC_API_KEY"] = "bench-not-a-real-key";
        environment["DISABLE_AUTOUPDATER"] = "1";
        environment["DISABLE_TELEMETRY"] = "1";
        environment["DISABLE_ERROR_REPORTING"] = "1";
        environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        var debugFile = Path.Combine(layout.Results, "interactive-debug.txt");
        var timeline = new JsonArray();
        void Mark(string what)
        {
            timeline.Add($"{Since(Clock.Now)} ms: {what}");
            Console.WriteLine($"{Since(Clock.Now),8} ms  {what}");
        }

        int? exitCode;
        string screen;
        using (var console = PseudoConsole.Start(
                   $"\"{claude}\" --model claude-haiku-4-5 --strict-mcp-config --debug-file \"{debugFile}\"", layout.Work, environment))
        {
            async Task Submit(string text)
            {
                // Typed in one write, Enter would count as part of a paste.
                console.Type(text);
                await Task.Delay(500);
                console.Type("\r");
                Mark($"typed {text}");
            }

            bool Received(string label, string suffix, int atLeast = 1) =>
                listener.Receipts.Count(r => labels[r.Route] == label && r.Event == suffix) >= atLeast;

            Mark("started");
            if (!SpinWait.SpinUntil(() => Directory.EnumerateFiles(probes).Any() || (helper.Length > 0 && console.Text.Contains('%')), TimeSpan.FromSeconds(60)))
            {
                Mark("no status line within 60 s");
            }

            await Task.Delay(2000);
            Mark("ready");

            // Turn 1: tool calls, a permission prompt at the last one, then the idle wait.
            await Submit("Read the file you are asked to read, then answer.");
            var progress = -1;
            var lastProgress = Stopwatch.StartNew();
            var turnLimit = Stopwatch.StartNew();
            while (!Received("A", "stop") && turnLimit.Elapsed < TimeSpan.FromMinutes(3))
            {
                if (api.Steps.Count != progress)
                {
                    progress = api.Steps.Count;
                    lastProgress.Restart();
                }
                else if (lastProgress.Elapsed > TimeSpan.FromSeconds(10))
                {
                    // Stalled: the CLI is waiting for an answer. Enter takes the highlighted "Yes".
                    console.Type("\r");
                    Mark($"pressed Enter after step {progress - 1} stalled");
                    lastProgress.Restart();
                }

                await Task.Delay(100);
            }

            Mark(Received("A", "stop") ? "turn 1 stopped" : "turn 1 did not stop");
            var idle = Stopwatch.StartNew();
            var notificationsBefore = listener.Receipts.Count(r => r.Event == "notification");
            while (idle.Elapsed < idleWait && listener.Receipts.Count(r => r.Event == "notification") == notificationsBefore)
            {
                await Task.Delay(250);
            }

            Mark($"idle wait ended after {idle.Elapsed.TotalSeconds:0} s");

            // Turns 2 and 3 after replacing the user settings, then the exit with hooks to C.
            foreach (var (label, turn) in new[] { ("B", 2), (null, 3) })
            {
                WriteSettings(label);
                Mark($"settings -> {label ?? "no hooks"}");
                await Task.Delay(3000);
                await Submit("Answer.");
                await Task.Delay(TimeSpan.FromSeconds(6));
                Mark($"turn {turn} waited");
            }

            WriteSettings("C");
            Mark("settings -> C");
            await Task.Delay(3000);
            await Submit("/exit");
            exitCode = console.WaitForExit(TimeSpan.FromSeconds(30));
            Mark($"exit {exitCode?.ToString() ?? "(still running)"}");
            screen = console.Text;
        }

        await Task.Delay(500);
        await listener.StopAsync(abort: false);
        File.Delete(settingsPath);

        var report = new JsonObject
        {
            ["claude"] = claude,
            ["exit_code"] = exitCode,
            ["calls"] = calls,
            ["step_delay_ms"] = stepDelayMs,
            ["probe_sleep_ms"] = probeSleepMs,
            ["timeline"] = timeline,
            ["hook_events"] = new JsonArray([.. listener.Receipts.Select(r =>
                (JsonNode)$"{Since(r.ArrivedTicks)} ms {ClaudeRunner.EventForSuffix(r.Event)}@{labels[r.Route]}{(r.Detail is null ? "" : $" ({r.Detail})")}{(r.ToolUseId is null ? "" : $" {r.ToolUseId}")}")]),
            ["side_requests"] = new JsonArray([.. api.SideRequests.Distinct().Select(s => (JsonNode)s)]),
            ["status_lines"] = StatusLines(probes, started),
            ["drawn_lines_with_percentages"] = new JsonArray([.. screen.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('%')).Distinct().Take(5).Select(l => (JsonNode)l)]),
        };
        File.WriteAllText(Path.Combine(layout.Results, "interactive-screen.txt"), screen);
        Json.Write(Path.Combine(layout.Results, "interactive.json"), report);
        Console.WriteLine(report.ToJsonString(Json.Indented));
        return 0;
    }

    /// <summary>
    /// The isolated folder's global configuration: onboarding done, the dummy key approved (the CLI keeps the last 20
    /// characters) and the working folder trusted, so the session opens at its prompt.
    /// </summary>
    private static void WriteGlobalConfig(Layout layout)
    {
        var projects = new JsonObject();
        foreach (var key in new[] { layout.Work, layout.Work.Replace('\\', '/') })
        {
            projects[key] = new JsonObject { ["hasTrustDialogAccepted"] = true, ["hasCompletedProjectOnboarding"] = true };
        }

        var config = new JsonObject
        {
            ["hasCompletedOnboarding"] = true,
            ["theme"] = "dark",
            ["customApiKeyResponses"] = new JsonObject { ["approved"] = new JsonArray("bench-not-a-real-key"), ["rejected"] = new JsonArray() },
            ["projects"] = projects,
        };
        File.WriteAllText(Path.Combine(layout.Config, ".claude.json"), config.ToJsonString(Json.Indented));
    }

    private static JsonObject StatusLines(string probes, long started)
    {
        var runs = Directory.EnumerateFiles(probes, "*-start.json")
            .Select(path => (Start: JsonNode.Parse(File.ReadAllText(path))!.AsObject(), End: ReadEnd(path)))
            .OrderBy(r => (long)r.Start["ticks"]!)
            .ToList();
        var list = new JsonArray();
        var overlapping = 0;
        for (var i = 0; i < runs.Count; i++)
        {
            var (start, end) = runs[i];
            var begin = (long)start["ticks"]!;
            long? finish = end is null ? null : (long)end["ticks"]!;
            if (i > 0 && runs[i - 1].End is { } previous && (long)previous["ticks"]! > begin)
            {
                overlapping++;
            }

            var pid = (int)start["pid"]!;
            list.Add($"{Math.Round(Clock.Ms(begin - started))} ms pid {pid}: {(finish is null ? "killed before its end" : $"ended after {Math.Round(Clock.Ms(finish.Value - begin))} ms, parent alive {end!["parent_alive"]}")}" +
                     $"; stdin {start["stdin_bytes"]} bytes; via {start["ancestors"]}");
        }

        return new JsonObject
        {
            ["runs"] = runs.Count,
            ["killed"] = runs.Count(r => r.End is null),
            ["started_before_previous_ended"] = overlapping,
            ["stdin_keys"] = runs.FirstOrDefault().Start?["stdin_keys"]?.DeepClone(),
            ["each"] = list,
        };
    }

    private static JsonObject? ReadEnd(string startPath)
    {
        var end = startPath.Replace("-start.json", "-end.json");
        return File.Exists(end) ? JsonNode.Parse(File.ReadAllText(end))!.AsObject() : null;
    }
}

/// <summary>
/// The status line command of <see cref="Interactive"/>: records when it ran, the processes above it and what it was
/// given, prints its process ID, sleeps, and records whether it got to the end.
/// </summary>
internal static class StatuslineProbe
{
    public static int Run(string folder, int sleepMs)
    {
        var ticks = Clock.Now;
        using var input = new MemoryStream();
        Console.OpenStandardInput().CopyTo(input);
        string[] keys = [];
        try
        {
            keys = [.. JsonDocument.Parse(input.ToArray()).RootElement.EnumerateObject().Select(p => p.Name)];
        }
        catch (JsonException)
        {
        }

        var processes = Snapshot();
        var pid = Environment.ProcessId;
        var ancestors = new List<string>();
        var parent = processes.TryGetValue(pid, out var me) ? me.Parent : 0;
        for (var current = parent; ancestors.Count < 6 && processes.TryGetValue(current, out var entry); current = entry.Parent)
        {
            ancestors.Add(entry.Name);
        }

        File.WriteAllText(Path.Combine(folder, $"{pid}-start.json"), new JsonObject
        {
            ["ticks"] = ticks,
            ["pid"] = pid,
            ["stdin_bytes"] = input.Length,
            ["stdin_keys"] = new JsonArray([.. keys.Select(k => (JsonNode)k)]),
            ["ancestors"] = string.Join(" < ", ancestors),
        }.ToJsonString());
        Console.Out.Write($"probe-{pid}");
        Console.Out.Flush();

        Thread.Sleep(sleepMs);
        var parentAlive = Snapshot().TryGetValue(parent, out var now) && processes.TryGetValue(parent, out var then) && now.Name == then.Name;
        File.WriteAllText(Path.Combine(folder, $"{pid}-end.json"), new JsonObject { ["ticks"] = Clock.Now, ["parent_alive"] = parentAlive }.ToJsonString());
        return 0;
    }

    private static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var result = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
