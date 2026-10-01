using System.Windows;
using System.Windows.Media;

namespace ClaudeUsageTracker.App.Views;

/// <summary>
/// Upstream's usage bar: a 4 DIP track with radius 2.5, a fill and a 2.5 x 8 DIP time marker centred on the
/// bar. An unavailable value draws the track only.
/// </summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = Register(nameof(Fraction), typeof(double?));
    public static readonly DependencyProperty MarkerProperty = Register(nameof(Marker), typeof(double?));
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), typeof(Brush));
    public static readonly DependencyProperty TrackProperty = Register(nameof(Track), typeof(Brush));
    public static readonly DependencyProperty MarkerBrushProperty = Register(nameof(MarkerBrush), typeof(Brush));

    private const double BarHeight = 4;
    private const double MarkerWidth = 2.5;
    private const double MarkerHeight = 8;

    public UsageBar()
    {
        Height = MarkerHeight;
        SnapsToDevicePixels = true;
    }

    public double? Fraction
    {
        get => (double?)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public double? Marker
    {
        get => (double?)GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush? MarkerBrush
    {
        get => (Brush?)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var top = (MarkerHeight - BarHeight) / 2;
        context.DrawRoundedRectangle(Track, null, new Rect(0, top, width, BarHeight), 2.5, 2.5);
        if (Fraction is { } fraction && fraction > 0)
        {
            context.DrawRoundedRectangle(Fill, null, new Rect(0, top, width * Math.Min(fraction, 1), BarHeight), 2.5, 2.5);
        }

        if (Marker is { } marker)
        {
            var x = Math.Round(width * Math.Clamp(marker, 0, 1)) - 0.75;
            context.DrawRoundedRectangle(MarkerBrush, null, new Rect(x, 0, MarkerWidth, MarkerHeight), 1, 1);
        }
    }

    private static DependencyProperty Register(string name, Type type) =>
        DependencyProperty.Register(name, type, typeof(UsageBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
