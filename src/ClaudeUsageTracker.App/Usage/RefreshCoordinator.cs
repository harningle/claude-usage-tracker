using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Status;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>
/// One sequential polling loop per watched profile. Upstream has no equivalent: its refreshes overlap,
/// are never cancelled and can land on whichever profile is active when they finish. Here each profile's
/// checks run one at a time and use only that profile's own credentials, a link change cancels the
/// in-flight check and bumps the generation so its result is dropped, and failures back off while
/// honouring <c>Retry-After</c> (plan.md section 3). Rejected credentials are not resent automatically
/// until they change; a manual refresh may retry them. An unexpected exception is shown as a failure and
/// polling continues.
/// </summary>
public sealed class RefreshCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Console billing is fetched at most this often unless refreshed by hand. Upstream fetched it on every
    /// refresh, four requests each; costs do not move at that pace (docs/parity.md).
    /// </summary>
    public static readonly TimeSpan BillingInterval = TimeSpan.FromMinutes(5);

    /// <summary>An automatic check after a wake or reconnect is skipped when the profile was checked this recently (upstream: 10 s).</summary>
    public static readonly TimeSpan CheckSoonDebounce = TimeSpan.FromSeconds(10);

    private readonly Func<UsageSourceKind, IUsageSource> _sources;
    private readonly UsageClient _client;
    private readonly ConsoleBillingSource _billing;
    private readonly UsageCacheStore _cache;
    private readonly UsageHistoryService _history;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Dictionary<Guid, Worker> _workers = [];
    private readonly Lock _gate = new();
    private Guid? _statusProfileId;

    /// <param name="client">Fetches the service status.</param>
    /// <param name="post">Runs a state notification on the UI thread.</param>
    public RefreshCoordinator(
        Func<UsageSourceKind, IUsageSource> sources, UsageClient client, ConsoleBillingSource billing, UsageCacheStore cache,
        UsageHistoryService history, TimeProvider time, Action<Action> post)
    {
        _sources = sources;
        _client = client;
        _billing = billing;
        _cache = cache;
        _history = history;
        _time = time;
        _post = post;
    }

    /// <summary>Raised on the UI thread with each new state of a profile.</summary>
    public event Action<ProfileUsageState>? StateChanged;

    /// <summary>Raised on the UI thread after each service-status check; null when it could not be read.</summary>
    public event Action<ServiceStatus?>? StatusChanged;

    /// <summary>
    /// Raised on the UI thread when a check of this run has just fetched usage for a profile. Cached values from an
    /// earlier run never raise it, so automation acts on fresh observations only.
    /// </summary>
    public event Action<Guid, CachedUsage>? UsageFetched;

    /// <summary>Starts polling a profile, or applies its changed links or interval.</summary>
    public void Watch(ProfileSettings profile)
    {
        var watch = ProfileWatch.From(profile);
        Worker? started = null;
        lock (_gate)
        {
            if (_workers.TryGetValue(profile.Id, out var worker))
            {
                worker.Update(watch);
                return;
            }

            started = new Worker(this, watch);
            _workers.Add(profile.Id, started);
        }

        started.Start();
    }

    public async Task UnwatchAsync(Guid profileId)
    {
        Worker? worker;
        lock (_gate)
        {
            if (!_workers.Remove(profileId, out worker))
            {
                return;
            }
        }

        await worker.StopAsync().ConfigureAwait(false);
    }

    /// <summary>The profiles being polled.</summary>
    public IReadOnlyList<Guid> Watched
    {
        get
        {
            lock (_gate)
            {
                return [.. _workers.Keys];
            }
        }
    }

    /// <summary>The profile whose checks also refresh the service status (the active profile).</summary>
    public void SetStatusProfile(Guid? profileId)
    {
        lock (_gate)
        {
            _statusProfileId = profileId;
        }
    }

    /// <summary>A manual refresh: checks now, except that a server-requested wait is still honoured.</summary>
    public void Refresh(Guid profileId)
    {
        lock (_gate)
        {
            if (_workers.TryGetValue(profileId, out var worker))
            {
                worker.Wake(manual: true);
            }
        }
    }

    /// <summary>
    /// An automatic check now, as after a wake from sleep or a network or session reconnect (upstream's wake and
    /// network-available refreshes). Unlike a manual refresh it does not resend a rejected login, and it is skipped
    /// for a profile checked within <see cref="CheckSoonDebounce"/>, and for one whose check is running or has not
    /// happened yet (a new profile checks at once anyway). A server-requested wait still holds.
    /// </summary>
    public void CheckSoon()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var worker in _workers.Values)
            {
                if (worker.State is { IsRefreshing: false, LastAttemptAt: { } last } && now - last >= CheckSoonDebounce)
                {
                    worker.Due();
                }
            }
        }
    }

    public ProfileUsageState? Current(Guid profileId)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(profileId, out var worker) ? worker.State : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Worker> workers;
        lock (_gate)
        {
            workers = [.. _workers.Values];
            _workers.Clear();
        }

        await Task.WhenAll(workers.Select(w => w.StopAsync())).ConfigureAwait(false);
    }

    private bool IsStatusProfile(Guid profileId)
    {
        lock (_gate)
        {
            return _statusProfileId == profileId;
        }
    }

    private sealed class Worker
    {
        private readonly RefreshCoordinator _owner;
        private readonly Guid _profileId;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Lock _gate = new();
        private ProfileWatch _watch;
        private long _generation;
        private CancellationTokenSource _cycle = new();
        private TaskCompletionSource _wake = NewSignal();
        private bool _manualPending;
        private bool _duePending;
        private string? _rejectedStamp;
        private DateTimeOffset? _billingLast;
        private DateTimeOffset _billingNotBefore;
        private Task _loop = Task.CompletedTask;

        public Worker(RefreshCoordinator owner, ProfileWatch watch)
        {
            _owner = owner;
            _profileId = watch.ProfileId;
            _watch = watch with { Interval = RefreshPolicy.ClampInterval(watch.Interval) };
            State = InitialState(watch, 0);
        }

        public ProfileUsageState State { get; private set; }

        public void Start()
        {
            Publish(0, State);
            _loop = Task.Run(RunAsync);
        }

        public void Update(ProfileWatch watch)
        {
            watch = watch with { Interval = RefreshPolicy.ClampInterval(watch.Interval) };
            long generation;
            ProfileUsageState state;
            lock (_gate)
            {
                if (_watch.LinkKey == watch.LinkKey)
                {
                    _watch = watch;
                    SignalLocked(manual: false);
                    return;
                }

                // Changed links are a new credential generation: cancel the in-flight check and drop its result.
                // A wait the server asked for still applies.
                _watch = watch;
                _generation++;
                _rejectedStamp = null;
                _billingLast = null;
                _cycle.Cancel();
                _cycle = new CancellationTokenSource();
                State = InitialState(watch, _generation) with { RetryNotBefore = State.RetryNotBefore };
                SignalLocked(manual: true);
                generation = _generation;
                state = State;
            }

            Publish(generation, state);
        }

        public void Wake(bool manual)
        {
            lock (_gate)
            {
                SignalLocked(manual);
            }
        }

        /// <summary>Ends the current wait without making the next check manual.</summary>
        public void Due()
        {
            lock (_gate)
            {
                _duePending = true;
                _wake.TrySetResult();
            }
        }

        public async Task StopAsync()
        {
            _lifetime.Cancel();
            lock (_gate)
            {
                _cycle.Cancel();
            }

            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private ProfileUsageState InitialState(ProfileWatch watch, long generation)
        {
            var cached = watch.Source is null ? null : _owner._cache.Load(_profileId);
            // Another account's cache is never shown under this link.
            if (cached is not null && cached.AccountIdentity != watch.AccountIdentity)
            {
                cached = null;
            }

            var api = watch.TracksBilling ? _owner._cache.LoadApi(_profileId) : null;
            if (api is not null && api.OrganizationId != watch.Console!.OrganizationId)
            {
                api = null;
            }

            return new ProfileUsageState
            {
                ProfileId = _profileId,
                Generation = generation,
                IsLinked = watch.Source is not null || watch.TracksBilling,
                Source = watch.Source,
                Usage = cached,
                UsageFromCache = cached is not null,
                Api = api,
                ApiFromCache = api is not null,
            };
        }

        private void SignalLocked(bool manual)
        {
            _manualPending |= manual;
            _wake.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task RunAsync()
        {
            var lifetime = _lifetime.Token;
            var manual = true; // the first check runs at once
            while (!lifetime.IsCancellationRequested)
            {
                ProfileWatch watch;
                long generation;
                CancellationTokenSource cycle;
                lock (_gate)
                {
                    watch = _watch;
                    generation = _generation;
                    cycle = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _cycle.Token);
                    _manualPending = false;
                    _duePending = false;
                    _wake = NewSignal();
                }

                TimeSpan delay;
                using (cycle)
                {
                    try
                    {
                        delay = await RunCycleAsync(watch, generation, manual, cycle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cycle.IsCancellationRequested)
                    {
                        if (lifetime.IsCancellationRequested)
                        {
                            return;
                        }

                        // The links changed mid-check; the new generation checks straight away.
                        manual = true;
                        continue;
                    }
                    catch (Exception e)
                    {
                        // Fail loud without stopping: show what went wrong (type and message only) and keep polling.
                        var failures = State.ConsecutiveFailures + 1;
                        delay = RefreshPolicy.NextDelay(watch.Interval, failures, FetchFailure.Network, null);
                        var now = _owner._time.GetUtcNow();
                        Commit(generation, State with
                        {
                            InternalError = $"{e.GetType().Name}: {e.Message}",
                            ConsecutiveFailures = failures,
                            IsRefreshing = false,
                            LastAttemptAt = now,
                            NextCheckAt = now + delay,
                        });
                    }
                }

                manual = await WaitAsync(delay, delay == watch.Interval, lifetime).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits for the delay or a manual refresh; returns whether a manual refresh was requested. An
        /// interval change re-times a normal wait but never shortens a backoff or a server-requested wait.
        /// </summary>
        private async Task<bool> WaitAsync(TimeSpan delay, bool delayIsInterval, CancellationToken lifetime)
        {
            var started = _owner._time.GetUtcNow();
            var due = started + delay;
            while (true)
            {
                Task wake;
                lock (_gate)
                {
                    if (_manualPending)
                    {
                        return true;
                    }

                    if (_duePending)
                    {
                        return false;
                    }

                    if (delayIsInterval)
                    {
                        due = started + _watch.Interval;
                    }

                    wake = _wake.Task;
                }

                var remaining = due - _owner._time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                using var timer = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                var sleep = Task.Delay(remaining, _owner._time, timer.Token);
                var finished = await Task.WhenAny(sleep, wake).ConfigureAwait(false);
                timer.Cancel();
                lifetime.ThrowIfCancellationRequested();
                if (finished == sleep)
                {
                    lock (_gate)
                    {
                        return _manualPending;
                    }
                }

                lock (_gate)
                {
                    if (_manualPending)
                    {
                        return true;
                    }

                    if (_duePending)
                    {
                        return false;
                    }

                    // Woken by an interval change: re-time and keep waiting.
                    _wake = NewSignal();
                }
            }
        }

        private async Task<TimeSpan> RunCycleAsync(ProfileWatch watch, long generation, bool manual, CancellationToken cycle)
        {
            var time = _owner._time;
            var interval = watch.Interval;
            if (watch.Source is null && !watch.TracksBilling)
            {
                return interval;
            }

            var now = time.GetUtcNow();
            var heldUntil = State.RetryNotBefore is { } notBefore && now < notBefore ? notBefore : (DateTimeOffset?)null;
            if (heldUntil is { } until && !watch.TracksBilling)
            {
                // A manual refresh never overrides the server's requested wait.
                Commit(generation, State with { NextCheckAt = until });
                return until - now;
            }

            Commit(generation, State with { IsRefreshing = true });
            var delay = heldUntil is { } wait ? wait - now : interval;
            if (watch.Source is { } kind && heldUntil is null)
            {
                delay = await UsageAsync(watch, kind, generation, manual, cycle).ConfigureAwait(false);
            }

            if (watch.TracksBilling)
            {
                await BillingAsync(watch, generation, manual, cycle).ConfigureAwait(false);
            }

            Commit(generation, State with { IsRefreshing = false, NextCheckAt = time.GetUtcNow() + delay });
            return delay;
        }

        /// <summary>One usage check. Returns how long to wait before the next one.</summary>
        private async Task<TimeSpan> UsageAsync(ProfileWatch watch, UsageSourceKind kind, long generation, bool manual, CancellationToken cycle)
        {
            var time = _owner._time;
            var interval = watch.Interval;
            var source = _owner._sources(kind);
            var credentials = await source.ResolveAsync(watch, cycle).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var checkedState = State with
            {
                Link = credentials.Status,
                MirrorError = credentials.StorageError,
                Ownership = credentials.Ownership,
                Notice = credentials.Notice,
                LastAttemptAt = now,
                InternalError = null,
            };

            if (!credentials.CanFetch || (!manual && credentials.Stamp == RejectedStamp()))
            {
                // Nothing to send: look at the login again at the normal interval.
                Commit(generation, checkedState);
                return interval;
            }

            var fetch = await source.FetchAsync(watch, credentials, State.Usage?.Usage, cycle).ConfigureAwait(false);
            if (_owner.IsStatusProfile(_profileId))
            {
                var statusUrl = watch.Provider == Provider.Codex ? ServiceStatus.OpenAiStatusUrl : ServiceStatus.ClaudeStatusUrl;
                var status = await _owner._client.FetchStatusAsync(cycle, statusUrl).ConfigureAwait(false);
                if (IsCurrent(generation))
                {
                    _owner._post(() => _owner.StatusChanged?.Invoke(status));
                }
            }

            now = time.GetUtcNow();
            checkedState = checkedState with { Link = fetch.Status ?? credentials.Status };
            switch (fetch.Result)
            {
                case FetchSucceeded success:
                {
                    var cached = new CachedUsage
                    {
                        ProfileId = _profileId,
                        ObservedAt = success.At,
                        Source = SourceName(kind),
                        AccountIdentity = watch.AccountIdentity,
                        Usage = success.Usage,
                    };
                    if (!IsCurrent(generation))
                    {
                        return interval;
                    }

                    try
                    {
                        _owner._cache.Save(cached);
                    }
                    catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                    {
                        // The cache only shortens the next start; live values are still shown.
                    }

                    var historyError = _owner._history.RecordUsage(_profileId, watch.Provider, State.Usage?.Usage, success.Usage);
                    SetRejectedStamp(generation, null);
                    Commit(generation, checkedState with
                    {
                        Usage = cached,
                        UsageFromCache = false,
                        LastFailure = null,
                        LastFailureStatus = null,
                        ConsecutiveFailures = 0,
                        LastSuccessAt = success.At,
                        RetryNotBefore = null,
                        HistoryError = historyError,
                    });
                    Announce(generation, cached);
                    return interval;
                }

                case FetchFailed failed:
                {
                    var failures = State.ConsecutiveFailures + 1;
                    var delay = RefreshPolicy.NextDelay(interval, failures, failed.Failure, failed.RetryAfter);
                    if (failed.Failure == FetchFailure.CredentialsRejected)
                    {
                        SetRejectedStamp(generation, credentials.Stamp);
                        delay = interval;
                    }

                    Commit(generation, checkedState with
                    {
                        LastFailure = failed.Failure,
                        LastFailureStatus = failed.Status,
                        ConsecutiveFailures = failures,
                        RetryNotBefore = failed.Failure == FetchFailure.RateLimited || failed.RetryAfter is not null ? now + delay : null,
                    });
                    return delay;
                }

                default:
                    throw new InvalidOperationException($"Unexpected fetch result {fetch.Result.GetType().Name}.");
            }
        }

        /// <summary>
        /// Console billing, at most every <see cref="BillingInterval"/> unless refreshed by hand. A failure
        /// keeps the last billing and never affects usage polling.
        /// </summary>
        private async Task BillingAsync(ProfileWatch watch, long generation, bool manual, CancellationToken cycle)
        {
            var now = _owner._time.GetUtcNow();
            DateTimeOffset? last;
            DateTimeOffset notBefore;
            lock (_gate)
            {
                last = _billingLast;
                notBefore = _billingNotBefore;
            }

            // A server-requested wait holds even a manual refresh; a manual refresh otherwise only skips the spacing.
            if (now < notBefore || (last is { } previous && now < previous + (manual ? RefreshPolicy.MinimumInterval : BillingInterval)))
            {
                return;
            }

            var fetch = await _owner._billing.FetchAsync(watch, cycle).ConfigureAwait(false);
            now = _owner._time.GetUtcNow();
            if (!IsCurrent(generation))
            {
                return;
            }

            lock (_gate)
            {
                if (generation == _generation)
                {
                    _billingLast = now;
                    if (fetch.RetryAfter is not null || fetch.Failure == FetchFailure.RateLimited)
                    {
                        _billingNotBefore = now + (fetch.RetryAfter ?? RefreshPolicy.DefaultRateLimitWait);
                    }
                }
            }

            if (fetch.Usage is not { } usage)
            {
                Commit(generation, State with { ApiFailure = fetch.Failure ?? FetchFailure.InvalidResponse, MirrorError = fetch.StorageError ?? State.MirrorError });
                return;
            }

            var cached = new CachedApiUsage
            {
                ProfileId = _profileId,
                ObservedAt = now,
                OrganizationId = watch.Console!.OrganizationId,
                Usage = usage,
            };
            try
            {
                _owner._cache.SaveApi(cached);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
            }

            var historyError = _owner._history.RecordBilling(_profileId, State.Api?.Usage, usage);
            Commit(generation, State with
            {
                Api = cached,
                ApiFromCache = false,
                ApiFailure = null,
                HistoryError = historyError ?? State.HistoryError,
                MirrorError = fetch.StorageError ?? State.MirrorError,
            });
        }

        private static string SourceName(UsageSourceKind kind) => kind switch
        {
            UsageSourceKind.ClaudeAi => "claudeAi",
            UsageSourceKind.Codex => "codex",
            _ => "oauthUsage",
        };

        private string? RejectedStamp()
        {
            lock (_gate)
            {
                return _rejectedStamp;
            }
        }

        private void SetRejectedStamp(long generation, string? stamp)
        {
            lock (_gate)
            {
                // A result of superseded links must not mark the new links' credential.
                if (generation == _generation)
                {
                    _rejectedStamp = stamp;
                }
            }
        }

        private bool IsCurrent(long generation)
        {
            lock (_gate)
            {
                return generation == _generation && !_lifetime.IsCancellationRequested;
            }
        }

        /// <summary>Applies a state only if no link change has superseded its generation.</summary>
        private void Commit(long generation, ProfileUsageState state)
        {
            lock (_gate)
            {
                if (generation != _generation || _lifetime.IsCancellationRequested)
                {
                    return;
                }

                State = state;
            }

            Publish(generation, state);
        }

        /// <summary>Raises <see cref="UsageFetched"/> on the UI thread, unless a link change has superseded the result by then.</summary>
        private void Announce(long generation, CachedUsage usage) => _owner._post(() =>
        {
            if (IsCurrent(generation))
            {
                _owner.UsageFetched?.Invoke(_profileId, usage);
            }
        });

        /// <summary>
        /// Notifies on the UI thread, checking the generation again when the notification runs: a link change
        /// between committing and posting would otherwise let an old result overwrite the newer display.
        /// </summary>
        private void Publish(long generation, ProfileUsageState state) => _owner._post(() =>
        {
            if (IsCurrent(generation))
            {
                _owner.StateChanged?.Invoke(state);
            }
        });
    }
}
