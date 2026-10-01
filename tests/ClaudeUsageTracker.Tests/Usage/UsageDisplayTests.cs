using System.Globalization;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>
/// Characterises upstream <c>PaceStatus</c>, <c>effectiveSessionPercentage</c> and the popover's text
/// helpers, which upstream did not test. Vectors follow the upstream source.
/// </summary>
public class UsageDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(0, 0.50, PaceStatus.Comfortable)]
    [InlineData(20, 0.50, PaceStatus.Comfortable)]
    [InlineData(30, 0.50, PaceStatus.OnTrack)]
    [InlineData(40, 0.50, PaceStatus.Warming)]
    [InlineData(47.5, 0.50, PaceStatus.Pressing)]
    [InlineData(55, 0.50, PaceStatus.Critical)]
    [InlineData(50, 0.10, PaceStatus.Runaway)]
    [InlineData(39, 0.175, PaceStatus.Runaway)]
    [InlineData(25, 0.50, PaceStatus.OnTrack)]
    [InlineData(37.5, 0.50, PaceStatus.Warming)]
    [InlineData(45, 0.50, PaceStatus.Pressing)]
    [InlineData(50, 0.50, PaceStatus.Critical)]
    [InlineData(60, 0.50, PaceStatus.Runaway)]
    public void PaceTiers(double used, double elapsed, PaceStatus expected) =>
        Assert.Equal(expected, UsageDisplay.Pace(used, elapsed));

    [Theory]
    [InlineData(0.02)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void NoPaceOutsideThePeriod(double elapsed) => Assert.Null(UsageDisplay.Pace(50, elapsed));

    [Fact]
    public void EffectiveSessionIsZeroOnlyAfterAKnownReset()
    {
        Assert.Equal(0.0, UsageDisplay.EffectiveSessionPercentage(
            new UsageData { SessionPercentage = 42, SessionResetTime = Now.AddMinutes(-1) }, Now));
        Assert.Equal(42.0, UsageDisplay.EffectiveSessionPercentage(
            new UsageData { SessionPercentage = 42, SessionResetTime = Now.AddMinutes(1) }, Now));
        Assert.Equal(42.0, UsageDisplay.EffectiveSessionPercentage(new UsageData { SessionPercentage = 42 }, Now));
        // Missing data never becomes 0 %.
        Assert.Null(UsageDisplay.EffectiveSessionPercentage(new UsageData { SessionResetTime = Now.AddMinutes(-1) }, Now));
    }

    [Theory]
    [InlineData(39.9, "39%")]
    [InlineData(100.4, "100%")]
    [InlineData(105, "105%")]
    [InlineData(0, "0%")]
    [InlineData(-0.5, "0%")]
    public void PercentTextTruncates(double value, string expected) => Assert.Equal(expected, UsageDisplay.PercentText(value));

    [Theory]
    [InlineData(TimeFormatPreference.TwelveHour, "en-GB", false)]
    [InlineData(TimeFormatPreference.TwentyFourHour, "en-US", true)]
    [InlineData(TimeFormatPreference.System, "en-US", false)]
    [InlineData(TimeFormatPreference.System, "de-DE", true)]
    public void TwentyFourHourPreference(TimeFormatPreference preference, string culture, bool expected) =>
        Assert.Equal(expected, UsageDisplay.Uses24HourTime(preference, CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData(2026, 10, 1, 15, 59, true, "Today 15:59")]
    [InlineData(2026, 10, 1, 15, 59, false, "Today 3:59PM")]
    [InlineData(2026, 10, 2, 7, 0, true, "Tomorrow 07:00")]
    [InlineData(2026, 10, 14, 2, 0, true, "Oct 14, 02:00")]
    public void AbsoluteResetTime(int year, int month, int day, int hour, int minute, bool use24Hour, string expected)
    {
        var reset = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        Assert.Equal(expected, UsageDisplay.AbsoluteResetTime(
            reset, Now, TimeZoneInfo.Utc, use24Hour, English, "Today", "Tomorrow"));
    }

    [Fact]
    public void AbsoluteResetTimeUsesTheGivenZoneForTheDay()
    {
        // 23:30 UTC is already tomorrow in UTC+2.
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var reset = new DateTimeOffset(2026, 10, 1, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal("Tomorrow 01:30", UsageDisplay.AbsoluteResetTime(reset, Now, zone, true, English, "Today", "Tomorrow"));
    }

    [Theory]
    [InlineData(-60, "→<1H")]
    [InlineData(59 * 60, "→<1H")]
    [InlineData(3600, "→1H")]
    [InlineData(3601, "→2H")]
    [InlineData(4 * 3600 + 8 * 60, "→5H")]
    public void RemainingHoursRoundsUp(int seconds, string expected) =>
        Assert.Equal(expected, UsageDisplay.RemainingHoursText(Now.AddSeconds(seconds), Now));
}
