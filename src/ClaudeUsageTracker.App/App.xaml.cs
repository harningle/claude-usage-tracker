using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.Browser;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.CodexCli;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Themes;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.App.Views;
using ClaudeUsageTracker.Core.Notifications;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Status;
using ClaudeUsageTracker.Core.Usage;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App;

/// <summary>
/// Startup, single instance and wiring between the tray, popover, settings, wizard and refresh coordinator.
/// Upstream relied on macOS to keep one instance; here a per-user mutex does, and a second launch only
/// asks the running instance to open its popover (a named event, the narrowest possible IPC).
/// </summary>
public partial class App : Application
{
    private const int MenuRefresh = 1, MenuSettings = 2, MenuQuit = 3;

    private readonly Dictionary<Guid, ProfileUsageState> _states = [];
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _signalWait;
    private AppPaths _paths = null!;
    private int _failed;
    private SettingsStore _store = null!;
    private AppSettings _settings = null!;
    private RequestLog _log = null!;
    private CliLinkService _links = null!;
    private UsageClient _client = null!;
    private RefreshCoordinator _coordinator = null!;
    private SetupService _setup = null!;
    private ProfileService _profiles = null!;
    private UsageHistoryService _history = null!;
    private BrowserSessions _browser = null!;
    private WizardServices _wizardServices = null!;
    private ThemeManager _theme = null!;
    private TrayHost _host = null!;
    private TrayController _tray = null!;
    private PopoverToggle _toggle = null!;
    private PopoverViewModel _popoverModel = null!;
    private PopoverWindow _popover = null!;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsModel;
    private SetupWizardWindow? _wizard;
    private DispatcherTimer? _clock;
    private DispatcherTimer? _autoStartTimer;
    private ServiceStatus? _status;
    private string _userProfile = "";
    private string? _configDirOverride;
    private UsageCacheStore _cache = null!;
    private ToastNotifier? _toasts;
    private AutomationService _automation = null!;
    private AutoStartService _autoStart = null!;
    private CliSwitcher _switcher = null!;
    private Hotkeys? _hotkeys;
    private SystemWatcher? _watcher;
    private LaunchAtLogin _launchAtLogin = null!;
    private IReadOnlyList<HotkeyAction> _refusedShortcuts = [];
    private IntegrationService _integration = null!;
    private IntegrationCoordinator _integrations = null!;
    private IntegrationPages _pages = null!;
    private HudService _hud = null!;
    private HudController? _hudController;
    private bool _integrationNeedsRepair;
    private int _ticks;

    // Usage this run fetched itself, per profile: what the status line may show as fresh.
    private readonly Dictionary<Guid, CachedUsage> _fetched = [];

    // The Claude Code folder of each session the HUD shows, read by polling threads; null stands for an unknown folder.
    private volatile IReadOnlyList<string?> _hudFolders = [];

    // The profile the popover shows: in multi-profile mode the one whose icon was clicked, otherwise the active one.
    private Guid? _viewedId;

    // Claude Code folders linked to profiles, read by polling threads; replaced whole on the UI thread.
    private volatile IReadOnlyList<string> _linkedFolders = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly)
        {
            // Created inside another program (a test host): never start the real tray app and its polling there.
            Shutdown(4);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        if (e.Args.Contains("--software-render"))
        {
            // Development aid: screen captures miss hardware-rendered WPF content on some displays.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        FixtureMode? fixture;
        try
        {
            fixture = FixtureMode.FromArguments(e.Args);
        }
        catch (ArgumentException error)
        {
            // Never fall back to the real login when fixture mode was asked for.
            MessageBox.Show(error.Message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var user = WindowsIdentity.GetCurrent().User?.Value ?? "user";
        var instanceName = $@"Local\ClaudeUsageTracker.{user}{(fixture is null ? "" : ".Fixture")}";
        _instance = new Mutex(true, instanceName, out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown(0);
            return;
        }

        _theme = new ThemeManager(this);
        _paths = fixture is null ? AppPaths.ForCurrentUser() : new AppPaths(fixture.DataRoot);

        // Notifications register this executable before anything else, so a click that started the app is routed.
        _toasts = ToastNotifier.Register(
            Strings.Get("app.name"), NotificationIcon.Write(_paths.NotificationIconFile), fixture is not null, action => Dispatcher.BeginInvoke(action));
        var coldActivation = _toasts.ColdActivation();
        if (coldActivation is { FromFixture: true } && fixture is null)
        {
            // A fixture-mode notification clicked after that run ended: it must never start the app with the real login.
            _toasts.Dispose();
            Shutdown(0);
            return;
        }

        RemoveStaleNotificationRegistrations();
        _store = new SettingsStore(_paths.SettingsFile);
        try
        {
            _settings = _store.Load();
        }
        catch (SettingsStoreException error)
        {
            // Never reset or overwrite damaged settings (plan.md section 2); say what is wrong and stop.
            MessageBox.Show(error.Message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _userProfile = fixture?.UserProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _configDirOverride = fixture is null ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") : null;
        var codexHome = CodexStore.Home(_userProfile, fixture is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null);
        var time = TimeProvider.System;
        var secrets = new ProfileSecretStore(new SecretStore(_paths.SecretsDirectory));
        var credentialLock = new CredentialLock();
        var cache = _cache = new UsageCacheStore(_paths.CacheDirectory);
        // Cookies are never stored by the HTTP stack: the Console's session cookie is sent explicitly per request.
        HttpMessageHandler handler = fixture?.Handler() ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _log = new RequestLog(_paths.DiagnosticsDirectory);
        var tokens = new OAuthTokenClient(http, time, _log);

        // A Claude Code started by one of the installed launchers is attributed to that launcher's folder; sessions the
        // HUD observes count as further evidence of use, never of idleness.
        _integration = new IntegrationService(_paths.Root, _userProfile, IntegrationService.DefaultBundledHelper, time);
        var processes = fixture is null
            ? CliProcessGuard.ForThisPc(() => _integration.LauncherImages, SessionObservedIn)
            : FixtureMode.ProcessGuard();
        _links = new CliLinkService(secrets, time, _userProfile, credentialLock, tokens, processes,
            () => CliStore.Inventory(_userProfile, _configDirOverride, _linkedFolders),
            new CliSwitchRecords(_paths.CliSwitchJournalFile, new SecretStore(Path.Combine(_paths.SecretsDirectory, "backups"))));
        _client = new UsageClient(http, time, _log);
        _browser = new BrowserSessions(_paths.BrowserDirectory, Dispatcher);
        var transport = fixture?.ClaudeAi() ?? _browser;
        var claudeAi = new ClaudeAiClient(transport, time, _log);
        var console = new ConsoleClient(http, time, _log, fixture is null ? BrowserSessions.UserAgent() : null);
        var codex = new CodexClient(http, time, _log);
        _wizardServices = new WizardServices(
            claudeAi, console, codex, fixture?.SignIn() ?? new BrowserSignIn(_browser, _paths.BrowserDirectory, () => _wizard), codexHome);
        var sources = new Dictionary<UsageSourceKind, IUsageSource>
        {
            [UsageSourceKind.ClaudeCode] = new CliUsageSource(_links, _client),
            [UsageSourceKind.ClaudeAi] = new ClaudeAiUsageSource(claudeAi, transport),
            [UsageSourceKind.Codex] = new CodexUsageSource(secrets, credentialLock, tokens, codex, time, () => codexHome),
        };
        _history = new UsageHistoryService(new UsageHistoryStore(_paths.HistoryDirectory), time);
        _setup = new SetupService(_store, _settings, _links, secrets, credentialLock, cache, time);
        _profiles = new ProfileService(_store, _settings, _links, secrets, credentialLock, cache, _history, time);
        _coordinator = new RefreshCoordinator(
            kind => sources[kind], _client, new ConsoleBillingSource(secrets, credentialLock, console), cache, _history, time,
            action => Dispatcher.BeginInvoke(action));
        _coordinator.StateChanged += state =>
        {
            // A late result of a profile that is no longer polled is dropped.
            if (_coordinator.Current(state.ProfileId) is not null)
            {
                _states[state.ProfileId] = state;
                UpdateViews();
            }
        };
        _coordinator.StatusChanged += status =>
        {
            // A failed status check keeps the last known status, as upstream does.
            _status = status ?? _status;
            UpdateViews();
        };
        _automation = new AutomationService(_settings, _toasts, new AutomationStateStore(_paths.AutomationFile), time, LastKnownUsage, OnAutoSwitch);
        _coordinator.UsageFetched += (profileId, usage) =>
        {
            _fetched[profileId] = usage;
            _integrations.SyncStatusline();
            if (_settings.Profiles.FirstOrDefault(p => p.Id == profileId) is { } profile)
            {
                _automation.UsageFetched(profile, usage.Usage);
            }
        };
        _hud = new HudService(
            new HookEndpointStore(new SecretStore(Path.Combine(_paths.SecretsDirectory, "integration"))), _integration.Store, _integration.Manifest,
            HookPorts.Query, IntegrationFolders, time, action => Dispatcher.BeginInvoke(action));
        _hud.Changed += () => _hudFolders = [.. _hud.Sessions.Sessions.Select(s => s.ConfigDirectory).Distinct()];
        var statuslineFiles = new StatuslineFiles(_paths.Root);
        _integrations = new IntegrationCoordinator(
            _settings, () => Persist(), _integration, _hud, statuslineFiles, new StatuslineSync(statuslineFiles, _links.Read, _userProfile),
            IntegrationFolders, ShellEnvironment.GitBashInstalled, profileId => _fetched.GetValueOrDefault(profileId));
        _integrations.Changed += () =>
        {
            // A launcher was installed or removed: its folder joins or leaves the known ones.
            RefreshKnownFolders();
            CheckIntegrations();
        };
        _pages = new IntegrationPages(
            _settings, _integrations, () => Persist(), () => _settings.ActiveProfile is { } active ? _states.GetValueOrDefault(active.Id) : null,
            _links.Read, ShellEnvironment.OnPath, _userProfile, time);
        _autoStart = new AutoStartService(_settings, claudeAi, _automation, time, _coordinator.Current, profileId =>
        {
            if (_coordinator.Current(profileId) is null)
            {
                _browser.Release(profileId);
            }
        });
        _switcher = new CliSwitcher(_settings, _links, _profiles, _automation, SharedFolder, OnSettingsApplied, time, Dispatcher);
        _switcher.Changed += UpdateViews;
        _launchAtLogin = fixture is null
            ? LaunchAtLogin.ForCurrentUser(Environment.ProcessPath!)
            // Fixture mode never registers the real startup entry: it would start the app with the real login.
            : new LaunchAtLogin(() => Registry.CurrentUser.CreateSubKey(@"Software\ClaudeUsageTracker.Fixture\Run", writable: true), () => null, Environment.ProcessPath!);
        _log.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_settingsWindow?.Section == SettingsSection.Debug)
            {
                _settingsModel?.ReloadLog();
            }
        });

        _host = new TrayHost();
        _host.AppearanceChanged += (_, _) => _theme.Apply();
        _tray = new TrayController(_host, () => _theme.TaskbarDark, Environment.ProcessPath!)
        {
            TooltipPrefix = fixture is null ? null : Strings.Get("windows.fixture.label"),
        };
        _tray.Activated += OnTrayActivated;
        _theme.Changed += (_, _) =>
        {
            _tray.Redraw();
            UpdateViews();
        };

        _toasts.Clicked += ShowForNotification;
        _hotkeys = new Hotkeys(_host);
        _hotkeys.Pressed += OnHotkey;
        _refusedShortcuts = _hotkeys.Apply(_settings.Shortcuts);
        _watcher = new SystemWatcher(_host, action => Dispatcher.BeginInvoke(action));
        _watcher.Resumed += OnResumed;
        _watcher.NetworkAvailable += () => _coordinator.CheckSoon();
        _watcher.SessionConnected += () =>
        {
            // A remote desktop connection can change the DPI and display layout the icons were drawn for.
            _tray.Redraw();
            _hudController?.Reposition();
            _coordinator.CheckSoon();
        };
        _watcher.SessionDisconnected += () =>
        {
            if (_popover.IsVisible && !_popover.IsDetached)
            {
                _popover.Dismiss();
            }
        };

        _toggle = new PopoverToggle(time);
        _popoverModel = new PopoverViewModel { FixtureLabel = fixture is null ? null : Strings.Get("windows.fixture.label") };
        _popover = new PopoverWindow(_popoverModel, _toggle);
        _popover.RefreshRequested += (_, _) => RefreshViewed();
        _popover.SettingsRequested += (_, _) => ShowSettings(null);
        _popover.SetupRequested += (_, _) => ShowWizard(null);
        _popover.ManageProfilesRequested += (_, _) => ShowSettings(SettingsSection.ManageProfiles);
        _popover.IntegrationRequested += (_, _) => ShowSettings(SettingsSection.ClaudeCode);
        _popover.ProfileSelected += OnProfileSelected;

        _signalWait = ThreadPool.RegisterWaitForSingleObject(
            _showSignal, (_, _) => Dispatcher.BeginInvoke(ShowFromSecondLaunch), null, Timeout.Infinite, false);

        CleanUpLeftovers();
        WatchDisplayed();
        // An install an earlier run left unfinished is settled, and what is switched on comes up: the HUD's listener
        // first, then its hook entries.
        _hudController = new HudController(_hud, _settings, time, Dispatcher, fixture is null ? null : Strings.Get("windows.fixture.label"));
        _integrations.Start();
        CheckIntegrations();
        UpdateViews();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            // Upstream sweeps silent HUD sessions every 30 seconds.
            _hud.Sweep();
            if (++_ticks % 10 == 0)
            {
                // Installed files are checked again now and then: an async hook or a status line fails quietly.
                _integration.CleanRetained();
                CheckIntegrations();
            }

            UpdateViews();
            _automation.Tick();
        }, Dispatcher);
        _clock.Start();
        _autoStartTimer = new DispatcherTimer(SessionAutoStart.CheckInterval, DispatcherPriority.Background, (_, _) => CheckAutoStart(), Dispatcher);
        _autoStartTimer.Start();
        // Upstream checks at once; a short delay lets the first usage checks of the profiles finish.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Dispatcher.BeginInvoke(CheckAutoStart), TaskScheduler.Default);
        SettleSwitch();
        if (coldActivation is { } clicked)
        {
            ShowForNotification(clicked.ProfileId);
        }

        // Upstream shows the wizard on the very first launch and whenever nothing is set up.
        if (!_settings.HasShownWizardOnce || _settings.ActiveProfile is not { } active || (active.UsageSource is null && active.Console is null))
        {
            _settings.HasShownWizardOnce = true;
            Persist();
            ShowWizard(null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clock?.Stop();
        _autoStartTimer?.Stop();
        _hotkeys?.Dispose();
        _watcher?.Dispose();
        _hudController?.Dispose();
        // The clean exit: the hook entries are removed before the listener stops.
        _integrations?.Shutdown();
        _toasts?.Dispose();
        _signalWait?.Unregister(null);
        if (_coordinator is not null)
        {
            _coordinator.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }

        _browser?.Dispose();
        _tray?.Dispose();
        _host?.Dispose();
        _showSignal?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private ProfileSettings? Viewed =>
        _settings.IsMultiProfile && _viewedId is { } id && _settings.Profiles.FirstOrDefault(p => p.Id == id) is { } viewed
            ? viewed
            : _settings.ActiveProfile;

    /// <summary>
    /// Polls exactly the profiles that are shown: the active one, and in multi-profile mode every profile
    /// selected for display. Each uses only its own credentials.
    /// </summary>
    private void WatchDisplayed()
    {
        var wanted = _settings.DisplayedProfiles
            .Concat(_settings.ActiveProfile is { } active ? [active] : [])
            .DistinctBy(p => p.Id)
            .ToList();
        foreach (var id in _coordinator.Watched.Except(wanted.Select(p => p.Id)).ToList())
        {
            StopWatching(id);
        }

        RefreshKnownFolders();
        foreach (var profile in wanted)
        {
            _coordinator.Watch(profile);
        }

        _coordinator.SetStatusProfile(Viewed?.Id);
    }

    private async void StopWatching(Guid profileId)
    {
        await _coordinator.UnwatchAsync(profileId);
        _states.Remove(profileId);
        _browser.Release(profileId);
        UpdateViews();
    }

    private Task<bool> ClearBrowser(Guid profileId) => _browser.ClearAsync(profileId);

    /// <summary>Finishes removals an earlier run could not complete, and removes old Console sign-in contexts.</summary>
    private async void CleanUpLeftovers()
    {
        BrowserSignIn.DeleteLeftovers(_paths.BrowserDirectory);
        if (await _profiles.RunPendingCleanupAsync(id => Task.FromResult(_browser.DeleteFolderOf(id))) is { } remaining)
        {
            MessageBox.Show(remaining, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void UpdateViews()
    {
        var viewed = Viewed;
        var now = DateTimeOffset.UtcNow;
        var use24Hour = UsageDisplay.Uses24HourTime(_settings.TimeFormat, CultureInfo.CurrentCulture);
        _tray.Show(TrayModel.Build(_settings, id => _states.GetValueOrDefault(id), use24Hour, now, TimeZoneInfo.Local, Strings.Culture));
        _popoverModel.Update(_settings, viewed, viewed is null ? null : _states.GetValueOrDefault(viewed.Id), _status, now, _integrationNeedsRepair);
        if (_settingsModel is not null)
        {
            var active = _settings.ActiveProfile;
            _settingsModel.Update(
                active is null ? null : _states.GetValueOrDefault(active.Id),
                active?.CliLink is { } link ? _links.Read(CliStore.ForLink(link, _userProfile)) : null,
                active?.CliLink is null ? null : _switcher.ViewFor(active, _links.Read(SharedFolder())),
                _refusedShortcuts);
        }
    }

    /// <summary>The folder the plain <c>claude</c> command uses, which an account switch changes: <c>CLAUDE_CONFIG_DIR</c> or the default.</summary>
    private CliConfigDirectory SharedFolder()
    {
        var inventory = CliStore.Inventory(_userProfile, _configDirOverride, []);
        return inventory.Count > 1 ? inventory[1] : inventory[0];
    }

    /// <summary>
    /// The known Claude Code folders besides the default: those profiles are linked to and those of installed launchers.
    /// Read from the settings and the install manifest as they are now, on the UI thread.
    /// </summary>
    private IReadOnlyList<string> KnownFolders() =>
        [.. _settings.Profiles.Select(p => p.CliLink?.ConfigDirectory).OfType<string>(), .. _integration.LauncherDirectories];

    /// <summary>Hands the known folders to the polling threads, whose takeover checks cover every one of them.</summary>
    private void RefreshKnownFolders() => _linkedFolders = KnownFolders();

    /// <summary>The folders whose <c>settings.json</c> carry the status line and the hook entries: every known Claude Code folder.</summary>
    private IReadOnlyList<string> IntegrationFolders() =>
        [.. CliStore.Inventory(_userProfile, _configDirOverride, KnownFolders()).Select(directory => directory.Path)];

    /// <summary>Whether the HUD shows a live session that uses this folder, or one whose folder is unknown. Called from polling threads.</summary>
    private bool SessionObservedIn(string directory) =>
        _hudFolders.Any(folder => folder is null || Core.Statusline.StatuslinePaths.SameDirectory(folder, directory));

    /// <summary>Looks again at everything installed for Claude Code; a fault shows in the popover and on the Claude CLI page.</summary>
    private void CheckIntegrations()
    {
        var needsRepair = _integrations.Problems().Count > 0;
        if (needsRepair != _integrationNeedsRepair)
        {
            _integrationNeedsRepair = needsRepair;
            UpdateViews();
        }
    }

    /// <summary>A profile's last known usage, live or from the cache of its current account, for choosing an auto-switch target.</summary>
    private UsageData? LastKnownUsage(Guid profileId)
    {
        if (_states.GetValueOrDefault(profileId)?.Usage is { } live)
        {
            return live.Usage;
        }

        return _settings.Profiles.FirstOrDefault(p => p.Id == profileId) is { } profile
               && _cache.Load(profileId) is { } cached && cached.AccountIdentity == ProfileWatch.From(profile).AccountIdentity
            ? cached.Usage
            : null;
    }

    /// <summary>
    /// Upstream's auto-switch activates the next profile. Viewing it never touches Claude Code; when Claude Code is on
    /// the account that reached its limit, it is also switched, and the switch waits while Claude Code runs.
    /// </summary>
    private void OnAutoSwitch(ProfileSettings from, ProfileSettings to)
    {
        var shared = _links.Read(SharedFolder());
        var onFrom = from.CliLink is { } link && shared.Credentials is not null
                     && Core.ClaudeCode.CliLoginLabels.Compare(link.Identity, shared.Account?.Identity, null, null) == Core.ClaudeCode.CliAccountMatch.SameAccount;
        OnProfileSelected(to.Id);
        if (onFrom && to.CliLink is not null && to.ProviderKind == Provider.Anthropic)
        {
            _ = _switcher.RequestAsync(to, from.Id, null);
        }
    }

    private async void SettleSwitch() => await _switcher.SettleAsync();

    private async void CheckAutoStart() => await _autoStart.CheckAllAsync(CancellationToken.None);

    /// <summary>Upstream's wake refresh: a check of every polled profile a few seconds after resume, and the auto-start check.</summary>
    private async void OnResumed()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        _coordinator.CheckSoon();
        CheckAutoStart();
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.TogglePopover:
                if (_popover.IsVisible)
                {
                    _popover.Dismiss();
                }
                else if (_tray.PrimaryIcon is { } icon)
                {
                    UpdateViews();
                    Interop.NativeMethods.GetCursorPos(out var cursor);
                    _popover.ShowAt(icon.Bounds(), new Point(cursor.X, cursor.Y), fromKeyboard: true);
                    _toggle.Opened();
                }

                break;
            case HotkeyAction.Refresh:
                RefreshViewed();
                break;
            case HotkeyAction.OpenSettings:
                ShowSettings(null);
                break;
            case HotkeyAction.NextProfile:
                // Upstream switchToNextProfile: the next profile in list order, wrapping round.
                var profiles = _settings.Profiles;
                if (profiles.Count > 1 && _settings.ActiveProfile is { } active)
                {
                    OnProfileSelected(profiles[(profiles.IndexOf(active) + 1) % profiles.Count].Id);
                }

                break;
        }
    }

    /// <summary>A notification was clicked: the popover opens on its profile.</summary>
    private void ShowForNotification(Guid? profileId)
    {
        var target = profileId is { } id && _settings.Profiles.Any(p => p.Id == id) ? id : (Guid?)null;
        if (target is { } shown && shown != Viewed?.Id)
        {
            if (_settings.IsMultiProfile && _settings.DisplayedProfiles.Any(p => p.Id == shown))
            {
                _viewedId = shown;
                _status = null;
                _coordinator.SetStatusProfile(shown);
            }
            else
            {
                // A profile that is not shown is opened as the profile switcher opens it: by activating it.
                OnProfileSelected(shown);
                if (Viewed?.Id != shown)
                {
                    // Activation failed and was reported; the popover would show another profile.
                    return;
                }
            }
        }

        var icon = (target is { } t ? _tray.IconFor(t) : null) ?? _tray.PrimaryIcon;
        if (icon is not null)
        {
            UpdateViews();
            Interop.NativeMethods.GetCursorPos(out var cursor);
            _popover.ShowAt(icon.Bounds(), new Point(cursor.X, cursor.Y));
            _toggle.Opened();
        }
    }

    /// <summary>
    /// Removes this app's notification registrations left by copies that were moved or deleted (Milestone 1, D6). A
    /// failure only leaves those stale entries behind, as before.
    /// </summary>
    private static void RemoveStaleNotificationRegistrations()
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            if (classes is not null)
            {
                NotificationRegistrations.RemoveStale(classes, Environment.ProcessPath!);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    /// <summary>Saves settings; a failure is shown, never ignored.</summary>
    private bool Persist()
    {
        try
        {
            _store.Save(_settings);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.Format("windows.setup.error.save_failed", error.Message), Strings.Get("app.name"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void OnSettingsEdited()
    {
        Persist();
        OnSettingsApplied();
    }

    /// <summary>After settings changed and were saved: poll what is now shown and redraw.</summary>
    private void OnSettingsApplied()
    {
        WatchDisplayed();
        // A new launcher or link is a new folder for the status line and the hooks; a rename reaches the status line.
        _integrations.FoldersChanged();
        _hudController?.Update();
        UpdateViews();
    }

    private void RefreshViewed()
    {
        if (Viewed is { } profile)
        {
            _coordinator.Refresh(profile.Id);
        }
    }

    /// <summary>Upstream's switcher activates the chosen profile; Claude Code's own login is not touched.</summary>
    private void OnProfileSelected(Guid profileId)
    {
        if (_settings.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile)
        {
            return;
        }

        try
        {
            _profiles.Activate(profile);
        }
        catch (SetupException error)
        {
            MessageBox.Show(error.Message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _automation.UserTriggered(profile.Id);
        _viewedId = profile.Id;
        _status = null;
        WatchDisplayed();
        _settingsModel?.Reload();
        _settingsWindow?.ProfileChanged();
        UpdateViews();
    }

    private void OnTrayActivated(TrayIcon icon, TrayActivation activation, Guid? profileId)
    {
        if (activation.Kind == TrayActivationKind.ContextMenu)
        {
            var choice = Interop.NativeMethods.ShowMenu(_host.Handle, (int)activation.Anchor.X, (int)activation.Anchor.Y,
            [
                (MenuRefresh, Strings.Get("common.refresh")),
                (0, null),
                (MenuSettings, Strings.Get("common.settings")),
                (MenuQuit, Strings.Get("common.quit")),
            ]);
            switch (choice)
            {
                case MenuRefresh:
                    RefreshViewed();
                    break;
                case MenuSettings:
                    ShowSettings(null);
                    break;
                case MenuQuit:
                    Shutdown();
                    break;
            }

            return;
        }

        // Upstream: any tray click closes a detached popover instead of opening another.
        if (_popover.IsVisible && _popover.IsDetached)
        {
            _popover.Dismiss();
            return;
        }

        // In multi-profile mode each icon shows its own profile; a click on another profile's icon shows that one.
        var target = profileId ?? _settings.ActiveProfile?.Id;
        var other = _settings.IsMultiProfile && target != Viewed?.Id;
        if (other)
        {
            _viewedId = target;
            _status = null;
            _coordinator.SetStatusProfile(target);
        }

        var action = _toggle.Activate();
        if (other && action != PopoverToggleAction.Open)
        {
            _toggle.Opened();
            action = PopoverToggleAction.Open;
        }

        switch (action)
        {
            case PopoverToggleAction.Open:
                UpdateViews();
                _popover.ShowAt(icon.Bounds(), activation.Anchor, activation.Kind == TrayActivationKind.KeySelect);
                break;
            case PopoverToggleAction.Close:
                _popover.Dismiss();
                break;
        }
    }

    private void ShowFromSecondLaunch()
    {
        if (_wizard is { IsVisible: true })
        {
            _wizard.Activate();
            return;
        }

        if (_tray.PrimaryIcon is { } icon && !_popover.IsVisible)
        {
            UpdateViews();
            Interop.NativeMethods.GetCursorPos(out var cursor);
            _popover.ShowAt(icon.Bounds(), new Point(cursor.X, cursor.Y));
            _toggle.Opened();
        }
    }

    private void ShowSettings(SettingsSection? section)
    {
        if (_settingsWindow is null)
        {
            var history = new HistoryViewModel(_history, TimeProvider.System,
                () => UsageDisplay.Uses24HourTime(_settings.TimeFormat, CultureInfo.CurrentCulture));
            _settingsModel = new SettingsViewModel(_settings, OnSettingsEdited, OnSettingsApplied, _profiles, history, _log, _theme.TaskbarDark,
                new SettingsIntegrations
                {
                    LaunchAtLogin = _launchAtLogin,
                    Notifier = _automation.Notifier,
                    AutomationProblem = () => _automation.StateProblem,
                    NotificationsSwitchedOn = _automation.NotificationsSwitchedOn,
                    ApplyShortcuts = () => _refusedShortcuts = _hotkeys!.Apply(_settings.Shortcuts),
                    SuspendShortcuts = () => _hotkeys!.Suspend(),
                    Pages = _pages,
                });
            _settingsWindow = new SettingsWindow(_settingsModel);
            _settingsWindow.SourceInitialized += (_, _) => _theme.StyleTitleBar(_settingsWindow);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _settingsModel = null;
            };
            _settingsWindow.SetupRequested += route => ShowWizard(route);
            _settingsWindow.WizardRequested += (_, _) => ShowWizard(null);
            _settingsWindow.QuitRequested += (_, _) => Shutdown();
            _settingsWindow.RemoveRequested += RemoveCredential;
            _settingsWindow.DeleteProfileRequested += DeleteProfile;
            _settingsWindow.SwitchCliRequested += SwitchCli;
            _settingsWindow.CancelCliSwitchRequested += (_, _) => _switcher.Cancel();
            _settingsWindow.SignedInAgainRequested += async (_, _) => await _switcher.SignedInAgainAsync();
            UpdateViews();
            _settingsWindow.Show();
        }

        if (section is { } target)
        {
            _settingsWindow.Navigate(target);
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    /// <summary>Removes one credential of the active profile. Checks of the old link stop before its data is deleted.</summary>
    private async void RemoveCredential(WizardRoute route)
    {
        if (_settings.ActiveProfile is not { } profile)
        {
            return;
        }

        void Rewatch() => _coordinator.Watch(profile);
        try
        {
            var message = route switch
            {
                WizardRoute.ClaudeAi => await _profiles.UnlinkClaudeAiAsync(profile, Rewatch, ClearBrowser),
                WizardRoute.Console => await Done(_profiles.UnlinkConsoleAsync(profile, Rewatch)),
                WizardRoute.Codex => await Done(_profiles.UnlinkCodexAsync(profile, Rewatch)),
                _ => await Done(_profiles.UnlinkCliAsync(profile, Rewatch)),
            };
            if (message is not null)
            {
                MessageBox.Show(message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (SetupException error)
        {
            MessageBox.Show(error.Message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        OnSettingsApplied();
        _settingsModel?.Reload();

        static async Task<string?> Done(Task removal)
        {
            await removal;
            return null;
        }
    }

    /// <summary>"Use in Claude Code" on the CLI Account page: switch now, or after Claude Code closes, confirming first when that signs a login out.</summary>
    private async void SwitchCli()
    {
        if (_settings.ActiveProfile is not { CliLink: not null } profile || _settingsWindow is null)
        {
            return;
        }

        var result = await _switcher.RequestAsync(profile, null, null);
        if (result is { Outcome: CliSwitchOutcome.NeedsConfirmation, OccupantStamp: { } stamp }
            && MessageBox.Show(_settingsWindow, _switcher.Describe(result, profile), Strings.Get("windows.cli.switch.confirm.title"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
        {
            await _switcher.RequestAsync(profile, null, stamp);
        }

        UpdateViews();
    }

    private async void DeleteProfile(Guid profileId)
    {
        if (_settings.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile)
        {
            return;
        }

        try
        {
            var message = await _profiles.DeleteAsync(profile, async id =>
            {
                await _coordinator.UnwatchAsync(id);
                _states.Remove(id);
            }, ClearBrowser);
            if (message is not null)
            {
                MessageBox.Show(message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (SetupException error)
        {
            MessageBox.Show(error.Message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_viewedId == profileId)
        {
            _viewedId = null;
        }

        OnSettingsApplied();
        _settingsModel?.Reload();
        _settingsWindow?.ProfileChanged();
    }

    /// <param name="route">Opens the wizard at one route's first step, for the settings pages; null starts at the provider choice.</param>
    private void ShowWizard(WizardRoute? route)
    {
        if (_wizard is not null)
        {
            _wizard.Activate();
            return;
        }

        var model = new SetupWizardViewModel(
            _links, _client, _setup, _wizardServices, TimeProvider.System, _userProfile, _configDirOverride, () => _integration.LauncherDirectories);
        model.Saved += profile =>
        {
            _automation.UserTriggered(profile.Id);
            _viewedId = profile.Id;
            _status = null;
            OnSettingsApplied();
            _settingsModel?.Reload();
            _settingsWindow?.ProfileChanged();
        };
        _wizard = new SetupWizardWindow(model);
        _wizard.SourceInitialized += (_, _) => _theme.StyleTitleBar(_wizard);
        _wizard.Closed += async (_, _) =>
        {
            _wizard = null;
            // A pasted claude.ai key or a sign-in that was never saved must not stay signed in.
            await model.DiscardAsync();
            if (Directory.Exists(_browser.FolderFor(model.TargetProfileId)))
            {
                await _profiles.DiscardUnlinkedBrowserAsync(model.TargetProfileId, ClearBrowser);
            }
        };
        if (route is { } start)
        {
            model.Start(start);
        }

        _wizard.Show();
        _wizard.Activate();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (Interlocked.Exchange(ref _failed, 1) == 1)
        {
            // The first error is on screen already. A dialog for each repeat would open inside the one before until
            // the stack ran out, ending the app without a trace.
            return;
        }

        // Fail loud: show the error type and message (never request or credential data), keep its type and where it
        // was thrown for diagnosis, then quit.
        var record = _paths is null ? null : CrashRecord.Write(_paths.DiagnosticsDirectory, e.Exception, DateTimeOffset.UtcNow);
        var text = $"{e.Exception.GetType().Name}: {e.Exception.Message}";
        MessageBox.Show(record is null ? text : text + "\n\n" + Strings.Format("windows.crash.record", record), Strings.Get("app.name"),
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(3);
    }
}
