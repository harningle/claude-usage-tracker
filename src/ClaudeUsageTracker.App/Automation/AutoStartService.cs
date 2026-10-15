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
/// sent through its claude.ai session, or for a Codex profile through the Codex CLI (not an upstream feature). This is
/// the app's only model request. It is never part of usage polling, it runs only for opted-in profiles with a login it
/// can send through, and it decides only on usage fetched within the last check interval by this run, never on cached
/// values. After an attempt it waits: until the new window resets (or one session length) after a start,
/// <see cref="SessionAutoStart.FailureHold"/> after a failure. The wait is kept on disk, so a restart does not send
/// again early. A polled profile's wait after a start ends early if its new window, once seen open, ends early
/// (<see cref="StartedWindowSeen"/>). Use it from the UI thread only.
/// </summary>
/// <param name="codexUsage">Reads the usage of a Codex profile that is not polled.</param>
/// <param name="checkNow">Asks a profile's polling loop for a check now and gives its state once that check is done (<see cref="RefreshCoordinator.CheckNowAsync"/>).</param>
public sealed class AutoStartService(
    AppSettings settings, ClaudeAiClient client, IUsageSource codexUsage, CodexSessionStarter codex, AutomationService automation,
    TimeProvider time, Func<Guid, ProfileUsageState?> current, Func<Guid, CancellationToken, Task<ProfileUsageState?>> checkNow,
    Action<Guid> releaseBrowser)
{
    // For profiles that are not polled: the wait the server asked for after a failed usage read.
    private readonly Dictionary<Guid, DateTimeOffset> _retryNotBefore = [];

    // The latest fresh usage of each Codex profile in this run, which shows whether the reset time moves with the clock.
    private readonly Dictionary<Guid, (UsageData Usage, DateTimeOffset At)> _codexReads = [];

    // Profiles whose window from the latest start this run has seen open (StartedWindowSeen).
    private readonly HashSet<Guid> _startedWindowSeen = [];
    private bool _running;

    /// <summary>How the wait before a Codex confirmation read passes; tests move their clock on instead.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = (delay, cancellation) => Task.Delay(delay, time, cancellation);
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    /// <summary>Upstream skips a check that comes within ten seconds of the previous one (a wake right after the timer).</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How close a read's reset time must be to the end of the wait after a start to show the window that start opened.
    /// Codex fixes the reset time during the CLI's run, which may take two minutes and ends just before the wait is set.
    /// </summary>
    internal static readonly TimeSpan StartedWindowTolerance = TimeSpan.FromMinutes(2);

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

    /// <summary>Upstream's gates: the switch, the provider's capability and a login to send through.</summary>
    public static bool IsEligible(ProfileSettings profile) =>
        profile.AutoStartSessionEnabled && CanSend(profile) && profile.ProviderKind.Descriptor().Capabilities.AutoStartSession;

    /// <summary>
    /// Whether the profile has a login the message can be sent through: a claude.ai sign-in, or for Codex the Codex CLI's
    /// login. Pasted Codex credentials do not count: the CLI sends with its own login, which such a profile does not follow,
    /// and running it with the pasted credentials instead could renew them in its copy and use up the refresh token the
    /// tracker holds.
    /// </summary>
    public static bool CanSend(ProfileSettings profile) =>
        profile.ProviderKind == Provider.Codex ? profile.Codex is { IsManual: false } : profile.ClaudeAi is not null;

    private bool IsWanted(ProfileSettings profile) => settings.Profiles.Contains(profile) && IsEligible(profile);

    private async Task CheckAsync(ProfileSettings profile, CancellationToken cancellation)
    {
        var memory = automation.StateOf(profile.Id);
        var now = time.GetUtcNow();
        if (memory.AutoStartHoldUntil is { } hold && now < hold && !StartedWindowSeen(profile, memory, hold))
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
        var codexLink = profile.ProviderKind == Provider.Codex ? profile.Codex! : null;
        var organization = profile.ClaudeAi?.OrganizationId;
        // While a request is out the user may switch auto-start off, sign out, link another login or delete the profile.
        bool StillWanted() => IsWanted(profile) && (codexLink is null
            ? profile.ClaudeAi?.OrganizationId == organization
            : profile.Codex?.Identity == codexLink.Identity && profile.Codex?.CodexHome == codexLink.CodexHome);

        if (await FreshUsageAsync(profile, cancellation).ConfigureAwait(true) is not { } fresh
            || !(codexLink is null ? SessionAutoStart.ShouldStart(fresh.Usage, time.GetUtcNow()) : ShouldStartCodex(profile.Id, fresh))
            || !StillWanted()
            || (codexLink is not null && (!await ConfirmCodexAsync(profile, StillWanted, cancellation).ConfigureAwait(true) || !StillWanted())))
        {
            return;
        }

        var start = codexLink is null
            ? client.StartSessionAsync(profile.Id, organization!, StillWanted, cancellation)
            : codex.StartSessionAsync(codexLink, cancellation);
        if (await start.ConfigureAwait(true) is not { } result)
        {
            // Stopped before the message; nothing was started or failed.
            return;
        }

        var now = time.GetUtcNow();
        _startedWindowSeen.Remove(profile.Id);
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
    /// Whether this run has seen open the window the latest start opened, so that its wait may end early: a provider can
    /// reset its limits before the window's reset time, as OpenAI did for Codex on 2026-10-15, and the wait would leave the
    /// profile without a window until then. Only a polled profile is watched, on its polling loop's reads, so nothing is
    /// requested; once its window was seen, each check lets the usual rules decide whether it is still open. The window
    /// is seen in a read at least one check interval after the start that shows the reset time the wait runs to. A start
    /// whose window never shows, and a failure, keep their wait in full, so a message the provider did not count is not
    /// sent again at every check.
    /// </summary>
    private bool StartedWindowSeen(ProfileSettings profile, ProfileAutomationState memory, DateTimeOffset hold)
    {
        if (memory.AutoStartFailureNotified || Polled(profile, current(profile.Id)) is not { } read)
        {
            return false;
        }

        if (_startedWindowSeen.Contains(profile.Id))
        {
            return true;
        }

        // By then an unstarted Codex window's reset time, which moves with the clock, is too far from the wait's end.
        if (read.At - (hold - SessionAutoStart.SessionLength) >= SessionAutoStart.CheckInterval
            && read.Usage.SessionResetTime is { } reset && (reset - hold).Duration() <= StartedWindowTolerance)
        {
            _startedWindowSeen.Add(profile.Id);
        }

        return false;
    }

    /// <summary>
    /// The profile's usage as fetched by this run within the last check interval, and when it was fetched: its polling
    /// loop's result when the profile is polled (only if that loop's latest check succeeded), otherwise one read-only
    /// request now. Null when there is none, or the server asked for no requests yet.
    /// </summary>
    private async Task<(UsageData Usage, DateTimeOffset At)?> FreshUsageAsync(ProfileSettings profile, CancellationToken cancellation)
    {
        var now = time.GetUtcNow();
        var state = current(profile.Id);
        if (state?.RetryNotBefore is { } notBefore && now < notBefore)
        {
            return null;
        }

        // A polled profile is never fetched beside its polling loop, so no two requests of it overlap; without a fresh
        // result of that loop this round is skipped.
        return state is not null ? Polled(profile, state) : await ReadAsync(profile, cancellation).ConfigureAwait(true);
    }

    /// <summary>
    /// A polling loop's usage and when it was fetched, if it is fresh: a result of this run from the profile's current
    /// source within the last check interval. A check that failed, broke or sent nothing after the last success (its attempt
    /// is later) leaves that success's values stale.
    /// </summary>
    private (UsageData Usage, DateTimeOffset At)? Polled(ProfileSettings profile, ProfileUsageState? state) =>
        state is { UsageFromCache: false, LastSuccessAt: { } at, Usage: { } cached } && state.Source == profile.UsageSource
        && state.LastAttemptAt <= at && time.GetUtcNow() - at <= SessionAutoStart.CheckInterval
            ? (cached.Usage, at)
            : null;

    /// <summary>
    /// One read-only usage request now, for a profile that is not polled, unless the server asked for no requests yet. Null
    /// when it brought no usage.
    /// </summary>
    private async Task<(UsageData Usage, DateTimeOffset At)?> ReadAsync(ProfileSettings profile, CancellationToken cancellation)
    {
        var now = time.GetUtcNow();
        if (_retryNotBefore.TryGetValue(profile.Id, out var wait) && now < wait)
        {
            return null;
        }

        var fetched = profile.ProviderKind == Provider.Codex
            ? await FetchCodexAsync(profile, cancellation).ConfigureAwait(true)
            : await client.FetchUsageAsync(profile.Id, profile.ClaudeAi!.OrganizationId, cancellation).ConfigureAwait(true);
        if (fetched is FetchFailed failed
            && (failed.RetryAfter ?? (failed.Failure == FetchFailure.RateLimited ? RefreshPolicy.DefaultRateLimitWait : null)) is { } asked)
        {
            _retryNotBefore[profile.Id] = failed.At + asked;
        }
        else
        {
            _retryNotBefore.Remove(profile.Id);
        }

        return fetched is FetchSucceeded success ? (success.Usage, success.At) : null;
    }

    /// <summary>
    /// For a Codex start: one more read, <see cref="SessionAutoStart.CodexMinimumGap"/> after the deciding one and just
    /// before the message. A window the user started shortly before the deciding read, which that read could not tell from
    /// an unstarted one, has stopped moving by then. A polled profile's loop is asked to make it, so the loop's schedule
    /// and any wait the server asks for apply to it; a profile that is not polled is read here.
    /// </summary>
    private async Task<bool> ConfirmCodexAsync(ProfileSettings profile, Func<bool> stillWanted, CancellationToken cancellation)
    {
        await Wait(SessionAutoStart.CodexMinimumGap, cancellation).ConfigureAwait(true);
        // Meanwhile the user may have switched auto-start off, unlinked the login or deleted the profile.
        if (!stillWanted())
        {
            return false;
        }

        var confirmation = current(profile.Id) is null
            ? await ReadAsync(profile, cancellation).ConfigureAwait(true)
            : Polled(profile, await checkNow(profile.Id, cancellation).ConfigureAwait(true));
        return confirmation is { } read && ShouldStartCodex(profile.Id, read);
    }

    /// <summary>The Codex rule, against the profile's previous fresh usage in this run, which this one then replaces.</summary>
    private bool ShouldStartCodex(Guid profileId, (UsageData Usage, DateTimeOffset At) fresh)
    {
        (UsageData, DateTimeOffset)? previous = _codexReads.TryGetValue(profileId, out var earlier) ? earlier : null;
        _codexReads[profileId] = fresh;
        return SessionAutoStart.ShouldStartCodex(fresh.Usage, fresh.At, previous);
    }

    /// <summary>One read-only usage request with the Codex CLI's login, as polling would send it; null when that login cannot be used now.</summary>
    private async Task<FetchResult?> FetchCodexAsync(ProfileSettings profile, CancellationToken cancellation)
    {
        var watch = ProfileWatch.From(profile);
        var credentials = await codexUsage.ResolveAsync(watch, cancellation).ConfigureAwait(true);
        return credentials.CanFetch
            ? (await codexUsage.FetchAsync(watch, credentials, null, cancellation).ConfigureAwait(true)).Result
            : null;
    }
}
