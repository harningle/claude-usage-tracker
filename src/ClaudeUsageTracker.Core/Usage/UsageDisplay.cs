using System.Globalization;

namespace ClaudeUsageTracker.Core.Usage;

/// <summary>Projected end-of-period usage tier for the time marker. Port of upstream <c>PaceStatus</c>.</summary>
public enum PaceStatus
{
    Comfortable,
    OnTrack,
    Warming,
    Pressing,
    Critical,
    Runaway,
}

/// <summary>How the popover phrases reset times. Persisted as upstream's raw values.</summary>
public enum PopoverTimeDisplay
{
    ResetTime,
    RemainingTime,
    Both,
}

public enum TimeFormatPreference
{
    System,
    TwelveHour,
    TwentyFourHour,
}

/// <summary>Popover and tray presentation rules shared by every view. Ports of upstream helpers.</summary>
public static class UsageDisplay
{
    public static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Pace tier, or null outside 3-100 % of the period. Port of upstream <c>PaceStatus.calculate</c>.
    /// </summary>
    public static PaceStatus? Pace(double usedPercentage, double elapsedFraction)
    {
        if (elapsedFraction is < 0.03 or >= 1.0 || double.IsNaN(elapsedFraction))
        {
            return null;
        }

        if (usedPercentage <= 0)
        {
            return PaceStatus.Comfortable;
        }

        return (usedPercentage / 100.0 / elapsedFraction) switch
        {
            < 0.50 => PaceStatus.Comfortable,
            < 0.75 => PaceStatus.OnTrack,
            < 0.90 => PaceStatus.Warming,
            < 1.00 => PaceStatus.Pressing,
            < 1.20 => PaceStatus.Critical,
            _ => PaceStatus.Runaway,
        };
    }

    /// <summary>
    /// The session percentage to display: 0 once a known reset time has passed (upstream
    /// <c>effectiveSessionPercentage</c>). A missing value stays missing.
    /// </summary>
    public static double? EffectiveSessionPercentage(UsageData usage, DateTimeOffset now) =>
        usage.SessionResetTime is { } reset && reset < now && usage.SessionPercentage is not null
            ? 0.0
            : usage.SessionPercentage;

    /// <summary>Upstream's <c>"\(Int(value))%"</c>: truncation toward zero, no clamp.</summary>
    public static string PercentText(double displayPercentage) =>
        // Adding 0.0 turns the -0 from truncating (-1, 0) into 0, as Swift's Int() does.
        (Math.Truncate(displayPercentage) + 0.0).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Whether times use a 24-hour clock under the preference and the user's regional format.</summary>
    public static bool Uses24HourTime(TimeFormatPreference preference, CultureInfo culture) => preference switch
    {
        TimeFormatPreference.TwelveHour => false,
        TimeFormatPreference.TwentyFourHour => true,
        _ => !culture.DateTimeFormat.ShortTimePattern.Contains('t', StringComparison.Ordinal),
    };

    /// <summary>
    /// Upstream <c>Date.resetTimeString</c>: "&lt;today&gt; 07:00", "&lt;tomorrow&gt; 3:59PM" or "Mar 14, 02:00",
    /// in <paramref name="zone"/>.
    /// </summary>
    public static string AbsoluteResetTime(
        DateTimeOffset reset, DateTimeOffset now, TimeZoneInfo zone, bool use24Hour, CultureInfo culture,
        string todayLabel, string tomorrowLabel)
    {
        var localReset = TimeZoneInfo.ConvertTime(reset, zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var time = localReset.ToString(use24Hour ? "HH:mm" : "h:mmtt", culture);
        var resetDay = DateOnly.FromDateTime(localReset.DateTime);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        if (resetDay == today)
        {
            return $"{todayLabel} {time}";
        }

        if (resetDay == today.AddDays(1))
        {
            return $"{tomorrowLabel} {time}";
        }

        return localReset.ToString("MMM d", culture) + ", " + time;
    }

    /// <summary>Upstream <c>Date.timeRemainingHoursString</c> for the tray: "→3H", or "→&lt;1H" under an hour.</summary>
    public static string RemainingHoursText(DateTimeOffset reset, DateTimeOffset now)
    {
        var remaining = (reset - now).TotalSeconds;
        return remaining < 3600
            ? "→<1H"
            : "→" + Math.Ceiling(remaining / 3600).ToString("0", CultureInfo.InvariantCulture) + "H";
    }
}
