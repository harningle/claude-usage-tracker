using System.IO;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.Tests.Integration;

/// <summary>
/// The integration choices carried out across every known Claude Code folder, and the check of whether
/// everything installed is intact (plan.md section 2: startup path checks, repair, removal).
/// </summary>
public sealed class IntegrationCoordinatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AppSettings _settings = new();
    private readonly IntegrationHarness _harness;

    public IntegrationCoordinatorTests()
    {
        _harness = new IntegrationHarness(_temp.Path, _settings, _time);
        _settings.Profiles.Add(new ProfileSettings { Name = "Work" });
    }

    public void Dispose()
    {
        _harness.Dispose();
        _temp.Dispose();
    }

    private IntegrationCoordinator Coordinator => _harness.Coordinator;

    private ProfileSettings Profile => _settings.Profiles[0];

    private static JsonObject? Settings(string folder) => ClaudeSettingsFile.Read(ClaudeSettingsFile.PathFor(folder));

    private static int HookCount(string folder) =>
        ((Settings(folder)?["hooks"] as JsonObject) ?? new JsonObject()).Sum(e => ((JsonArray)e.Value!).Count);

    private IEnumerable<IntegrationProblemKind> Problems => Coordinator.Problems().Select(p => p.Kind);

    [Fact]
    public void NothingInstalledIsNotAProblem()
    {
        Coordinator.Start();

        Assert.Empty(Coordinator.Problems());
        Assert.Equal(HudServerStatus.Stopped, _harness.Hud.Status);
        Assert.Null(Settings(_harness.DefaultFolder));
        Assert.False(Directory.Exists(Path.Combine(_harness.Data, "bin")));
    }

    [Fact]
    public void ApplyInstallsTheHelperWritesTheConfigurationAndSetsEveryFolder()
    {
        _settings.Statusline.ShowWeekly = true;

        var result = Coordinator.ApplyStatusline([]);

        Assert.True(result.Applied);
        Assert.Null(result.Problem);
        Assert.Empty(result.Foreign);
        Assert.True(_settings.StatuslineEnabled);
        Assert.True(_harness.Saves > 0);
        Assert.Equal(HelperState.Installed, _harness.Service.Helper());
        Assert.True(StatuslineCommand.LoadConfig(_harness.Data).ShowWeekly);
        Assert.True(StatuslineSettings.IsOwned(Settings(_harness.DefaultFolder)!["statusLine"], _harness.Service.HelperPath));
        Assert.Empty(Coordinator.Problems());
    }

    [Fact]
    public void ALaterConfigurationChangeReachesAnAppliedStatusLineOnly()
    {
        _settings.Statusline.ShowModel = false;
        Coordinator.StatuslineConfigChanged();
        // Nothing is applied yet: the helper's file is not written by a mere choice on the page.
        Assert.False(File.Exists(StatuslinePaths.ConfigFile(_harness.Data)));

        Coordinator.ApplyStatusline([]);
        _settings.Statusline.ShowModel = true;
        _settings.Statusline.Use24HourTime = true;
        Coordinator.StatuslineConfigChanged();

        var config = StatuslineCommand.LoadConfig(_harness.Data);
        Assert.True(config is { ShowModel: true, Use24HourTime: true });
    }

    [Fact]
    public void ResetRemovesTheStatusLineAndItsCaches()
    {
        Coordinator.ApplyStatusline([]);

        Assert.Null(Coordinator.ResetStatusline());

        Assert.False(_settings.StatuslineEnabled);
        Assert.Null(Settings(_harness.DefaultFolder)!["statusLine"]);
        Assert.Empty(Directory.GetFiles(StatuslinePaths.Directory(_harness.Data), "*.cache.json"));
        Assert.Empty(Coordinator.Problems());
    }

    [Fact]
    public void AFolderWithItsOwnStatusLineIsReportedBackAndIsNoFault()
    {
        Directory.CreateDirectory(_harness.DefaultFolder);
        File.WriteAllText(ClaudeSettingsFile.PathFor(_harness.DefaultFolder), """{"statusLine":{"type":"command","command":"my-line.sh"}}""");
        _harness.Folders.Add(Path.Combine(_harness.Home, ".claude-other"));

        var result = Coordinator.ApplyStatusline([]);

        var foreign = Assert.Single(result.Foreign);
        Assert.Equal((_harness.DefaultFolder, "my-line.sh"), (foreign.ConfigDirectory, foreign.Command));
        // The other folder was set, so the status line counts as applied; the kept one is the user's choice.
        Assert.True(result.Applied);
        Assert.True(_settings.StatuslineEnabled);
        Assert.Empty(Coordinator.Problems());

        // With the user's consent it is replaced, and Reset puts it back.
        Assert.Empty(Coordinator.ApplyStatusline([_harness.DefaultFolder]).Foreign);
        Coordinator.ResetStatusline();
        Assert.Equal("my-line.sh", (string?)Settings(_harness.DefaultFolder)!["statusLine"]!["command"]);
    }

    [Fact]
    public void ABuildWithoutAHelperCannotApply()
    {
        using var harness = new IntegrationHarness(Path.Combine(_temp.Path, "no-helper"), _settings, _time, bundled: false);

        var result = harness.Coordinator.ApplyStatusline([]);

        Assert.False(result.Applied);
        Assert.NotNull(result.Problem);
        Assert.False(_settings.StatuslineEnabled);
        Assert.Null(Settings(harness.DefaultFolder));
    }

    [Fact]
    public void EnablingTheHudListensAndInstallsHooksAndDisablingRemovesThem()
    {
        Coordinator.SetHudEnabled(true);

        Assert.True(_settings.HudEnabled);
        Assert.Equal(HudServerStatus.Running, _harness.Hud.Status);
        Assert.Equal(8, HookCount(_harness.DefaultFolder));
        Assert.Empty(Coordinator.Problems());

        Coordinator.SetHudEnabled(false);

        Assert.False(_settings.HudEnabled);
        Assert.Equal(HudServerStatus.Stopped, _harness.Hud.Status);
        Assert.Equal(0, HookCount(_harness.DefaultFolder));
        Assert.Empty(Coordinator.Problems());
    }

    [Fact]
    public void ANewLauncherFolderGetsTheStatusLineAndTheHooksAndLosesThemWhenTheLauncherGoes()
    {
        Coordinator.ApplyStatusline([]);
        Coordinator.SetHudEnabled(true);

        Assert.Equal(InstallOutcome.Installed, Coordinator.InstallLauncher(Profile).Outcome);

        var folder = Path.Combine(_harness.Home, ".claude-work");
        Assert.True(StatuslineSettings.IsOwned(Settings(folder)!["statusLine"], _harness.Service.HelperPath));
        Assert.Equal(8, HookCount(folder));
        Assert.Empty(Coordinator.Problems());

        // Someone signs in there, so the folder has more than the tracker's entries.
        File.WriteAllText(Path.Combine(folder, ".credentials.json"), "{}");
        Assert.Null(Coordinator.RemoveLauncher(Profile));

        // The folder is no longer known: the tracker's entries are taken out, the login stays.
        Assert.Equal("{}", Settings(folder)!.ToJsonString());
        Assert.True(File.Exists(Path.Combine(folder, ".credentials.json")));
        Assert.Equal(8, HookCount(_harness.DefaultFolder));
        Assert.Empty(Coordinator.Problems());
    }

    [Fact]
    public void StartBringsUpWhatWasSwitchedOnAndTheCleanExitRemovesOnlyTheHooks()
    {
        Coordinator.ApplyStatusline([]);
        Coordinator.SetHudEnabled(true);
        Coordinator.Shutdown();
        Assert.Equal(0, HookCount(_harness.DefaultFolder));
        Assert.NotNull(Settings(_harness.DefaultFolder)!["statusLine"]);

        // The next run: the same settings and data root.
        using var next = new IntegrationHarness(_temp.Path, _settings, _time);
        next.Coordinator.Start();

        Assert.Equal(HudServerStatus.Running, next.Hud.Status);
        Assert.Equal(8, HookCount(next.DefaultFolder));
        Assert.Empty(next.Coordinator.Problems());
    }

    [Fact]
    public void MissingAndBrokenPiecesAreFoundAndRepaired()
    {
        Coordinator.ApplyStatusline([]);
        Coordinator.SetHudEnabled(true);
        Coordinator.InstallLauncher(Profile);
        var launcher = Path.Combine(_harness.Home, ".local", "bin", "claude-work.exe");

        // As after cleaning up by hand: the helper and a launcher are gone, the status line entry was deleted, and
        // one hook entry was removed.
        File.Delete(_harness.Service.HelperPath);
        File.Delete(launcher);
        var settings = Settings(_harness.DefaultFolder)!;
        settings.Remove("statusLine");
        ((JsonObject)settings["hooks"]!).Remove("Stop");
        File.WriteAllText(ClaudeSettingsFile.PathFor(_harness.DefaultFolder), settings.ToJsonString());

        Assert.Equal(
            [IntegrationProblemKind.HelperMissing, IntegrationProblemKind.LauncherMissing, IntegrationProblemKind.StatuslineNotInstalled, IntegrationProblemKind.HooksIncomplete],
            Problems);

        Assert.Empty(Coordinator.Repair());

        Assert.True(File.Exists(_harness.Service.HelperPath));
        Assert.True(File.Exists(launcher));
        Assert.NotNull(Settings(_harness.DefaultFolder)!["statusLine"]);
        Assert.Equal(8, HookCount(_harness.DefaultFolder));
    }

    [Fact]
    public void RepairNeverOverwritesWhatIsNoLongerTheTrackers()
    {
        Coordinator.ApplyStatusline([]);
        Coordinator.InstallLauncher(Profile);
        var launcher = Path.Combine(_harness.Home, ".local", "bin", "claude-work.exe");
        File.WriteAllText(launcher, "someone else's file now");
        File.WriteAllText(_harness.Service.HelperPath, "tampered");

        Assert.Equal([IntegrationProblemKind.HelperModified, IntegrationProblemKind.LauncherReplaced], Problems);

        // Neither place is taken back: each file stays as it is and stays reported.
        Assert.Equal([IntegrationProblemKind.HelperModified, IntegrationProblemKind.LauncherReplaced], Coordinator.Repair().Select(p => p.Kind));
        Assert.Equal("someone else's file now", File.ReadAllText(launcher));
        Assert.Equal("tampered", File.ReadAllText(_harness.Service.HelperPath));
        Assert.Empty(_harness.Service.Manifest.Retained);
    }

    [Fact]
    public void AStatusLineCommandForAnotherShellIsOutdatedUntilRepaired()
    {
        // A data root whose path needs quoting, so the command depends on the shell.
        using var harness = new IntegrationHarness(Path.Combine(_temp.Path, "with space"), _settings, _time);
        harness.GitBash = false;
        harness.Coordinator.ApplyStatusline([]);

        harness.GitBash = true;
        Assert.Equal([IntegrationProblemKind.StatuslineOutdated], harness.Coordinator.Problems().Select(p => p.Kind));

        Assert.Empty(harness.Coordinator.Repair());
        Assert.StartsWith("'", (string?)Settings(harness.DefaultFolder)!["statusLine"]!["command"]);
    }

    [Fact]
    public void ABusyPortIsAProblemAndRepairMovesToAnotherPort()
    {
        Coordinator.SetHudEnabled(true);
        var port = _harness.Hud.Port!.Value;

        // A second run of the same data whose saved port is held by the first.
        using var second = new IntegrationHarness(_temp.Path, _settings, _time);
        second.Coordinator.Start();
        Assert.Equal([IntegrationProblemKind.HudPortBusy], second.Coordinator.Problems().Select(p => p.Kind));

        Assert.Empty(second.Coordinator.Repair());
        Assert.NotEqual(port, second.Hud.Port);
        Assert.Equal(HudServerStatus.Running, second.Hud.Status);
    }

    [Fact]
    public void ADamagedManifestIsTheOnlyProblemReportedAndNothingIsTouched()
    {
        Coordinator.ApplyStatusline([]);
        _settings.HudEnabled = true;
        File.WriteAllText(Path.Combine(_harness.Data, "install.json"), "{ torn");
        using var next = new IntegrationHarness(_temp.Path, _settings, _time);

        next.Coordinator.Start();

        Assert.Equal([IntegrationProblemKind.ManifestUnreadable], next.Coordinator.Problems().Select(p => p.Kind));
        Assert.Equal([IntegrationProblemKind.ManifestUnreadable], next.Coordinator.Repair().Select(p => p.Kind));
        Assert.False(next.Coordinator.ApplyStatusline([]).Applied);
        next.Coordinator.SetHudEnabled(true);
        // Without the record nothing can be told apart as the tracker's own, so not even the HUD's hooks are written.
        Assert.Equal(HudServerStatus.Stopped, next.Hud.Status);
        Assert.Null(Settings(next.DefaultFolder)!["hooks"]);
        Assert.Equal("{ torn", File.ReadAllText(Path.Combine(_harness.Data, "install.json")));
    }

    [Fact]
    public void FetchedUsageReachesTheFoldersCache()
    {
        var folder = _harness.DefaultFolder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".credentials.json"), LinkHarness.Credentials("access", "refresh", DateTimeOffset.UtcNow.AddHours(4)));
        File.WriteAllText(Path.Combine(folder, ".claude.json"), LinkHarness.Account("acct-a", "a@example.com"));
        Profile.CliLink = new CliLink { ConfigDirectory = folder, AccountUuid = "acct-a" };
        Coordinator.ApplyStatusline([]);
        Assert.Null(_harness.Files.ReadCache(folder)!.ObservedAt);

        _harness.Fetched[Profile.Id] = new CachedUsage
        {
            ProfileId = Profile.Id, ObservedAt = _time.Now, Source = "oauthUsage", AccountIdentity = "acct-a",
            Usage = new Core.Usage.UsageData { SessionPercentage = 55 },
        };
        Coordinator.SyncStatusline();

        Assert.Equal(55, _harness.Files.ReadCache(folder)!.SessionPercentage);
    }
}
