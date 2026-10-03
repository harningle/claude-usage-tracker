namespace ClaudeUsageTracker.Core.Statusline;

/// <summary>Where a value on the status line came from.</summary>
public enum StatuslineSource
{
    /// <summary>The tracker's cache for this folder, written within twice its polling interval.</summary>
    Cache,

    /// <summary>Claude Code's own <c>rate_limits</c> for this session.</summary>
    Stdin,

    /// <summary>The tracker's cache for this folder, older than that; shown labelled as stale.</summary>
    StaleCache,
}

public sealed record StatuslineValue(double Percentage, DateTimeOffset? ResetsAt, StatuslineSource Source);

public sealed record StatuslineCost(decimal Used, decimal Limit, string Currency);

/// <summary>
/// The usage a status line invocation shows (plan.md section 2, "Fast, secret-free statusline"). Each window
/// is taken from the folder's cache while that is fresh, else from this session's stdin, else from the
/// stale cache with a label. A value nobody reported stays missing; it is never 0.
/// </summary>
public sealed record StatuslineUsage(StatuslineValue? Session, StatuslineValue? Weekly, StatuslineCost? Cost, string? ProfileName)
{
    /// <summary>A cache written a little in the future (a clock correction) still counts; one far ahead does not.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <param name="cache">The cache file found for the session's folder, or null.</param>
    /// <param name="sessionConfigDirectory">The folder the Claude Code session uses.</param>
    public static StatuslineUsage Resolve(StatuslineCache? cache, string sessionConfigDirectory, StatuslineInput input, DateTimeOffset now)
    {
        // Another folder's values, or a format this helper does not know, are never shown.
        if (cache is not null
            && (cache.Version > StatuslineCache.CurrentVersion || !StatuslinePaths.SameDirectory(cache.ConfigDirectory, sessionConfigDirectory)))
        {
            cache = null;
        }

        var age = cache?.ObservedAt is { } observed ? now - observed : (TimeSpan?)null;
        var fresh = age is { } a && a >= -ClockTolerance && a <= TimeSpan.FromSeconds(2 * Math.Max(0, cache!.RefreshIntervalSeconds));

        StatuslineValue? Pick(double? cached, DateTimeOffset? cachedReset, StatuslineWindow? reported, bool zeroAfterReset)
        {
            var resetPassed = cachedReset is { } reset && reset <= now;
            if (fresh && cached is { } value)
            {
                // Upstream's effective session percentage: 0 once the window's known reset time has passed.
                return zeroAfterReset && resetPassed
                    ? new StatuslineValue(0, null, StatuslineSource.Cache)
                    : new StatuslineValue(value, cachedReset, StatuslineSource.Cache);
            }

            if (reported is not null)
            {
                return new StatuslineValue(reported.UsedPercentage, reported.ResetsAt, StatuslineSource.Stdin);
            }

            // A stale value of a window that has since reset says nothing about the current one.
            return age is not null && cached is { } stale && !resetPassed
                ? new StatuslineValue(stale, cachedReset, StatuslineSource.StaleCache)
                : null;
        }

        var cost = fresh && cache is { CostUsed: { } used, CostLimit: { } limit and > 0, CostCurrency: { Length: > 0 } currency }
            ? new StatuslineCost(used, limit, currency)
            : null;
        return new StatuslineUsage(
            Pick(cache?.SessionPercentage, cache?.SessionResetsAt, input.FiveHour, zeroAfterReset: true),
            Pick(cache?.WeeklyPercentage, cache?.WeeklyResetsAt, input.SevenDay, zeroAfterReset: false),
            cost,
            string.IsNullOrEmpty(cache?.ProfileName) ? null : cache.ProfileName);
    }
}
