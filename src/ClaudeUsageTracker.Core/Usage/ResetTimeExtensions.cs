namespace ClaudeUsageTracker.Core.Usage;

/// <summary>Reset-time calculations. Port of the tested parts of upstream <c>Date+Extensions</c>.</summary>
public static class ResetTimeExtensions
{
    /// <summary>The next Monday at 12:59 in <paramref name="timeZone"/>; a Monday moves a full week on.</summary>
    public static DateTimeOffset NextMonday1259Pm(this DateTimeOffset from, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(from, timeZone);
        var daysUntilMonday = local.DayOfWeek == DayOfWeek.Monday
            ? 7
            : ((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7;

        var wallClock = DateOnly.FromDateTime(local.DateTime)
            .AddDays(daysUntilMonday)
            .ToDateTime(new TimeOnly(12, 59));
        return new DateTimeOffset(wallClock, timeZone.GetUtcOffset(wallClock));
    }

    /// <summary>Time remaining until <paramref name="target"/>, e.g. "3h 45m", "2 days" or "Reset now".</summary>
    public static string TimeRemainingString(this DateTimeOffset target, DateTimeOffset now)
    {
        var interval = (target - now).TotalSeconds;
        if (interval < 0)
        {
            return "Reset now";
        }

        var seconds = (long)interval;
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var days = hours / 24;

        if (days > 0)
        {
            var remainingHours = hours % 24;
            if (remainingHours > 0)
            {
                return $"{days}d {remainingHours}h";
            }

            return days == 1 ? "1 day" : $"{days} days";
        }

        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        return minutes > 0 ? $"{minutes}m" : "< 1m";
    }
}
