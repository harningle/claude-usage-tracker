using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace HookBench;

/// <summary>
/// Does a running CLI pick up hook edits? The settings file is atomically replaced between tool
/// calls: the endpoint changes at step 6, hooks are removed at step 12 and re-added at step 18.
/// Each configuration posts to a distinct path secret, so receipts show which one each tool used.
/// Runs once with the <c>--settings</c> file and once with the isolated config directory's
/// user settings (<c>--setting-sources user</c>), the location production hooks would use.
/// </summary>
internal static class Reload
{
    public static async Task<int> RunAsync(Options options)
    {
        var claude = options.Get("claude", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe"));
        var layout = Layout.Create(Path.GetFullPath(options.Required("out")));
        var waitMs = options.GetInt("wait-ms", 3000);
        const int calls = 24;
        var port = Ports.PickEligible(new HashSet<int>());
        var apiPort = Ports.PickEligible(new HashSet<int> { port });
        var labels = new[] { "A", "B", "C" }.ToDictionary(l => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), l => l);
        var secretFor = labels.ToDictionary(p => p.Value, p => p.Key);
        var edits = new (int Step, string? Label)[] { (6, "B"), (12, null), (18, "C") };

        var report = new JsonObject { ["wait_ms_after_each_edit"] = waitMs };
        foreach (var scenario in new[] { "settings-flag", "user-settings" })
        {
            var path = scenario == "settings-flag"
                ? Path.Combine(layout.Root, "settings-reload.json")
                : Path.Combine(layout.Config, "settings.json");
            string[] arguments = scenario == "settings-flag"
                ? ["--settings", path, "--setting-sources", ""]
                : ["--setting-sources", "user"];

            ClaudeRunner.WriteSettings(path, ClaudeRunner.HttpHooks(port, secretFor["A"]));
            var listener = new HttpHookListener(port, labels.Keys.ToArray());
            await listener.StartAsync();
            var editedAt = new Dictionary<int, long>();

            async Task BeforeStep(int step)
            {
                foreach (var (editStep, label) in edits.Where(e => e.Step == step))
                {
                    ClaudeRunner.WriteSettings(path,
                        label is null ? ClaudeRunner.NoHooks() : ClaudeRunner.HttpHooks(port, secretFor[label]));
                    editedAt[editStep] = Clock.Now;
                    await Task.Delay(waitMs);
                }
            }

            ClaudeRun run;
            await using (var api = new FakeAnthropicApi(apiPort, layout.ReadTarget, layout.MissingTarget, calls, BeforeStep))
            {
                await api.StartAsync();
                run = await ClaudeRunner.RunAsync(claude, layout.Work, layout.Config, apiPort, arguments);
            }

            await Task.Delay(200);
            await listener.StopAsync(abort: false);
            if (scenario == "user-settings")
            {
                File.Delete(path);
            }

            // Per tool step: the configurations that received its PreToolUse and PostToolUse.
            var perStep = new JsonArray();
            for (var s = 0; s < calls; s++)
            {
                var id = $"toolu_bench_{s:D3}";
                string Seen(string suffix) => string.Join('+', listener.Receipts
                    .Where(r => r.ToolUseId == id && r.Event == suffix).Select(r => labels[r.Route]).Distinct()) is { Length: > 0 } seen
                    ? seen
                    : "-";
                perStep.Add($"{s}: pre {Seen("pre-tool-use")} post {Seen("post-tool-use")}");
            }

            report[scenario] = new JsonObject
            {
                ["exit_code"] = run.ExitCode,
                ["result"] = run.ResultSubtype,
                ["edits"] = "step 6 -> B, step 12 -> no hooks, step 18 -> C",
                ["per_step"] = perStep,
                ["session_events"] = string.Join(", ", listener.Receipts
                    .Where(r => r.ToolUseId is null).Select(r => $"{ClaudeRunner.EventForSuffix(r.Event)}@{labels[r.Route]}")),
            };
        }

        Json.Write(Path.Combine(layout.Results, "reload.json"), report);
        Console.WriteLine(report.ToJsonString(Json.Indented));
        return 0;
    }
}
