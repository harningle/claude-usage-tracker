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

    /// <summary>Changes whenever the profile's links change; results of older generations are dropped.</summary>
    public long Generation { get; init; }

    /// <summary>Whether the profile has anything to poll: a usage source or Console billing.</summary>
    public bool IsLinked { get; init; }

    /// <summary>Where the profile's usage comes from, or null when only billing (or nothing) is set up.</summary>
    public UsageSourceKind? Source { get; init; }

    /// <summary>The last usage that belongs to the linked account, live or from the previous run's cache.</summary>
    public CachedUsage? Usage { get; init; }

    /// <summary>True until this run has fetched usage itself: the values came from the cache file.</summary>
    public bool UsageFromCache { get; init; }

    /// <summary>The state of the profile's login at the last check, or null before the first check.</summary>
    public LinkStatus? Link { get; init; }

    /// <summary>Who may renew the profile's login, for sources that have such a login.</summary>
    public CredentialOwnership? Ownership { get; init; }

    /// <summary>Something about the login worth telling although usage may still be shown.</summary>
    public LinkNotice? Notice { get; init; }

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

    /// <summary>The last Console billing of the linked organisation, live or from the previous run's cache.</summary>
    public CachedApiUsage? Api { get; init; }

    public bool ApiFromCache { get; init; }

    /// <summary>The last billing failure, or null after a success (or before any billing fetch).</summary>
    public FetchFailure? ApiFailure { get; init; }

    /// <summary>The history file could not be used or saved; nothing was overwritten.</summary>
    public string? HistoryError { get; init; }
}
