using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.App.Views;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.ViewModels;

/// <summary>Tests that create real WPF windows or the app's resources; they never run alongside other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "WPF";
}

/// <summary>
/// The popover window itself, on screen. Opening it for a profile whose cards need a different height
/// must place it by the new height: it once kept the previous profile's top, floating above the taskbar or
/// running into it (found in the Milestone 3 live check). The popover flashes briefly on the primary monitor.
/// </summary>
[Collection(WpfCollection.Name)]
public class PopoverWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task EachOpeningIsPlacedByTheHeightOfTheProfileItShows() => WpfApplication.Run(() =>
    {
        var tall = new ProfileSettings { Name = "Tall", Console = new ConsoleLink() };
        var codex = new ProfileSettings { Name = "Short", Provider = Provider.Codex.RawValue(), Codex = new CodexLink() };
        var settings = new AppSettings { Profiles = [tall, codex], IsMultiProfile = true };
        var model = new PopoverViewModel();
        var window = new PopoverWindow(model, new PopoverToggle(TimeProvider.System));
        var (icon, monitor, work, dpi) = IconAtBottomOfPrimaryMonitor();

        var heights = new List<int>();
        foreach (var (profile, state) in new[] { (tall, TallState(tall.Id)), (codex, ShortState(codex.Id)), (tall, TallState(tall.Id)) })
        {
            // As a tray click does: new content, then shown at once, with no layout pass in between.
            model.Update(settings, profile, state, null, Now);
            window.ShowAt(icon, new Point(icon.X, icon.Y));
            Settle();

            // WPF's own record of the window (DIPs). A test process cannot keep the foreground, so the popover may
            // already have hidden itself on deactivation; it keeps its last position and size.
            var scale = dpi / 96.0;
            var size = new Size(window.ActualWidth * scale, window.ActualHeight * scale);
            var expected = PopoverPlacement.Place(icon, new Point(icon.X, icon.Y), monitor, work, size, 12 * scale);
            Assert.InRange(window.Left * scale, expected.X - 2, expected.X + 2);
            Assert.InRange(window.Top * scale, expected.Y - 2, expected.Y + 2);
            heights.Add((int)size.Height);
            window.Dismiss();
            Settle();
        }

        // The check is only meaningful if the profiles really need different heights.
        Assert.True(heights[0] > heights[1], $"heights: {string.Join(", ", heights)}");
        window.Close();
    });

    [Fact]
    public Task AWaitingSwitchOfClaudeCodeIsShownWithItsCancelWhicheverProfileIsShown() => WpfApplication.Run(() =>
    {
        var work = new ProfileSettings { Name = "Work" };
        var home = new ProfileSettings { Name = "Home" };
        var settings = new AppSettings { Profiles = [work, home], ActiveProfileId = home.Id, IsMultiProfile = true };
        var model = new PopoverViewModel();
        var window = new PopoverWindow(model, new PopoverToggle(TimeProvider.System));
        var cancelled = 0;
        window.CancelSwitchRequested += (_, _) => cancelled++;
        var cancel = (Button)window.FindName("CancelSwitchButton");
        var strip = (Border)((Grid)cancel.Parent).Parent;

        // Claude Code waits to switch to Home while the popover shows Work's icon.
        model.Update(settings, work, TallState(work.Id), null, Now, pendingSwitch: home);
        Settle();
        Assert.Equal("Claude Code switches to Home when it closes.", model.PendingSwitch);
        Assert.Equal(Visibility.Visible, strip.Visibility);

        cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, cancelled);

        model.Update(settings, work, TallState(work.Id), null, Now);
        Settle();
        Assert.Null(model.PendingSwitch);
        Assert.Equal(Visibility.Collapsed, strip.Visibility);
        window.Close();
    });

    private static ProfileUsageState TallState(Guid id) => new()
    {
        ProfileId = id,
        IsLinked = true,
        Source = UsageSourceKind.ClaudeCode,
        Link = LinkStatus.Ready,
        LastAttemptAt = Now,
        Usage = new CachedUsage
        {
            ProfileId = id, ObservedAt = Now, Source = "oauthUsage",
            Usage = new UsageData { SessionPercentage = 40, WeeklyPercentage = 60, SonnetWeeklyPercentage = 10, OpusWeeklyPercentage = 5 },
        },
        Api = new CachedApiUsage
        {
            ProfileId = id, ObservedAt = Now, OrganizationId = "org",
            Usage = new ApiUsage { CurrentSpendCents = 1234, PrepaidCreditsCents = 8766, Currency = "USD" },
        },
    };

    private static ProfileUsageState ShortState(Guid id) => new()
    {
        ProfileId = id,
        IsLinked = true,
        Source = UsageSourceKind.Codex,
        Link = LinkStatus.Ready,
        LastAttemptAt = Now,
        Usage = new CachedUsage { ProfileId = id, ObservedAt = Now, Source = "codex", Usage = new UsageData { SessionPercentage = 20 } },
    };

    /// <summary>A pretend tray icon in the middle of the primary monitor's bottom edge, with that monitor's areas.</summary>
    private static (Int32Rect Icon, Int32Rect Monitor, Int32Rect Work, double Dpi) IconAtBottomOfPrimaryMonitor()
    {
        var handle = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = 0, Y = 0 }, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        NativeMethods.GetMonitorInfo(handle, ref info);
        var dpi = NativeMethods.GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 ? dpiX : 96u;
        var monitor = Rect(info.rcMonitor);
        var work = Rect(info.rcWork);
        var size = (int)(32 * dpi / 96);
        var icon = new Int32Rect(work.X + work.Width / 2, monitor.Y + monitor.Height - size, size, size);
        return (icon, monitor, work, dpi);
    }

    private static Int32Rect Rect(NativeMethods.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>Lets layout, rendering and the resulting moves finish.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }
}
