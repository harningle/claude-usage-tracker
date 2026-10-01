using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Refresh;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>
/// Everything the popover and tray show for one profile: the last good usage with its observation time,
/// why it may be stale, and the last failure. Missing, stale and failed are kept distinct
/// (AGENTS.md); nothing here is fabricated to fill a gap.
/// </summary>
public sealed record ProfileUsageState
{
    public required Guid ProfileId { get; init; }

    /// <summary>Changes whenever the profile's link changes; results of older generations are dropped.</summary>
    public long Generation { get; init; }

    public bool IsLinked { get; init; }

    /// <summary>The last usage that belongs to the linked account, live or from the previous run's cache.</summary>
    public CachedUsage? Usage { get; init; }

    /// <summary>True until this run has fetched usage itself: the values came from the cache file.</summary>
    public bool UsageFromCache { get; init; }

    /// <summary>The linked Claude Code login's state at the last check, or null before the first check.</summary>
    public LinkStatus? Link { get; init; }

    /// <summary>The last fetch failure, or null after a success (or before any fetch).</summary>
    public FetchFailure? LastFailure { get; init; }

    public int? LastFailureStatus { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>The server asked for no requests before this time.</summary>
    public DateTimeOffset? RetryNotBefore { get; init; }

    public DateTimeOffset? NextCheckAt { get; init; }

    public bool IsRefreshing { get; init; }

    /// <summary>The app's encrypted copy of the login could not be read or updated; polling continues.</summary>
    public string? MirrorError { get; init; }

    /// <summary>An unexpected failure of the last check (exception type and message, never request data).</summary>
    public string? InternalError { get; init; }
}
