using System.Windows;

namespace ClaudeUsageTracker.App.Tray;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// Where the popover goes, in physical pixels on the monitor holding the tray icon (plan.md section 2):
/// next to the icon on the taskbar's side, clamped to the work area, using the activation point when
/// the shell reports no icon rectangle. Upstream anchors below the menu bar item; Windows taskbars can be
/// on any edge.
/// </summary>
public static class PopoverPlacement
{
    /// <param name="icon">The icon rectangle from <c>Shell_NotifyIconGetRect</c>, if any.</param>
    /// <param name="anchor">The point Explorer reported with the activation.</param>
    /// <param name="gap">Distance from the work-area edge, already scaled to the monitor's DPI.</param>
    public static Point Place(Int32Rect? icon, Point anchor, Int32Rect monitor, Int32Rect work, Size size, double gap)
    {
        var target = icon is { } r ? new Point(r.X + r.Width / 2.0, r.Y + r.Height / 2.0) : anchor;
        var edge = Edge(monitor, work, target);
        double x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = target.X - size.Width / 2;
                y = work.Y + gap;
                break;
            case TaskbarEdge.Left:
                x = work.X + gap;
                y = target.Y - size.Height / 2;
                break;
            case TaskbarEdge.Right:
                x = work.X + work.Width - size.Width - gap;
                y = target.Y - size.Height / 2;
                break;
            default:
                x = target.X - size.Width / 2;
                y = work.Y + work.Height - size.Height - gap;
                break;
        }

        // Keep the whole popover inside the work area; a popover taller than it sticks to the top.
        x = Math.Max(work.X + gap, Math.Min(x, work.X + work.Width - size.Width - gap));
        y = Math.Max(work.Y + gap, Math.Min(y, work.Y + work.Height - size.Height - gap));
        return new Point(Math.Round(x), Math.Round(y));
    }

    /// <summary>
    /// The taskbar's edge: where the work area is shorter than the monitor, or, for an auto-hidden taskbar,
    /// the monitor edge nearest the icon.
    /// </summary>
    public static TaskbarEdge Edge(Int32Rect monitor, Int32Rect work, Point target)
    {
        if (work.Y + work.Height < monitor.Y + monitor.Height)
        {
            return TaskbarEdge.Bottom;
        }

        if (work.Y > monitor.Y)
        {
            return TaskbarEdge.Top;
        }

        if (work.X > monitor.X)
        {
            return TaskbarEdge.Left;
        }

        if (work.X + work.Width < monitor.X + monitor.Width)
        {
            return TaskbarEdge.Right;
        }

        var distances = new[]
        {
            (TaskbarEdge.Bottom, monitor.Y + monitor.Height - target.Y),
            (TaskbarEdge.Top, target.Y - monitor.Y),
            (TaskbarEdge.Left, target.X - monitor.X),
            (TaskbarEdge.Right, monitor.X + monitor.Width - target.X),
        };
        return distances.MinBy(d => d.Item2).Item1;
    }
}
