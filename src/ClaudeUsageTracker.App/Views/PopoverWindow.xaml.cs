using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Themes;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Status;

namespace ClaudeUsageTracker.App.Views;

/// <summary>
/// The tray popover. It closes when it loses activation, unless detached by dragging its header, and
/// reports every open and close to <see cref="PopoverToggle"/> so a click on the owning icon dismisses it
/// once without reopening it.
/// </summary>
public partial class PopoverWindow : Window
{
    /// <summary>Gap between the popover and the taskbar, in DIPs (Windows 11 flyouts use about 12).</summary>
    private const double EdgeGap = 12;

    private readonly PopoverToggle _toggle;
    private readonly Storyboard _spin;
    private Int32Rect? _iconRect;
    private Point _anchor;
    private bool _detached;

    public PopoverWindow(PopoverViewModel viewModel, PopoverToggle toggle)
    {
        InitializeComponent();
        DataContext = viewModel;
        _toggle = toggle;
        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(animation, RefreshGlyph);
        Storyboard.SetTargetProperty(animation, new PropertyPath("RenderTransform.Angle"));
        _spin = new Storyboard { Children = { animation } };
        viewModel.PropertyChanged += OnViewModelChanged;
        SourceInitialized += (_, _) => ThemeManager.RoundCorners(this);
        Deactivated += OnDeactivated;
        SizeChanged += (_, _) => Reposition();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? SetupRequested;

    public bool IsDetached => _detached;

    /// <summary>
    /// Shows the popover next to a tray icon (physical-pixel rectangle) or the activation point. Opened from
    /// the keyboard, focus moves to its first control; upstream clears focus, which suits mouse opens only.
    /// </summary>
    public void ShowAt(Int32Rect? iconRect, Point anchor, bool fromKeyboard = false)
    {
        _iconRect = iconRect;
        _anchor = anchor;
        _detached = false;
        Topmost = true;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var (_, work, dpi) = MonitorFor();

        // Move the hidden window onto the icon's monitor first, so WPF adopts that monitor's DPI before layout.
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, work.X, work.Y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        if (Content is FrameworkElement content)
        {
            content.Measure(new Size(Width - BorderThickness.Left - BorderThickness.Right, double.PositiveInfinity));
            var height = content.DesiredSize.Height + BorderThickness.Top + BorderThickness.Bottom;
            Place(new Size(Width * dpi / 96.0, height * dpi / 96.0));
        }

        Show();
        Activate();
        NativeMethods.SetForegroundWindow(handle);
        Reposition();
        if (fromKeyboard)
        {
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    /// <summary>Closes without counting as a deactivation dismissal.</summary>
    public void Dismiss()
    {
        _detached = false;
        Hide();
        _toggle.Closed();
    }

    private (Int32Rect Monitor, Int32Rect Work, double Dpi) MonitorFor()
    {
        var target = _iconRect is { } r
            ? new NativeMethods.Point { X = r.X + r.Width / 2, Y = r.Y + r.Height / 2 }
            : new NativeMethods.Point { X = (int)_anchor.X, Y = (int)_anchor.Y };
        var monitor = NativeMethods.MonitorFromPoint(target, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        var dpi = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96u;
        return (ToRect(info.rcMonitor), ToRect(info.rcWork), dpi);
    }

    private void Reposition()
    {
        if (!IsVisible || _detached)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(handle, out var rect);
        Place(new Size(rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    private void Place(Size physicalSize)
    {
        var (monitor, work, dpi) = MonitorFor();
        var position = PopoverPlacement.Place(_iconRect, _anchor, monitor, work, physicalSize, EdgeGap * dpi / 96.0);
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, (int)position.X, (int)position.Y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        MaxHeight = work.Height * 96.0 / dpi - 2 * EdgeGap;
    }

    private static Int32Rect ToRect(NativeMethods.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_detached || !IsVisible)
        {
            return;
        }

        Hide();
        _toggle.Deactivated();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Upstream detaches the popover when it is dragged away; the detached window stays until closed.
        if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is not DependencyObject source
            || FindAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(handle, out var before);
        var wasDetached = _detached;
        _detached = true; // keep the popover open while the drag's mouse capture moves activation around
        DragMove();
        NativeMethods.GetWindowRect(handle, out var after);
        _detached = wasDetached || before.Left != after.Left || before.Top != after.Top;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PopoverViewModel.IsRefreshing))
        {
            return;
        }

        var refreshing = ((PopoverViewModel)DataContext).IsRefreshing;
        RefreshButton.IsEnabled = !refreshing;
        if (refreshing)
        {
            _spin.Begin(this, true);
        }
        else
        {
            _spin.Stop(this);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Dismiss();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatusClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ServiceStatus.ClaudeStatusPage) { UseShellExecute = true });

    private void OnBannerClick(object sender, RoutedEventArgs e)
    {
        switch ((BannerButton.DataContext as BannerView)?.Action)
        {
            case BannerAction.Refresh:
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                break;
            case BannerAction.OpenSettings:
                Dismiss();
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case BannerAction.OpenSetup:
                Dismiss();
                SetupRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
