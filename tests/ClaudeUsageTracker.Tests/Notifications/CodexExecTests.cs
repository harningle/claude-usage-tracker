using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Automation;

namespace ClaudeUsageTracker.Tests.Notifications;

/// <summary>
/// The Codex auto-start's run of the CLI as a real process, against a stand-in <c>codex.exe</c> that records what it was
/// started with: fixed arguments, the profile's Codex home, an empty folder of its own, no API key, a closed input, and a
/// run that takes too long stopped with its children. Nothing here starts the real CLI or reaches an account.
/// </summary>
public sealed class CodexExecTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();
    private readonly string _bin;
    private readonly string _home;

    public CodexExecTests()
    {
        _bin = Path.Combine(_temp.Path, "bin dir");
        _home = Path.Combine(_temp.Path, "codex home");
        Directory.CreateDirectory(_home);
        InstallFakeCodex(_bin);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The build output folder name of this configuration, e.g. <c>debug</c>.</summary>
    private static string Configuration => Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

    private static string FakeCodexDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "FakeCodex", Configuration));

    private string Executable => Path.Combine(_bin, CodexExec.FileName);

    /// <summary>Puts the stand-in <c>codex.exe</c> in a folder, with the files it needs to run.</summary>
    private static void InstallFakeCodex(string folder)
    {
        Assert.True(File.Exists(Path.Combine(FakeCodexDirectory, CodexExec.FileName)), $"The stand-in is not built: {FakeCodexDirectory}");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(FakeCodexDirectory, "codex.*"))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
    }

    /// <summary>Tells the stand-in what to do, through a file in the Codex home the runner gives it.</summary>
    private void Fake(object settings) => File.WriteAllText(Path.Combine(_home, "fake.json"), JsonSerializer.Serialize(settings));

    private JsonObject Report() => JsonNode.Parse(File.ReadLines(Path.Combine(_home, "report.jsonl")).Single())!.AsObject();

    private Task<CodexExecResult> Run(TimeSpan? timeout = null) =>
        CodexExec.RunAsync(Executable, _home, timeout ?? Patience, CancellationToken.None);

    private static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task TheCliRunsWithFixedArgumentsTheProfilesHomeAndAnEmptyFolderOfItsOwnWithoutAnApiKey()
    {
        Fake(new { });
        // An API key in the tracker's environment must not reach the CLI, which would bill it instead of the plan.
        var saved = (Environment.GetEnvironmentVariable("CODEX_API_KEY"), Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        Environment.SetEnvironmentVariable("CODEX_API_KEY", "test-key");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        CodexExecResult result;
        try
        {
            result = await Run();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_API_KEY", saved.Item1);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", saved.Item2);
        }

        Assert.Equal(new CodexExecResult(CodexExecOutcome.Completed, 0), result);
        var report = Report();
        Assert.Equal(CodexExec.Arguments, report["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(["exec", "--json", "--ephemeral", "--skip-git-repo-check", "--ignore-user-config", "--sandbox", "read-only", "Hi"], CodexExec.Arguments);
        Assert.False(report["codexApiKey"]!.GetValue<bool>());
        Assert.False(report["openAiApiKey"]!.GetValue<bool>());
        // Its input was closed at once, so it read nothing.
        Assert.Equal("", report["input"]!.GetValue<string>());
        // A new, empty folder, removed afterwards.
        var folder = report["cwd"]!.GetValue<string>();
        Assert.Equal(0, report["cwdEntries"]!.GetValue<int>());
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "ClaudeUsageTracker", "codex-auto-start-"), folder, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("completed", 0, CodexExecOutcome.Completed)]
    [InlineData("none", 0, CodexExecOutcome.Failed)]
    [InlineData("failed", 1, CodexExecOutcome.Failed)]
    [InlineData("completed", 3, CodexExecOutcome.Failed)]
    public async Task OnlyAFinishedTurnAndExitCodeZeroCountAsAStart(string events, int exit, CodexExecOutcome outcome)
    {
        Fake(new { events, exit });

        Assert.Equal(new CodexExecResult(outcome, exit), await Run());
    }

    [Fact]
    public async Task ARunThatTakesTooLongIsStoppedWithItsChildren()
    {
        Fake(new { sleep = 60, linger = 60 });

        var started = Stopwatch.StartNew();
        var result = await Run(TimeSpan.FromSeconds(3));

        Assert.Equal(new CodexExecResult(CodexExecOutcome.TimedOut, null), result);
        Assert.True(started.Elapsed < Patience);
        var report = Report();
        Assert.False(Running(report["pid"]!.GetValue<int>()));
        Assert.False(Running(report["lingeringPid"]!.GetValue<int>()));
    }

    [Fact]
    public async Task AChildThatKeepsTheOutputOpenDoesNotHoldTheStartUp()
    {
        Fake(new { linger = 60 });
        int? lingering = null;
        try
        {
            var started = Stopwatch.StartNew();
            var result = await Run();
            lingering = Report()["lingeringPid"]!.GetValue<int>();

            Assert.Equal(new CodexExecResult(CodexExecOutcome.Completed, 0), result);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"It waited {started.Elapsed}.");
            Assert.True(Running(lingering.Value));
        }
        finally
        {
            if (lingering is { } pid && Running(pid))
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                // Its files stay locked until it has exited, and the temporary folder is removed next.
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public async Task AnEarlierRunsFolderThatWasStillInUseIsRemovedByTheNextRun()
    {
        Fake(new { });
        var earlier = Path.Combine(Path.GetTempPath(), "ClaudeUsageTracker", "codex-auto-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(earlier);

        await Run();

        Assert.False(Directory.Exists(earlier));
    }

    [Fact]
    public async Task WithoutTheCliNothingRuns() =>
        Assert.Equal(new CodexExecResult(CodexExecOutcome.NotFound, null), await CodexExec.RunAsync(null, _home, Patience, CancellationToken.None));

    [Fact]
    public async Task ACodexExeWindowsWillNotStartIsReportedWithWindowsError()
    {
        var broken = Path.Combine(_temp.Path, "broken", CodexExec.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "not a program");

        var result = await CodexExec.RunAsync(broken, _home, Patience, CancellationToken.None);

        Assert.Equal((CodexExecOutcome.StartFailed, (int?)null), (result.Outcome, result.ExitCode));
        Assert.Equal(broken, result.StartError?.Path);
        // Which error is Windows' choice: 216 (ERROR_EXE_MACHINE_TYPE_MISMATCH) on Windows 11 26200.
        Assert.NotEqual(0, result.StartError?.Code ?? 0);
        Assert.False(string.IsNullOrWhiteSpace(result.StartError?.Message));
    }

    [Fact]
    public void TheFirstCodexExeOnThePathIsUsedAndNothingElse()
    {
        var second = Path.Combine(_temp.Path, "second");
        InstallFakeCodex(second);
        var npm = Path.Combine(_temp.Path, "npm");
        Directory.CreateDirectory(npm);
        File.WriteAllText(Path.Combine(npm, "codex.cmd"), "@echo off");

        // Saved PATH values may quote an entry; an npm install's codex.cmd is not run.
        Assert.Equal(Executable, CodexExec.Find([null, $@"C:\nowhere;{npm}", $"\"{_bin}\";{second}"]));
        Assert.Equal(Path.Combine(second, CodexExec.FileName), CodexExec.Find([second, _bin]));
        Assert.Null(CodexExec.Find([npm, null, ""]));
    }

    [Theory]
    [InlineData("""{"type":"turn.completed","usage":{}}""", true)]
    [InlineData("""{"type":"turn.failed"}""", false)]
    [InlineData("""{"type":"item.completed","item":{"text":"turn.completed"}}""", false)]
    [InlineData("turn.completed", false)]
    [InlineData("", false)]
    public void OnlyTheTurnCompletedEventCounts(string line, bool completed) => Assert.Equal(completed, CodexExec.IsTurnCompleted(line));
}
