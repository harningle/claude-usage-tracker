using System.IO;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Tests.Notifications;

namespace ClaudeUsageTracker.Tests.ViewModels;

/// <summary>The Milestone 4 settings: shortcut recording (upstream <c>ShortcutRecorderView</c>), notification thresholds and auto-switch.</summary>
public sealed class MilestoneFourSettingsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const int Control = KeyCombo.Control, Shift = KeyCombo.Shift, Alt = KeyCombo.Alt;
    private const int U = 0x55, R = 0x52;

    private readonly TempDirectory _temp = new();
    private readonly ScratchKey _registry = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly AppSettings _settings = new();
    private readonly List<string> _calls = [];
    private bool _saveFails;

    public MilestoneFourSettingsTests()
    {
        _settings.Profiles.Add(new ProfileSettings { Name = "Work" });
        _settings.ActiveProfileId = _settings.Profiles[0].Id;
    }

    public void Dispose()
    {
        _registry.Dispose();
        _temp.Dispose();
    }

    private ProfileSettings Profile => _settings.Profiles[0];

    /// <summary>Settings view models draw icon previews, so they are made on an STA thread like the window's.</summary>
    private Task OnSta(Action<SettingsViewModel> test)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                test(Create());
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private SettingsViewModel Create()
    {
        var links = new LinkHarness(_temp.Path, Path.Combine(_temp.Path, "home"), _time);
        var store = new SettingsStore(Path.Combine(_temp.Path, "settings.json"));
        var cache = new UsageCacheStore(Path.Combine(_temp.Path, "cache"));
        var history = new UsageHistoryService(new UsageHistoryStore(Path.Combine(_temp.Path, "history")), _time);
        var profiles = new ProfileService(store, _settings, links.Service, links.Secrets, links.Lock, cache, history, _time);
        return new SettingsViewModel(
            _settings,
            () =>
            {
                if (_saveFails)
                {
                    return false;
                }

                store.Save(_settings);
                return true;
            },
            () => { }, profiles, new HistoryViewModel(history, _time, () => true),
            new RequestLog(Path.Combine(_temp.Path, "diagnostics")), taskbarDark: true,
            new SettingsIntegrations
            {
                LaunchAtLogin = new LaunchAtLogin(() => _registry.Open("Run"), () => null, @"C:\Apps\ClaudeUsageTracker.App.exe"),
                Notifier = new RecordingNotifier(),
                AutomationProblem = () => null,
                NotificationsSwitchedOn = p => _calls.Add("on:" + p.Name),
                ApplyShortcuts = () =>
                {
                    _calls.Add("apply");
                    return [];
                },
                SuspendShortcuts = () => _calls.Add("suspend"),
                Pages = new Integration.IntegrationHarness(Path.Combine(_temp.Path, "integration"), _settings, _time).Pages,
                ProfilePicked = p => _calls.Add("picked:" + p.Name),
            });
    }

    [Theory]
    [InlineData(U, Control, true)]
    [InlineData(U, Alt | Shift, true)]
    [InlineData(0x74, KeyCombo.Windows, true)] // Win+F5
    [InlineData(U, Shift, false)] // Shift alone would block typing a capital U
    [InlineData(U, 0, false)]
    [InlineData(0x11, Control, false)] // Ctrl itself
    [InlineData(0xA0, Shift | Control, false)] // left Shift itself
    public void OnlyAKeyWithCtrlAltOrWinIsAShortcut(int key, int modifiers, bool acceptable) =>
        Assert.Equal(acceptable, Shortcuts.IsAcceptable(key, modifiers));

    [Fact]
    public void ShortcutsAreWrittenTheWindowsWay() =>
        Assert.Equal("Ctrl+Shift+U", Shortcuts.Describe(new KeyCombo { VirtualKey = U, Modifiers = Control | Shift }));

    [Fact]
    public Task PickingAProfileAsksForClaudeCodeToFollowItEachTime() => OnSta(model =>
    {
        // As in the popover's switcher, picking the active profile again asks again (after a restart dropped a waiting switch).
        var home = new ProfileSettings { Name = "Home" };
        _settings.Profiles.Add(home);

        model.ActivateProfile(home.Id);
        model.ActivateProfile(home.Id);

        Assert.Equal(home.Id, _settings.ActiveProfileId);
        Assert.Equal(["picked:Home", "picked:Home"], _calls);
    });

    [Fact]
    public Task APickThatCouldNotBeSavedAsksForNoSwitch() => OnSta(model =>
    {
        var home = new ProfileSettings { Name = "Home" };
        _settings.Profiles.Add(home);
        Directory.CreateDirectory(Path.Combine(_temp.Path, "settings.json"));

        model.ActivateProfile(home.Id);

        Assert.Equal(Profile.Id, _settings.ActiveProfileId);
        Assert.NotNull(model.ProfileError);
        Assert.Empty(_calls);
    });

    [Fact]
    public Task RecordingTakesAShortcutAndRegistersTheShortcutsAgain() => OnSta(model =>
    {
        model.StartRecording(HotkeyAction.Refresh);
        Assert.True(model.IsRecordingShortcut);
        Assert.Equal("Press shortcut...", model.ShortcutRows.Single(r => r.Action == HotkeyAction.Refresh).Text);

        Assert.True(model.Record(U, Control | Shift));

        Assert.False(model.IsRecordingShortcut);
        Assert.True(_settings.Shortcuts.Refresh?.SameKeys(new KeyCombo { VirtualKey = U, Modifiers = Control | Shift }));
        Assert.Equal(["suspend", "apply"], _calls);
        Assert.Equal("Ctrl+Shift+U", model.ShortcutRows.Single(r => r.Action == HotkeyAction.Refresh).Text);
        Assert.Contains("\"refresh\"", File.ReadAllText(Path.Combine(_temp.Path, "settings.json")), StringComparison.Ordinal);
    });

    [Fact]
    public Task AModifierAloneKeepsWaitingAndShiftAloneIsRefused() => OnSta(model =>
    {
        model.StartRecording(HotkeyAction.TogglePopover);

        Assert.True(model.Record(0x11, Control));
        Assert.Null(model.ShortcutMessage);
        Assert.True(model.Record(U, Shift));
        Assert.NotNull(model.ShortcutMessage);

        Assert.True(model.IsRecordingShortcut);
        Assert.Null(_settings.Shortcuts.TogglePopover);
    });

    [Fact]
    public Task EscapeCancelsRecording() => OnSta(model =>
    {
        model.StartRecording(HotkeyAction.OpenSettings);

        Assert.True(model.Record(0x1B, Control));

        Assert.False(model.IsRecordingShortcut);
        Assert.Null(_settings.Shortcuts.OpenSettings);
    });

    [Fact]
    public Task AShortcutAnotherActionUsesIsRefused() => OnSta(model =>
    {
        _settings.Shortcuts.Refresh = new KeyCombo { VirtualKey = R, Modifiers = Control | Alt };
        model.StartRecording(HotkeyAction.NextProfile);

        model.Record(R, Control | Alt);

        Assert.Equal("That shortcut is already used for Refresh Usage.", model.ShortcutMessage);
        Assert.Null(_settings.Shortcuts.NextProfile);
        Assert.True(model.IsRecordingShortcut);
    });

    [Fact]
    public Task ClearingAShortcutRegistersTheRestAgain() => OnSta(model =>
    {
        _settings.Shortcuts.Refresh = new KeyCombo { VirtualKey = R, Modifiers = Control | Alt };

        model.ClearShortcut(HotkeyAction.Refresh);

        Assert.Null(_settings.Shortcuts.Refresh);
        Assert.Equal(["apply"], _calls);
    });

    [Fact]
    public Task CustomThresholdsFollowUpstreamsRules() => OnSta(model =>
    {
        Assert.True(model.AddCustomThreshold("60"));
        Assert.True(model.AddCustomThreshold(" 30 "));
        Assert.False(model.AddCustomThreshold("60"));
        Assert.False(model.AddCustomThreshold("75"));
        Assert.False(model.AddCustomThreshold("101"));
        Assert.False(model.AddCustomThreshold("abc"));

        Assert.Equal(["30%", "60%"], model.CustomThresholds.Select(r => r.Text));
        model.RemoveCustomThreshold(30);
        Assert.Equal([60], Profile.NotificationSettings.CustomThresholds);
    });

    [Fact]
    public Task SwitchingNotificationsOnConfirmsOnceAndTheRestPersist() => OnSta(model =>
    {
        model.NotificationsEnabled = false;
        model.NotificationsEnabled = true;
        model.Threshold90 = false;
        model.Sound = model.Sounds.Single(s => s.RawValue == "mail");
        model.AutoSwitchEnabled = true;
        model.AutoStartEnabled = true;

        Assert.Equal(["on:Work"], _calls);
        var saved = new SettingsStore(Path.Combine(_temp.Path, "settings.json")).Load();
        var profile = saved.Profiles.Single();
        Assert.True(saved.AutoSwitchProfileEnabled);
        Assert.True(profile.AutoStartSessionEnabled);
        Assert.False(profile.NotificationSettings.Threshold90Enabled);
        Assert.Equal("mail", profile.NotificationSettings.SoundName);
        // Without a claude.ai sign-in the page says it cannot auto-start, and the switch is greyed.
        Assert.True(model.AutoStartNeedsLogin);
        Assert.False(model.CanSwitchAutoStart);
        Assert.Equal("This profile can't auto-start a session because it has no claude.ai sign-in.", model.AutoStartNeedsLoginText);

        Profile.ClaudeAi = new ClaudeAiLink { OrganizationId = "org" };
        Assert.False(model.AutoStartNeedsLogin);
        Assert.True(model.CanSwitchAutoStart);
    });

    [Fact]
    public Task ACodexProfileCanAutoStartOnlyWithTheCodexClisLogin() => OnSta(model =>
    {
        Profile.Provider = Provider.Codex.RawValue();
        Profile.Codex = new CodexLink { Source = CodexLink.ManualSource };

        Assert.True(model.CanAutoStart);
        Assert.True(model.AutoStartNeedsLogin);
        Assert.False(model.CanSwitchAutoStart);
        Assert.Equal("This profile can't auto-start a session because it doesn't use the Codex CLI's login.", model.AutoStartNeedsLoginText);
        Assert.StartsWith("When this profile's 5-hour session has not started, run one short Codex request", model.AutoStartDescription, StringComparison.Ordinal);
        Assert.Contains("codex.exe", model.AutoStartRequirement, StringComparison.Ordinal);

        // A claude.ai sign-in means nothing to a Codex profile.
        Profile.ClaudeAi = new ClaudeAiLink { OrganizationId = "org" };
        Assert.False(model.CanSwitchAutoStart);

        Profile.Codex = new CodexLink { Source = CodexLink.AuthFileSource };
        Assert.False(model.AutoStartNeedsLogin);
        Assert.True(model.CanSwitchAutoStart);
    });

    [Fact]
    public Task AnEditWhoseSaveFailsIsUndone() => OnSta(model =>
    {
        var threshold75 = Profile.NotificationSettings.Threshold75Enabled;
        var refresh = Profile.RefreshInterval;
        var shortcut = new KeyCombo { VirtualKey = U, Modifiers = Control | Alt };
        _settings.Shortcuts.Set(HotkeyAction.Refresh, shortcut);
        _saveFails = true;

        // Auto-start sends messages once it is on, so it must not run on a setting that was not saved.
        model.AutoStartEnabled = true;
        model.Threshold75 = !threshold75;
        model.RefreshInterval = refresh + 60;
        Assert.True(model.AddCustomThreshold("42"));
        model.AutoSwitchEnabled = true;
        model.Monochrome = true;
        model.ClearShortcut(HotkeyAction.Refresh);

        Assert.False(Profile.AutoStartSessionEnabled);
        Assert.False(model.AutoStartEnabled);
        Assert.Equal(threshold75, Profile.NotificationSettings.Threshold75Enabled);
        Assert.Equal(refresh, Profile.RefreshInterval);
        Assert.DoesNotContain(42, Profile.NotificationSettings.CustomThresholds);
        Assert.False(_settings.AutoSwitchProfileEnabled);
        Assert.False(Profile.IconConfig.Monochrome);
        Assert.Same(shortcut, _settings.Shortcuts.Get(HotkeyAction.Refresh));
    });

    [Fact]
    public Task PastedCodexCredentialsThatCannotBeSavedOrRenewedSaySo() => OnSta(model =>
    {
        Profile.Provider = Provider.Codex.RawValue();
        Profile.Codex = new CodexLink { Source = CodexLink.ManualSource };
        const string error = "Another profile's saved credentials could not be read.";

        model.Update(new ProfileUsageState { ProfileId = Profile.Id, Source = UsageSourceKind.Codex, MirrorError = error }, null, null, []);
        Assert.Contains(model.Codex.Details, r => r.Value == error);

        model.Update(new ProfileUsageState { ProfileId = Profile.Id, Source = UsageSourceKind.Codex }, null, null, []);
        Assert.DoesNotContain(model.Codex.Details, r => r.Value == error);
    });

    [Fact]
    public Task TheFoldersLoginIsShownOnlyWhileItIsTheProfilesAccount() => OnSta(model =>
    {
        Profile.CliLink = new CliLink { ConfigDirectory = Path.Combine(_temp.Path, ".claude"), AccountUuid = "acc-a" };
        CliSnapshot Holding(string uuid) => new()
        {
            Directory = new CliConfigDirectory(Path.Combine(_temp.Path, ".claude"), true, _temp.Path),
            State = CliLoginState.Usable,
            Credentials = Core.ClaudeCode.CliCredentials.Parse(
                """{"claudeAiOauth":{"accessToken":"synthetic-access-token-0000","subscriptionType":"max","scopes":["user:inference"]}}"""),
            Account = new Core.ClaudeCode.CliAccount(uuid, null, null, null),
            ReadAt = Now,
        };

        model.Update(null, Holding("acc-a"), null, []);
        Assert.Contains(model.Cli.Details, r => r.Value == "max");

        // After a switch the folder holds another profile's account; its subscription is not this profile's.
        model.Update(null, Holding("acc-b"), null, []);
        Assert.DoesNotContain(model.Cli.Details, r => r.Value == "max");
    });

    [Fact]
    public Task LaunchAtLoginReadsAndWritesTheEntry() => OnSta(model =>
    {
        Assert.False(model.LaunchAtLogin);
        model.LaunchAtLogin = true;
        Assert.True(model.LaunchAtLogin);
        Assert.Null(model.LaunchAtLoginNote);
        model.LaunchAtLogin = false;
        Assert.False(model.LaunchAtLogin);
    });
}
