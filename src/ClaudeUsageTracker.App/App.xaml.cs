using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using ClaudeUsageTracker.App.Browser;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.CodexCli;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Themes;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.App.Views;
using ClaudeUsageTracker.Core.Status;
using ClaudeUsageTracker.Core.Usage;

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
    private ServiceStatus? _status;
    private string _userProfile = "";
    private string? _configDirOverride;

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
        var cache = new UsageCacheStore(_paths.CacheDirectory);
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

        // Launchers arrive in Milestone 5; until then no running Claude Code is attributed to a profile.
        var processes = fixture is null ? CliProcessGuard.ForThisPc(() => new Dictionary<string, string>()) : FixtureMode.ProcessGuard();
        _links = new CliLinkService(secrets, time, _userProfile, credentialLock, tokens, processes,
            () => CliStore.Inventory(_userProfile, _configDirOverride, _linkedFolders));
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

        _toggle = new PopoverToggle(time);
        _popoverModel = new PopoverViewModel { FixtureLabel = fixture is null ? null : Strings.Get("windows.fixture.label") };
        _popover = new PopoverWindow(_popoverModel, _toggle);
        _popover.RefreshRequested += (_, _) => RefreshViewed();
        _popover.SettingsRequested += (_, _) => ShowSettings(null);
        _popover.SetupRequested += (_, _) => ShowWizard(null);
        _popover.ManageProfilesRequested += (_, _) => ShowSettings(SettingsSection.ManageProfiles);
        _popover.ProfileSelected += OnProfileSelected;

        _signalWait = ThreadPool.RegisterWaitForSingleObject(
            _showSignal, (_, _) => Dispatcher.BeginInvoke(ShowFromSecondLaunch), null, Timeout.Infinite, false);

        CleanUpLeftovers();
        WatchDisplayed();
        UpdateViews();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => UpdateViews(), Dispatcher);
        _clock.Start();

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

        _linkedFolders = [.. _settings.Profiles.Select(p => p.CliLink?.ConfigDirectory).OfType<string>()];
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
        _popoverModel.Update(_settings, viewed, viewed is null ? null : _states.GetValueOrDefault(viewed.Id), _status, now);
        if (_settingsModel is not null)
        {
            var active = _settings.ActiveProfile;
            _settingsModel.Update(
                active is null ? null : _states.GetValueOrDefault(active.Id),
                active?.CliLink is { } link ? _links.Read(CliStore.ForLink(link, _userProfile)) : null);
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
            _settingsModel = new SettingsViewModel(_settings, OnSettingsEdited, OnSettingsApplied, _profiles, history, _log, _theme.TaskbarDark);
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

        var model = new SetupWizardViewModel(_links, _client, _setup, _wizardServices, TimeProvider.System, _userProfile, _configDirOverride);
        model.Saved += profile =>
        {
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
        // Fail loud: show the error type and message (never request or credential data), then quit.
        MessageBox.Show($"{e.Exception.GetType().Name}: {e.Exception.Message}", Strings.Get("app.name"),
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(3);
    }
}
