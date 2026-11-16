using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.Core.Usage;
using ClaudeUsageTracker.Tests.ViewModels;

namespace ClaudeUsageTracker.Tests.Tray;

/// <summary>
/// Where the tray icons draw the elapsed-time tick. Upstream's multi-profile bars and circles draw a tick for the
/// session and for the week at any usage. The single-profile progress bar does too, where upstream hides it while the
/// fill is under a point wide (docs/parity.md difference 18). At 1 % no fill is drawn, as in a window just started.
/// </summary>
[Collection(WpfCollection.Name)]
public class TrayIconRendererTests
{
    private const int Size = 32;

    // With safe (green) fills, the Runaway and Critical pace colours come from nothing but ticks.
    private static readonly Color Purple = TrayIconRenderer.PaceColour(PaceStatus.Runaway, true);
    private static readonly Color Red = TrayIconRenderer.PaceColour(PaceStatus.Critical, true);

    [Fact]
    public Task ProgressBarDrawsTheTickBeforeAnyFill() => OnStaThread(() =>
    {
        var spec = new TrayIconSpec(TrayIconStyle.ProgressBar, 0.01, 1, UsageStatusLevel.Safe, 0.5, PaceStatus.Runaway, false, null, false);

        Assert.NotEmpty(Pixels(spec, Purple));
        Assert.Empty(Pixels(spec with { MarkerFraction = null }, Purple));
    });

    [Fact]
    public Task MultiProfileBarsDrawTheSessionAndWeekTicksBeforeAnyFill() => OnStaThread(() =>
    {
        // The session bar is in the top half and the week bar in the bottom half; each tick has its own pace colour.
        var spec = new TrayIconSpec(TrayIconStyle.DualBar, 0.01, 1, UsageStatusLevel.Safe, 0.5, PaceStatus.Runaway, false, null, false)
        {
            HasSecondary = true,
            SecondaryFraction = 0.01,
            SecondaryLevel = UsageStatusLevel.Safe,
            SecondaryMarkerFraction = 0.5,
            SecondaryPace = PaceStatus.Critical,
        };

        var session = Pixels(spec, Purple);
        var week = Pixels(spec, Red);

        Assert.NotEmpty(session);
        Assert.All(session, p => Assert.True(p.Y < Size / 2, $"Session tick pixel at {p}."));
        Assert.NotEmpty(week);
        Assert.All(week, p => Assert.True(p.Y >= Size / 2, $"Week tick pixel at {p}."));
        Assert.Empty(Pixels(spec with { SecondaryMarkerFraction = null }, Red));
    });

    [Fact]
    public Task MultiProfileCirclesDrawTheWeekTickOnTheOuterRing() => OnStaThread(() =>
    {
        // The week's tick at a quarter of the week, at three o'clock on the outer ring; the session has no tick.
        var spec = new TrayIconSpec(TrayIconStyle.Concentric, 0.4, 40, UsageStatusLevel.Safe, null, null, false, null, false)
        {
            HasSecondary = true,
            SecondaryFraction = 0.3,
            SecondaryLevel = UsageStatusLevel.Safe,
            SecondaryMarkerFraction = 0.25,
            SecondaryPace = PaceStatus.Runaway,
        };

        var ticks = Pixels(spec, Purple);

        Assert.NotEmpty(ticks);
        Assert.All(ticks, p => Assert.True(p.X > Size * 3 / 4 && Math.Abs(p.Y - Size / 2) <= 2, $"Tick pixel at {p}."));
        Assert.Empty(Pixels(spec with { SecondaryMarkerFraction = null }, Purple));
    });

    /// <summary>The opaque pixels near <paramref name="colour"/> in the icon drawn for a dark taskbar.</summary>
    private static List<(int X, int Y)> Pixels(TrayIconSpec spec, Color colour)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            TrayIconRenderer.Draw(context, Size, spec, true);
        }

        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var bgra = new byte[Size * Size * 4];
        new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0).CopyPixels(bgra, Size * 4, 0);
        var pixels = new List<(int X, int Y)>();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = (y * Size + x) * 4;
                if (bgra[i + 3] > 200 && Near(bgra[i + 2], colour.R) && Near(bgra[i + 1], colour.G) && Near(bgra[i], colour.B))
                {
                    pixels.Add((x, y));
                }
            }
        }

        return pixels;
    }

    private static bool Near(byte value, byte expected) => Math.Abs(value - expected) <= 40;

    private static Task OnStaThread(Action action)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
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
}
