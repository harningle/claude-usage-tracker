using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.App.Hud;

/// <summary>
/// The floating HUD. It never takes the keyboard from the terminal: it is not activated when shown or
/// clicked, and it is a tool window, so it stays out of the taskbar and Alt+Tab and shows on every virtual desktop.
/// </summary>
public partial class HudWindow : Window
{
    private const double TopMargin = 8;

    public HudWindow(HudViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>A click on the HUD: upstream's tap, which switches between the compact and the expanded view.</summary>
    public event EventHandler? Toggled;

    /// <summary>Centres the HUD at the top of the primary display's work area.</summary>
    public void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + TopMargin;
    }

    private void OnToggle(object sender, RoutedEventArgs e) => Toggled?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Shows, hides and refreshes the HUD from the observed sessions. Port of upstream <c>NotchHUDController</c>;
/// session state lives in <see cref="HudService"/>.
/// </summary>
public sealed class HudController : IDisposable
{
    private readonly HudService _service;
    private readonly AppSettings _settings;
    private readonly TimeProvider _time;
    private readonly HudViewModel _viewModel;
    private readonly DispatcherTimer _idleHide;
    private readonly DispatcherTimer _clock;
    private HudWindow? _window;

    /// <param name="fixtureLabel">Set in fixture mode, so the HUD never passes for real activity.</param>
    public HudController(HudService service, AppSettings settings, TimeProvider time, Dispatcher dispatcher, string? fixtureLabel = null)
    {
        _viewModel = new HudViewModel { FixtureLabel = fixtureLabel };
        _service = service;
        _settings = settings;
        _time = time;
        _idleHide = new DispatcherTimer(HudPresentation.IdleHideDelay, DispatcherPriority.Background, (_, _) => Hide(), dispatcher) { IsEnabled = false };
        // One timer drives every elapsed label, as upstream's single timeline does.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher) { IsEnabled = false };
        _service.Changed += Update;
    }

    /// <summary>Applies the visibility rule to the current sessions; called on every change of them and of the auto-hide setting.</summary>
    public void Update()
    {
        _idleHide.Stop();
        var visibility = HudPresentation.Visibility(_service.Sessions.Sessions, _settings.HudAutoHide);
        if (!visibility.Visible)
        {
            Hide();
            return;
        }

        Refresh();
        if (_window is null)
        {
            _window = new HudWindow(_viewModel);
            _window.Toggled += (_, _) => _viewModel.IsExpanded = !_viewModel.IsExpanded;
        }

        if (!_window.IsVisible)
        {
            // It appears quietly: compact, and without taking the focus.
            _viewModel.IsExpanded = false;
            _window.Show();
            _window.Place();
            _clock.Start();
        }

        if (visibility.HideAfterIdleDelay)
        {
            _idleHide.Start();
        }
    }

    /// <summary>The display layout changed: the HUD is placed again.</summary>
    public void Reposition() => _window?.Place();

    public void Dispose()
    {
        _service.Changed -= Update;
        _idleHide.Stop();
        _clock.Stop();
        _window?.Close();
        _window = null;
    }

    private void Refresh() => _viewModel.Update(_service.Sessions, _time.GetUtcNow());

    private void Hide()
    {
        _idleHide.Stop();
        _clock.Stop();
        _viewModel.IsExpanded = false;
        _window?.Hide();
    }
}
