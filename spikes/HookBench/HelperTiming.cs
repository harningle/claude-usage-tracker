using System.Diagnostics;
using System.Text.Json.Nodes;

namespace HookBench;

/// <summary>
/// Times the NativeAOT helper spike directly and through each shell runner, measured from .NET
/// <c>Process.Start</c> to exit with a realistic statusline payload on stdin.
/// </summary>
internal static class HelperTiming
{
    private const string StatuslineInput = """
        {"hook_event_name":"Status","session_id":"abc123","transcript_path":"C:/Users/u/.claude/projects/p/abc123.jsonl",
         "cwd":"C:/Users/u/project","model":{"id":"claude-opus-4-1","display_name":"Opus"},
         "workspace":{"current_dir":"C:/Users/u/project","project_dir":"C:/Users/u/project"},
         "version":"2.1.286","output_style":{"name":"default"},
         "cost":{"total_cost_usd":0.01234,"total_duration_ms":45000,"total_api_duration_ms":2300,"total_lines_added":156,"total_lines_removed":23},
         "context_window":{"total_input_tokens":15234,"total_output_tokens":4521,"context_window_size":200000,"used_percentage":8,"remaining_percentage":92},
         "exceeds_200k_tokens":false,
         "rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1738425600},"seven_day":{"used_percentage":41.2,"resets_at":1738857600}}}
        """;

    public static async Task<int> RunAsync(Options options)
    {
        var helper = Path.GetFullPath(options.Required("helper"));
        var output = Path.GetFullPath(options.Required("out"));
        var runs = options.GetInt("runs", 50);
        var gitBash = options.Get("bash", @"C:\Program Files\Git\bin\bash.exe");
        Directory.CreateDirectory(output);
        var forward = helper.Replace('\\', '/');
        var pipeName = $"ClaudeUsageTracker.HookBench.{Guid.NewGuid():N}";
        var hookInput = """{"hook_event_name":"PreToolUse","session_id":"abc123","tool_use_id":"toolu_1","tool_name":"Read","tool_input":{"file_path":"C:/x.txt"}}""";

        var cases = new List<(string Name, Func<ProcessStartInfo> Start, string Input)>
        {
            ("direct noop", () => Start(helper, "noop"), ""),
            ("direct statusline", () => Start(helper, "statusline"), StatuslineInput),
            ("direct hook-pipe, server down", () => Start(helper, "hook-pipe", pipeName + ".absent"), hookInput),
            ("direct hook-pipe, server up", () => Start(helper, "hook-pipe", pipeName), hookInput),
            ("powershell 5.1 statusline", () => Start("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", $"& '{forward}' statusline"), StatuslineInput),
            ("cmd statusline", () => new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{helper}\" statusline\"") { UseShellExecute = false }, StatuslineInput),
            ("git bash statusline", () => Start(gitBash, "-c", $"'{forward}' statusline"), StatuslineInput),
            ("git bash noop (shell only)", () => Start(gitBash, "-c", "true"), ""),
            ("powershell 5.1 noop (shell only)", () => Start("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "exit 0"), ""),
        };

        await using var pipe = new PipeHookServer(pipeName);
        pipe.Start();

        var report = new JsonObject
        {
            ["runs_per_case"] = runs,
            ["measured"] = ".NET Process.Start to exit, stdin written and closed, stdout drained",
        };
        foreach (var (name, start, input) in cases)
        {
            for (var i = 0; i < 5; i++)
            {
                await TimeOnceAsync(start(), input);
            }

            var samples = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                samples.Add(await TimeOnceAsync(start(), input));
            }

            report[name] = System.Text.Json.JsonSerializer.SerializeToNode(Stats.Summary(samples));
            Console.WriteLine($"{name,-36} {report[name]!.ToJsonString()}");
        }

        // First invocation of a freshly written executable (includes any on-access scanning).
        var firstRun = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var copy = Path.Combine(output, $"first-run-{Guid.NewGuid():N}", $"HelperSpike-{i}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(helper, copy);
            firstRun.Add(await TimeOnceAsync(Start(copy, "statusline"), StatuslineInput));
            var second = await TimeOnceAsync(Start(copy, "statusline"), StatuslineInput);
            Console.WriteLine($"fresh copy {i}: first {firstRun[^1]:0.0} ms, second {second:0.0} ms");
        }

        report["direct statusline, first run of a fresh copy"] = System.Text.Json.JsonSerializer.SerializeToNode(Stats.Summary(firstRun));
        report["forwarded_to_pipe"] = pipe.Receipts.Count;
        Json.Write(Path.Combine(output, "helper-timing.json"), report);
        Console.WriteLine(report.ToJsonString(Json.Indented));
        return 0;
    }

    private static ProcessStartInfo Start(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static async Task<double> TimeOnceAsync(ProcessStartInfo start, string input)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        stopwatch.Stop();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{start.FileName} exited {process.ExitCode}: {await stderr}");
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
