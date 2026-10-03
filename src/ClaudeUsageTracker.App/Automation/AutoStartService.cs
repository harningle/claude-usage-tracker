using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Notifications;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Automation;

/// <summary>
/// Opt-in session auto-start (upstream <c>AutoStartSessionService</c>): every five minutes, and after a wake, each
/// profile whose user switched it on is checked, and when its session window has not started, one short message is
/// sent through its claude.ai session. This is the app's only model request. It is never part of usage polling,
/// it runs only for opted-in profiles with a claude.ai sign-in, and it decides only on usage fetched within the
/// last check interval by this run, never on cached values. After an attempt it waits: until the new window resets
/// (or one session length) after a start, <see cref="SessionAutoStart.FailureHold"/> after a failure. The wait is
/// kept on disk, so a restart does not send again early. Use it from the UI thread only.
/// </summary>
public sealed class AutoStartService(
    AppSettings settings, ClaudeAiClient client, AutomationService automation, TimeProvider time,
    Func<Guid, ProfileUsageState?> current, Action<Guid> releaseBrowser)
{
    // For profiles that are not polled: the wait the server asked for after a failed usage read.
    private readonly Dictionary<Guid, DateTimeOffset> _retryNotBefore = [];
    private bool _running;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    /// <summary>Upstream skips a check that comes within ten seconds of the previous one (a wake right after the timer).</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    /// <summary>Checks every opted-in profile once. Returns when all checks have finished.</summary>
    public async Task CheckAllAsync(CancellationToken cancellation)
    {
        var now = time.GetUtcNow();
        if (_running || now - _lastCheck < Debounce)
        {
            return;
        }

        _running = true;
        _lastCheck = now;
        try
        {
            foreach (var profile in settings.Profiles.Where(IsEligible).ToList())
            {
                // An earlier profile's requests give the user time to switch this one off, sign it out or delete it.
                if (IsWanted(profile))
                {
                    await CheckAsync(profile, cancellation).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Upstream's gates: the switch, the provider's capability and a claude.ai sign-in.</summary>
    public static bool IsEligible(ProfileSettings profile) =>
        profile.AutoStartSessionEnabled && profile.ClaudeAi is not null && profile.ProviderKind.Descriptor().Capabilities.AutoStartSession;

    private bool IsWanted(ProfileSettings profile) => settings.Profiles.Contains(profile) && IsEligible(profile);

    private async Task CheckAsync(ProfileSettings profile, CancellationToken cancellation)
    {
        var memory = automation.StateOf(profile.Id);
        var now = time.GetUtcNow();
        if (memory.AutoStartHoldUntil is { } hold && now < hold)
        {
            return;
        }

        try
        {
            await CheckSessionAsync(profile, memory, cancellation).ConfigureAwait(true);
        }
        finally
        {
            // A profile that is not polled has no browser to keep once its check is over.
            if (current(profile.Id) is null)
            {
                releaseBrowser(profile.Id);
            }
        }
    }

    private async Task CheckSessionAsync(ProfileSettings profile, ProfileAutomationState memory, CancellationToken cancellation)
    {
        var organization = profile.ClaudeAi!.OrganizationId;
        // While a request is out the user may switch auto-start off, sign out or delete the profile.
        bool StillWanted() => IsWanted(profile) && profile.ClaudeAi?.OrganizationId == organization;

        if (await FreshUsageAsync(profile, organization, cancellation).ConfigureAwait(true) is not { } usage
            || !SessionAutoStart.ShouldStart(usage, time.GetUtcNow())
            || !StillWanted())
        {
            return;
        }

        if (await client.StartSessionAsync(profile.Id, organization, StillWanted, cancellation).ConfigureAwait(true) is not { } result)
        {
            // Stopped before the message; nothing was started or failed.
            return;
        }

        var now = time.GetUtcNow();
        if (result.Started)
        {
            memory.AutoStartHoldUntil = result.ResetAt is { } reset && reset > now ? reset : now + SessionAutoStart.SessionLength;
            memory.AutoStartFailureNotified = false;
            automation.AutoStartNotification(profile, null);
        }
        else
        {
            memory.AutoStartHoldUntil = now + SessionAutoStart.FailureHold;
            if (!memory.AutoStartFailureNotified)
            {
                memory.AutoStartFailureNotified = true;
                automation.AutoStartNotification(profile, result.Failure);
            }
        }

        automation.Save();
    }

    /// <summary>
    /// The profile's usage as fetched by this run within the last check interval: its polling loop's result when the
    /// profile is polled (only if that loop's latest check succeeded), otherwise one read-only request now. Null when
    /// there is none, or the server asked for no requests yet.
    /// </summary>
    private async Task<UsageData?> FreshUsageAsync(ProfileSettings profile, string organization, CancellationToken cancellation)
    {
        var now = time.GetUtcNow();
        var state = current(profile.Id);
        if (state?.RetryNotBefore is { } notBefore && now < notBefore)
        {
            return null;
        }

        if (state is not null)
        {
            // A polled profile is never fetched beside its polling loop, so no two requests of it overlap; without a
            // fresh result of that loop this round is skipped. A check that failed, broke or sent nothing after the last
            // success (its attempt is later) leaves that success's values stale.
            return state is { Source: UsageSourceKind.ClaudeAi, UsageFromCache: false, LastSuccessAt: { } at, Usage: { } cached }
                   && state.LastAttemptAt <= at && now - at <= SessionAutoStart.CheckInterval
                ? cached.Usage
                : null;
        }

        if (_retryNotBefore.TryGetValue(profile.Id, out var wait) && now < wait)
        {
            return null;
        }

        var fetched = await client.FetchUsageAsync(profile.Id, organization, cancellation).ConfigureAwait(true);
        if (fetched is FetchFailed failed
            && (failed.RetryAfter ?? (failed.Failure == FetchFailure.RateLimited ? RefreshPolicy.DefaultRateLimitWait : null)) is { } asked)
        {
            _retryNotBefore[profile.Id] = failed.At + asked;
        }
        else
        {
            _retryNotBefore.Remove(profile.Id);
        }

        return fetched is FetchSucceeded success ? success.Usage : null;
    }
}
