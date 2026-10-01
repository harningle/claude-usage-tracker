using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Tray;

public enum TrayIconStyle
{
    Battery,
    ProgressBar,
    PercentageOnly,
    Ring,
    Dot,
}

/// <summary>What one tray icon shows. A null <see cref="Fraction"/> means the value is unavailable.</summary>
/// <param name="Fraction">Displayed fraction 0-1 (used, or remaining when that is shown).</param>
/// <param name="DisplayPercentage">The number for the percentage style.</param>
/// <param name="Label">"S" or "W" for the ring's centre when labels are on.</param>
/// <param name="Dimmed">Stale or failed data, drawn at reduced opacity.</param>
public sealed record TrayIconSpec(
    TrayIconStyle Style,
    double? Fraction,
    double? DisplayPercentage,
    UsageStatusLevel? Level,
    double? MarkerFraction,
    PaceStatus? Pace,
    bool Monochrome,
    string? Label,
    bool Dimmed);

/// <summary>
/// Square renderings of upstream's <c>MenuBarIconRenderer</c> styles. macOS status items are wide images;
/// Windows tray icons are square small icons (16 px at 100 %), so each style keeps its geometry (battery,
/// pill bar, number, ring, dot) and drops the text labels that cannot fit; the tooltip and popover carry the
/// details (docs/parity.md).
/// </summary>
public static class TrayIconRenderer
{
    public static void Draw(DrawingContext context, int size, TrayIconSpec spec, bool taskbarDark)
    {
        var foreground = taskbarDark ? Colors.White : Colors.Black;
        var fill = spec.Monochrome || spec.Level is null ? foreground : StatusColour(spec.Level.Value, taskbarDark);
        var marker = spec.Pace is { } pace ? PaceColour(pace, taskbarDark) : foreground;
        if (spec.Dimmed)
        {
            context.PushOpacity(0.55);
        }

        var s = size / 16.0;
        switch (spec.Style)
        {
            case TrayIconStyle.Battery:
                Battery(context, size, s, spec, foreground, fill, marker);
                break;
            case TrayIconStyle.ProgressBar:
                Bar(context, size, s, spec, foreground, fill, marker);
                break;
            case TrayIconStyle.PercentageOnly:
                Number(context, size, spec, foreground, fill);
                break;
            case TrayIconStyle.Ring:
                Ring(context, size, s, spec, foreground, fill, marker);
                break;
            default:
                Dot(context, size, s, spec, foreground, fill, marker);
                break;
        }

        if (spec.Dimmed)
        {
            context.Pop();
        }
    }

    /// <summary>
    /// The icon for a profile without usage tracking set up: upstream's fallback logo, a ring with a centre
    /// dot (upstream's own artwork is not reused).
    /// </summary>
    public static void DrawLogo(DrawingContext context, int size, bool taskbarDark)
    {
        var foreground = taskbarDark ? Colors.White : Colors.Black;
        var s = size / 16.0;
        var centre = new Point(size / 2.0, size / 2.0);
        context.DrawEllipse(null, Pen(foreground, 0.7, 1.6 * s), centre, size / 2.0 - 1.6 * s, size / 2.0 - 1.6 * s);
        context.DrawEllipse(Brush(foreground, 1), null, centre, 1.8 * s, 1.8 * s);
    }

    private static void Battery(DrawingContext context, int size, double s, TrayIconSpec spec, Color foreground, Color fill, Color marker)
    {
        var nub = 1.5 * s;
        var body = new Rect(0.6 * s, size * 0.27, size - nub - 1.2 * s, size * 0.46);
        context.DrawRoundedRectangle(null, Pen(foreground, 0.6, 1.2 * s), body, 2 * s, 2 * s);
        context.DrawRectangle(Brush(foreground, 0.6), null,
            new Rect(body.Right + 0.4 * s, size / 2.0 - 1.6 * s, nub, 3.2 * s));
        var inner = new Rect(body.X + 1.6 * s, body.Y + 1.6 * s, body.Width - 3.2 * s, body.Height - 3.2 * s);
        if (spec.Fraction is { } fraction && inner.Width * fraction > 0.5)
        {
            context.DrawRoundedRectangle(Brush(fill, 1), null,
                new Rect(inner.X, inner.Y, inner.Width * Math.Clamp(fraction, 0, 1), inner.Height), 1 * s, 1 * s);
        }

        if (spec.MarkerFraction is { } m)
        {
            var x = Math.Round(inner.X + inner.Width * Math.Clamp(m, 0, 1));
            context.DrawLine(Pen(marker, 1, 1.4 * s), new Point(x, body.Top), new Point(x, body.Bottom));
        }
    }

    private static void Bar(DrawingContext context, int size, double s, TrayIconSpec spec, Color foreground, Color fill, Color marker)
    {
        var track = new Rect(0.8 * s, size * 0.33, size - 1.6 * s, size * 0.34);
        var radius = track.Height / 2;
        context.DrawRoundedRectangle(Brush(foreground, 0.25), null, track, radius, radius);
        if (spec.Fraction is { } fraction && track.Width * fraction > 1)
        {
            context.DrawRoundedRectangle(Brush(fill, 1), null,
                new Rect(track.X, track.Y, track.Width * Math.Clamp(fraction, 0, 1), track.Height), radius, radius);

            // Upstream draws the progress-bar tick only where a fill is drawn.
            if (spec.MarkerFraction is { } m)
            {
                var x = Math.Round(track.X + track.Width * Math.Clamp(m, 0, 1));
                context.DrawLine(Pen(marker, 1, 1.4 * s), new Point(x, track.Top - 1.5 * s), new Point(x, track.Bottom + 1.5 * s));
            }
        }
    }

    private static void Number(DrawingContext context, int size, TrayIconSpec spec, Color foreground, Color fill)
    {
        var text = spec.DisplayPercentage is { } value
            ? (Math.Truncate(value) + 0.0).ToString("0", CultureInfo.InvariantCulture)
            : "–";
        var emSize = size * (text.Length >= 3 ? 0.56 : 0.78);
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            emSize, Brush(spec.DisplayPercentage is null ? foreground : fill, 1), 1.0);
        // Centre on the glyph ink rather than the line box, so digits sit in the middle of the square.
        var ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        context.DrawText(formatted, new Point((size - ink.Width) / 2 - ink.X, (size - ink.Height) / 2 - ink.Y));
    }

    private static void Ring(DrawingContext context, int size, double s, TrayIconSpec spec, Color foreground, Color fill, Color marker)
    {
        var stroke = 2.4 * s;
        var radius = size / 2.0 - stroke / 2 - 0.4 * s;
        var centre = new Point(size / 2.0, size / 2.0);
        context.DrawEllipse(null, Pen(foreground, 0.25, stroke), centre, radius, radius);
        if (spec.Fraction is { } fraction && fraction > 0)
        {
            context.DrawGeometry(null, Pen(fill, 1, stroke, PenLineCap.Round), Arc(centre, radius, Math.Clamp(fraction, 0, 1)));
        }

        if (spec.MarkerFraction is { } m)
        {
            var angle = Math.Clamp(m, 0, 1) * 2 * Math.PI;
            var direction = new Vector(Math.Sin(angle), -Math.Cos(angle));
            context.DrawLine(Pen(marker, 1, 1.3 * s), centre + direction * (radius - stroke), centre + direction * (radius + stroke / 2));
        }

        if (spec.Label is { Length: > 0 } label && size >= 20)
        {
            var formatted = new FormattedText(
                label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.38, Brush(foreground, 1), 1.0);
            var ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
            context.DrawText(formatted, new Point((size - ink.Width) / 2 - ink.X, (size - ink.Height) / 2 - ink.Y));
        }
    }

    private static void Dot(DrawingContext context, int size, double s, TrayIconSpec spec, Color foreground, Color fill, Color marker)
    {
        var centre = new Point(size / 2.0, size / 2.0);
        var radius = size * 0.31;
        if (spec.Fraction is null)
        {
            context.DrawEllipse(null, Pen(foreground, 0.6, 1.2 * s), centre, radius, radius);
        }
        else
        {
            context.DrawEllipse(Brush(fill, 1), null, centre, radius, radius);
        }

        if (spec.Pace is not null)
        {
            var paceRadius = size * 0.14;
            context.DrawEllipse(Brush(marker, 1), Pen(foreground == Colors.White ? Colors.Black : Colors.White, 1, 0.8 * s),
                new Point(size - paceRadius - 0.4 * s, size - paceRadius - 0.4 * s), paceRadius, paceRadius);
        }
    }

    /// <summary>A clockwise arc from 12 o'clock covering <paramref name="fraction"/> of the circle.</summary>
    private static Geometry Arc(Point centre, double radius, double fraction)
    {
        if (fraction >= 0.9999)
        {
            return new EllipseGeometry(centre, radius, radius);
        }

        var angle = fraction * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    /// <summary>Status colours for the taskbar's mode (the popover palette's values).</summary>
    public static Color StatusColour(UsageStatusLevel level, bool dark) => level switch
    {
        UsageStatusLevel.Safe => dark ? Rgb(0x3C, 0xC7, 0x5F) : Rgb(0x1B, 0x6B, 0x34),
        UsageStatusLevel.Moderate => dark ? Rgb(0xFF, 0x92, 0x30) : Rgb(0xC7, 0x6A, 0x00),
        _ => dark ? Rgb(0xFF, 0x42, 0x45) : Rgb(0xE0, 0x26, 0x2B),
    };

    public static Color PaceColour(PaceStatus pace, bool dark) => pace switch
    {
        PaceStatus.Comfortable => dark ? Rgb(0x30, 0xD1, 0x58) : Rgb(0x34, 0xC7, 0x59),
        PaceStatus.OnTrack => dark ? Rgb(0x00, 0xD2, 0xE0) : Rgb(0x00, 0xA3, 0xB0),
        PaceStatus.Warming => dark ? Rgb(0xFF, 0xD6, 0x00) : Rgb(0xB8, 0x92, 0x00),
        PaceStatus.Pressing => dark ? Rgb(0xFF, 0x92, 0x30) : Rgb(0xE0, 0x76, 0x10),
        PaceStatus.Critical => dark ? Rgb(0xFF, 0x42, 0x45) : Rgb(0xE0, 0x26, 0x2B),
        _ => dark ? Rgb(0xDB, 0x34, 0xF2) : Rgb(0xB0, 0x20, 0xC8),
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Brush(Color colour, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Color colour, double opacity, double thickness, PenLineCap cap = PenLineCap.Flat)
    {
        var pen = new Pen(Brush(colour, opacity), thickness) { StartLineCap = cap, EndLineCap = cap };
        pen.Freeze();
        return pen;
    }
}
