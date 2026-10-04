using System.IO;
using System.Windows.Media;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Hud;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Statusline;
using ClaudeUsageTracker.Core.Usage;
using ClaudeUsageTracker.Tests.Integration;

namespace ClaudeUsageTracker.Tests.ViewModels;

/// <summary>The Milestone 5 pages: Claude CLI (upstream <c>ClaudeCodeView</c>), Activity HUD (<c>NotchHUDSettingsView</c>) and the launcher card (<c>TerminalLauncherSection</c>).</summary>
public sealed class MilestoneFiveViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AppSettings _settings = new();
    private readonly IntegrationHarness _harness;

    public MilestoneFiveViewTests()
    {
        _settings.Profiles.Add(new ProfileSettings { Name = "Work" });
        _settings.ActiveProfileId = _settings.Profiles[0].Id;
        _harness = new IntegrationHarness(_temp.Path, _settings, _time);
    }

    public void Dispose()
    {
        _harness.Dispose();
        _temp.Dispose();
    }

    private StatuslineViewModel Statusline => _harness.Pages.Statusline;

    private HudSettingsViewModel Hud => _harness.Pages.Hud;

    private LauncherViewModel Launcher => _harness.Pages.Launcher;

    private ProfileSettings Profile => _settings.Profiles[0];

    private string Preview => string.Concat(Statusline.Preview.Select(run => run.Text));

    // Claude CLI

    [Fact]
    public void ThePreviewIsTheRenderersOwnLineForSampleData()
    {
        Assert.Equal("claude-usage │ ⎇ main │ Opus │ Ctx: 48% │ Usage: 29% ▓▓▓░░░┃░░░ → Reset: " + ResetTime(_time.Now.AddHours(2)), Preview);

        Statusline.ShowBranch = false;
        Statusline.ShowProfile = true;
        Statusline.ContextAsTokens = true;
        Statusline.ShowWeekly = true;
        Statusline.ShowExtraUsage = true;
        Statusline.ShowResetTime = false;
        Statusline.ShowWeeklyResetTime = false;

        Assert.Equal("claude-usage │ Opus │ Work │ Ctx: 96K │ Usage: 29% ▓▓▓░░░┃░░░ │ Weekly: 45% ▓▓▓┃▓░░░░░ │ 9.49 USD", Preview);
        // Every choice is kept as it is made.
        Assert.True(_settings.Statusline is { ShowBranch: false, ShowProfile: true, ContextAsTokens: true, ShowWeekly: true });
        Assert.True(_harness.Saves >= 7);
    }

    [Fact]
    public void ThePreviewUsesTheActiveProfilesLastUsage()
    {
        _harness.ActiveState = new ProfileUsageState
        {
            ProfileId = Profile.Id,
            Usage = new CachedUsage
            {
                ProfileId = Profile.Id, ObservedAt = _time.Now, Source = "oauthUsage",
                Usage = new UsageData { SessionPercentage = 71.6, SessionResetTime = _time.Now.AddHours(4) },
            },
        };
        Statusline.ShowResetTime = false;

        Assert.EndsWith("Usage: 71% ▓▓▓▓▓▓▓░░░", Preview.Replace("┃", "▓"));
    }

    [Fact]
    public void ThePreviewCarriesTheTerminalsColours()
    {
        var runs = Statusline.Preview;

        Assert.Equal(Color.FromRgb(0, 0, 238), ((SolidColorBrush)runs[0].Brush).Color);
        // 29 % is the third usage level: colour 34 of the 256-colour palette.
        Assert.Equal(Color.FromRgb(0, 175, 0), ((SolidColorBrush)runs.Last(r => r.Text.Contains("Usage")).Brush).Color);

        Statusline.ColourMode = Statusline.ColourModes.Single(m => m.RawValue == StatuslineConfig.SingleColor);
        Statusline.SingleColour = "ff8000";
        Assert.Equal("#FF8000", _settings.Statusline.SingleColorHex);
        Assert.All(Statusline.Preview, run => Assert.Equal(Color.FromRgb(255, 128, 0), ((SolidColorBrush)run.Brush).Color));
        Assert.True(Statusline.IsSingleColour);
    }

    [Fact]
    public void AColourThatIsNotHexIsNotTaken()
    {
        Statusline.SingleColour = "orange";
        Statusline.DirectoryColour = "#12";

        Assert.Equal("#00BFFF", Statusline.SingleColour);
        Assert.Equal("#0000EE", Statusline.DirectoryColour);
    }

    [Fact]
    public void OptionalElementColoursAreSwitchedOnAndOffAsUpstream()
    {
        Statusline.ColourMode = Statusline.ColourModes.Single(m => m.RawValue == StatuslineConfig.PerElement);
        Assert.True(Statusline.IsPerElement);
        Assert.False(Statusline.OverrideUsageColour);

        Statusline.OverrideUsageColour = true;
        Assert.Equal("#00BB00", Statusline.UsageColour);
        Statusline.UsageColour = "#123456";
        Assert.Equal("#123456", _settings.Statusline.ElementColors.UsageBaseHex);

        Statusline.OverrideUsageColour = false;
        Assert.Null(_settings.Statusline.ElementColors.UsageBaseHex);
    }

    [Fact]
    public void SubOptionsShowOnlyUnderTheirParents()
    {
        Assert.True(Statusline is { ShowBarOptions: true, ShowPaceOptions: true, ShowResetOptions: true, ShowWeeklyOptions: false });

        Statusline.ShowProgressBar = false;
        Assert.False(Statusline.ShowBarOptions);
        Assert.False(Statusline.ShowPaceOptions);

        Statusline.ShowUsage = false;
        Assert.False(Statusline.ShowResetOptions);
        Assert.False(Statusline.ShowContextLabelOption);
    }

    [Fact]
    public void NothingSelectedSaysSoAndCannotBeApplied()
    {
        foreach (var off in new Action[]
                 {
                     () => Statusline.ShowDirectory = false, () => Statusline.ShowBranch = false, () => Statusline.ShowModel = false,
                     () => Statusline.ShowContext = false, () => Statusline.ShowUsage = false,
                 })
        {
            off();
        }

        Assert.Equal("No components selected", Preview);

        Assert.Empty(Statusline.Apply([]));
        Assert.Equal("Please select at least one component to display", Statusline.Message);
        Assert.True(Statusline.MessageIsError);
        Assert.False(Statusline.IsApplied);
        Assert.Null(ClaudeSettingsFile.Read(ClaudeSettingsFile.PathFor(_harness.DefaultFolder)));
    }

    [Fact]
    public void ApplyAndResetReportWhatHappened()
    {
        Assert.Equal("Not applied", Statusline.AppliedText);
        Assert.Equal("Not installed", Statusline.HelperText);

        Assert.Empty(Statusline.Apply([]));

        Assert.True(Statusline.IsApplied);
        Assert.Equal("Applied", Statusline.AppliedText);
        Assert.Equal("Applied to 1 Claude Code folder(s).", Statusline.Message);
        Assert.False(Statusline.MessageIsError);
        Assert.Equal("Installed", Statusline.HelperText);
        Assert.True(Statusline.HelperOk);
        Assert.False(Statusline.HasProblems);

        Statusline.Reset();
        Assert.False(Statusline.IsApplied);
        Assert.Equal("Status line removed.", Statusline.Message);

        Statusline.DismissMessage();
        Assert.Null(Statusline.Message);
    }

    [Fact]
    public void AnExistingStatusLineIsAskedAboutAndKeptUnlessAgreed()
    {
        Directory.CreateDirectory(_harness.DefaultFolder);
        File.WriteAllText(ClaudeSettingsFile.PathFor(_harness.DefaultFolder), """{"statusLine":{"type":"command","command":"mine.sh"}}""");

        var foreign = Assert.Single(Statusline.Apply([]));
        Assert.Equal("mine.sh", foreign.Command);
        Statusline.KeptForeign([foreign.ConfigDirectory]);

        Assert.Contains("The existing status line was kept in", Statusline.Message);
        Assert.Equal("mine.sh", (string?)ClaudeSettingsFile.Read(ClaudeSettingsFile.PathFor(_harness.DefaultFolder))!["statusLine"]!["command"]);

        Assert.Empty(Statusline.Apply([foreign.ConfigDirectory]));
        Assert.Equal("Applied to 1 Claude Code folder(s).", Statusline.Message);
    }

    [Fact]
    public void ProblemsAreListedAndRepairedFromThePage()
    {
        Statusline.Apply([]);
        File.Delete(_harness.Service.HelperPath);

        Statusline.Refresh();
        Assert.True(Statusline.HasProblems);
        Assert.Equal($"The helper is missing: {_harness.Service.HelperPath}", Assert.Single(Statusline.Problems));
        Assert.Equal("The helper is missing", Statusline.HelperText);
        Assert.True(Statusline.CanUpdateHelper);

        Statusline.Repair();
        Assert.False(Statusline.HasProblems);
        Assert.Equal("Everything is in place again.", Statusline.Message);
        Assert.False(Statusline.CanUpdateHelper);
    }

    [Fact]
    public void ADifferentHelperInThisBuildIsOfferedAsAnUpdate()
    {
        Statusline.Apply([]);
        // A later build of the app, on the same data.
        using var updated = new IntegrationHarness(_temp.Path, _settings, _time, helper: "helper build 2");
        var page = updated.Pages.Statusline;

        Assert.Equal("This build of the app has a different helper", page.HelperText);
        Assert.True(page.CanUpdateHelper);
        Assert.False(page.HasProblems);

        page.UpdateHelper();
        Assert.Equal("The helper is up to date.", page.Message);
        Assert.Equal("helper build 2", File.ReadAllText(updated.Service.HelperPath));
        Assert.False(page.CanUpdateHelper);
    }

    // Activity HUD

    [Fact]
    public void TheHudPageFollowsTheListenerAndTheHooks()
    {
        Assert.False(Hud.Enabled);
        Assert.Equal("Hooks not configured", Hud.HookStatusText);
        Assert.Equal("Install", Hud.HookButton);
        Assert.False(Hud.CanChangeHooks);
        Assert.False(Hud.ShowServer);

        Hud.Enabled = true;

        Assert.True(_settings.HudEnabled);
        Assert.Equal("Hooks installed", Hud.HookStatusText);
        Assert.Equal("Remove", Hud.HookButton);
        Assert.True(Hud is { CanChangeHooks: true, ShowServer: true, ServerOk: true, CanRetry: false, CanUseAnotherPort: false });
        Assert.Equal($"Listening on 127.0.0.1:{_harness.Hud.Port}", Hud.ServerText);
        var folder = Assert.Single(Hud.Folders);
        Assert.Equal((_harness.DefaultFolder, "Hooks installed", "Status.Safe"), (folder.Folder, folder.Text, folder.Colour));

        // Upstream's Remove and Install on the status row.
        Hud.ChangeHooks();
        Assert.Equal("Hooks not configured", Hud.HookStatusText);
        Assert.Equal("Install", Hud.HookButton);
        Hud.ChangeHooks();
        Assert.Equal("Hooks installed", Hud.HookStatusText);

        Hud.Enabled = false;
        Assert.Equal("Hooks not configured", Hud.HookStatusText);
        Assert.False(Hud.ShowServer);
    }

    [Fact]
    public void APartialSetOffersRepair()
    {
        Hud.Enabled = true;
        var path = ClaudeSettingsFile.PathFor(_harness.DefaultFolder);
        ClaudeSettingsFile.Update(path, settings => ((System.Text.Json.Nodes.JsonObject)settings["hooks"]!).Remove("Stop"));

        Hud.Refresh();
        Assert.Equal("Hooks partially installed", Hud.HookStatusText);
        Assert.Equal("Repair", Hud.HookButton);

        Hud.ChangeHooks();
        Assert.Equal("Hooks installed", Hud.HookStatusText);
    }

    [Fact]
    public void ABusyPortOffersRetryAndAnotherPort()
    {
        Hud.Enabled = true;
        using var second = new IntegrationHarness(_temp.Path, _settings, _time);
        second.Coordinator.Start();
        var page = second.Pages.Hud;
        page.Refresh();

        Assert.Equal("The listener's port is in use by another program", page.ServerText);
        Assert.True(page is { ServerOk: false, CanRetry: true, CanUseAnotherPort: true });

        page.Retry();
        Assert.False(page.ServerOk);

        page.UseAnotherPort();
        Assert.True(page.ServerOk);
        Assert.NotEqual(_harness.Hud.Port, second.Hud.Port);
    }

    [Fact]
    public void AutoHideIsKept()
    {
        Assert.True(Hud.AutoHide);

        Hud.AutoHide = false;

        Assert.False(_settings.HudAutoHide);
        Assert.True(_harness.Saves > 0);
    }

    // The HUD's own texts and visibility

    [Fact]
    public void TheHudShowsUpstreamsTextsForEachSession()
    {
        var store = new HudSessionStore(_time);
        store.Apply(new HookEvent.UserPromptSubmit("s1", "Fix the build") { WorkingDirectory = @"C:\work\api-server" });
        store.Apply(new HookEvent.PreToolUse("s1", @"C:\work\api-server", SessionStatus.WritingCode, new HudTaskText("notch.task.editing", "Program.cs")) { ToolUseId = "t1" });
        _time.Now += TimeSpan.FromSeconds(75);
        store.Apply(new HookEvent.UserPromptSubmit("s2", "Add a page") { WorkingDirectory = @"C:\work\webapp" });
        store.Apply(new HookEvent.Stop("s2"));
        _time.Now += TimeSpan.FromMinutes(61);
        store.Apply(new HookEvent.ToolFailure("s1") { ToolUseId = "t1" });
        store.Apply(new HookEvent.PreToolUse("s1", null, SessionStatus.RunningCommand, new HudTaskText("notch.task.running_command", null)) { ToolUseId = "t2" });

        var rows = HudPresentation.Rows(store, _time.Now);

        Assert.Equal(
            [("api-server", "Running…", "Running command", true, "1h 02m"), ("webapp", "Done", "Add a page", false, "1h 01m")],
            rows.Select(r => (r.Name, r.StatusText, r.Secondary, r.HasError, r.Elapsed)));
        Assert.Equal("api-server", HudPresentation.Primary(store, _time.Now)!.Name);
        Assert.Equal("Editing Program.cs", HudPresentation.TaskText(new HudTaskText("notch.task.editing", "Program.cs")));
        Assert.Equal("dotnet build", HudPresentation.TaskText(HudTaskText.Literal("dotnet build")));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(600, "10:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1h 00m")]
    [InlineData(-5, "0:00")]
    public void ElapsedTimeReadsAsUpstreams(int seconds, string text) =>
        Assert.Equal(text, HudPresentation.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheHudIsShownForSessionsAndHidesAfterIdleOnlyWhenAskedTo()
    {
        var store = new HudSessionStore(_time);
        Assert.Equal(new HudVisibility(false, false), HudPresentation.Visibility(store.Sessions, autoHide: true));

        store.Apply(new HookEvent.UserPromptSubmit("s1", "go"));
        Assert.Equal(new HudVisibility(true, false), HudPresentation.Visibility(store.Sessions, autoHide: true));

        store.Apply(new HookEvent.Stop("s1"));
        Assert.Equal(new HudVisibility(true, true), HudPresentation.Visibility(store.Sessions, autoHide: true));
        Assert.Equal(new HudVisibility(true, false), HudPresentation.Visibility(store.Sessions, autoHide: false));

        // One working session keeps it up, and so does one that waits for the user.
        store.Apply(new HookEvent.Notification("s2", "needs permission"));
        Assert.Equal(new HudVisibility(true, false), HudPresentation.Visibility(store.Sessions, autoHide: true));
    }

    [Fact]
    public void TheHudModelCountsSessionsAndNeverExpandsByItself()
    {
        var store = new HudSessionStore(_time);
        var model = new HudViewModel();
        store.Apply(new HookEvent.UserPromptSubmit("s1", "go"));
        model.Update(store, _time.Now);

        Assert.True(model is { IsCompact: true, IsExpanded: false, HasSeveral: false });

        store.Apply(new HookEvent.Notification("s2", "needs permission"));
        model.Update(store, _time.Now);
        Assert.True(model is { IsExpanded: false, HasSeveral: true, CountText: "×2", Count: "2" });
        Assert.Equal(SessionStatus.NeedsAttention, model.Primary!.Status);
    }

    // The launcher card

    [Fact]
    public void TheLauncherCardInstallsLinksAndRemoves()
    {
        Assert.True(Launcher is { Show: true, Installed: false, NotInstalled: true });
        Assert.Contains("Installs a claude-work command.", Launcher.Description);
        Assert.Contains(Path.Combine(_harness.Home, ".claude-work"), Launcher.Description);

        Launcher.Install();

        Assert.True(Launcher is { Installed: true, Command: "claude-work", Linked: false, NeedsLogin: true });
        Assert.Null(Launcher.Error);
        Assert.StartsWith("One-time setup: run claude-work in a terminal", Launcher.LoginText);
        Assert.Null(Launcher.PathWarning);
        Assert.True(_harness.Saves > 0);

        // Someone signed in with the launcher, but the profile does not follow that folder yet.
        var folder = Path.Combine(_harness.Home, ".claude-work");
        File.WriteAllText(Path.Combine(folder, ".credentials.json"), LinkHarness.Credentials("a", "r", DateTimeOffset.UtcNow.AddHours(4)));
        Launcher.Refresh();
        Assert.StartsWith("The launcher's folder is signed in", Launcher.LoginText);

        Profile.CliLink = new CliLink { ConfigDirectory = folder };
        Launcher.Refresh();
        Assert.True(Launcher is { Linked: true, NeedsLogin: false });
        Assert.Equal("Linked: usage tracking follows this login.", Launcher.LoginText);

        Launcher.Remove();
        Assert.True(Launcher is { Installed: false });
        Assert.Null(Launcher.Error);
        Assert.Null(Profile.TerminalLauncherSlug);
        Assert.True(File.Exists(Path.Combine(folder, ".credentials.json")));
    }

    [Fact]
    public void TheLauncherCardWarnsAboutPathAndABrokenFile()
    {
        _harness.OnPath = false;
        Launcher.Install();

        Assert.StartsWith(Path.Combine(_harness.Home, ".local", "bin") + " is not on your PATH", Launcher.PathWarning);

        File.Delete(Path.Combine(_harness.Home, ".local", "bin", "claude-work.exe"));
        Launcher.Refresh();
        Assert.Equal("The launcher file is missing. Install it again.", Launcher.FileProblem);
    }

    [Fact]
    public void ALauncherThatCannotBeInstalledSaysWhy()
    {
        using var harness = new IntegrationHarness(Path.Combine(_temp.Path, "no-helper"), _settings, _time, bundled: false);

        harness.Pages.Launcher.Install();

        Assert.StartsWith("Couldn't install the launcher:", harness.Pages.Launcher.Error);
        Assert.False(harness.Pages.Launcher.Installed);
    }

    [Fact]
    public void CodexProfilesHaveNoLauncherAndTheSectionFollowsUpstreamsRule()
    {
        Profile.Provider = Provider.Codex.RawValue();
        Launcher.Refresh();

        Assert.False(Launcher.Show);
    }

    // The preview's colour parser

    [Fact]
    public void AnsiColoursAreReadAsATerminalReadsThem()
    {
        var runs = AnsiText.Parse("plain\e[0;34mblue\e[0m\e[38;5;124mred\e[0m \e[38;2;1;2;3mrgb\e[0mend");

        Assert.Equal(
            [("plain", null), ("blue", Color.FromRgb(0, 0, 238)), ("red", Color.FromRgb(175, 0, 0)), (" ", null), ("rgb", Color.FromRgb(1, 2, 3)), ("end", null)],
            runs.Select(r => (r.Text, r.Colour)));
        Assert.Equal(Color.FromRgb(0, 95, 0), AnsiText.Indexed(22));
        Assert.Equal(Color.FromRgb(175, 95, 255), AnsiText.Indexed(135));
        Assert.Equal(Color.FromRgb(8, 8, 8), AnsiText.Indexed(232));
        Assert.Null(AnsiText.Indexed(3));
    }

    // The process guard with launchers and the HUD

    [Fact]
    public void ASessionTheHudObservesMakesItsFolderInUseAndNothingElseIdle()
    {
        var defaultFolder = new CliConfigDirectory(@"C:\Users\u\.claude", true, @"C:\Users\u");
        var launcherFolder = new CliConfigDirectory(@"C:\Users\u\.claude-work", false, @"C:\Users\u");
        var launchers = new Dictionary<string, LauncherImage> { [@"C:\Users\u\.local\bin\claude-work.exe"] = new(launcherFolder.Path, "recorded") };
        var observed = new List<string>();
        List<ProcessEntry> table = [];
        var guard = new CliProcessGuard(() => table, () => launchers, folder => observed.Contains(folder, StringComparer.OrdinalIgnoreCase), _ => "recorded");

        // No process and no observation: idle.
        Assert.Equal(StoreActivity.Idle, guard.ActivityFor(defaultFolder));

        // A session the HUD shows in the launcher's folder, whose process the snapshot does not attribute to it.
        observed.Add(launcherFolder.Path);
        Assert.Equal(StoreActivity.InUse, guard.ActivityFor(launcherFolder));
        Assert.Equal(StoreActivity.Idle, guard.ActivityFor(defaultFolder));

        // An empty HUD proves nothing: a launcher's own process still counts.
        observed.Clear();
        var started = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        table =
        [
            new ProcessEntry(10, 1, "claude-work.exe", true, @"C:\Users\u\.local\bin\claude-work.exe", started),
            new ProcessEntry(11, 10, CliProcessGuard.ImageName, true, @"C:\Users\u\.local\bin\claude.exe", started.AddSeconds(1)),
        ];
        Assert.Equal(StoreActivity.InUse, guard.ActivityFor(launcherFolder));
        // With the launcher's session attributed to its own folder, the default folder is free for a takeover or a switch.
        Assert.Equal(StoreActivity.Idle, guard.ActivityFor(defaultFolder));

        // A process whose details cannot be read stays unknown, whatever the HUD shows.
        table.Add(new ProcessEntry(12, 1, CliProcessGuard.ImageName, null, null, null));
        observed.Add(defaultFolder.Path);
        Assert.Equal(StoreActivity.Unknown, guard.ActivityFor(defaultFolder));
    }

    private static string ResetTime(DateTimeOffset reset) =>
        TimeZoneInfo.ConvertTime(reset, TimeZoneInfo.Local).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
}
