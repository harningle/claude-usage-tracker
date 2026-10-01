using System.Windows;
using ClaudeUsageTracker.App.Tray;

namespace ClaudeUsageTracker.Tests.Tray;

/// <summary>Popover anchoring in physical pixels, including the second monitor of a mixed-DPI desk.</summary>
public class PopoverPlacementTests
{
    // A 3840 x 2160 monitor at 250 % with a 120 px bottom taskbar (the development machine's layout).
    private static readonly Int32Rect Monitor = new(0, 0, 3840, 2160);
    private static readonly Int32Rect BottomWork = new(0, 0, 3840, 2040);
    private static readonly Size Popover = new(700, 1000); // 280 x 400 DIP at 250 %
    private const double Gap = 20;

    [Fact]
    public void BottomTaskbarOpensAboveTheIconCentred()
    {
        var icon = new Int32Rect(3000, 2060, 80, 120);

        var position = PopoverPlacement.Place(icon, default, Monitor, BottomWork, Popover, Gap);

        Assert.Equal(new Point(3040 - 350, 2040 - 1000 - 20), position);
    }

    [Fact]
    public void IconNearTheCornerIsClampedIntoTheWorkArea()
    {
        var icon = new Int32Rect(3760, 2060, 80, 120);

        var position = PopoverPlacement.Place(icon, default, Monitor, BottomWork, Popover, Gap);

        Assert.Equal(3840 - 700 - 20, position.X);
    }

    [Fact]
    public void MissingIconRectangleFallsBackToTheActivationPoint()
    {
        var position = PopoverPlacement.Place(null, new Point(1000, 2100), Monitor, BottomWork, Popover, Gap);

        Assert.Equal(new Point(650, 1020), position);
    }

    [Fact]
    public void TopTaskbarOpensBelow()
    {
        var work = new Int32Rect(0, 120, 3840, 2040);

        var position = PopoverPlacement.Place(new Int32Rect(3000, 0, 80, 120), default, Monitor, work, Popover, Gap);

        Assert.Equal(new Point(2690, 140), position);
    }

    [Fact]
    public void LeftAndRightTaskbarsOpenBesideTheIcon()
    {
        var left = PopoverPlacement.Place(new Int32Rect(0, 1800, 120, 80), default, Monitor, new Int32Rect(120, 0, 3720, 2160), Popover, Gap);
        var right = PopoverPlacement.Place(new Int32Rect(3720, 1800, 120, 80), default, Monitor, new Int32Rect(0, 0, 3720, 2160), Popover, Gap);

        Assert.Equal(new Point(140, 1140), left);
        Assert.Equal(new Point(3720 - 700 - 20, 1140), right);
    }

    [Fact]
    public void SecondMonitorUsesItsOwnCoordinates()
    {
        // A 1920 x 1080 monitor at 100 % to the right of the 4K one: physical coordinates continue at x 3840.
        var monitor = new Int32Rect(3840, 0, 1920, 1080);
        var work = new Int32Rect(3840, 0, 1920, 1032);
        var size = new Size(280, 400);

        var position = PopoverPlacement.Place(new Int32Rect(5500, 1040, 24, 40), default, monitor, work, size, 8);

        Assert.Equal(new Point(5512 - 140, 1032 - 400 - 8), position);
    }

    [Fact]
    public void AutoHiddenTaskbarIsInferredFromTheIcon()
    {
        Assert.Equal(TaskbarEdge.Bottom, PopoverPlacement.Edge(Monitor, Monitor, new Point(3000, 2150)));
        Assert.Equal(TaskbarEdge.Right, PopoverPlacement.Edge(Monitor, Monitor, new Point(3830, 1000)));
    }

    [Fact]
    public void PopoverTallerThanTheWorkAreaStartsAtItsTop()
    {
        var position = PopoverPlacement.Place(new Int32Rect(3000, 2060, 80, 120), default, Monitor, BottomWork, new Size(700, 2500), Gap);

        Assert.Equal(20, position.Y);
    }
}
