using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Windows.Media;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.Updates;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Notifications;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Updates;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.ViewModels;

/// <summary>An icon style option with a preview drawn by the tray renderer itself.</summary>
public sealed record IconStyleOption(string RawValue, string Name, ImageSource Preview);

public sealed record DetailRow(string Label, string Value) : ListItem;

/// <summary>One credential page: whether it is set up, what it tracks and how it is doing.</summary>
/// <param name="Notice">Something about the credential the user should act on or know.</param>
public sealed record CredentialView(
    WizardRoute Route, bool IsLinked, string StatusTitle, string? StatusDetail, IReadOnlyList<DetailRow> Details, string SetupLabel, string? Notice);

/// <summary>
/// A profile in Manage Profiles and in the multi-profile selection. A row stays the same object while its profile exists
/// and its values change in place, so an edit does not rebuild the lists: rebuilding them on Tab from a profile's name
/// took the keyboard focus out of the window (Milestone 6).
/// </summary>
public sealed class ProfileRow(Guid id) : ObservableObject
{
    private string _name = "";
    private string _providerName = "";
    private bool _isActive;
    private bool _isDisplayed;
    private bool _canDelete;

    public Guid Id { get; } = id;

    public string Name { get => _name; internal set => Set(ref _name, value); }

    public string ProviderName { get => _providerName; internal set => Set(ref _providerName, value); }

    public bool IsActive { get => _isActive; internal set => Set(ref _isActive, value); }

    public bool IsDisplayed { get => _isDisplayed; internal set => Set(ref _isDisplayed, value); }

    public bool CanDelete { get => _canDelete; internal set => Set(ref _canDelete, value); }
}

/// <summary>
/// A request of the network log, as text. It holds only what <see cref="RequestLogEntry"/> holds, with a logged profile's
/// current name for its ID.
/// </summary>
public sealed record LogRow(string Time, string Operation, string Request, string Status, string Duration, string? Error) : ListItem;

/// <summary>A custom notification threshold, with upstream's colour band.</summary>
public sealed record ThresholdRow(int Value, string Text, string Colour) : ListItem;

public sealed record SoundOption(string RawValue, string Name);

/// <summary>One global shortcut as the Shortcuts page shows it.</summary>
/// <param name="Problem">Why the shortcut does not work, e.g. another app holds it.</param>
public sealed record ShortcutRow(HotkeyAction Action, string Title, string Description, string Text, bool IsSet, bool IsRecording, string? Problem) : ListItem;

/// <summary>The app services the Milestone 4 settings use.</summary>
public sealed class SettingsIntegrations
{
    public required LaunchAtLogin LaunchAtLogin { get; init; }

    public required INotifier Notifier { get; init; }

    /// <summary>Why the automation state file cannot be used, or null.</summary>
    public required Func<string?> AutomationProblem { get; init; }

    /// <summary>Upstream confirms with a notification when notifications are switched on.</summary>
    public required Action<ProfileSettings> NotificationsSwitchedOn { get; init; }

    /// <summary>Registers the shortcuts again and returns those Windows refused.</summary>
    public required Func<IReadOnlyList<HotkeyAction>> ApplyShortcuts { get; init; }

    /// <summary>Releases every shortcut while a new one is recorded.</summary>
    public required Action SuspendShortcuts { get; init; }

    /// <summary>The Milestone 5 pages: the status line, the activity HUD and the launcher card.</summary>
    public required IntegrationPages Pages { get; init; }

    /// <summary>A profile was picked and is now active: Claude Code is switched to its account too, if it can be.</summary>
    public required Action<ProfileSettings> ProfilePicked { get; init; }

    /// <summary>The About page's update check.</summary>
    public required UpdateService Updates { get; init; }
}

/// <summary>
/// The settings window's state. Like upstream, every control persists on change; unlike upstream, a
/// changed refresh interval applies at once instead of after a profile switch or relaunch.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>The network log view shows this many of the newest requests; the export holds all of them.</summary>
    public const int LogRowLimit = 200;

    private readonly AppSettings _settings;
    private readonly Func<bool> _changed;
    private readonly Action _applied;
    private readonly ProfileService _profiles;
    private readonly RequestLog _log;
    private readonly SettingsIntegrations _integrations;
    private readonly ObservableCollection<ProfileRow> _profileRows = [];
    private ProfileUsageState? _state;
    private CliSnapshot? _snapshot;
    private CliSwitchView? _cliSwitch;
    private IReadOnlyList<HotkeyAction> _refusedShortcuts = [];
    private HotkeyAction? _recording;
    private string? _shortcutMessage;
    private string? _launchError;
    private string? _profileError;

    /// <param name="changed">
    /// Saves the settings and applies them, after an edit made here. False when the save failed, which it has shown; the
    /// edit is then undone.
    /// </param>
    /// <param name="applied">Applies settings that <paramref name="profiles"/> already saved.</param>
    public SettingsViewModel(
        AppSettings settings, Func<bool> changed, Action applied, ProfileService profiles, HistoryViewModel history, RequestLog log, bool taskbarDark,
        SettingsIntegrations integrations)
    {
        _settings = settings;
        _changed = changed;
        _applied = applied;
        _profiles = profiles;
        _log = log;
        _integrations = integrations;
        History = history;
        IconStyles =
        [
            Option("battery", "windows.icon_style.battery", new TrayIconSpec(TrayIconStyle.Battery, 0.6, 60, UsageStatusLevel.Safe, 0.45, PaceStatus.OnTrack, false, "S", false), taskbarDark),
            Option("progressBar", "windows.icon_style.progress_bar", new TrayIconSpec(TrayIconStyle.ProgressBar, 0.6, 60, UsageStatusLevel.Safe, 0.45, PaceStatus.OnTrack, false, "S", false), taskbarDark),
            Option("percentageOnly", "windows.icon_style.percentage", new TrayIconSpec(TrayIconStyle.PercentageOnly, 0.6, 60, UsageStatusLevel.Safe, 0.45, PaceStatus.OnTrack, false, "S", false), taskbarDark),
            Option("icon", "windows.icon_style.icon", new TrayIconSpec(TrayIconStyle.Ring, 0.6, 60, UsageStatusLevel.Safe, 0.45, PaceStatus.OnTrack, false, "S", false), taskbarDark),
            Option("compact", "windows.icon_style.compact", new TrayIconSpec(TrayIconStyle.Dot, 0.6, 60, UsageStatusLevel.Safe, 0.45, PaceStatus.OnTrack, false, "S", false), taskbarDark),
        ];
        static TrayIconSpec Multi(TrayIconStyle style) =>
            new(style, 0.6, 60, UsageStatusLevel.Moderate, 0.45, PaceStatus.OnTrack, false, null, false)
            {
                HasSecondary = true, SecondaryFraction = 0.35, SecondaryLevel = UsageStatusLevel.Safe,
            };
        MultiStyles =
        [
            Option("concentric", "multiprofile.style_circles", Multi(TrayIconStyle.Concentric), taskbarDark),
            Option("progressBar", "multiprofile.style_bars", Multi(TrayIconStyle.DualBar), taskbarDark),
            Option("compact", "multiprofile.style_dots", Multi(TrayIconStyle.Dot), taskbarDark),
            Option("percentage", "multiprofile.style_percent", Multi(TrayIconStyle.PercentageOnly), taskbarDark),
        ];
    }

    public IReadOnlyList<IconStyleOption> IconStyles { get; }

    public IReadOnlyList<IconStyleOption> MultiStyles { get; }

    public HistoryViewModel History { get; }

    private ProfileSettings? Profile => _settings.ActiveProfile;
    private IconConfiguration Icon => Profile?.IconConfig ?? new IconConfiguration();
    private MultiProfileDisplayConfig MultiConfig => _settings.MultiProfileDisplayConfig;

    public bool HasProfile => Profile is not null;
    public string ProfileName => Profile?.Name ?? Strings.Get("popover.no_profile");

    /// <summary>The credential pages a provider has: claude.ai, Console and Claude Code for Anthropic, Codex for Codex.</summary>
    public bool IsCodexProfile => Profile?.ProviderKind == Provider.Codex;
    public bool IsAnthropicProfile => !IsCodexProfile;

    // Appearance (per profile), upstream AppearanceSettingsView.

    public bool Monochrome { get => Icon.Monochrome; set => Edit(v => Icon.ColorMode = v, Icon.ColorMode, value ? "monochrome" : "multiColor"); }
    public bool ShowIconNames { get => Icon.ShowIconNames; set => Edit(v => Icon.ShowIconNames = v, ShowIconNames, value); }
    public bool ShowRemaining { get => Icon.ShowRemainingPercentage; set => Edit(v => Icon.ShowRemainingPercentage = v, ShowRemaining, value); }
    public bool ShowTimeMarker { get => Icon.ShowTimeMarker; set => Edit(v => Icon.ShowTimeMarker = v, ShowTimeMarker, value); }
    public bool ShowPaceMarker { get => Icon.ShowPaceMarker; set => Edit(v => Icon.ShowPaceMarker = v, ShowPaceMarker, value); }
    public bool UsePaceColoring { get => Icon.UsePaceColoring; set => Edit(v => Icon.UsePaceColoring = v, UsePaceColoring, value); }

    public bool SessionEnabled { get => Metric("session").IsEnabled; set => Edit(v => Metric("session").IsEnabled = v, SessionEnabled, value); }
    public bool WeekEnabled { get => Metric("week").IsEnabled; set => Edit(v => Metric("week").IsEnabled = v, WeekEnabled, value); }
    public bool ApiEnabled { get => Metric("api").IsEnabled; set => Edit(v => Metric("api").IsEnabled = v, ApiEnabled, value); }

    public IconStyleOption SessionStyle
    {
        get => StyleOf("session");
        set => Edit(v => Metric("session").IconStyle = v, Metric("session").IconStyle, value.RawValue);
    }

    public IconStyleOption WeekStyle
    {
        get => StyleOf("week");
        set => Edit(v => Metric("week").IconStyle = v, Metric("week").IconStyle, value.RawValue);
    }

    public IconStyleOption ApiStyle
    {
        get => StyleOf("api");
        set => Edit(v => Metric("api").IconStyle = v, Metric("api").IconStyle, value.RawValue);
    }

    /// <summary>The API credits metric is offered only where it can show something: a profile with Console billing set up.</summary>
    public bool HasApiMetric => Profile is { Console: not null } profile && profile.ProviderKind.Descriptor().Capabilities.ConsoleBilling;

    public bool AllMetricsOff => !SessionEnabled && !WeekEnabled && !(HasApiMetric && ApiEnabled);

    /// <summary>In multi-profile mode the tray is drawn from the multi-profile options, as upstream.</summary>
    public bool IsMultiProfile
    {
        get => _settings.IsMultiProfile;
        set => Run(() => _profiles.SetMultiProfile(value));
    }

    // General (per profile): upstream GeneralSettingsView's refresh interval.

    public double RefreshInterval
    {
        get => Profile?.RefreshInterval ?? 30;
        set => Edit(v => Profile!.RefreshInterval = v, RefreshInterval, Math.Clamp(Math.Round(value / 10) * 10, 10, 300));
    }

    public string RefreshText => Strings.Format("general.refresh_seconds", (int)RefreshInterval);

    // General (per profile): upstream GeneralSettingsView's auto-start and notifications.

    /// <summary>Upstream shows the auto-start card only for providers with the capability, since it sends a message.</summary>
    public bool CanAutoStart => Profile is { } profile && profile.ProviderKind.Descriptor().Capabilities.AutoStartSession;

    public bool AutoStartEnabled
    {
        get => Profile?.AutoStartSessionEnabled == true;
        set => Edit(v => Profile!.AutoStartSessionEnabled = v, AutoStartEnabled, value);
    }

    /// <summary>Upstream's requirement: a login the message is sent through, a claude.ai session (or for Codex the Codex CLI's login).</summary>
    public bool AutoStartNeedsLogin => CanAutoStart && !AutoStartService.CanSend(Profile!);

    /// <summary>
    /// Unlike upstream, the switch is greyed without that login, since it could not send anything. It still shows what is
    /// stored, so a profile switched on earlier starts sending once it has the login.
    /// </summary>
    public bool CanSwitchAutoStart => CanAutoStart && AutoStartService.CanSend(Profile!);

    public string AutoStartDescription =>
        Strings.Get(IsCodexProfile ? "windows.general.autostart_description_codex" : "windows.general.autostart_description");

    public string AutoStartRequirement =>
        Strings.Get(IsCodexProfile ? "windows.general.autostart_requirement_codex" : "windows.general.autostart_requirement");

    public string AutoStartNeedsLoginText =>
        Strings.Get(IsCodexProfile ? "windows.general.autostart_needs_codex_cli" : "windows.general.autostart_needs_claude_ai");

    private NotificationSettings Notify => Profile?.NotificationSettings ?? new NotificationSettings();

    public bool NotificationsEnabled
    {
        get => Notify.Enabled;
        set
        {
            Edit(v => Notify.Enabled = v, NotificationsEnabled, value);
            if (value && NotificationsEnabled && Profile is { } profile)
            {
                _integrations.NotificationsSwitchedOn(profile);
            }
        }
    }

    public bool Threshold75 { get => Notify.Threshold75Enabled; set => Edit(v => Notify.Threshold75Enabled = v, Threshold75, value); }
    public bool Threshold90 { get => Notify.Threshold90Enabled; set => Edit(v => Notify.Threshold90Enabled = v, Threshold90, value); }
    public bool Threshold95 { get => Notify.Threshold95Enabled; set => Edit(v => Notify.Threshold95Enabled = v, Threshold95, value); }

    /// <summary>Upstream's custom thresholds, ascending, coloured by upstream's bands.</summary>
    public IReadOnlyList<ThresholdRow> CustomThresholds => Notify.CustomThresholds
        .Order()
        .Select(v => new ThresholdRow(v, $"{v}%", v switch
        {
            >= 90 => "Status.Critical",
            >= 70 => "Status.Moderate",
            >= 50 => "Accent",
            _ => "Status.Safe",
        }))
        .ToList();

    /// <summary>Upstream <c>addThreshold</c>: a whole percentage from 1 to 100 that is not listed yet; anything else is ignored.</summary>
    public bool AddCustomThreshold(string text)
    {
        if (!int.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || !AlertThresholds.IsValidCustom(value, Notify.CustomThresholds))
        {
            return false;
        }

        Edit(() => Notify.CustomThresholds.Add(value), () => Notify.CustomThresholds.Remove(value));
        return true;
    }

    public void RemoveCustomThreshold(int value)
    {
        if (Notify.CustomThresholds.Contains(value))
        {
            Edit(() => Notify.CustomThresholds.Remove(value), () => Notify.CustomThresholds.Add(value));
        }
    }

    public IReadOnlyList<SoundOption> Sounds { get; } = [.. NotificationSounds.Options.Select(o => new SoundOption(o.RawValue, Strings.Get(o.NameKey)))];

    public SoundOption Sound
    {
        get => Sounds.FirstOrDefault(s => s.RawValue == NotificationSounds.For(Notify.SoundName).RawValue) ?? Sounds[0];
        set => Edit(v => Notify.SoundName = v, Notify.SoundName, value.RawValue);
    }

    public void PreviewSound() => NotificationSounds.Preview(Notify.SoundName);

    /// <summary>Why Windows will not show notifications (switched off, registration failed), or null.</summary>
    public string? NotificationProblem => _integrations.Notifier.Problem;

    public string? AutomationProblem => _integrations.AutomationProblem();

    // App settings: upstream AppSettingsView's launch at login.

    public bool LaunchAtLogin
    {
        get => LaunchState is LaunchAtLoginState.On or LaunchAtLoginState.DisabledInTaskManager;
        set
        {
            try
            {
                _integrations.LaunchAtLogin.Set(value);
                _launchError = null;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
            {
                _launchError = Strings.Format("windows.launch.failed", e.Message);
            }

            RaisePropertyChanged(nameof(LaunchAtLogin));
            RaisePropertyChanged(nameof(LaunchAtLoginNote));
        }
    }

    public string? LaunchAtLoginNote => _launchError ?? LaunchState switch
    {
        LaunchAtLoginState.OtherCopy => Strings.Format("windows.launch.other_copy", _integrations.LaunchAtLogin.RegisteredPath ?? ""),
        LaunchAtLoginState.DisabledInTaskManager => Strings.Get("windows.launch.disabled_in_task_manager"),
        _ => null,
    };

    private LaunchAtLoginState LaunchState
    {
        get
        {
            try
            {
                return _integrations.LaunchAtLogin.State;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
            {
                _launchError = Strings.Format("windows.launch.failed", e.Message);
                return LaunchAtLoginState.Off;
            }
        }
    }

    // Shortcuts: upstream ShortcutsSettingsView.

    public IReadOnlyList<ShortcutRow> ShortcutRows => Shortcuts.Actions
        .Select(action =>
        {
            var (title, description) = action switch
            {
                HotkeyAction.TogglePopover => ("shortcuts.open_popover", "shortcuts.open_popover_desc"),
                HotkeyAction.Refresh => ("shortcuts.refresh", "shortcuts.refresh_desc"),
                HotkeyAction.OpenSettings => ("shortcuts.open_settings", "shortcuts.open_settings_desc"),
                _ => ("shortcuts.next_profile", "shortcuts.next_profile_desc"),
            };
            var combo = _settings.Shortcuts.Get(action);
            var recording = _recording == action;
            return new ShortcutRow(
                action, Strings.Get(title), Strings.Get(description),
                recording ? Strings.Get("shortcuts.recording") : combo is null ? Strings.Get("shortcuts.record") : Shortcuts.Describe(combo),
                combo is not null, recording,
                !recording && combo is not null && _refusedShortcuts.Contains(action) ? Strings.Get("windows.shortcuts.in_use") : null);
        })
        .ToList();

    /// <summary>Why the key just pressed was not taken.</summary>
    public string? ShortcutMessage { get => _shortcutMessage; private set => Set(ref _shortcutMessage, value); }

    public bool IsRecordingShortcut => _recording is not null;

    /// <summary>Starts recording a shortcut. The registered shortcuts are released meanwhile, so pressing one records it.</summary>
    public void StartRecording(HotkeyAction action)
    {
        _integrations.SuspendShortcuts();
        _recording = action;
        ShortcutMessage = null;
        RaisePropertyChanged(nameof(ShortcutRows));
        RaisePropertyChanged(nameof(IsRecordingShortcut));
    }

    public void StopRecording()
    {
        if (_recording is null)
        {
            return;
        }

        _recording = null;
        _refusedShortcuts = _integrations.ApplyShortcuts();
        RaisePropertyChanged(nameof(ShortcutRows));
        RaisePropertyChanged(nameof(IsRecordingShortcut));
    }

    /// <summary>
    /// A key pressed while recording, as upstream's recorder takes it: Escape cancels, a modifier alone keeps waiting, and
    /// a key with a modifier becomes the shortcut. Returns whether the key was taken by the recorder.
    /// </summary>
    public bool Record(int virtualKey, int modifiers)
    {
        if (_recording is not { } action)
        {
            return false;
        }

        if (virtualKey == 0x1B)
        {
            StopRecording();
            return true;
        }

        if (Shortcuts.IsModifierKey(virtualKey))
        {
            return true;
        }

        if (!Shortcuts.IsAcceptable(virtualKey, modifiers))
        {
            ShortcutMessage = Strings.Get("windows.shortcuts.needs_modifier");
            return true;
        }

        var combo = new KeyCombo { VirtualKey = virtualKey, Modifiers = modifiers };
        // Upstream let two actions share a shortcut, so one of them could never fire.
        if (Shortcuts.Actions.Where(a => a != action && combo.SameKeys(_settings.Shortcuts.Get(a))).Select(a => (HotkeyAction?)a).FirstOrDefault()
            is { } taken)
        {
            ShortcutMessage = Strings.Format("windows.shortcuts.duplicate", ShortcutRows.First(r => r.Action == taken).Title);
            return true;
        }

        EditApp(c => _settings.Shortcuts.Set(action, c), _settings.Shortcuts.Get(action), combo);
        ShortcutMessage = null;
        StopRecording();
        return true;
    }

    public void ClearShortcut(HotkeyAction action)
    {
        EditApp(c => _settings.Shortcuts.Set(action, c), _settings.Shortcuts.Get(action), null);
        _refusedShortcuts = _integrations.ApplyShortcuts();
        RaisePropertyChanged(nameof(ShortcutRows));
    }

    // Claude CLI, Activity HUD and the launcher card: upstream ClaudeCodeView, NotchHUDSettingsView and TerminalLauncherSection.

    public StatuslineViewModel Statusline => _integrations.Pages.Statusline;

    public HudSettingsViewModel Hud => _integrations.Pages.Hud;

    public LauncherViewModel Launcher => _integrations.Pages.Launcher;

    /// <summary>
    /// Upstream hides the Claude CLI section only when no profile's provider has a Claude Code login and nothing
    /// is installed; while the status line is applied, the page must stay reachable to remove it.
    /// </summary>
    public bool ShowClaudeCodeSection =>
        _settings.StatuslineEnabled || _settings.Profiles.Any(p => p.ProviderKind.Descriptor().Capabilities.CliAccountSync);

    // Manage Profiles: upstream's auto-switch card.

    public bool AutoSwitchEnabled
    {
        get => _settings.AutoSwitchProfileEnabled;
        set => EditApp(v => _settings.AutoSwitchProfileEnabled = v, AutoSwitchEnabled, value);
    }

    // Popover (app-wide), upstream PopoverSettingsView.

    public bool TimeDisplayReset { get => _settings.TimeDisplay == PopoverTimeDisplay.ResetTime; set => SetTimeDisplay(value, PopoverTimeDisplay.ResetTime); }
    public bool TimeDisplayRemaining { get => _settings.TimeDisplay == PopoverTimeDisplay.RemainingTime; set => SetTimeDisplay(value, PopoverTimeDisplay.RemainingTime); }
    public bool TimeDisplayBoth { get => _settings.TimeDisplay == PopoverTimeDisplay.Both; set => SetTimeDisplay(value, PopoverTimeDisplay.Both); }
    public bool TimeFormatSystem { get => _settings.TimeFormat == TimeFormatPreference.System; set => SetTimeFormat(value, TimeFormatPreference.System); }
    public bool TimeFormat12 { get => _settings.TimeFormat == TimeFormatPreference.TwelveHour; set => SetTimeFormat(value, TimeFormatPreference.TwelveHour); }
    public bool TimeFormat24 { get => _settings.TimeFormat == TimeFormatPreference.TwentyFourHour; set => SetTimeFormat(value, TimeFormatPreference.TwentyFourHour); }

    // Credentials: upstream PersonalUsageView, APIBillingView, CLIAccountView and CodexAccountView.

    public bool IsLinked => Profile?.CliLink is not null;

    /// <summary>Switching Claude Code's shared folder to this profile's account: shown for a profile with a Claude Code link.</summary>
    public bool ShowCliSwitch => _cliSwitch is not null && IsAnthropicProfile;

    public bool CanSwitchCli => _cliSwitch is { CanSwitch: true, Pending: false };

    public bool CliSwitchPending => _cliSwitch?.Pending == true;

    public string CliSwitchStatus => _cliSwitch switch
    {
        null => "",
        { InUse: true } => Strings.Format("windows.cli.switch.in_use", _cliSwitch.Folder),
        { Pending: true } => Strings.Get("windows.cli.switch.pending"),
        _ => Strings.Format("windows.cli.switch.not_in_use", _cliSwitch.Folder),
    };

    /// <summary>The outcome of the last switch, or why the pending one waits.</summary>
    public string? CliSwitchMessage => _cliSwitch?.Message;

    /// <summary>An unfinished switch left Claude Code's folder unclear until the user signs in again there and says so.</summary>
    public bool CliNeedsSignIn => _cliSwitch?.NeedsSignIn == true;
    public bool HasClaudeAi => Profile?.ClaudeAi is not null;
    public bool HasConsole => Profile?.Console is not null;
    public bool HasCodex => Profile?.Codex is not null;

    public CredentialView ClaudeAi
    {
        get
        {
            var link = Profile?.ClaudeAi;
            var rows = new List<DetailRow>();
            if (link is not null)
            {
                rows.Add(new(Strings.Get("wizard.organization"), link.OrganizationName ?? link.OrganizationId));
                rows.Add(new(Strings.Get("windows.cli.current_state"), _state?.Source == UsageSourceKind.ClaudeAi ? SessionState() : Strings.Get("windows.cli.state.not_checked")));
                rows.Add(new(Strings.Get("wizard.session_key"), Strings.Get("windows.claude_ai.session_in_browser")));
            }

            return new CredentialView(
                WizardRoute.ClaudeAi, link is not null,
                Strings.Get(link is null ? "personal.status_not_connected" : "personal.status_connected"),
                link is null ? null : LinkedAt(link.LinkedAt), rows,
                Strings.Get(link is null ? "personal.signin_button" : "personal.reconfigure"),
                link is not null && Profile?.CliLink is not null ? Strings.Get("windows.claude_ai.used_before_cli") : null);
        }
    }

    public CredentialView Console
    {
        get
        {
            var link = Profile?.Console;
            var rows = new List<DetailRow>();
            if (link is not null)
            {
                rows.Add(new(Strings.Get("wizard.organization"), link.OrganizationName ?? link.OrganizationId));
                if (_state?.Api is { } api)
                {
                    rows.Add(new(Strings.Get("menubar.api_credits"), Strings.Format(
                        "windows.setup.review.billing_values",
                        CurrencyText.Format(api.Usage.UsedAmount, api.Usage.Currency, Strings.Culture),
                        CurrencyText.Format(api.Usage.RemainingAmount, api.Usage.Currency, Strings.Culture))));
                    rows.Add(new(Strings.Get("windows.api.last_updated"), LocalTime(api.ObservedAt)));
                }

                if (link.SessionKeyExpiry is { } expiry)
                {
                    rows.Add(new(Strings.Get("windows.api.session_expires"), LocalTime(expiry)));
                }
            }

            return new CredentialView(
                WizardRoute.Console, link is not null,
                Strings.Get(link is null ? "common.not_connected" : "common.connected"),
                link is null ? null : LinkedAt(link.LinkedAt), rows,
                Strings.Get(link is null ? "api.configure" : "personal.reconfigure"),
                _state?.ApiFailure switch
                {
                    null => null,
                    FetchFailure.CredentialsRejected => Strings.Get("windows.api.note.session_expired"),
                    _ => Strings.Get("windows.api.note.not_updated"),
                });
        }
    }

    public CredentialView Cli
    {
        get
        {
            var link = Profile?.CliLink;
            var rows = new List<DetailRow>();
            if (link is not null)
            {
                rows.Add(new(Strings.Get("windows.setup.review.account"),
                    link.EmailAddress is { } email ? (link.OrganizationName is { } org ? $"{email} — {org}" : email) : Strings.Get("windows.setup.review.account_unknown")));
                rows.Add(new(Strings.Get("windows.setup.review.folder"), link.ConfigDirectory));
                var checkedNow = _state?.Source == UsageSourceKind.ClaudeCode;
                rows.Add(new(Strings.Get("windows.cli.current_state"), checkedNow ? CliState() : Strings.Get("windows.cli.state.not_checked")));
                if (checkedNow && _state?.Ownership is { } ownership)
                {
                    rows.Add(new(Strings.Get("windows.cli.ownership"), Strings.Get(ownership switch
                    {
                        CredentialOwnership.AppOwned => "windows.cli.ownership.app",
                        CredentialOwnership.Unresolved => "windows.cli.ownership.unresolved",
                        _ => "windows.cli.ownership.cli",
                    })));
                }

                if (_state?.MirrorError is { } mirrorError)
                {
                    rows.Add(new(Strings.Get("windows.cli.mirror"), mirrorError));
                }

                // The folder's login describes this profile only while the folder holds its account; after a switch it
                // holds another profile's.
                if (_snapshot?.Credentials is { } credentials
                    && Core.ClaudeCode.CliLoginLabels.Compare(link.Identity, _snapshot.Account?.Identity, null, null) == Core.ClaudeCode.CliAccountMatch.SameAccount)
                {
                    // Only the token's fixed type prefix is shown; upstream also showed the last four characters.
                    rows.Add(new(Strings.Get("cli.access_token"), MaskedToken(credentials.AccessToken)));
                    rows.Add(new(Strings.Get("cli.subscription"), credentials.SubscriptionType ?? link.SubscriptionType ?? "unknown"));
                    if (credentials.Scopes.Count > 0)
                    {
                        rows.Add(new(Strings.Get("cli.scopes"), string.Join(", ", credentials.Scopes)));
                    }
                }
            }

            return new CredentialView(
                WizardRoute.ClaudeCode, link is not null,
                Strings.Get(link is null ? "cli.not_synced" : "cli.synced"),
                link is null ? null : LinkedAt(link.LinkedAt), rows,
                Strings.Get(link is null ? "cli.sync_from_code" : "cli.resync"),
                _state?.Source != UsageSourceKind.ClaudeCode ? null : _state.Notice switch
                {
                    LinkNotice.HandBackPending => Strings.Get("windows.banner.hand_back_pending"),
                    LinkNotice.RenewalFailed => Strings.Get("windows.banner.renewal_failed"),
                    LinkNotice.OtherFolderUnreadable => Strings.Get("windows.cli.notice.other_folder_unreadable"),
                    LinkNotice.OtherCopyUnreadable => Strings.Get("windows.cli.notice.other_copy_unreadable"),
                    LinkNotice.SwitchIncomplete => Strings.Get("windows.cli.notice.switch_incomplete"),
                    _ => null,
                });
        }
    }

    /// <summary>
    /// The user's statement that this Claude Code login is also used where the tracker cannot see it
    /// (another PC, a copied folder). The tracker then never renews it.
    /// </summary>
    public bool UsedElsewhere
    {
        get => Profile?.CliLink?.UsedElsewhere == true;
        set => Edit(
            v =>
            {
                if (Profile?.CliLink is { } link)
                {
                    link.UsedElsewhere = v;
                }
            },
            UsedElsewhere,
            value);
    }

    public CredentialView Codex
    {
        get
        {
            var link = Profile?.Codex;
            var rows = new List<DetailRow>();
            if (link is not null)
            {
                rows.Add(new(Strings.Get("windows.setup.review.account"), link.Email ?? Strings.Get("windows.setup.review.codex_account_unknown")));
                rows.Add(new(Strings.Get("windows.setup.review.source"), Strings.Get(link.IsManual ? "codex.source_manual" : "codex.source_authfile")));
                if (!link.IsManual && link.CodexHome is { } home)
                {
                    rows.Add(new(Strings.Get("windows.codex.folder"), home));
                }

                rows.Add(new(Strings.Get("windows.cli.current_state"), _state?.Source == UsageSourceKind.Codex ? CodexState() : Strings.Get("windows.cli.state.not_checked")));
                if (_state?.Usage?.Usage.PlanType is { } plan)
                {
                    rows.Add(new(Strings.Get("cli.subscription"), plan));
                }

                // Pasted credentials whose renewal could not be saved, or may not be renewed yet.
                if (_state is { Source: UsageSourceKind.Codex, MirrorError: { } storageError })
                {
                    rows.Add(new(Strings.Get("windows.cli.mirror"), storageError));
                }
            }

            return new CredentialView(
                WizardRoute.Codex, link is not null,
                Strings.Get(link is null ? "codex.not_connected" : "codex.connected"),
                link is null ? null : LinkedAt(link.LinkedAt), rows,
                Strings.Get(link is null ? "windows.codex.connect" : "personal.reconfigure"),
                link is { IsManual: false } ? Strings.Get("codex.live_read_note") : null);
        }
    }

    // Manage Profiles, upstream ManageProfilesView.

    /// <summary>
    /// The profiles in their order. Each read brings the same collection up to date in place, adding, moving and removing
    /// rows and updating their values, so the lists bound to it keep their controls (see <see cref="ProfileRow"/>).
    /// </summary>
    public IReadOnlyList<ProfileRow> Profiles
    {
        get
        {
            for (var i = 0; i < _settings.Profiles.Count; i++)
            {
                var profile = _settings.Profiles[i];
                var at = _profileRows.ToList().FindIndex(r => r.Id == profile.Id);
                if (at < 0)
                {
                    _profileRows.Insert(i, new ProfileRow(profile.Id));
                }
                else if (at != i)
                {
                    _profileRows.Move(at, i);
                }

                var row = _profileRows[i];
                row.Name = profile.Name;
                row.ProviderName = profile.ProviderKind.Descriptor().DisplayName;
                row.IsActive = profile.Id == Profile?.Id;
                row.IsDisplayed = profile.IsSelectedForDisplay;
                row.CanDelete = _settings.Profiles.Count > 1;
            }

            while (_profileRows.Count > _settings.Profiles.Count)
            {
                _profileRows.RemoveAt(_profileRows.Count - 1);
            }

            return _profileRows;
        }
    }

    /// <summary>Why the last profile action did not happen.</summary>
    public string? ProfileError { get => _profileError; private set => Set(ref _profileError, value); }

    public IconStyleOption MultiStyle
    {
        get => MultiStyles.FirstOrDefault(o => o.RawValue == MultiConfig.IconStyle) ?? MultiStyles[0];
        set => EditApp(v => MultiConfig.IconStyle = v, MultiConfig.IconStyle, value.RawValue);
    }

    public bool MultiShowWeek { get => MultiConfig.ShowWeek; set => EditApp(v => MultiConfig.ShowWeek = v, MultiShowWeek, value); }
    public bool MultiMonochrome { get => MultiConfig.UseSystemColor; set => EditApp(v => MultiConfig.UseSystemColor = v, MultiMonochrome, value); }
    public bool MultiShowRemaining { get => MultiConfig.ShowRemainingPercentage; set => EditApp(v => MultiConfig.ShowRemainingPercentage = v, MultiShowRemaining, value); }
    public bool MultiShowTimeMarker { get => MultiConfig.ShowTimeMarker; set => EditApp(v => MultiConfig.ShowTimeMarker = v, MultiShowTimeMarker, value); }
    public bool MultiShowPaceMarker { get => MultiConfig.ShowPaceMarker; set => EditApp(v => MultiConfig.ShowPaceMarker = v, MultiShowPaceMarker, value); }
    public bool MultiUsePaceColoring { get => MultiConfig.UsePaceColoring; set => EditApp(v => MultiConfig.UsePaceColoring = v, MultiUsePaceColoring, value); }
    public bool MultiActiveIndicator { get => MultiConfig.ShowActiveProfileIndicator; set => EditApp(v => MultiConfig.ShowActiveProfileIndicator = v, MultiActiveIndicator, value); }

    /// <summary>The Percent style has room for the session only; the page says so while the week is switched on.</summary>
    public bool MultiWeekInTooltipOnly => MultiConfig is { IconStyle: "percentage", ShowWeek: true };

    public void CreateProfile(string? name, Provider provider) => Run(() => _profiles.Create(name, provider));

    public void RenameProfile(Guid id, string name) => Run(() =>
    {
        if (Find(id) is { } profile)
        {
            _profiles.Rename(profile, name);
        }
    });

    /// <summary>
    /// Makes a profile the one that is viewed and configured here, and asks for Claude Code to be switched to its account,
    /// as picking it in the popover does; picking the active profile again asks again.
    /// </summary>
    public void ActivateProfile(Guid id) => Run(() =>
    {
        if (Find(id) is { } profile)
        {
            if (profile.Id != Profile?.Id)
            {
                _profiles.Activate(profile);
            }

            _integrations.ProfilePicked(profile);
        }
    });

    public void ToggleDisplayed(Guid id) => Run(() =>
    {
        if (Find(id) is { } profile && !_profiles.ToggleDisplayed(profile))
        {
            throw new SetupException(Strings.Get("multiprofile.at_least_one"));
        }
    });

    /// <summary>Reads the active profile's history again; the History page calls it when it opens.</summary>
    public void ReloadHistory() => History.Load(Profile);

    // Debug Tools: the network log, upstream DebugNetworkLogView.

    public IReadOnlyList<LogRow> LogRows { get; private set; } = [];

    public string LogCount => LogRows.Count == 0 ? Strings.Get("debug.no_requests") : Strings.Format("debug.requests_logged", LogRows.Count);

    public void ReloadLog()
    {
        LogRows = _log.ReadRecent(LogRowLimit)
            .Select(e => new LogRow(
                TimeZoneInfo.ConvertTime(e.At, TimeZoneInfo.Local).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.Operation,
                $"{e.Method} {e.Route} ({e.Transport})" + (e.ProfileId is { } id ? $" → {LogProfileName(id)}" : ""),
                e.Status?.ToString(CultureInfo.InvariantCulture) ?? "—",
                $"{e.DurationMs.ToString(CultureInfo.InvariantCulture)} ms",
                e.ErrorCategory))
            .ToList();
        RaisePropertyChanged(nameof(LogRows));
        RaisePropertyChanged(nameof(LogCount));
    }

    /// <summary>A logged profile's current name, or the start of its ID once it is deleted.</summary>
    private string LogProfileName(Guid id) => _settings.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? id.ToString()[..8];

    public void ClearLog()
    {
        _log.Clear();
        ReloadLog();
    }

    public int ExportLog(string path) => _log.Export(path);

    public string Version =>
        Strings.Format("about.version", Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    // Updates: an opt-in check of GitHub's releases on the About page, in place of upstream's Sparkle (difference 55).

    public bool AutomaticUpdateChecks
    {
        get => _settings.AutomaticUpdateChecks;
        set => EditApp(v => _settings.AutomaticUpdateChecks = v, AutomaticUpdateChecks, value);
    }

    public bool IsCheckingForUpdates => _integrations.Updates.IsChecking;

    /// <summary>What the last update check found, or when GitHub last answered one; null before any check.</summary>
    public string? UpdateStatus
    {
        get
        {
            var updates = _integrations.Updates;
            if (updates.IsChecking)
            {
                return Strings.Get("windows.updates.checking");
            }

            if (updates.Last is not { } check)
            {
                return _settings.LastUpdateCheck is { } last ? Strings.Format("windows.updates.last_checked", LocalTime(last)) : null;
            }

            return check.Outcome switch
            {
                UpdateCheckOutcome.UpToDate => Strings.Format("windows.updates.up_to_date", updates.Current.ToString(), LocalTime(check.At)),
                UpdateCheckOutcome.Available => Strings.Format("windows.updates.available", check.Release!.Version.ToString()),
                UpdateCheckOutcome.NoReleases => Strings.Get("windows.updates.no_releases"),
                UpdateCheckOutcome.RateLimited => Strings.Get("windows.updates.rate_limited"),
                UpdateCheckOutcome.Failed => Strings.Format("windows.updates.failed", check.Status?.ToString(CultureInfo.InvariantCulture) ?? ""),
                UpdateCheckOutcome.Unreachable => Strings.Get("windows.updates.unreachable"),
                _ => Strings.Get("windows.updates.invalid"),
            };
        }
    }

    /// <summary>The newer release's page on GitHub, or null when the last check found none.</summary>
    public string? UpdateReleasePage =>
        _integrations.Updates.Last is { Outcome: UpdateCheckOutcome.Available, Release: { } release } ? GitHubReleases.Page(release.Tag)?.AbsoluteUri : null;

    public Task CheckForUpdatesAsync() => _integrations.Updates.CheckNowAsync();

    /// <summary>An update check started or ended.</summary>
    public void UpdatesChanged()
    {
        RaisePropertyChanged(nameof(IsCheckingForUpdates));
        RaisePropertyChanged(nameof(UpdateStatus));
        RaisePropertyChanged(nameof(UpdateReleasePage));
    }

    /// <summary>Refreshes the credential pages from the coordinator's state and a fresh read of the linked Claude Code folder.</summary>
    /// <param name="cliSwitch">Whether Claude Code uses the active profile's account and any switch to it, or null without a link.</param>
    /// <param name="refusedShortcuts">The shortcuts Windows refused when they were last registered.</param>
    public void Update(ProfileUsageState? state, CliSnapshot? snapshot, CliSwitchView? cliSwitch, IReadOnlyList<HotkeyAction> refusedShortcuts)
    {
        _state = state;
        _snapshot = snapshot;
        _cliSwitch = cliSwitch;
        _refusedShortcuts = refusedShortcuts;
        History.RecordingError = state?.HistoryError;
        foreach (var name in new[]
                 {
                     nameof(ClaudeAi), nameof(Console), nameof(Cli), nameof(Codex), nameof(ShowCliSwitch), nameof(CanSwitchCli),
                     nameof(CliSwitchPending), nameof(CliSwitchStatus), nameof(CliSwitchMessage), nameof(CliNeedsSignIn), nameof(NotificationProblem),
                 })
        {
            RaisePropertyChanged(name);
        }

        if (_recording is null)
        {
            RaisePropertyChanged(nameof(ShortcutRows));
        }
    }

    /// <summary>Re-reads every value, e.g. after the wizard changed the active profile.</summary>
    public void Reload()
    {
        _integrations.Pages.SettingsChanged();
        foreach (var property in GetType().GetProperties())
        {
            RaisePropertyChanged(property.Name);
        }
    }

    public static string MaskedToken(string token) =>
        token.Length > 20 ? token[..12] + "•••••" : "•••••••••";

    private ProfileSettings? Find(Guid id) => _settings.Profiles.FirstOrDefault(p => p.Id == id);

    private static string LinkedAt(DateTimeOffset at) => Strings.Format("windows.cli.linked_at", LocalTime(at));

    private static string LocalTime(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("g", Strings.Culture);

    private string CliState() => _state?.Link switch
    {
        LinkStatus.Ready => Strings.Get("windows.cli.state.ready"),
        LinkStatus.WaitingForCliRefresh => Strings.Get("windows.banner.waiting_for_cli"),
        LinkStatus.NotSignedIn => Strings.Get("windows.banner.cli_signed_out"),
        LinkStatus.Unreadable or LinkStatus.Invalid => Strings.Get("windows.banner.cli_unreadable"),
        LinkStatus.AccountChanged => Strings.Get("windows.banner.account_changed"),
        LinkStatus.AccountUnverified => Strings.Get("windows.banner.account_unverified"),
        LinkStatus.LoginRejected => Strings.Get("windows.banner.login_rejected"),
        LinkStatus.RefreshFailed => Strings.Get("windows.banner.renewal_failed"),
        _ => Strings.Get("windows.cli.state.not_checked"),
    };

    private string SessionState() => (_state?.Link, _state?.LastFailure) switch
    {
        (LinkStatus.NotSignedIn, _) or (_, FetchFailure.CredentialsRejected) => Strings.Get("popover.banner.credentials_expired"),
        (LinkStatus.AccountChanged, _) => Strings.Get("windows.banner.claude_ai_other_account"),
        (_, FetchFailure.Challenge) => Strings.Get("windows.banner.challenge"),
        (LinkStatus.Ready, null) when _state?.LastSuccessAt is not null => Strings.Get("windows.claude_ai.state.ready"),
        _ => Strings.Get("windows.cli.state.not_checked"),
    };

    private string CodexState() => _state?.Link switch
    {
        LinkStatus.Ready => Strings.Get("windows.cli.state.ready"),
        LinkStatus.WaitingForCliRefresh => Strings.Get("windows.banner.waiting_for_codex"),
        LinkStatus.NotSignedIn => Strings.Get("error.codex_credentials_not_found"),
        LinkStatus.Unreadable or LinkStatus.Invalid => Strings.Get("error.codex_credentials_invalid"),
        LinkStatus.AccountChanged or LinkStatus.AccountUnverified => Strings.Get("windows.banner.codex_account_changed"),
        LinkStatus.LoginRejected => Strings.Get("error.codex_auth_expired"),
        LinkStatus.RefreshFailed => Strings.Get("error.codex_auth_refresh_failed"),
        _ => Strings.Get("windows.cli.state.not_checked"),
    };

    private MetricIconConfig Metric(string type)
    {
        if (Icon.Metric(type) is { } metric)
        {
            return metric;
        }

        var created = new MetricIconConfig { MetricType = type, Order = Icon.Metrics.Count };
        Icon.Metrics.Add(created);
        return created;
    }

    private IconStyleOption StyleOf(string type) =>
        IconStyles.FirstOrDefault(o => o.RawValue == Metric(type).IconStyle) ?? IconStyles[0];

    /// <summary>An edit of the active profile's own settings; see <see cref="EditApp(Action, Action)"/>.</summary>
    private void Edit(Action change, Action undo)
    {
        if (Profile is not null)
        {
            EditApp(change, undo);
        }
    }

    /// <summary>Sets one of the active profile's settings from <paramref name="old"/> to <paramref name="value"/>.</summary>
    private void Edit<T>(Action<T> set, T old, T value) => Edit(() => set(value), () => set(old));

    /// <summary>
    /// An edit of an app-wide setting, saved and then applied. A failed save is shown and the edit undone, so nothing runs on
    /// a setting that was not saved.
    /// </summary>
    private void EditApp(Action change, Action undo)
    {
        change();
        if (!_changed())
        {
            undo();
        }

        Reload();
    }

    /// <summary>Sets an app-wide setting from <paramref name="old"/> to <paramref name="value"/>.</summary>
    private void EditApp<T>(Action<T> set, T old, T value) => EditApp(() => set(value), () => set(old));

    /// <summary>A profile action that saves the settings itself; a failure is shown on the page and nothing changes.</summary>
    private void Run(Action action)
    {
        try
        {
            action();
            ProfileError = null;
        }
        catch (SetupException e)
        {
            ProfileError = e.Message;
        }

        _applied();
        Reload();
    }

    private void SetTimeDisplay(bool selected, PopoverTimeDisplay value)
    {
        if (selected && _settings.TimeDisplay != value)
        {
            EditApp(v => _settings.TimeDisplay = v, _settings.TimeDisplay, value);
        }
    }

    private void SetTimeFormat(bool selected, TimeFormatPreference value)
    {
        if (selected && _settings.TimeFormat != value)
        {
            EditApp(v => _settings.TimeFormat = v, _settings.TimeFormat, value);
        }
    }

    private static IconStyleOption Option(string raw, string nameKey, TrayIconSpec spec, bool taskbarDark)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            // A dark tile behind the preview, like the taskbar it will sit on.
            context.DrawRoundedRectangle(new SolidColorBrush(taskbarDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
                null, new System.Windows.Rect(-6, -6, 44, 44), 6, 6);
            TrayIconRenderer.Draw(context, 32, spec, taskbarDark);
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return new IconStyleOption(raw, Strings.Get(nameKey), image);
    }
}
