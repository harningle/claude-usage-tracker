namespace ClaudeUsageTracker.Core.Usage;

/// <summary>Usage status level for colour coding.</summary>
public enum UsageStatusLevel
{
    Safe,
    Moderate,
    Critical,
}

/// <summary>
/// Status levels and display percentages, with optional pace-aware colouring.
/// Port of upstream <c>UsageStatusCalculator</c>.
/// </summary>
public static class UsageStatusCalculator
{
    /// <param name="usedPercentage">Percentage used (0-100).</param>
    /// <param name="showRemaining">Use remaining-based rather than used-based thresholds.</param>
    /// <param name="elapsedFraction">Fraction (0-1) of the period elapsed; enables pace-aware colouring.</param>
    public static UsageStatusLevel CalculateStatus(
        double usedPercentage, bool showRemaining, double? elapsedFraction = null)
    {
        // Pace-aware: project end-of-period usage once enough of the period has elapsed.
        var used = usedPercentage / 100.0;
        if (elapsedFraction is { } elapsed && elapsed >= 0.15 && elapsed < 1.0 && used > 0)
        {
            return (used / elapsed) switch
            {
                < 0.70 => UsageStatusLevel.Safe,
                < 0.90 => UsageStatusLevel.Moderate,
                _ => UsageStatusLevel.Critical,
            };
        }

        if (showRemaining)
        {
            return Math.Max(0, 100 - usedPercentage) switch
            {
                >= 30 => UsageStatusLevel.Safe,
                >= 10 => UsageStatusLevel.Moderate,
                _ => UsageStatusLevel.Critical,
            };
        }

        return usedPercentage switch
        {
            >= 0 and < 70 => UsageStatusLevel.Safe,
            >= 70 and < 90 => UsageStatusLevel.Moderate,
            _ => UsageStatusLevel.Critical,
        };
    }

    /// <summary>
    /// Fraction (0-1) of the period elapsed, inverted when showing remaining usage.
    /// Returns null when the reset time is unknown or the duration is not positive.
    /// </summary>
    public static double? ElapsedFraction(
        DateTimeOffset? resetTime, TimeSpan duration, bool showRemaining, DateTimeOffset now)
    {
        if (resetTime is not { } reset || duration <= TimeSpan.Zero)
        {
            return null;
        }

        if (reset <= now)
        {
            return showRemaining ? 0.0 : 1.0;
        }

        var fraction = Math.Clamp((duration - (reset - now)) / duration, 0, 1);
        return showRemaining ? 1.0 - fraction : fraction;
    }

    public static double GetDisplayPercentage(double usedPercentage, bool showRemaining) =>
        showRemaining ? Math.Max(0, 100 - usedPercentage) : usedPercentage;
}
