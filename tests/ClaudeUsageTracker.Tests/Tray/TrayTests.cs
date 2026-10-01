using ClaudeUsageTracker.App.Tray;

namespace ClaudeUsageTracker.Tests.Tray;

public class PopoverToggleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ClickOpensThenClosesWhenNoDeactivationIntervenes()
    {
        var toggle = new PopoverToggle(_time);

        Assert.Equal(PopoverToggleAction.Open, toggle.Activate());
        _time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(PopoverToggleAction.Close, toggle.Activate());
        Assert.False(toggle.IsOpen);
    }

    [Fact]
    public void ClickAfterDeactivationCloseDismissesOnceWithoutReopening()
    {
        var toggle = new PopoverToggle(_time);
        toggle.Activate();
        _time.Now += TimeSpan.FromSeconds(2);

        // Clicking the icon deactivates the popover first, then delivers the click.
        toggle.Deactivated();
        _time.Now += TimeSpan.FromMilliseconds(120);
        Assert.Equal(PopoverToggleAction.None, toggle.Activate());
        Assert.False(toggle.IsOpen);

        // The next click is a fresh request to open.
        _time.Now += TimeSpan.FromMilliseconds(100);
        Assert.Equal(PopoverToggleAction.Open, toggle.Activate());
    }

    [Fact]
    public void ClickLongAfterDeactivationOpens()
    {
        var toggle = new PopoverToggle(_time);
        toggle.Activate();
        _time.Now += TimeSpan.FromSeconds(2);
        toggle.Deactivated();

        _time.Now += PopoverToggle.DismissGrace + TimeSpan.FromMilliseconds(1);

        Assert.Equal(PopoverToggleAction.Open, toggle.Activate());
    }

    [Fact]
    public void RepeatedActivationStraightAfterOpeningIsIgnored()
    {
        var toggle = new PopoverToggle(_time);
        Assert.Equal(PopoverToggleAction.Open, toggle.Activate());

        _time.Now += TimeSpan.FromMilliseconds(30);

        Assert.Equal(PopoverToggleAction.None, toggle.Activate());
        Assert.True(toggle.IsOpen);
    }

    [Fact]
    public void ExplicitCloseClearsTheDismissGrace()
    {
        var toggle = new PopoverToggle(_time);
        toggle.Activate();
        _time.Now += TimeSpan.FromSeconds(1);
        toggle.Deactivated();
        toggle.Closed();

        Assert.Equal(PopoverToggleAction.Open, toggle.Activate());
    }

    [Fact]
    public void DeactivationWhileClosedChangesNothing()
    {
        var toggle = new PopoverToggle(_time);
        toggle.Deactivated();

        Assert.Equal(PopoverToggleAction.Open, toggle.Activate());
    }
}

public class TrayIdentityTests
{
    [Fact]
    public void GuidIsStableForTheSamePathAndSlot()
    {
        var slot = TrayIdentity.Slot(Guid.Parse("0b9c1d7e-1111-4222-8333-444455556666"), "session");

        Assert.Equal(
            TrayIdentity.For(@"C:\Apps\Tracker\ClaudeUsageTracker.App.exe", slot),
            TrayIdentity.For(@"c:\apps\tracker\ClaudeUsageTracker.App.exe", slot));
    }

    [Fact]
    public void GuidMatchesTheMilestoneOneDerivation()
    {
        // Pinned so a refactor cannot silently give every installed build a new tray identity.
        Assert.Equal(
            Guid.Parse("99fc2019-c831-5ae6-b972-635ff87c77fa"),
            TrayIdentity.For(@"C:\Apps\Tracker\ClaudeUsageTracker.App.exe", "profile-default|session"));
    }

    [Fact]
    public void GuidDiffersByPathAndSlot()
    {
        var a = TrayIdentity.For(@"C:\A\app.exe", "profile-1|session");

        Assert.NotEqual(a, TrayIdentity.For(@"C:\B\app.exe", "profile-1|session"));
        Assert.NotEqual(a, TrayIdentity.For(@"C:\A\app.exe", "profile-1|weekly"));
        Assert.Equal(5, a.Version);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(127, 127)]
    [InlineData(200, 127)]
    public void TooltipsAreCutToTheShellLimit(int length, int expected) =>
        Assert.Equal(expected, TrayIcon.Truncate(new string('x', length)).Length);

    [Fact]
    public void TooltipTruncationKeepsSurrogatePairsWhole()
    {
        var text = new string('x', 126) + "😀" + "tail";

        var truncated = TrayIcon.Truncate(text);

        Assert.Equal(126, truncated.Length);
        Assert.False(char.IsHighSurrogate(truncated[^1]));
    }
}
