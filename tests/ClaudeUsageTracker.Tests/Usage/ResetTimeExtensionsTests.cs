using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>
/// Port of upstream <c>DateExtensionsTests</c>. Upstream used the machine's time zone; these use fixed
/// zones so results do not depend on the test machine.
/// </summary>
public class ResetTimeExtensionsTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // Next Monday

    [Fact]
    public void NextMondayFromSunday()
    {
        var nextMonday = InLondon(LondonTime(2024, 12, 15, 10).NextMonday1259Pm(London));

        Assert.Equal(DayOfWeek.Monday, nextMonday.DayOfWeek);
        Assert.Equal(12, nextMonday.Hour);
        Assert.Equal(59, nextMonday.Minute);
    }

    [Fact]
    public void NextMondayFromMonday()
    {
        var nextMonday = InLondon(LondonTime(2024, 12, 16, 10).NextMonday1259Pm(London));

        Assert.Equal(DayOfWeek.Monday, nextMonday.DayOfWeek);
        Assert.Equal(23, nextMonday.Day);
    }

    [Fact]
    public void NextMondayFromWednesday()
    {
        var nextMonday = InLondon(LondonTime(2024, 12, 18, 10).NextMonday1259Pm(London));

        Assert.Equal(DayOfWeek.Monday, nextMonday.DayOfWeek);
        Assert.Equal(23, nextMonday.Day);
    }

    [Fact]
    public void NextMondayAcrossDaylightSavingStart()
    {
        // UK clocks go forward on Sunday 31 March 2024: 12:59 BST on Monday is 11:59 UTC.
        var nextMonday = LondonTime(2024, 3, 30, 10).NextMonday1259Pm(London);

        Assert.Equal(new DateTimeOffset(2024, 4, 1, 11, 59, 0, TimeSpan.Zero), nextMonday);
        Assert.Equal(TimeSpan.FromHours(1), nextMonday.Offset);
    }

    [Fact]
    public void NextMondayUsesTheGivenTimeZone()
    {
        // Sunday evening in UTC is already Monday morning in Tokyo, so the next Monday is a week on.
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var nextMonday = new DateTimeOffset(2024, 12, 15, 20, 0, 0, TimeSpan.Zero).NextMonday1259Pm(tokyo);

        Assert.Equal(new DateTimeOffset(2024, 12, 23, 12, 59, 0, TimeSpan.FromHours(9)), nextMonday);
    }

    // Time remaining

    [Theory]
    [InlineData(3 * 3600 + 45 * 60, "3h 45m")] // TimeRemainingHoursAndMinutes
    [InlineData(2 * 3600, "2h")] // TimeRemainingHoursOnly
    [InlineData(30 * 60, "30m")] // TimeRemainingMinutesOnly
    [InlineData(3 * 24 * 3600, "3 days")] // TimeRemainingDays
    [InlineData(24 * 3600, "1 day")] // TimeRemainingOneDay
    [InlineData(-3600, "Reset now")] // TimeRemainingPast
    [InlineData(30, "< 1m")] // TimeRemainingLessThanMinute
    [InlineData(26 * 3600, "1d 2h")] // days with hours (no upstream case)
    public void TimeRemainingString(int secondsFromNow, string expected) =>
        Assert.Equal(expected, Now.AddSeconds(secondsFromNow).TimeRemainingString(Now));

    private static DateTimeOffset LondonTime(int year, int month, int day, int hour)
    {
        var local = new DateTime(year, month, day, hour, 0, 0);
        return new DateTimeOffset(local, London.GetUtcOffset(local));
    }

    private static DateTimeOffset InLondon(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, London);
}
