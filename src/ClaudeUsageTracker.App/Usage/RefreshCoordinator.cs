using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Status;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>
/// One sequential polling loop per watched profile. Upstream has no equivalent: its refreshes overlap,
/// are never cancelled and can land on whichever profile is active when they finish. Here each profile's
/// checks run one at a time, a link change cancels the in-flight check and bumps the generation so its
/// result is dropped, and failures back off while honouring <c>Retry-After</c> (plan.md section 3,
/// Milestone 2). Rejected credentials are not resent automatically until Claude Code's token changes;
/// a manual refresh may retry them. An unexpected exception is shown as a failure and polling continues.
/// </summary>
public sealed class RefreshCoordinator : IAsyncDisposable
{
    private readonly CliLinkService _links;
    private readonly UsageClient _client;
    private readonly UsageCacheStore _cache;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Dictionary<Guid, Worker> _workers = [];
    private readonly Lock _gate = new();
    private Guid? _statusProfileId;

    /// <param name="post">Runs a state notification on the UI thread.</param>
    public RefreshCoordinator(CliLinkService links, UsageClient client, UsageCacheStore cache, TimeProvider time, Action<Action> post)
    {
        _links = links;
        _client = client;
        _cache = cache;
        _time = time;
        _post = post;
    }

    /// <summary>Raised on the UI thread with each new state of a profile.</summary>
    public event Action<ProfileUsageState>? StateChanged;

    /// <summary>Raised on the UI thread after each service-status check; null when it could not be read.</summary>
    public event Action<ServiceStatus?>? StatusChanged;

    /// <summary>Starts polling a profile, or applies its changed link or interval.</summary>
    public void Watch(ProfileSettings profile)
    {
        Worker? started = null;
        lock (_gate)
        {
            if (_workers.TryGetValue(profile.Id, out var worker))
            {
                worker.Update(profile.CliLink, TimeSpan.FromSeconds(profile.RefreshInterval));
                return;
            }

            started = new Worker(this, profile.Id, profile.CliLink, TimeSpan.FromSeconds(profile.RefreshInterval));
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
        private CliLink? _link;
        private TimeSpan _interval;
        private long _generation;
        private CancellationTokenSource _cycle = new();
        private TaskCompletionSource _wake = NewSignal();
        private bool _manualPending;
        private string? _rejectedAccessToken;
        private Task _loop = Task.CompletedTask;

        public Worker(RefreshCoordinator owner, Guid profileId, CliLink? link, TimeSpan interval)
        {
            _owner = owner;
            _profileId = profileId;
            _link = link;
            _interval = RefreshPolicy.ClampInterval(interval);
            State = InitialState(link, 0);
        }

        public ProfileUsageState State { get; private set; }

        public void Start()
        {
            Publish(0, State);
            _loop = Task.Run(RunAsync);
        }

        public void Update(CliLink? link, TimeSpan interval)
        {
            long generation;
            ProfileUsageState state;
            lock (_gate)
            {
                _interval = RefreshPolicy.ClampInterval(interval);
                if (SameLink(_link, link))
                {
                    _link = link;
                    SignalLocked(manual: false);
                    return;
                }

                // A new link is a new credential generation: cancel the in-flight check and drop its result.
                // A wait the server asked for still applies.
                _link = link;
                _generation++;
                _rejectedAccessToken = null;
                _cycle.Cancel();
                _cycle = new CancellationTokenSource();
                State = InitialState(link, _generation) with { RetryNotBefore = State.RetryNotBefore };
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

        private ProfileUsageState InitialState(CliLink? link, long generation)
        {
            var cached = link is null ? null : _owner._cache.Load(_profileId);
            // Another account's cache is never shown under this link.
            if (cached is not null && cached.AccountIdentity != link?.Identity)
            {
                cached = null;
            }

            return new ProfileUsageState
            {
                ProfileId = _profileId,
                Generation = generation,
                IsLinked = link is not null,
                Usage = cached,
                UsageFromCache = cached is not null,
            };
        }

        private static bool SameLink(CliLink? a, CliLink? b) =>
            a is null ? b is null
            : b is not null && string.Equals(a.ConfigDirectory, b.ConfigDirectory, StringComparison.OrdinalIgnoreCase)
                            && a.Identity == b.Identity && a.LinkedAt == b.LinkedAt;

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
                CliLink? link;
                long generation;
                CancellationTokenSource cycle;
                TimeSpan interval;
                lock (_gate)
                {
                    link = _link;
                    generation = _generation;
                    cycle = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _cycle.Token);
                    interval = _interval;
                    _manualPending = false;
                    _wake = NewSignal();
                }

                TimeSpan delay;
                using (cycle)
                {
                    try
                    {
                        delay = await RunCycleAsync(link, generation, manual, interval, cycle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cycle.IsCancellationRequested)
                    {
                        if (lifetime.IsCancellationRequested)
                        {
                            return;
                        }

                        // The link changed mid-check; the new generation checks straight away.
                        manual = true;
                        continue;
                    }
                    catch (Exception e)
                    {
                        // Fail loud without stopping: show what went wrong (type and message only) and keep polling.
                        var failures = State.ConsecutiveFailures + 1;
                        delay = RefreshPolicy.NextDelay(interval, failures, FetchFailure.Network, null);
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

                manual = await WaitAsync(delay, delay == interval, lifetime).ConfigureAwait(false);
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

                    if (delayIsInterval)
                    {
                        due = started + _interval;
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

                    // Woken by an interval change: re-time and keep waiting.
                    _wake = NewSignal();
                }
            }
        }

        private async Task<TimeSpan> RunCycleAsync(CliLink? link, long generation, bool manual, TimeSpan interval, CancellationToken cycle)
        {
            var time = _owner._time;
            if (link is null)
            {
                return interval;
            }

            var now = time.GetUtcNow();
            if (State.RetryNotBefore is { } notBefore && now < notBefore)
            {
                // A manual refresh never overrides the server's requested wait.
                Commit(generation, State with { NextCheckAt = notBefore });
                return notBefore - now;
            }

            Commit(generation, State with { IsRefreshing = true });
            var check = await _owner._links.CheckAsync(_profileId, link, cycle).ConfigureAwait(false);
            now = time.GetUtcNow();
            var withLink = State with { Link = check.Status, MirrorError = check.MirrorError, LastAttemptAt = now, InternalError = null };

            if (check.Status != LinkStatus.Ready)
            {
                // Nothing to send: re-read Claude Code's file at the normal interval.
                Commit(generation, withLink with { IsRefreshing = false, NextCheckAt = now + interval });
                return interval;
            }

            var token = check.Credentials!.AccessToken;
            if (!manual && token == RejectedToken())
            {
                Commit(generation, withLink with { IsRefreshing = false, NextCheckAt = now + interval });
                return interval;
            }

            var result = await _owner._client.FetchUsageAsync(token, cycle).ConfigureAwait(false);
            if (_owner.IsStatusProfile(_profileId))
            {
                var status = await _owner._client.FetchStatusAsync(cycle).ConfigureAwait(false);
                if (IsCurrent(generation))
                {
                    _owner._post(() => _owner.StatusChanged?.Invoke(status));
                }
            }

            now = time.GetUtcNow();
            switch (result)
            {
                case FetchSucceeded success:
                {
                    var cached = new CachedUsage
                    {
                        ProfileId = _profileId,
                        ObservedAt = success.At,
                        Source = "oauthUsage",
                        AccountIdentity = link.Identity,
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

                    SetRejectedToken(generation, null);
                    Commit(generation, withLink with
                    {
                        Usage = cached,
                        UsageFromCache = false,
                        LastFailure = null,
                        LastFailureStatus = null,
                        ConsecutiveFailures = 0,
                        LastSuccessAt = success.At,
                        RetryNotBefore = null,
                        IsRefreshing = false,
                        NextCheckAt = now + interval,
                    });
                    return interval;
                }

                case FetchFailed failed:
                {
                    var failures = State.ConsecutiveFailures + 1;
                    var delay = RefreshPolicy.NextDelay(interval, failures, failed.Failure, failed.RetryAfter);
                    if (failed.Failure == FetchFailure.CredentialsRejected)
                    {
                        SetRejectedToken(generation, token);
                        delay = interval;
                    }

                    Commit(generation, withLink with
                    {
                        LastFailure = failed.Failure,
                        LastFailureStatus = failed.Status,
                        ConsecutiveFailures = failures,
                        RetryNotBefore = failed.Failure == FetchFailure.RateLimited || failed.RetryAfter is not null ? now + delay : null,
                        IsRefreshing = false,
                        NextCheckAt = now + delay,
                    });
                    return delay;
                }

                default:
                    throw new InvalidOperationException($"Unexpected fetch result {result.GetType().Name}.");
            }
        }

        private string? RejectedToken()
        {
            lock (_gate)
            {
                return _rejectedAccessToken;
            }
        }

        private void SetRejectedToken(long generation, string? token)
        {
            lock (_gate)
            {
                // A result of a superseded link must not mark the new link's token.
                if (generation == _generation)
                {
                    _rejectedAccessToken = token;
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
