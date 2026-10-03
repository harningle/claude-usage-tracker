using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.Core.Integration;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.Tests.Launchers;

/// <summary>
/// The published NativeAOT helper as real processes (plan.md section 3, Milestone 5 acceptance): as a launcher
/// under PowerShell, Command Prompt and Git Bash it passes arguments and exit codes through and survives
/// Ctrl+C and Ctrl+Break with its child; as the status line it runs through both shells Claude Code uses.
/// The child is a stand-in <c>claude.exe</c> that records what it was started with. These cases need the helper
/// published first; without it they are reported as skipped, never as passed.
/// </summary>
public sealed partial class LauncherProcessTests : IDisposable
{
    private const string PublishHint =
        "The NativeAOT helper is not published. Run: dotnet publish src/ClaudeUsageTracker.Helper/ClaudeUsageTracker.Helper.csproj -c Release -r win-x64";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();
    private readonly string _home;
    private readonly string _bin;
    private readonly string _launcher;
    private readonly string _report;

    public LauncherProcessTests()
    {
        _home = Path.Combine(_temp.Path, "home dir");
        _bin = Path.Combine(_home, ".local", "bin");
        _launcher = Path.Combine(_bin, "claude-work.exe");
        _report = Path.Combine(_temp.Path, "report.jsonl");
        Directory.CreateDirectory(_bin);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>An exit code a shell printed, as opposed to the command typed to ask for it.</summary>
    [GeneratedRegex(@"EXIT:-?\d+")]
    private static partial Regex ExitCodeShown();

    /// <summary>The build output folder name of this configuration, e.g. <c>debug</c>.</summary>
    private static string Configuration => Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

    private static string PublishedHelper => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "publish", "ClaudeUsageTracker.Helper", "release_win-x64", HelperInfo.FileName));

    private static string FakeClaudeDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "FakeClaude", Configuration));

    private static string GitBash => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");

    /// <summary>Installs the launcher as the app does: a copy of the helper named <c>claude-work.exe</c> in <c>.local\bin</c>.</summary>
    private void InstallLauncher()
    {
        Assert.SkipUnless(File.Exists(PublishedHelper), PublishHint);
        File.Copy(PublishedHelper, _launcher);
    }

    /// <summary>Puts the stand-in <c>claude.exe</c> in a folder, with the files it needs to run.</summary>
    private static void InstallFakeClaude(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(FakeClaudeDirectory, "claude.*"))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
    }

    private Dictionary<string, string> Environment_(int exitCode = 0, int interrupts = 0, string? extraPath = null, bool raiseBreak = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            environment[(string)variable.Key] = (string?)variable.Value ?? "";
        }

        // Only system folders and what the test adds: the real claude.exe is never found.
        environment["PATH"] = string.Join(';', new[] { extraPath, Environment.SystemDirectory }.OfType<string>());
        environment["FAKE_CLAUDE_REPORT"] = _report;
        environment["FAKE_CLAUDE_EXIT"] = exitCode.ToString();
        environment["FAKE_CLAUDE_INTERRUPTS"] = interrupts.ToString();
        environment["FAKE_CLAUDE_RAISE_BREAK"] = raiseBreak ? "1" : "0";
        // As a session started by another launcher would have it: the launcher sets its own.
        environment["CLAUDE_CONFIG_DIR"] = @"C:\somewhere\else";
        environment.Remove("DOTNET_ROOT");
        return environment;
    }

    /// <summary>What the stand-in has recorded so far; it may be appending at this moment.</summary>
    private List<JsonObject> Reports()
    {
        if (!File.Exists(_report))
        {
            return [];
        }

        using var stream = new FileStream(_report, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        // Only complete lines: the last one may still be on its way.
        return [.. text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => (JsonObject)JsonNode.Parse(line)!)];
    }

    private (int ExitCode, string Output, string Error) RunDirect(string[] arguments, Dictionary<string, string> environment, string? executable = null)
    {
        var start = new ProcessStartInfo(executable ?? _launcher)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = _temp.Path,
        };
        start.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(Patience), "The launcher did not exit.");
        return (process.ExitCode, output.Result, error.Result);
    }

    // The launcher by itself

    [Fact]
    public void EveryArgumentReachesClaudeUnchangedWithTheProfilesFolderAndTheExitCodeComesBack()
    {
        InstallLauncher();
        InstallFakeClaude(_bin);
        string[] arguments =
        [
            "-p", "hello world", "", "a\"quoted\"b", @"C:\path with spaces\file name.txt", @"trailing\backslash\", "%PATH%", "$HOME", "`tick`",
            "a&b|c<d>e^f", "semi;colon,comma", "*.cs", "!bang!", "(paren)", "--flag=va lue", "héllo wörld 日本語", "statusline", "version", "--",
        ];

        var (exitCode, output, _) = RunDirect(arguments, Environment_(exitCode: 42));

        Assert.Equal(42, exitCode);
        var report = Assert.Single(Reports());
        Assert.Equal(arguments, report["args"]!.AsArray().Select(a => (string?)a));
        // The folder comes from the launcher's own name and place, whatever the environment said before.
        Assert.Equal(Path.Combine(_home, ".claude-work"), (string?)report["configDir"]);
        Assert.Equal(_temp.Path, (string?)report["cwd"]);
        // The child writes straight to the caller's console or pipe.
        Assert.Contains("fake claude ready", output);
    }

    [Fact]
    public void ClaudeIsFoundOnPathWhenItIsNotBesideTheLauncher()
    {
        InstallLauncher();
        var elsewhere = Path.Combine(_temp.Path, "native install");
        InstallFakeClaude(elsewhere);

        var (exitCode, _, _) = RunDirect(["--version"], Environment_(exitCode: 3, extraPath: elsewhere));

        Assert.Equal(3, exitCode);
        Assert.Equal(["--version"], Assert.Single(Reports())["args"]!.AsArray().Select(a => (string?)a));
    }

    [Fact]
    public void WithoutClaudeTheLauncherSaysSoAndStartsNothing()
    {
        InstallLauncher();
        // Another launcher and the helper are in the folder; neither is Claude Code.
        File.Copy(PublishedHelper, Path.Combine(_bin, "claude-other.exe"));
        File.Copy(PublishedHelper, Path.Combine(_bin, HelperInfo.FileName));

        var (exitCode, output, error) = RunDirect(["-p", "hi"], Environment_());

        Assert.Equal(127, exitCode);
        Assert.Contains("claude.exe was not found", error);
        Assert.Equal("", output);
        Assert.Empty(Reports());
    }

    [Fact]
    public void ALauncherOutsideLocalBinRefusesToGuessAFolder()
    {
        Assert.SkipUnless(File.Exists(PublishedHelper), PublishHint);
        var moved = Path.Combine(_temp.Path, "tools", "claude-work.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Copy(PublishedHelper, moved);
        InstallFakeClaude(Path.GetDirectoryName(moved)!);

        var (exitCode, _, error) = RunDirect(["-p", "hi"], Environment_(), moved);

        Assert.Equal(127, exitCode);
        Assert.Contains(".local\\bin", error);
        Assert.Empty(Reports());
    }

    [Fact]
    public void TheLauncherOutlivesCtrlBreakAndReturnsItsChildsExitCode()
    {
        InstallLauncher();
        InstallFakeClaude(_bin);
        var environment = Environment_(exitCode: 9, interrupts: 1, raiseBreak: true);

        // In a console of their own, the child raises Ctrl+Break for both and handles its own.
        using var console = PseudoConsole.Start($"\"{_launcher}\" -p waiting", _temp.Path, environment);

        Assert.Equal(9, console.WaitForExit(Patience));
        Assert.Equal(["ControlBreak"], Reports().Skip(1).Select(r => (string?)r["interrupt"]));
        Assert.True(console.WaitForText("fake claude exiting", Patience), console.Text);
    }

    // The launcher under each shell: arguments with spaces and metacharacters, the exit code, and Ctrl+C.

    public static TheoryData<string> Shells => ["powershell", "cmd", "bash"];

    private (string CommandLine, string Run, string ExitCode, string Marker) Shell(string shell)
    {
        // The launcher's path has a space, so each shell must quote it its own way.
        var forward = _launcher.Replace('\\', '/');
        return shell switch
        {
            // What is typed must not reach the user's own PowerShell history.
            "powershell" => ("powershell.exe -NoLogo -NoProfile -NoExit -Command \"Set-PSReadLineOption -HistorySaveStyle SaveNothing\"",
                $"& '{_launcher}' -p 'hello world' 'a&b|c' '$HOME' '%PATH%' 'x;y' --flag='va lue'", "echo \"EXIT:$LASTEXITCODE\"", "echo \"MARK-$(1+1)\""),
            "cmd" => ($"{Path.Combine(Environment.SystemDirectory, "cmd.exe")} /d /q",
                $"\"{_launcher}\" -p \"hello world\" \"a&b|c\" $HOME \"x;y\" \"--flag=va lue\"", "echo EXIT:%ERRORLEVEL%", "echo MARK-2"),
            _ => ($"\"{GitBash}\" --norc --noprofile -i",
                $"'{forward}' -p 'hello world' 'a&b|c' '$HOME' '%PATH%' 'x;y' --flag='va lue'", "echo \"EXIT:$?\"", "echo \"MARK-$((1+1))\""),
        };
    }

    private static string[] ExpectedArguments(string shell) => shell == "cmd"
        ? ["-p", "hello world", "a&b|c", "$HOME", "x;y", "--flag=va lue"]
        : ["-p", "hello world", "a&b|c", "$HOME", "%PATH%", "x;y", "--flag=va lue"];

    private void SkipUnlessShell(string shell) =>
        Assert.SkipWhen(shell == "bash" && !File.Exists(GitBash), "Git Bash is not installed on this PC.");

    [Theory]
    [MemberData(nameof(Shells))]
    public void UnderEachShellArgumentsAndTheExitCodeArePreserved(string shell)
    {
        SkipUnlessShell(shell);
        InstallLauncher();
        InstallFakeClaude(_bin);
        var (commandLine, run, exitCode, _) = Shell(shell);

        using var console = PseudoConsole.Start(commandLine, _temp.Path, Environment_(exitCode: 37));
        console.Type(run + "\r");
        Assert.True(console.WaitForText("fake claude exiting", Patience), console.Text);
        console.Type(exitCode + "\r");

        Assert.True(console.WaitForText("EXIT:37", Patience), console.Text);
        var report = Assert.Single(Reports());
        Assert.Equal(ExpectedArguments(shell), report["args"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(Path.Combine(_home, ".claude-work"), (string?)report["configDir"]);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void UnderEachShellCtrlCReachesClaudeAndThePromptDoesNotReturnOverALiveChild(string shell)
    {
        SkipUnlessShell(shell);
        InstallLauncher();
        InstallFakeClaude(_bin);
        var (commandLine, run, exitCode, marker) = Shell(shell);

        using var console = PseudoConsole.Start(commandLine, _temp.Path, Environment_(exitCode: 5, interrupts: 3));
        console.Type(run + "\r");
        Assert.True(console.WaitForText("fake claude ready", Patience), console.Text);
        var child = (int)Assert.Single(Reports())["pid"]!;

        // Repeated interruption: Claude Code handles each one itself and keeps running.
        for (var press = 1; press <= 2; press++)
        {
            console.Type("\x03");
            Assert.True(SpinWait.SpinUntil(() => Reports().Count == 1 + press, Patience), console.Text);
        }

        // The session is still alive, and so is the launcher: a command typed now must not run, because the
        // shell must not have taken the prompt back.
        console.Type(marker + "\r");
        Thread.Sleep(1500);
        Assert.False(Process.GetProcessById(child).HasExited);
        Assert.DoesNotContain("MARK-2", console.Text);
        Assert.DoesNotContain("fake claude exiting", console.Text);

        console.Type("\x03");
        Assert.True(console.WaitForText("fake claude exiting", Patience), console.Text);
        Assert.Equal(["ControlC", "ControlC", "ControlC"], Reports().Skip(1).Select(r => (string?)r["interrupt"]));

        // Only now is the shell back; it reports the child's own exit code where it keeps one after an interruption.
        // PowerShell drops keys typed before its prompt is back, with or without the launcher, so ask until it answers.
        var asking = Stopwatch.StartNew();
        do
        {
            console.Type("\r" + exitCode + "\r");
        }
        while (!SpinWait.SpinUntil(() => ExitCodeShown().IsMatch(console.Text), TimeSpan.FromSeconds(2)) && asking.Elapsed < Patience);
        Assert.True(ExitCodeShown().IsMatch(console.Text), console.Text);
        TestContext.Current.SendDiagnosticMessage($"{shell} after Ctrl+C: {ExitCodeShown().Match(console.Text).Value}");
        if (shell != "bash")
        {
            // Bash reports the exit status of whatever ran last, and a command was typed meanwhile.
            Assert.Contains("EXIT:5", console.Text);
        }
    }

    // The status line through the shells Claude Code runs it with

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheStatusLineCommandRunsThroughBothShellsEvenFromAPathWithSpaces(bool gitBash)
    {
        Assert.SkipUnless(File.Exists(PublishedHelper), PublishHint);
        Assert.SkipWhen(gitBash && !File.Exists(GitBash), "Git Bash is not installed on this PC.");
        var data = Path.Combine(_temp.Path, "data root with spaces & more");
        var helper = HelperInfo.InstalledPath(data);
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.Copy(PublishedHelper, helper);
        var folder = Path.Combine(_home, ".claude");
        var files = new StatuslineFiles(data);
        files.WriteConfig(new StatuslineConfig { ShowProfile = true, ShowProgressBar = false, ShowResetTime = false, ColorMode = StatuslineConfig.Monochrome });
        files.WriteCache(new StatuslineCache
        {
            ConfigDirectory = folder, ProfileName = "Work", RefreshIntervalSeconds = 30, ObservedAt = DateTimeOffset.UtcNow, SessionPercentage = 64,
        });
        var command = StatuslineSettings.Command(helper, gitBash);

        var start = gitBash
            ? new ProcessStartInfo(GitBash) { ArgumentList = { "-c", command } }
            : new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command } };
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.CreateNoWindow = true;
        start.Environment["CLAUDE_CONFIG_DIR"] = folder;
        using var process = Process.Start(start)!;
        process.StandardInput.Write("""{"model":{"display_name":"Opus"},"workspace":{"current_dir":"C:/work/my project"},"context_window":{"context_window_size":200000}}""");
        process.StandardInput.Close();
        var cancellation = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        Assert.True(process.WaitForExit(Patience));

        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal("my project\e[0m │ \e[0mOpus\e[0m │ \e[0mWork\e[0m │ \e[0mCtx: 0%\e[0m │ \e[0mUsage: 64%\e[0m", (await output).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void TheHelperItselfReadsNoLoginAndAnswersItsVersion()
    {
        Assert.SkipUnless(File.Exists(PublishedHelper), PublishHint);

        var (exitCode, output, _) = RunDirect(["version"], Environment_(), PublishedHelper);
        Assert.Equal(0, exitCode);
        Assert.Equal($"ClaudeUsageTracker.Helper {HelperInfo.Version}", output.Trim());

        var (unknown, _, error) = RunDirect(["hook"], Environment_(), PublishedHelper);
        Assert.Equal(2, unknown);
        Assert.Contains("usage:", error);
    }

    [Fact]
    public async Task StatusLinesKeepWorkingWhileTheHelperIsUpgradedUnderThem()
    {
        Assert.SkipUnless(File.Exists(PublishedHelper), PublishHint);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var data = Path.Combine(_temp.Path, "data");
        var service = new IntegrationService(data, _home, PublishedHelper, time);
        Assert.Equal(InstallOutcome.Installed, service.InstallHelper().Outcome);
        var environment = Environment_();
        using var stop = new CancellationTokenSource();
        var runs = 0;
        var failures = 0;

        // Four sessions asking for their status line again and again, as Claude Code does after each message.
        var sessions = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var start = new ProcessStartInfo(service.HelperPath, "statusline")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                try
                {
                    using var process = Process.Start(start)!;
                    process.StandardInput.Write("""{"model":{"display_name":"Opus"}}""");
                    process.StandardInput.Close();
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    Interlocked.Increment(ref runs);
                    if (process.ExitCode != 0 || !output.Contains("Opus"))
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // The moment between the two renames, when the canonical name does not exist.
                    Interlocked.Increment(ref runs);
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToArray();

        // Ten upgrades meanwhile: each renames the running image aside and promotes a new copy.
        var upgrades = 0;
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            service.Manifest.Helper!.Sha256 = "changed";
            service.Store.Save(service.Manifest);
            // The record no longer matches the file, so the service installs the bundled helper again.
            if (service.InstallHelper().Outcome == InstallOutcome.Installed)
            {
                upgrades++;
            }
        }

        stop.Cancel();
        await Task.WhenAll(sessions);

        Assert.Equal(10, upgrades);
        Assert.True(runs > 20, $"Only {runs} status lines ran.");
        // The rename gap may fail an invocation; it must stay rare, and nothing may be left broken.
        Assert.True(failures <= runs / 10, $"{failures} of {runs} status lines failed.");
        Assert.Equal(HelperState.Installed, service.Helper());
        Assert.True(SpinWait.SpinUntil(() =>
        {
            service.CleanRetained();
            return service.Manifest.Retained.Count == 0;
        }, Patience));
        Assert.Equal([HelperInfo.FileName], Directory.EnumerateFiles(Path.GetDirectoryName(service.HelperPath)!).Select(Path.GetFileName));
        TestContext.Current.SendDiagnosticMessage($"{runs} status lines during 10 upgrades, {failures} failed in the rename gap.");
    }
}
