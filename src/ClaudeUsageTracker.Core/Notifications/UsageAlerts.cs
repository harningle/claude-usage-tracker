using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Core.Notifications;

/// <summary>Port of upstream <c>NotificationManager.AlertType</c>'s session alerts.</summary>
public enum UsageAlertKind
{
    /// <summary>A threshold below 90 % (upstream <c>sessionInfo</c>, the 75 % alert).</summary>
    SessionInfo,

    /// <summary>A threshold from 90 % to below 95 % (upstream <c>sessionWarning</c>).</summary>
    SessionWarning,

    /// <summary>A threshold of 95 % or more (upstream <c>sessionCritical</c>).</summary>
    SessionCritical,

    /// <summary>The session went from some use back to 0 % (upstream <c>sessionReset</c>).</summary>
    SessionReset,
}

/// <summary>The threshold part of upstream <c>NotificationSettings</c>.</summary>
public sealed record AlertThresholds(bool Enabled, bool Threshold75, bool Threshold90, bool Threshold95, IReadOnlyList<int> Custom)
{
    /// <summary>Upstream <c>sortedThresholds</c>: the enabled built-in thresholds and the custom ones, distinct and ascending.</summary>
    public IReadOnlyList<int> Sorted =>
    [
        .. new[] { (Threshold75, 75), (Threshold90, 90), (Threshold95, 95) }.Where(t => t.Item1).Select(t => t.Item2)
            .Concat(Custom)
            .Distinct()
            .Order(),
    ];

    /// <summary>
    /// Upstream <c>CustomThresholdsEditor.addThreshold</c>: a whole percentage from 1 to 100, not already listed and
    /// not one of the built-in 75, 90 and 95.
    /// </summary>
    public static bool IsValidCustom(int value, IEnumerable<int> existing) =>
        value is > 0 and <= 100 and not (75 or 90 or 95) && !existing.Contains(value);
}

/// <summary>
/// What has been notified for one profile's session window. It is kept on disk, so a restart in the middle of a
/// window does not repeat an alert; a new window starts with nothing notified.
/// </summary>
/// <param name="WindowResetAt">The reset time of the window the levels belong to, when one was reported.</param>
public sealed record SessionAlertState(DateTimeOffset? WindowResetAt, IReadOnlyList<int> NotifiedLevels)
{
    public static readonly SessionAlertState Empty = new(null, []);
}

/// <param name="Level">The threshold crossed, or 0 for a reset.</param>
/// <param name="Percentage">The session use the alert reports.</param>
public sealed record UsageAlert(UsageAlertKind Kind, int Level, double Percentage, DateTimeOffset? ResetAt);

/// <param name="Effective">The effective session percentage of this observation, or null when it was not reported.</param>
public sealed record AlertEvaluation(IReadOnlyList<UsageAlert> Alerts, SessionAlertState State, double? Effective);

/// <summary>
/// Upstream <c>NotificationManager.checkAndNotify(usage:profileName:settings:)</c>: a session reset alert when the
/// session goes from some use to 0 %, and an alert for the highest threshold the session has reached, each once.
/// Differences: deduplication is per session window and survives a restart (upstream cleared its persisted list
/// only on a reset it saw itself, so after a restart across a reset a threshold was never notified again); a
/// session percentage that was not reported causes nothing; and the reset alert needs a previous observation from
/// the same run, as upstream's in-memory comparison did, so a reset that happened while the app was closed is not
/// announced as news.
/// </summary>
public static class UsageAlerts
{
    /// <summary>A reset time that moves by at least this much is another window (as history's reset detection).</summary>
    private static readonly TimeSpan WindowTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Upstream <c>effectiveSessionPercentage</c>: the reported percentage, or 0 once the reported reset time has
    /// passed. Null when no percentage was reported.
    /// </summary>
    public static double? EffectiveSessionPercentage(UsageData usage, DateTimeOffset now) =>
        usage.SessionPercentage is not { } percentage ? null
        : usage.SessionResetTime is { } reset && reset < now ? 0
        : percentage;

    /// <param name="state">What was notified for the profile so far; null when nothing was.</param>
    /// <param name="previousEffective">The effective percentage of the profile's previous fresh observation in this run, if any.</param>
    /// <param name="usage">A fresh observation; cached or stale data must never be evaluated.</param>
    public static AlertEvaluation Evaluate(
        AlertThresholds thresholds, SessionAlertState? state, double? previousEffective, UsageData usage, DateTimeOffset now)
    {
        state ??= SessionAlertState.Empty;
        if (EffectiveSessionPercentage(usage, now) is not { } effective)
        {
            return new AlertEvaluation([], state, null);
        }

        var openWindow = usage.SessionResetTime is { } reset && reset >= now ? reset : (DateTimeOffset?)null;
        var newWindow = effective == 0
                        || (openWindow is { } current && state.WindowResetAt is { } known && (current - known).Duration() >= WindowTolerance);
        var levels = newWindow ? [] : state.NotifiedLevels.ToList();
        var windowResetAt = openWindow ?? (newWindow ? null : state.WindowResetAt);

        var alerts = new List<UsageAlert>();
        if (thresholds.Enabled)
        {
            if (previousEffective > 0 && effective == 0)
            {
                alerts.Add(new UsageAlert(UsageAlertKind.SessionReset, 0, effective, usage.SessionResetTime));
            }

            // Upstream looks only at the highest threshold reached, so a jump past several sends one alert.
            var highest = thresholds.Sorted.Where(t => effective >= t).DefaultIfEmpty(0).Max();
            if (highest > 0 && !levels.Contains(highest))
            {
                var kind = highest >= 95 ? UsageAlertKind.SessionCritical
                    : highest >= 90 ? UsageAlertKind.SessionWarning
                    : UsageAlertKind.SessionInfo;
                alerts.Add(new UsageAlert(kind, highest, effective, openWindow));
                levels.Add(highest);
            }
        }

        return new AlertEvaluation(alerts, new SessionAlertState(windowResetAt, levels), effective);
    }
}

/// <summary>Upstream <c>MenuBarManager.findNextAvailableProfile</c>.</summary>
public static class AutoSwitch
{
    /// <summary>Upstream switches when the active profile's effective session use reaches 100 %.</summary>
    public const double Limit = 100;

    /// <summary>
    /// The next profile after <paramref name="currentIndex"/>, wrapping round, that has usage credentials and whose last
    /// known session is below the limit. A profile with no known usage counts as available, as upstream; one whose
    /// last known session is at the limit counts as available again once that window's reset time has passed.
    /// </summary>
    /// <returns>The index of the profile, or null when none is available.</returns>
    public static int? FindNext<T>(
        IReadOnlyList<T> profiles, int currentIndex, Func<T, bool> hasCredentials, Func<T, UsageData?> lastKnown, DateTimeOffset now)
    {
        for (var offset = 1; offset < profiles.Count; offset++)
        {
            var index = (currentIndex + offset) % profiles.Count;
            var candidate = profiles[index];
            if (!hasCredentials(candidate))
            {
                continue;
            }

            if (lastKnown(candidate) is not { } usage
                || UsageAlerts.EffectiveSessionPercentage(usage, now) is not { } effective
                || effective < Limit)
            {
                return index;
            }
        }

        return null;
    }
}
