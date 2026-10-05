using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.App.Views;

namespace ClaudeUsageTracker.Tests.ViewModels;

/// <summary>
/// The app's own WPF controls. A dependency property registered with a default of the wrong type fails only
/// when XAML first touches the control, which would end the app; these tests touch every control here.
/// </summary>
[Collection(WpfCollection.Name)]
public class ViewControlTests
{
    public static TheoryData<Type> DependencyObjectTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(BarChart).Assembly.GetTypes().Where(t => typeof(DependencyObject).IsAssignableFrom(t) && !t.IsAbstract))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DependencyObjectTypes))]
    public void EveryControlTypeInitialises(Type type) =>
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);

    [Fact]
    public Task ChartsRenderWithDataAndWithout() => OnStaThread(() =>
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var history = new HistoryChart
        {
            Start = now.AddHours(-24),
            End = now,
            Session = [new ChartPoint(now.AddHours(-20), 10), new ChartPoint(now.AddHours(-2), 80)],
            Weekly = [new ChartPoint(now.AddHours(-23), 40)],
            SessionBrush = Brushes.Orange,
            WeeklyBrush = Brushes.Indigo,
        };
        var bars = new BarChart
        {
            Bars = [new ChartBar("1", 1.5, "Oct 1: $1.50"), new ChartBar("2", 0.8, "Oct 2: $0.80")],
            AxisLabel = v => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Fill = Brushes.Orange,
        };

        foreach (FrameworkElement element in new FrameworkElement[] { history, bars, new HistoryChart(), new BarChart(), new UsageBar() })
        {
            Render(element);
        }

        Assert.Equal(80, HistoryChart.ValueAt(history.Session, now));
        Assert.Null(HistoryChart.ValueAt(history.Session, now.AddHours(-21)));
    });

    /// <summary>
    /// Rows rebuilt with equal values must stay distinct items, or WPF's automation peers keep pointing at
    /// discarded containers and screen readers lose the list (seen with the profile list in fixture mode).
    /// </summary>
    [Fact]
    public void ListItemsCompareByReference()
    {
        var first = new ProfileRow(Guid.Empty);
        var same = new ProfileRow(Guid.Empty);

        Assert.NotEqual(first, same);
        Assert.Equal(first, first);
        Assert.NotEqual(new DetailRow("a", "b"), new DetailRow("a", "b"));
    }

    private static void Render(FrameworkElement element)
    {
        element.Measure(new Size(400, 160));
        element.Arrange(new Rect(0, 0, 400, 160));
        new RenderTargetBitmap(400, 160, 96, 96, PixelFormats.Pbgra32).Render(element);
    }

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
