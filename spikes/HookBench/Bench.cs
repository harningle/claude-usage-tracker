using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HookBench;

/// <summary>
/// The Milestone 1 hook-latency conditions: no hooks, accepting/stopped/restarting HTTP listener
/// (upstream's transport) and direct-exec async command hooks with the pipe server up or down.
/// </summary>
internal static class Bench
{
    public static readonly string[] AllConditions = ["none", "http-up", "http-down", "http-restart", "async-up", "async-down"];

    public static async Task<int> RunAsync(Options options)
    {
        var claude = options.Get("claude", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe"));
        var helper = Path.GetFullPath(options.Required("helper"));
        var output = Path.GetFullPath(options.Required("out"));
        var calls = options.GetInt("calls", 25);
        var repeat = options.GetInt("repeat", 3);
        var conditions = options.Get("conditions", string.Join(',', AllConditions)).Split(',');

        var layout = Layout.Create(output);
        var apiPort = Ports.PickEligible(new HashSet<int>());
        var hookPort = Ports.PickEligible(new HashSet<int> { apiPort });
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var pipe = $"ClaudeUsageTracker.HookBench.{Guid.NewGuid():N}";
        Console.WriteLine($"claude {claude}; api port {apiPort}; hook port {hookPort}; {calls} calls x {repeat} per condition");

        // A discarded warm-up absorbs first-run work in the fresh config directory; its CLI debug
        // log shows which hooks were effective.
        var warmup = options.Get("warmup-condition", "none");
        var warmupRecord = await RunConditionAsync(warmup, 0, layout, claude, helper, apiPort, hookPort, secret, pipe, 3,
            Path.Combine(layout.Results, $"warmup-{warmup}-debug.txt"));
        Json.Write(Path.Combine(layout.Results, $"warmup-{warmup}.json"), warmupRecord);

        var runs = new List<RunRecord>();
        for (var rep = 1; rep <= repeat; rep++)
        {
            foreach (var condition in conditions)
            {
                var record = await RunConditionAsync(condition, rep, layout, claude, helper, apiPort, hookPort, secret,
                    pipe, calls, null);
                runs.Add(record);
                Json.Write(Path.Combine(layout.Results, $"{condition}-{rep}.json"), record);
                Console.WriteLine($"{condition,-13} rep {rep}: exit {record.ExitCode} {record.ResultSubtype} steps {record.StepsCompleted}/{calls + 2} " +
                                  $"gap p50 {Stats.Round(Stats.Percentile(record.GapsMs, 0.5))} ms p95 {Stats.Round(Stats.Percentile(record.GapsMs, 0.95))} ms " +
                                  $"events {record.EventCounts.Values.Sum()} total {record.TotalMs:0} ms");
            }
        }

        var summary = Summarise(runs, calls);
        Json.Write(Path.Combine(layout.Results, "summary.json"), summary);
        Console.WriteLine(summary.ToJsonString(Json.Indented));
        return 0;
    }

    private static async Task<RunRecord> RunConditionAsync(string condition, int rep, Layout layout, string claude,
        string helper, int apiPort, int hookPort, string secret, string pipeName, int calls, string? debugFile)
    {
        var settingsPath = Path.Combine(layout.Root, $"settings-{condition}.json");
        ClaudeRunner.WriteSettings(settingsPath, condition switch
        {
            "none" => ClaudeRunner.NoHooks(),
            "http-up" or "http-down" or "http-restart" => ClaudeRunner.HttpHooks(hookPort, secret),
            "async-up" or "async-down" => ClaudeRunner.AsyncCommandHooks(helper, pipeName),
            _ => throw new ArgumentException($"Unknown condition '{condition}'."),
        });

        var listener = new HttpHookListener(hookPort, [secret]);
        PipeHookServer? pipe = null;
        if (condition is "http-up" or "http-restart")
        {
            await listener.StartAsync();
        }

        if (condition == "async-up")
        {
            pipe = new PipeHookServer(pipeName);
            pipe.Start();
        }

        // Restart condition: the listener is up for steps 0-4, down (aborted) for 5-9, up for 10-14...
        var listenerUp = new ConcurrentDictionary<int, bool>();
        Func<int, Task>? beforeStep = condition != "http-restart" ? null : async step =>
        {
            var up = step / 5 % 2 == 0;
            if (up && !listener.Running)
            {
                await listener.StartAsync();
            }
            else if (!up && listener.Running)
            {
                await listener.StopAsync(abort: true);
            }

            listenerUp[step] = up;
        };

        ClaudeRun run;
        FakeAnthropicApi api;
        await using (api = new FakeAnthropicApi(apiPort, layout.ReadTarget, layout.MissingTarget, calls, beforeStep))
        {
            await api.StartAsync();
            run = await ClaudeRunner.RunAsync(claude, layout.Work, layout.Config, apiPort,
                ["--settings", settingsPath, "--setting-sources", ""], debugFile);
        }

        // Async hooks may still be delivering after the CLI exits.
        await Task.Delay(condition.StartsWith("async", StringComparison.Ordinal) ? 2000 : 200);
        await listener.StopAsync(abort: false);
        if (pipe is not null)
        {
            await pipe.DisposeAsync();
        }

        var receipts = listener.Receipts
            .Select(r => r with { Event = ClaudeRunner.EventForSuffix(r.Event) })
            .Concat(pipe?.Receipts ?? Enumerable.Empty<HookReceipt>())
            .ToList();
        return RunRecord.From(condition, rep, calls, run, api, receipts, listenerUp);
    }

    private static JsonObject Summarise(List<RunRecord> runs, int calls)
    {
        var baseline = runs.Where(r => r.Condition == "none").SelectMany(r => r.GapsMs).ToList();
        var summary = new JsonObject
        {
            ["calls_per_run"] = calls,
            ["gap_definition"] = "ms from the end of a tool_use response to the next request: CLI parsing, PreToolUse hooks, Read, PostToolUse hooks",
        };
        foreach (var group in runs.GroupBy(r => r.Condition))
        {
            var gaps = group.SelectMany(r => r.GapsMs).ToList();
            var entry = new JsonObject
            {
                ["runs"] = group.Count(),
                ["failed_runs"] = group.Count(r => r.ExitCode != 0 || r.IsError == true || r.TimedOut),
                ["gap_ms"] = Node(Stats.Summary(gaps)),
                ["overhead_vs_none_ms"] = new JsonObject
                {
                    ["p50"] = Stats.Round(Stats.Percentile(gaps, 0.5) - Stats.Percentile(baseline, 0.5)),
                    ["p95"] = Stats.Round(Stats.Percentile(gaps, 0.95) - Stats.Percentile(baseline, 0.95)),
                    ["mean"] = Stats.Round(gaps.DefaultIfEmpty(double.NaN).Average() - baseline.DefaultIfEmpty(double.NaN).Average()),
                },
                ["startup_ms"] = Node(Stats.Summary(group.Select(r => r.StartupMs).ToList())),
                ["teardown_ms"] = Node(Stats.Summary(group.Select(r => r.TeardownMs).ToList())),
                ["total_ms"] = Node(Stats.Summary(group.Select(r => r.TotalMs).ToList())),
                ["pre_tool_hook_arrival_ms"] = Node(Stats.Summary(group.SelectMany(r => r.PreToolArrivalMs).ToList())),
                ["events_per_run"] = Node(group.SelectMany(r => r.EventCounts).GroupBy(p => p.Key)
                    .ToDictionary(p => p.Key, p => p.Sum(x => x.Value) / (double)group.Count())),
            };
            if (group.Key == "http-restart")
            {
                entry["gap_ms_listener_up"] = Node(Stats.Summary(group.SelectMany(r => r.GapsWhenUpMs).ToList()));
                entry["gap_ms_listener_down"] = Node(Stats.Summary(group.SelectMany(r => r.GapsWhenDownMs).ToList()));
            }

            summary[group.Key] = entry;
        }

        return summary;
    }

    private static JsonNode? Node(object value) => JsonSerializer.SerializeToNode(value);
}

internal sealed record Layout(string Root, string Work, string Config, string Results, string ReadTarget, string MissingTarget)
{
    public static Layout Create(string root)
    {
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        var results = Directory.CreateDirectory(Path.Combine(root, "results")).FullName;
        var target = Path.Combine(work, "target.txt");
        File.WriteAllText(target, "hook benchmark target\n");
        return new Layout(root, work, config, results, target, Path.Combine(work, "missing.txt"));
    }
}

internal sealed record RunRecord(
    string Condition, int Rep, int ExitCode, bool TimedOut, string? ResultSubtype, bool? IsError, int? NumTurns,
    int StepsCompleted, List<double> GapsMs, List<double> GapsWhenUpMs, List<double> GapsWhenDownMs, double? FailureGapMs,
    double StartupMs, double TeardownMs, double TotalMs, List<double> PreToolArrivalMs,
    Dictionary<string, int> EventCounts, List<string> SideRequests, List<string> Anomalies, string StderrTail)
{
    public static RunRecord From(string condition, int rep, int calls, ClaudeRun run, FakeAnthropicApi api,
        List<HookReceipt> receipts, IReadOnlyDictionary<int, bool> listenerUp)
    {
        var steps = api.Steps;
        double? Gap(int s) =>
            steps.TryGetValue(s, out var a) && steps.TryGetValue(s + 1, out var b) && a.ResponseEndTicks > 0
                ? Clock.Ms(b.RequestTicks - a.ResponseEndTicks)
                : null;

        var gaps = new List<double>();
        var up = new List<double>();
        var down = new List<double>();
        var preArrival = new List<double>();
        for (var s = 0; s < calls; s++)
        {
            if (Gap(s) is not { } gap)
            {
                continue;
            }

            gaps.Add(gap);
            if (listenerUp.TryGetValue(s, out var isUp))
            {
                (isUp ? up : down).Add(gap);
            }

            var pre = receipts.FirstOrDefault(r => r.Event == "PreToolUse" && r.ToolUseId == $"toolu_bench_{s:D3}");
            if (pre is not null)
            {
                preArrival.Add(Clock.Ms(pre.ReceivedTicks - steps[s].ResponseEndTicks));
            }
        }

        var startup = steps.TryGetValue(0, out var first) ? Clock.Ms(first.RequestTicks - run.StartedTicks) : double.NaN;
        var teardown = steps.TryGetValue(calls + 1, out var last) && last.ResponseEndTicks > 0
            ? Clock.Ms(run.ExitedTicks - last.ResponseEndTicks)
            : double.NaN;
        return new RunRecord(condition, rep, run.ExitCode, run.TimedOut, run.ResultSubtype, run.IsError, run.NumTurns,
            steps.Count, gaps, up, down, Gap(calls), startup, teardown, Clock.Ms(run.ExitedTicks - run.StartedTicks),
            preArrival, receipts.GroupBy(r => r.Event).ToDictionary(g => g.Key, g => g.Count()),
            api.SideRequests.Distinct().ToList(), api.Anomalies.ToList(), run.StderrTail);
    }
}
