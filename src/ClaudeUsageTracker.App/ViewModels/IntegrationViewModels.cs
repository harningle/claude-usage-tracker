using System.Windows;
using System.Windows.Media;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.App.ViewModels;

/// <summary>What the Milestone 5 settings pages need from the app.</summary>
public sealed class IntegrationPages
{
    /// <param name="persist">Saves the settings after an edit made on one of these pages.</param>
    /// <param name="activeState">The active profile's usage state, for the status line preview.</param>
    /// <param name="readFolder">Reads a Claude Code folder's login, to tell whether a launcher's folder is signed in.</param>
    /// <param name="onPath">Whether a folder is on the PATH new terminals get.</param>
    public IntegrationPages(
        AppSettings settings, IntegrationCoordinator coordinator, Action persist, Func<ProfileUsageState?> activeState,
        Func<CliConfigDirectory, CliSnapshot> readFolder, Func<string, bool> onPath, string userProfile, TimeProvider time)
    {
        Statusline = new StatuslineViewModel(settings, coordinator, persist, activeState, time);
        Hud = new HudSettingsViewModel(settings, coordinator, persist);
        Launcher = new LauncherViewModel(settings, coordinator, readFolder, onPath, userProfile);
        coordinator.Changed += Refresh;
        coordinator.Hud.Changed += Hud.Refresh;
    }

    public StatuslineViewModel Statusline { get; }

    public HudSettingsViewModel Hud { get; }

    public LauncherViewModel Launcher { get; }

    /// <summary>Reads everything again, files included: when one of the pages opens, and after anything installed changed.</summary>
    public void Refresh()
    {
        Statusline.Refresh();
        Hud.Refresh();
        Launcher.Refresh();
    }

    /// <summary>The active profile or a setting changed: what depends on them is shown again, without checking the installed files.</summary>
    public void SettingsChanged()
    {
        Statusline.RaiseAll();
        Launcher.Refresh();
    }
}

public sealed record ColourModeOption(string RawValue, string Name, string Description, string Glyph);

/// <summary>A stretch of the preview in one colour.</summary>
public sealed record PreviewRun(string Text, Brush Brush);

/// <summary>Problems and their repair, shared by the pages that show them.</summary>
public static class IntegrationText
{
    public static string Describe(IntegrationProblem problem) => problem.Kind switch
    {
        IntegrationProblemKind.ManifestUnreadable => Strings.Format("windows.integration.problem.manifest", problem.Detail),
        IntegrationProblemKind.HelperMissing => Strings.Format("windows.integration.problem.helper_missing", problem.Detail),
        IntegrationProblemKind.HelperModified => Strings.Format("windows.integration.problem.helper_modified", problem.Detail),
        IntegrationProblemKind.HelperRepairRequired => Strings.Format("windows.integration.problem.helper_repair", problem.Detail),
        IntegrationProblemKind.LauncherMissing => Strings.Format("windows.integration.problem.launcher_missing", problem.Detail),
        IntegrationProblemKind.LauncherReplaced => Strings.Format("windows.integration.problem.launcher_replaced", problem.Detail),
        IntegrationProblemKind.StatuslineNotInstalled => Strings.Format("windows.integration.problem.statusline_missing", problem.Detail),
        IntegrationProblemKind.StatuslineOutdated => Strings.Format("windows.integration.problem.statusline_outdated", problem.Detail),
        IntegrationProblemKind.HooksIncomplete => Strings.Format("windows.integration.problem.hooks_incomplete", problem.Detail),
        IntegrationProblemKind.HooksOutdated => Strings.Format("windows.integration.problem.hooks_outdated", problem.Detail),
        IntegrationProblemKind.HudPortBusy => Strings.Get("windows.integration.problem.port_busy"),
        IntegrationProblemKind.HudFailed => Strings.Format("windows.integration.problem.hud_failed", problem.Detail ?? ""),
        _ => problem.Detail ?? "",
    };

    public static string? Describe(InstallResult result) => result.Outcome switch
    {
        InstallOutcome.Installed => null,
        InstallOutcome.Deferred => Strings.Format("windows.integration.deferred", result.Problem ?? ""),
        _ => Strings.Format("windows.integration.failed", result.Problem ?? ""),
    };
}

/// <summary>
/// The Claude CLI page: upstream <c>ClaudeCodeView</c>. The choices are kept as they are made; Apply installs
/// the status line with them and Reset removes it, as upstream. The preview is the renderer's own output for
/// sample session data, so it shows exactly what a terminal gets.
/// </summary>
public sealed class StatuslineViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IntegrationCoordinator _coordinator;
    private readonly Action _persist;
    private readonly Func<ProfileUsageState?> _activeState;
    private readonly TimeProvider _time;
    private string? _message;
    private bool _messageIsError;
    private IReadOnlyList<string> _problems = [];
    private HelperState _helper;

    public StatuslineViewModel(
        AppSettings settings, IntegrationCoordinator coordinator, Action persist, Func<ProfileUsageState?> activeState, TimeProvider time)
    {
        _settings = settings;
        _coordinator = coordinator;
        _persist = persist;
        _activeState = activeState;
        _time = time;
        ColourModes =
        [
            new(StatuslineConfig.Colored, Strings.Get("windows.claudecode.colour.colored"), Strings.Get("windows.claudecode.colour.colored.description"), ""),
            new(StatuslineConfig.Monochrome, Strings.Get("windows.claudecode.colour.monochrome"), Strings.Get("windows.claudecode.colour.monochrome.description"), ""),
            new(StatuslineConfig.SingleColor, Strings.Get("windows.claudecode.colour.single"), Strings.Get("windows.claudecode.colour.single.description"), ""),
            new(StatuslineConfig.PerElement, Strings.Get("windows.claudecode.colour.per_element"), Strings.Get("windows.claudecode.colour.per_element.description"), ""),
        ];
        _helper = coordinator.Service.Helper();
    }

    private StatuslineConfig Config => _settings.Statusline;

    private StatuslineElementColors Elements => Config.ElementColors;

    // Components, in upstream's order.

    public bool ShowDirectory { get => Config.ShowDirectory; set => Edit(() => Config.ShowDirectory = value); }
    public bool ShowBranch { get => Config.ShowBranch; set => Edit(() => Config.ShowBranch = value); }
    public bool ShowModel { get => Config.ShowModel; set => Edit(() => Config.ShowModel = value); }
    public bool ShowProfile { get => Config.ShowProfile; set => Edit(() => Config.ShowProfile = value); }
    public bool ShowContext { get => Config.ShowContext; set => Edit(() => Config.ShowContext = value); }
    public bool ContextAsTokens { get => Config.ContextAsTokens; set => Edit(() => Config.ContextAsTokens = value); }
    public bool ShowUsage { get => Config.ShowUsage; set => Edit(() => Config.ShowUsage = value); }
    public bool ShowProgressBar { get => Config.ShowProgressBar; set => Edit(() => Config.ShowProgressBar = value); }
    public bool ShowPaceMarker { get => Config.ShowPaceMarker; set => Edit(() => Config.ShowPaceMarker = value); }
    public bool PaceMarkerStepColors { get => Config.PaceMarkerStepColors; set => Edit(() => Config.PaceMarkerStepColors = value); }
    public bool ShowResetTime { get => Config.ShowResetTime; set => Edit(() => Config.ShowResetTime = value); }
    public bool Use24HourTime { get => Config.Use24HourTime; set => Edit(() => Config.Use24HourTime = value); }
    public bool ShowContextLabel { get => Config.ShowContextLabel; set => Edit(() => Config.ShowContextLabel = value); }
    public bool ShowUsageLabel { get => Config.ShowUsageLabel; set => Edit(() => Config.ShowUsageLabel = value); }
    public bool ShowResetLabel { get => Config.ShowResetLabel; set => Edit(() => Config.ShowResetLabel = value); }
    public bool ShowWeekly { get => Config.ShowWeekly; set => Edit(() => Config.ShowWeekly = value); }
    public bool ShowWeeklyBar { get => Config.ShowWeeklyBar; set => Edit(() => Config.ShowWeeklyBar = value); }
    public bool ShowWeeklyPaceMarker { get => Config.ShowWeeklyPaceMarker; set => Edit(() => Config.ShowWeeklyPaceMarker = value); }
    public bool ShowWeeklyResetTime { get => Config.ShowWeeklyResetTime; set => Edit(() => Config.ShowWeeklyResetTime = value); }
    public bool ShowWeeklyLabel { get => Config.ShowWeeklyLabel; set => Edit(() => Config.ShowWeeklyLabel = value); }
    public bool ShowExtraUsage { get => Config.ShowExtraUsage; set => Edit(() => Config.ShowExtraUsage = value); }

    // The sub-options upstream shows only under their parent.

    public bool ShowBarOptions => ShowUsage && ShowProgressBar;
    public bool ShowPaceOptions => ShowBarOptions && ShowPaceMarker;
    public bool ShowResetOptions => ShowUsage && ShowResetTime;
    public bool ShowContextLabelOption => ShowUsage && ShowContext;
    public bool ShowWeeklyOptions => ShowUsage && ShowWeekly;
    public bool ShowWeeklyBarOptions => ShowWeeklyOptions && ShowWeeklyBar;

    // Colours

    public IReadOnlyList<ColourModeOption> ColourModes { get; }

    public ColourModeOption ColourMode
    {
        get => ColourModes.FirstOrDefault(m => m.RawValue == Config.ColorMode) ?? ColourModes[0];
        set
        {
            if (value is not null)
            {
                Edit(() => Config.ColorMode = value.RawValue);
            }
        }
    }

    public bool IsSingleColour => Config.ColorMode == StatuslineConfig.SingleColor;

    public bool IsPerElement => Config.ColorMode == StatuslineConfig.PerElement;

    public string SingleColour { get => Config.SingleColorHex; set => EditColour(value, hex => Config.SingleColorHex = hex); }
    public string DirectoryColour { get => Elements.DirectoryHex; set => EditColour(value, hex => Elements.DirectoryHex = hex); }
    public string BranchColour { get => Elements.BranchHex; set => EditColour(value, hex => Elements.BranchHex = hex); }
    public string ModelColour { get => Elements.ModelHex; set => EditColour(value, hex => Elements.ModelHex = hex); }
    public string ProfileColour { get => Elements.ProfileHex; set => EditColour(value, hex => Elements.ProfileHex = hex); }
    public string ContextColour { get => Elements.ContextHex; set => EditColour(value, hex => Elements.ContextHex = hex); }
    public string SeparatorColour { get => Elements.SeparatorHex; set => EditColour(value, hex => Elements.SeparatorHex = hex); }

    // The four optional overrides: off keeps the gradient or the tiers, on fixes one colour (upstream starts it at green).

    private const string OverrideDefault = "#00BB00";

    public bool OverrideUsageColour { get => Elements.UsageBaseHex is not null; set => Edit(() => Elements.UsageBaseHex = value ? OverrideDefault : null); }
    public string UsageColour { get => Elements.UsageBaseHex ?? ""; set => EditColour(value, hex => Elements.UsageBaseHex = hex); }
    public bool OverridePaceColour { get => Elements.PaceBaseHex is not null; set => Edit(() => Elements.PaceBaseHex = value ? OverrideDefault : null); }
    public string PaceColour { get => Elements.PaceBaseHex ?? ""; set => EditColour(value, hex => Elements.PaceBaseHex = hex); }
    public bool OverrideWeeklyColour { get => Elements.WeeklyBaseHex is not null; set => Edit(() => Elements.WeeklyBaseHex = value ? OverrideDefault : null); }
    public string WeeklyColour { get => Elements.WeeklyBaseHex ?? ""; set => EditColour(value, hex => Elements.WeeklyBaseHex = hex); }
    public bool OverrideExtraColour { get => Elements.ExtraUsageBaseHex is not null; set => Edit(() => Elements.ExtraUsageBaseHex = value ? OverrideDefault : null); }
    public string ExtraColour { get => Elements.ExtraUsageBaseHex ?? ""; set => EditColour(value, hex => Elements.ExtraUsageBaseHex = hex); }

    // Preview

    /// <summary>The line for sample session data, with the active profile's last usage where there is one.</summary>
    public IReadOnlyList<PreviewRun> Preview
    {
        get
        {
            var now = _time.GetUtcNow();
            var usage = _activeState()?.Usage?.Usage;
            var content = new StatuslineContent(
                "claude-usage", "main", "Opus", 96_000, 200_000,
                new StatuslineUsage(
                    usage?.SessionPercentage is { } session
                        ? new StatuslineValue(session, usage.SessionResetTime, StatuslineSource.Cache)
                        : new StatuslineValue(29, now.AddHours(2), StatuslineSource.Cache),
                    usage?.WeeklyPercentage is { } weekly
                        ? new StatuslineValue(weekly, usage.WeeklyResetTime, StatuslineSource.Cache)
                        : new StatuslineValue(45, now.AddDays(4.9), StatuslineSource.Cache),
                    usage is { CostUsed: { } used, CostLimit: { } limit and > 0, CostCurrency: { } currency }
                        ? new StatuslineCost(used, limit, currency)
                        : new StatuslineCost(9.49m, 50m, "USD"),
                    _settings.ActiveProfile?.Name ?? "Profile"));
            var line = StatuslineRenderer.Render(Config, content, now, TimeZoneInfo.Local);
            var plain = Resource("Foreground.Primary");
            return line.Length == 0
                ? [new PreviewRun(Strings.Get("claudecode.preview_no_components"), Resource("Foreground.Secondary"))]
                : [.. AnsiText.Parse(line).Select(run => new PreviewRun(run.Text, run.Colour is { } colour ? Frozen(colour) : plain))];
        }
    }

    // Apply, Reset, helper and repair

    public bool IsApplied => _settings.StatuslineEnabled;

    public string AppliedText => Strings.Get(IsApplied ? "windows.claudecode.state.applied" : "windows.claudecode.state.not_applied");

    public string? Message { get => _message; private set => Set(ref _message, value); }

    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public void DismissMessage() => Message = null;

    public string HelperText => !_coordinator.Service.HasBundledHelper && _helper is HelperState.NotInstalled or HelperState.Missing or HelperState.Modified
        ? Strings.Get("windows.helper.state.no_bundle")
        : Strings.Get(_helper switch
        {
            HelperState.Installed => "windows.helper.state.installed",
            HelperState.UpdateAvailable => "windows.helper.state.update",
            HelperState.NewerInstalled => "windows.helper.state.newer",
            HelperState.Missing => "windows.helper.state.missing",
            HelperState.Modified => "windows.helper.state.modified",
            HelperState.RepairRequired => "windows.helper.state.repair",
            _ => "windows.helper.state.not_installed",
        });

    public bool HelperOk => _helper is HelperState.Installed or HelperState.NewerInstalled;

    /// <summary>Update or reinstall is offered when this build carries a helper that differs from, or is missing at, the installed path.</summary>
    public bool CanUpdateHelper => _coordinator.Service.HasBundledHelper
                                   && _helper is HelperState.UpdateAvailable or HelperState.Missing or HelperState.Modified or HelperState.RepairRequired;

    public string HelperPath => _coordinator.Service.HelperPath;

    public IReadOnlyList<string> Problems { get => _problems; private set => Set(ref _problems, value); }

    public bool HasProblems => _problems.Count > 0;

    /// <summary>
    /// Upstream's Apply. Returns the folders that have a status line of their own, which was left alone, so the
    /// window can ask whether to replace it and call again with the folders the user agreed to.
    /// </summary>
    public IReadOnlyList<StatuslineFolder> Apply(IReadOnlyCollection<string> replaceForeignIn)
    {
        if (!Config.ShowsAnything)
        {
            Show(Strings.Get("claudecode.error_no_components"), error: true);
            return [];
        }

        var result = _coordinator.ApplyStatusline(replaceForeignIn);
        if (result.Applied)
        {
            var text = Strings.Format("windows.claudecode.applied", _coordinator.Service.Manifest.Statuslines.Count);
            Show(result.Problem is null ? text : $"{text} {result.Problem}", error: result.Problem is not null);
        }
        else if (result.Foreign.Count == 0)
        {
            Show(Strings.Format("windows.claudecode.failed", result.Problem ?? ""), error: true);
        }
        else
        {
            // Every folder has a status line of its own: nothing is said until the user has been asked.
            Message = null;
        }

        Refresh();
        return result.Foreign;
    }

    /// <summary>The user chose to keep a folder's own status line.</summary>
    public void KeptForeign(IEnumerable<string> folders) =>
        Show($"{Message} {Strings.Format("windows.claudecode.foreign.kept", string.Join(", ", folders))}".Trim(), Message is not null && MessageIsError);

    /// <summary>Upstream's Reset: the status line is removed from Claude Code's settings.</summary>
    public void Reset()
    {
        var problem = _coordinator.ResetStatusline();
        Show(problem ?? Strings.Get("windows.claudecode.removed"), error: problem is not null);
        Refresh();
    }

    public void UpdateHelper()
    {
        var result = _coordinator.Service.InstallHelper();
        Show(IntegrationText.Describe(result) ?? Strings.Get("windows.helper.updated"), result.Outcome != InstallOutcome.Installed);
        Refresh();
    }

    public void Repair()
    {
        var remaining = _coordinator.Repair();
        Show(remaining.Count == 0 ? Strings.Get("windows.integration.repaired") : string.Join(" ", remaining.Select(IntegrationText.Describe)), remaining.Count > 0);
        Refresh();
    }

    /// <summary>Checks the installed helper and every installed entry again.</summary>
    public void Refresh()
    {
        _helper = _coordinator.Service.Helper();
        Problems = [.. _coordinator.Problems().Select(IntegrationText.Describe)];
        RaiseAll();
    }

    /// <summary>Shows every value again, e.g. the preview after the active profile changed.</summary>
    public void RaiseAll()
    {
        foreach (var property in GetType().GetProperties())
        {
            if (property.Name is not (nameof(Message) or nameof(MessageIsError) or nameof(Problems)))
            {
                RaisePropertyChanged(property.Name);
            }
        }
    }

    private void Show(string message, bool error)
    {
        MessageIsError = error;
        Message = message;
    }

    private void Edit(Action change)
    {
        change();
        _persist();
        _coordinator.StatuslineConfigChanged();
        RaiseAll();
    }

    /// <summary>Takes a colour only when it is written as #RRGGBB; anything else leaves the colour as it was.</summary>
    private void EditColour(string? text, Action<string> set)
    {
        var hex = text?.Trim() ?? "";
        if (!hex.StartsWith('#'))
        {
            hex = "#" + hex;
        }

        if (StatuslineRenderer.HexToAnsi(hex).Length > 0)
        {
            Edit(() => set(hex.ToUpperInvariant()));
        }
        else
        {
            Edit(() => { });
        }
    }

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Brush Resource(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>One Claude Code folder's hook entries, as a row.</summary>
public sealed record HookFolderRow(string Folder, string Text, string Colour) : ListItem;

/// <summary>The Activity HUD page: upstream <c>NotchHUDSettingsView</c>.</summary>
public sealed class HudSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IntegrationCoordinator _coordinator;
    private readonly Action _persist;
    private IReadOnlyList<HookFolderStatus> _folders = [];

    public HudSettingsViewModel(AppSettings settings, IntegrationCoordinator coordinator, Action persist)
    {
        _settings = settings;
        _coordinator = coordinator;
        _persist = persist;
        _folders = coordinator.Hud.FolderStatuses();
    }

    private HudService Hud => _coordinator.Hud;

    public bool Enabled
    {
        get => _settings.HudEnabled;
        set
        {
            _coordinator.SetHudEnabled(value);
            Refresh();
        }
    }

    public bool AutoHide
    {
        get => _settings.HudAutoHide;
        set
        {
            _settings.HudAutoHide = value;
            _persist();
            Refresh();
        }
    }

    // Hook status: upstream's single row, summed over the folders, with one row per folder beneath.

    private HookInstallStatus Overall =>
        _folders.Count > 0 && _folders.All(f => f.Status == HookInstallStatus.Installed) ? HookInstallStatus.Installed
        : _folders.Any(f => f.Status == HookInstallStatus.Outdated) ? HookInstallStatus.Outdated
        : _folders.Any(f => f.Status is HookInstallStatus.Installed or HookInstallStatus.Partial) ? HookInstallStatus.Partial
        : HookInstallStatus.NotInstalled;

    public string HookStatusText => Text(Overall, null);

    public string HookColour => Colour(Overall, null);

    /// <summary>Upstream's button: Remove when installed, Repair when partial or outdated, Install otherwise.</summary>
    public string HookButton => Strings.Get(Overall switch
    {
        HookInstallStatus.Installed => "notch.hooks.remove",
        HookInstallStatus.Partial or HookInstallStatus.Outdated => "notch.hooks.repair",
        _ => "notch.hooks.install",
    });

    /// <summary>Hooks can be installed only for a running listener; leftovers can be removed at any time.</summary>
    public bool CanChangeHooks => Hud.Status == HudServerStatus.Running || Overall == HookInstallStatus.Outdated;

    public IReadOnlyList<HookFolderRow> Folders =>
        [.. _folders.Select(f => new HookFolderRow(f.ConfigDirectory, Text(f.Status, f.Problem), Colour(f.Status, f.Problem)))];

    public void ChangeHooks()
    {
        if (Hud.Status != HudServerStatus.Running || Overall == HookInstallStatus.Installed)
        {
            Hud.RemoveHooks();
        }
        else
        {
            Hud.InstallHooks();
        }

        Refresh();
    }

    // Listener status

    public bool ShowServer => Enabled;

    public bool ServerOk => Hud.Status == HudServerStatus.Running;

    public string ServerText => Hud.Status switch
    {
        HudServerStatus.Running => Strings.Format("windows.hud.server.listening", Hud.Port),
        HudServerStatus.PortBusy => Strings.Get("windows.hud.server.port_busy"),
        HudServerStatus.Failed => Strings.Format("windows.hud.server.failed", Hud.Problem ?? ""),
        _ => Strings.Get("windows.hud.server.stopped"),
    };

    public bool CanRetry => Enabled && Hud.Status is HudServerStatus.PortBusy or HudServerStatus.Failed or HudServerStatus.Stopped;

    public bool CanUseAnotherPort => Enabled && Hud.Status == HudServerStatus.PortBusy;

    /// <summary>What went wrong with a folder's settings while the listener runs, or null.</summary>
    public string? Problem => Hud.Status == HudServerStatus.Running ? Hud.Problem : null;

    /// <summary>Upstream's Retry after a busy port.</summary>
    public void Retry()
    {
        Hud.Start();
        Refresh();
    }

    public void UseAnotherPort()
    {
        Hud.Start(newPort: true);
        Refresh();
    }

    public void Refresh()
    {
        _folders = Hud.FolderStatuses();
        foreach (var property in GetType().GetProperties())
        {
            RaisePropertyChanged(property.Name);
        }
    }

    private static string Text(HookInstallStatus status, string? problem) => problem is not null
        ? Strings.Get("windows.hud.hooks.status_unreadable")
        : Strings.Get(status switch
        {
            HookInstallStatus.Installed => "notch.hooks.status_installed",
            HookInstallStatus.Partial => "notch.hooks.status_partial",
            HookInstallStatus.Outdated => "windows.hud.hooks.status_outdated",
            _ => "notch.hooks.status_not_installed",
        });

    private static string Colour(HookInstallStatus status, string? problem) => problem is not null ? "Status.Critical" : status switch
    {
        HookInstallStatus.Installed => "Status.Safe",
        HookInstallStatus.Partial or HookInstallStatus.Outdated => "Status.Moderate",
        _ => "Foreground.Secondary",
    };
}

/// <summary>The Terminal Launcher card of the active profile: upstream <c>TerminalLauncherSection</c>.</summary>
public sealed class LauncherViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IntegrationCoordinator _coordinator;
    private readonly Func<CliConfigDirectory, CliSnapshot> _readFolder;
    private readonly Func<string, bool> _onPath;
    private readonly string _userProfile;
    private LauncherView? _view;
    private bool _signedIn;
    private bool _onPathNow = true;
    private string? _error;

    public LauncherViewModel(
        AppSettings settings, IntegrationCoordinator coordinator, Func<CliConfigDirectory, CliSnapshot> readFolder, Func<string, bool> onPath,
        string userProfile)
    {
        _settings = settings;
        _coordinator = coordinator;
        _readFolder = readFolder;
        _onPath = onPath;
        _userProfile = userProfile;
        Refresh();
    }

    private ProfileSettings? Profile => _settings.ActiveProfile;

    /// <summary>Upstream shows the card only for providers whose profiles have a Claude Code login.</summary>
    public bool Show => Profile is { } profile && profile.ProviderKind.Descriptor().Capabilities.CliAccountSync;

    public bool Installed => _view?.Installed == true;

    public bool NotInstalled => !Installed;

    /// <summary>The command, e.g. <c>claude-work</c>.</summary>
    public string Command => _view?.Command ?? "";

    public string Description => _view is null ? "" : Strings.Format("windows.launcher.description", _view.Command, _view.ConfigDirectory);

    /// <summary>The profile follows the launcher's own folder: upstream's "pinned".</summary>
    public bool Linked => Installed && Profile?.CliLink is { } link && _view is not null
                          && StatuslinePaths.SameDirectory(link.ConfigDirectory, _view.ConfigDirectory);

    public bool NeedsLogin => Installed && !Linked;

    public string LoginText => !Installed ? ""
        : Linked ? Strings.Get("windows.launcher.linked")
        : _signedIn ? Strings.Get("windows.launcher.other_login")
        : Strings.Format("windows.launcher.login_needed", Command);

    public string? FileProblem => _view?.FileProblem switch
    {
        null => null,
        "missing" => Strings.Get("windows.launcher.file_missing"),
        _ => Strings.Get("windows.launcher.file_replaced"),
    };

    /// <summary>Upstream's PATH warning; the tracker never edits PATH itself.</summary>
    public string? PathWarning => Installed && !_onPathNow ? Strings.Format("windows.launcher.path_warning", _coordinator.Service.BinDirectory) : null;

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public void Install()
    {
        if (Profile is not { } profile)
        {
            return;
        }

        var result = _coordinator.InstallLauncher(profile);
        Error = result.Outcome == InstallOutcome.Installed ? null : Strings.Format("launcher.install_failed", result.Problem ?? "");
        Refresh();
    }

    public void Remove()
    {
        if (Profile is not { } profile)
        {
            return;
        }

        var view = _view;
        var note = _coordinator.RemoveLauncher(profile);
        // The service names the file it left in place; anything else is why the removal did not happen.
        Error = note is null ? null : note == view?.Path ? Strings.Format("windows.launcher.removed_kept", note) : note;
        Refresh();
    }

    public void Refresh()
    {
        _view = Profile is { } profile && Show ? _coordinator.Service.LauncherFor(profile) : null;
        if (_view is { Installed: true })
        {
            _signedIn = _readFolder(new CliConfigDirectory(CliStore.Normalise(_view.ConfigDirectory), false, _userProfile)).Credentials is not null;
            _onPathNow = _onPath(_coordinator.Service.BinDirectory);
        }

        foreach (var property in GetType().GetProperties())
        {
            if (property.Name != nameof(Error))
            {
                RaisePropertyChanged(property.Name);
            }
        }
    }
}
