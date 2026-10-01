using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUsageTracker.App.Views;

/// <param name="Label">The text under the bar.</param>
/// <param name="Detail">What the pointer read-out says for this bar.</param>
public sealed record ChartBar(string Label, double Value, string Detail);

public sealed record ChartPoint(DateTimeOffset Time, double Value);

/// <summary>
/// Shared drawing for the charts, which stand in for upstream's Swift Charts views. They are drawn by hand
/// rather than with a charting library (plan.md: no general-purpose frameworks for single uses).
/// </summary>
internal static class ChartDrawing
{
    public static readonly Typeface Typeface = new("Segoe UI Variable Text, Segoe UI");

    public static FormattedText Text(string text, double size, Brush brush, Visual visual) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, size, brush,
            VisualTreeHelper.GetDpi(visual).PixelsPerDip);

    public static Pen Pen(Brush? brush, double thickness, double[]? dashes = null)
    {
        var pen = new Pen(brush, thickness);
        if (dashes is not null)
        {
            pen.DashStyle = new DashStyle(dashes, 0);
        }

        pen.Freeze();
        return pen;
    }

    /// <summary>The pointer read-out: a small box in the plot's top-right corner.</summary>
    public static void Readout(DrawingContext context, string text, Rect plot, Brush foreground, Brush background, Visual visual)
    {
        var formatted = Text(text, 10, foreground, visual);
        var box = new Rect(plot.Right - formatted.Width - 12, plot.Top, formatted.Width + 12, formatted.Height + 4);
        context.DrawRoundedRectangle(background, null, box, 4, 4);
        context.DrawText(formatted, new Point(box.X + 6, box.Y + 2));
    }

    public static DependencyProperty Register<TOwner>(string name, Type type, object? defaultValue = null) =>
        DependencyProperty.Register(name, type, typeof(TOwner),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>
/// A bar chart: upstream's <c>DailyCostChart</c> in the popover and <c>BillingCycleChart</c> on the History
/// page. Moving the pointer over a bar highlights it and shows its detail.
/// </summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = ChartDrawing.Register<BarChart>(nameof(Bars), typeof(IReadOnlyList<ChartBar>));
    public static readonly DependencyProperty MinimumScaleProperty = ChartDrawing.Register<BarChart>(nameof(MinimumScale), typeof(double), 0.01);
    public static readonly DependencyProperty AxisLabelProperty = ChartDrawing.Register<BarChart>(nameof(AxisLabel), typeof(Func<double, string>));
    public static readonly DependencyProperty FillProperty = ChartDrawing.Register<BarChart>(nameof(Fill), typeof(Brush));
    public static readonly DependencyProperty ForegroundProperty = ChartDrawing.Register<BarChart>(nameof(Foreground), typeof(Brush));
    public static readonly DependencyProperty GridProperty = ChartDrawing.Register<BarChart>(nameof(Grid), typeof(Brush));
    public static readonly DependencyProperty ReadoutBackgroundProperty = ChartDrawing.Register<BarChart>(nameof(ReadoutBackground), typeof(Brush));
    public static readonly DependencyProperty MaxBarWidthProperty = ChartDrawing.Register<BarChart>(nameof(MaxBarWidth), typeof(double), 12.0);

    private int? _hovered;

    public IReadOnlyList<ChartBar>? Bars { get => (IReadOnlyList<ChartBar>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }

    /// <summary>The smallest value the vertical scale reaches, so tiny amounts do not fill the chart.</summary>
    public double MinimumScale { get => (double)GetValue(MinimumScaleProperty); set => SetValue(MinimumScaleProperty, value); }

    /// <summary>Formats an axis value; no axis labels are drawn without it.</summary>
    public Func<double, string>? AxisLabel { get => (Func<double, string>?)GetValue(AxisLabelProperty); set => SetValue(AxisLabelProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public Brush? Grid { get => (Brush?)GetValue(GridProperty); set => SetValue(GridProperty, value); }

    public Brush? ReadoutBackground { get => (Brush?)GetValue(ReadoutBackgroundProperty); set => SetValue(ReadoutBackgroundProperty, value); }

    public double MaxBarWidth { get => (double)GetValue(MaxBarWidthProperty); set => SetValue(MaxBarWidthProperty, value); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var bars = Bars;
        var plot = Plot();
        var x = e.GetPosition(this).X;
        int? hovered = bars is { Count: > 0 } && x >= plot.Left && x <= plot.Right
            ? Math.Clamp((int)((x - plot.Left) / (plot.Width / bars.Count)), 0, bars.Count - 1)
            : null;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        // A transparent background makes the whole chart, not only its bars, receive the pointer.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Bars is not { Count: > 0 } bars)
        {
            return;
        }

        var plot = Plot();
        var top = Math.Max(bars.Max(b => b.Value) * 1.15, MinimumScale);
        if (AxisLabel is { } axis)
        {
            foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
            {
                var y = plot.Bottom - plot.Height * fraction;
                context.DrawLine(ChartDrawing.Pen(Grid, 0.5), new Point(plot.Left, y), new Point(plot.Right, y));
                var label = ChartDrawing.Text(axis(top * fraction), 8, Foreground ?? Brushes.Gray, this);
                context.DrawText(label, new Point(plot.Left - label.Width - 4, y - label.Height / 2));
            }
        }

        var slot = plot.Width / bars.Count;
        var width = Math.Min(MaxBarWidth, slot * 0.7);
        // Every label is drawn only when they fit; otherwise every second, third and so on.
        var labelEvery = Math.Max(1, (int)Math.Ceiling(14 / slot));
        for (var i = 0; i < bars.Count; i++)
        {
            var centre = plot.Left + slot * (i + 0.5);
            var height = plot.Height * Math.Clamp(bars[i].Value / top, 0, 1);
            if (height > 0)
            {
                context.PushOpacity(_hovered is null || _hovered == i ? 1 : 0.45);
                context.DrawRoundedRectangle(Fill, null, new Rect(centre - width / 2, plot.Bottom - height, width, height), 2, 2);
                context.Pop();
            }

            if (i % labelEvery == 0)
            {
                var label = ChartDrawing.Text(bars[i].Label, 8, Foreground ?? Brushes.Gray, this);
                context.DrawText(label, new Point(centre - label.Width / 2, plot.Bottom + 2));
            }
        }

        if (_hovered is { } hovered && hovered < bars.Count)
        {
            ChartDrawing.Readout(context, bars[hovered].Detail, plot, Foreground ?? Brushes.Gray, ReadoutBackground ?? Brushes.Transparent, this);
        }
    }

    private Rect Plot()
    {
        var left = AxisLabel is null ? 0 : 34;
        return new Rect(left, 2, Math.Max(ActualWidth - left, 1), Math.Max(ActualHeight - 16, 1));
    }
}

/// <summary>
/// Upstream's <c>CombinedUsageChart</c>: session and weekly percentages over a time window on one 0-100 %
/// axis, drawn as step lines (a value holds until the next sample), the session with a filled area and the
/// week dashed. Moving the pointer shows both values at that time.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    public static readonly DependencyProperty SessionProperty = ChartDrawing.Register<HistoryChart>(nameof(Session), typeof(IReadOnlyList<ChartPoint>));
    public static readonly DependencyProperty WeeklyProperty = ChartDrawing.Register<HistoryChart>(nameof(Weekly), typeof(IReadOnlyList<ChartPoint>));
    public static readonly DependencyProperty StartProperty = ChartDrawing.Register<HistoryChart>(nameof(Start), typeof(DateTimeOffset), default(DateTimeOffset));
    public static readonly DependencyProperty EndProperty = ChartDrawing.Register<HistoryChart>(nameof(End), typeof(DateTimeOffset), default(DateTimeOffset));
    public static readonly DependencyProperty TimeFormatProperty = ChartDrawing.Register<HistoryChart>(nameof(TimeFormat), typeof(string), "HH:mm");
    public static readonly DependencyProperty ReadoutFormatProperty = ChartDrawing.Register<HistoryChart>(nameof(ReadoutFormat), typeof(string), "MMM d, HH:mm");
    public static readonly DependencyProperty SessionLabelProperty = ChartDrawing.Register<HistoryChart>(nameof(SessionLabel), typeof(string), "");
    public static readonly DependencyProperty WeeklyLabelProperty = ChartDrawing.Register<HistoryChart>(nameof(WeeklyLabel), typeof(string), "");
    public static readonly DependencyProperty SessionBrushProperty = ChartDrawing.Register<HistoryChart>(nameof(SessionBrush), typeof(Brush));
    public static readonly DependencyProperty WeeklyBrushProperty = ChartDrawing.Register<HistoryChart>(nameof(WeeklyBrush), typeof(Brush));
    public static readonly DependencyProperty ForegroundProperty = ChartDrawing.Register<HistoryChart>(nameof(Foreground), typeof(Brush));
    public static readonly DependencyProperty GridProperty = ChartDrawing.Register<HistoryChart>(nameof(Grid), typeof(Brush));
    public static readonly DependencyProperty ReadoutBackgroundProperty = ChartDrawing.Register<HistoryChart>(nameof(ReadoutBackground), typeof(Brush));

    private double? _pointer;

    public IReadOnlyList<ChartPoint>? Session { get => (IReadOnlyList<ChartPoint>?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }

    public IReadOnlyList<ChartPoint>? Weekly { get => (IReadOnlyList<ChartPoint>?)GetValue(WeeklyProperty); set => SetValue(WeeklyProperty, value); }

    public DateTimeOffset Start { get => (DateTimeOffset)GetValue(StartProperty); set => SetValue(StartProperty, value); }

    public DateTimeOffset End { get => (DateTimeOffset)GetValue(EndProperty); set => SetValue(EndProperty, value); }

    /// <summary>The .NET format of the axis times.</summary>
    public string TimeFormat { get => (string)GetValue(TimeFormatProperty); set => SetValue(TimeFormatProperty, value); }

    public string ReadoutFormat { get => (string)GetValue(ReadoutFormatProperty); set => SetValue(ReadoutFormatProperty, value); }

    public string SessionLabel { get => (string)GetValue(SessionLabelProperty); set => SetValue(SessionLabelProperty, value); }

    public string WeeklyLabel { get => (string)GetValue(WeeklyLabelProperty); set => SetValue(WeeklyLabelProperty, value); }

    public Brush? SessionBrush { get => (Brush?)GetValue(SessionBrushProperty); set => SetValue(SessionBrushProperty, value); }

    public Brush? WeeklyBrush { get => (Brush?)GetValue(WeeklyBrushProperty); set => SetValue(WeeklyBrushProperty, value); }

    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public Brush? Grid { get => (Brush?)GetValue(GridProperty); set => SetValue(GridProperty, value); }

    public Brush? ReadoutBackground { get => (Brush?)GetValue(ReadoutBackgroundProperty); set => SetValue(ReadoutBackgroundProperty, value); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var plot = Plot();
        var x = e.GetPosition(this).X;
        _pointer = x >= plot.Left && x <= plot.Right ? x : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _pointer = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = Plot();
        var span = (End - Start).TotalSeconds;
        if (span <= 0)
        {
            return;
        }

        var foreground = Foreground ?? Brushes.Gray;
        foreach (var percent in new[] { 0, 25, 50, 75, 100 })
        {
            var y = plot.Bottom - plot.Height * percent / 100;
            context.DrawLine(ChartDrawing.Pen(Grid, 0.5, [4, 4]), new Point(plot.Left, y), new Point(plot.Right, y));
            var label = ChartDrawing.Text(percent.ToString(CultureInfo.InvariantCulture) + "%", 9, foreground, this);
            context.DrawText(label, new Point(plot.Left - label.Width - 4, y - label.Height / 2));
        }

        const int ticks = 4;
        for (var i = 0; i <= ticks; i++)
        {
            var x = plot.Left + plot.Width * i / ticks;
            context.DrawLine(ChartDrawing.Pen(Grid, 0.5), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var time = TimeZoneInfo.ConvertTime(Start.AddSeconds(span * i / ticks), TimeZoneInfo.Local);
            var label = ChartDrawing.Text(time.ToString(TimeFormat, CultureInfo.CurrentCulture), 9, foreground, this);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(ActualWidth - label.Width, 0)), plot.Bottom + 3));
        }

        double X(DateTimeOffset time) => plot.Left + plot.Width * (time - Start).TotalSeconds / span;
        double Y(double value) => plot.Bottom - plot.Height * Math.Clamp(value, 0, 100) / 100;

        context.PushClip(new RectangleGeometry(plot));
        if (Session is { Count: > 0 } session)
        {
            var area = Steps(session, X, Y, plot.Bottom);
            var fill = SessionBrush?.CloneCurrentValue();
            if (fill is not null)
            {
                fill.Opacity = 0.18;
                context.DrawGeometry(fill, null, area);
            }

            context.DrawGeometry(null, ChartDrawing.Pen(SessionBrush, 2), Steps(session, X, Y, null));
        }

        if (Weekly is { Count: > 0 } weekly)
        {
            context.DrawGeometry(null, ChartDrawing.Pen(WeeklyBrush, 2, [3, 2]), Steps(weekly, X, Y, null));
        }

        context.Pop();

        if (_pointer is { } pointer)
        {
            context.DrawLine(ChartDrawing.Pen(foreground, 0.75), new Point(pointer, plot.Top), new Point(pointer, plot.Bottom));
            var at = Start.AddSeconds(span * (pointer - plot.Left) / plot.Width);
            var parts = new List<string> { TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString(ReadoutFormat, CultureInfo.CurrentCulture) };
            foreach (var (label, points) in new[] { (SessionLabel, Session), (WeeklyLabel, Weekly) })
            {
                if (ValueAt(points, at) is { } value)
                {
                    parts.Add($"{label} {value.ToString("0.#", CultureInfo.CurrentCulture)}%");
                }
            }

            ChartDrawing.Readout(context, string.Join("  ·  ", parts), plot, foreground, ReadoutBackground ?? Brushes.Transparent, this);
        }
    }

    /// <summary>The value a step line shows at a time: the last sample at or before it.</summary>
    public static double? ValueAt(IReadOnlyList<ChartPoint>? points, DateTimeOffset at)
    {
        ChartPoint? last = null;
        foreach (var point in points ?? [])
        {
            if (point.Time <= at && (last is null || point.Time >= last.Time))
            {
                last = point;
            }
        }

        return last?.Value;
    }

    /// <summary>A step line through time-ordered points; with a baseline, the closed area under it.</summary>
    private static StreamGeometry Steps(IReadOnlyList<ChartPoint> points, Func<DateTimeOffset, double> x, Func<double, double> y, double? baseline)
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            var first = new Point(x(points[0].Time), y(points[0].Value));
            stream.BeginFigure(baseline is { } b ? new Point(first.X, b) : first, baseline is not null, baseline is not null);
            if (baseline is not null)
            {
                stream.LineTo(first, true, false);
            }

            var previous = first;
            for (var i = 1; i < points.Count; i++)
            {
                var next = new Point(x(points[i].Time), y(points[i].Value));
                stream.LineTo(new Point(next.X, previous.Y), true, false);
                stream.LineTo(next, true, false);
                previous = next;
            }

            if (baseline is { } bottom)
            {
                stream.LineTo(new Point(previous.X, bottom), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private Rect Plot() => new(34, 4, Math.Max(ActualWidth - 42, 1), Math.Max(ActualHeight - 22, 1));
}
