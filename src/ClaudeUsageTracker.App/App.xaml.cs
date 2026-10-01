using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Localisation;
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
    private CliLinkService _links = null!;
    private UsageClient _client = null!;
    private RefreshCoordinator _coordinator = null!;
    private SetupService _setup = null!;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        if (e.Args.Contains("--software-render"))
        {
            // Development aid: screen captures miss hardware-rendered WPF content on some displays.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        var fixture = FixtureMode.FromArguments(e.Args);
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
        var time = TimeProvider.System;
        var secrets = new SecretStore(_paths.SecretsDirectory);
        var cache = new UsageCacheStore(_paths.CacheDirectory);
        HttpMessageHandler handler = fixture?.Handler() ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _links = new CliLinkService(secrets, time, _userProfile);
        _client = new UsageClient(http, time, new RequestLog(_paths.DiagnosticsDirectory));
        _setup = new SetupService(_store, _settings, _links, cache, time);
        _coordinator = new RefreshCoordinator(_links, _client, cache, time, action => Dispatcher.BeginInvoke(action));
        _coordinator.StateChanged += state =>
        {
            _states[state.ProfileId] = state;
            UpdateViews();
        };
        _coordinator.StatusChanged += status =>
        {
            // A failed status check keeps the last known status, as upstream does.
            _status = status ?? _status;
            UpdateViews();
        };

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
        _popover.RefreshRequested += (_, _) => RefreshActive();
        _popover.SettingsRequested += (_, _) => ShowSettings(null);
        _popover.SetupRequested += (_, _) => ShowWizard();

        _signalWait = ThreadPool.RegisterWaitForSingleObject(
            _showSignal, (_, _) => Dispatcher.BeginInvoke(ShowFromSecondLaunch), null, Timeout.Infinite, false);

        WatchActiveProfile();
        UpdateViews();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => UpdateViews(), Dispatcher);
        _clock.Start();

        // Upstream shows the wizard on the very first launch and whenever nothing is set up.
        if (!_settings.HasShownWizardOnce || _settings.ActiveProfile?.CliLink is null)
        {
            _settings.HasShownWizardOnce = true;
            Persist();
            ShowWizard();
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

        _tray?.Dispose();
        _host?.Dispose();
        _showSignal?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void WatchActiveProfile()
    {
        if (_settings.ActiveProfile is { } profile)
        {
            _coordinator.SetStatusProfile(profile.Id);
            _coordinator.Watch(profile);
        }
    }

    private void UpdateViews()
    {
        var profile = _settings.ActiveProfile;
        var state = profile is null ? null : _states.GetValueOrDefault(profile.Id);
        var now = DateTimeOffset.UtcNow;
        var use24Hour = UsageDisplay.Uses24HourTime(_settings.TimeFormat, CultureInfo.CurrentCulture);
        _tray.Show(profile?.Id, TrayModel.Build(profile, state, use24Hour, now, TimeZoneInfo.Local, Strings.Culture));
        _popoverModel.Update(_settings, profile, state, _status, now);
        if (_settingsModel is not null && profile?.CliLink is { } link)
        {
            _settingsModel.UpdateLink(state, _links.Read(CliStore.ForLink(link, _userProfile)));
        }
        else
        {
            _settingsModel?.UpdateLink(state, null);
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
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.Format("windows.setup.error.save_failed", error.Message), Strings.Get("app.name"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void OnSettingsEdited()
    {
        Persist();
        WatchActiveProfile();
        UpdateViews();
    }

    private void RefreshActive()
    {
        if (_settings.ActiveProfile is { } profile)
        {
            _coordinator.Refresh(profile.Id);
        }
    }

    private void OnTrayActivated(TrayIcon icon, TrayActivation activation)
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
                    RefreshActive();
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

        switch (_toggle.Activate())
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
            _settingsModel = new SettingsViewModel(_settings, OnSettingsEdited, _theme.TaskbarDark);
            _settingsWindow = new SettingsWindow(_settingsModel);
            _settingsWindow.SourceInitialized += (_, _) => _theme.StyleTitleBar(_settingsWindow);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _settingsModel = null;
            };
            _settingsWindow.SyncRequested += (_, _) => ShowWizard();
            _settingsWindow.WizardRequested += (_, _) => ShowWizard();
            _settingsWindow.QuitRequested += (_, _) => Shutdown();
            _settingsWindow.RemoveRequested += (_, _) => RemoveLink();
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

    private void RemoveLink()
    {
        if (_settings.ActiveProfile is not { } profile)
        {
            return;
        }

        try
        {
            // Stop checks of the old link before its encrypted copy is deleted.
            _setup.Unlink(profile, () => _coordinator.Watch(profile));
        }
        catch (SetupException error)
        {
            MessageBox.Show(error.Message, Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _coordinator.Watch(profile);
        _settingsModel?.Reload();
        UpdateViews();
    }

    private void ShowWizard()
    {
        if (_wizard is not null)
        {
            _wizard.Activate();
            return;
        }

        var model = new SetupWizardViewModel(_links, _client, _setup, TimeProvider.System, _userProfile, _configDirOverride);
        model.Saved += profile =>
        {
            _coordinator.SetStatusProfile(profile.Id);
            _coordinator.Watch(profile);
            _settingsModel?.Reload();
            UpdateViews();
        };
        _wizard = new SetupWizardWindow(model);
        _wizard.SourceInitialized += (_, _) => _theme.StyleTitleBar(_wizard);
        _wizard.Closed += (_, _) => _wizard = null;
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
