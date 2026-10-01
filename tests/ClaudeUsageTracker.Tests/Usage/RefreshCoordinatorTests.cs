using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Status;
using TestTime = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>
/// The per-profile refresh coordinator against a scripted HTTP handler and a synthetic Claude Code
/// directory. Time is simulated; no real network or account is involved.
/// </summary>
public sealed class RefreshCoordinatorTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly TestTime _time = new(Start);
    private readonly ConcurrentQueue<ProfileUsageState> _states = new();
    private readonly ConcurrentQueue<ServiceStatus?> _statuses = new();
    private readonly Guid _profileId = Guid.NewGuid();
    private RefreshCoordinator? _coordinator;

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string ClaudeDirectory => Path.Combine(UserProfile, ".claude");
    private UsageCacheStore Cache => new(Path.Combine(_temp.Path, "cache"));

    public RefreshCoordinatorTests() => Directory.CreateDirectory(ClaudeDirectory);

    public async ValueTask DisposeAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
        }

        _temp.Dispose();
    }

    private void WriteLogin(string accessToken, DateTimeOffset expires, string accountUuid = "acc-1")
    {
        File.WriteAllText(Path.Combine(ClaudeDirectory, ".credentials.json"),
            $$$"""{"claudeAiOauth":{"accessToken":"{{{accessToken}}}","refreshToken":"rt-{{{accessToken}}}","expiresAt":{{{expires.ToUnixTimeMilliseconds()}}}}}""");
        File.WriteAllText(Path.Combine(UserProfile, ".claude.json"),
            $$$"""{"oauthAccount":{"accountUuid":"{{{accountUuid}}}","emailAddress":"user@example.com"}}""");
    }

    private ProfileSettings Profile(int intervalSeconds = 30, DateTimeOffset? linkedAt = null) => new()
    {
        Id = _profileId,
        Name = "Test",
        RefreshInterval = intervalSeconds,
        CliLink = new CliLink
        {
            ConfigDirectory = ClaudeDirectory,
            IsDefaultDirectory = true,
            AccountUuid = "acc-1",
            LinkedAt = linkedAt ?? Start,
        },
    };

    private (RefreshCoordinator Coordinator, ScriptedHandler Handler) Create(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var count = 0;
        var handler = new ScriptedHandler((request, _) => Task.FromResult(respond(request, Interlocked.Increment(ref count))));
        return Create(handler);
    }

    private (RefreshCoordinator Coordinator, ScriptedHandler Handler) Create(ScriptedHandler handler, Action<Action>? post = null)
    {
        var links = new CliLinkService(new SecretStore(Path.Combine(_temp.Path, "secrets")), _time, UserProfile);
        var client = new UsageClient(new HttpClient(handler), _time, new RequestLog(Path.Combine(_temp.Path, "diagnostics")));
        _coordinator = new RefreshCoordinator(links, client, Cache, _time, post ?? (action => action()));
        _coordinator.StateChanged += _states.Enqueue;
        _coordinator.StatusChanged += _statuses.Enqueue;
        return (_coordinator, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);
        return response;
    }

    private static HttpResponseMessage Usage(double session) =>
        Json(HttpStatusCode.OK, $$$"""{"five_hour":{"utilization":{{{session}}},"resets_at":"2026-10-01T15:00:00Z"}}""");

    private static int UsageRequests(ScriptedHandler handler) =>
        handler.Requests.Count(r => r.RequestUri?.AbsolutePath == "/api/oauth/usage");

    private async Task<ProfileUsageState> UntilAsync(Func<ProfileUsageState, bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (_coordinator?.Current(_profileId) is { } state && condition(state))
            {
                return state;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The coordinator never reached the expected state.");
    }

    /// <summary>Advances simulated time in small steps, letting the loop observe each step.</summary>
    private async Task AdvanceAsync(TimeSpan total)
    {
        var step = TimeSpan.FromSeconds(1);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            _time.Advance(step);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FirstCheckFetchesAndCachesUsage()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, _) => Usage(12));

        coordinator.Watch(Profile());
        var state = await UntilAsync(s => s.LastSuccessAt is not null);

        Assert.Equal(LinkStatus.Ready, state.Link);
        Assert.Equal(12, state.Usage?.Usage.SessionPercentage);
        Assert.False(state.UsageFromCache);
        Assert.Equal(1, UsageRequests(handler));
        Assert.Equal("acc-1", Cache.Load(_profileId)?.AccountIdentity);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.AbsolutePath.Contains("messages", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task CachedUsageIsShownFirstAndLabelled()
    {
        WriteLogin("at-1", Start.AddHours(8));
        Cache.Save(new CachedUsage
        {
            ProfileId = _profileId, ObservedAt = Start.AddHours(-3), Source = "oauthUsage", AccountIdentity = "acc-1",
            Usage = new Core.Usage.UsageData { SessionPercentage = 5 },
        });
        var (coordinator, _) = Create((_, _) => Usage(12));

        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastSuccessAt is not null);

        var first = _states.First();
        Assert.True(first.UsageFromCache);
        Assert.Equal(5, first.Usage?.Usage.SessionPercentage);
        Assert.Equal(Start.AddHours(-3), first.Usage?.ObservedAt);
    }

    [Fact]
    public async Task AnotherAccountsCacheIsNeverShown()
    {
        WriteLogin("at-1", Start.AddHours(8));
        Cache.Save(new CachedUsage
        {
            ProfileId = _profileId, ObservedAt = Start, Source = "oauthUsage", AccountIdentity = "acc-other",
            Usage = new Core.Usage.UsageData { SessionPercentage = 99 },
        });
        var (coordinator, _) = Create((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{}"));

        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastFailure is not null);

        Assert.All(_states, s => Assert.Null(s.Usage));
    }

    [Fact]
    public async Task RejectedCredentialsAreNotResentUntilTheTokenChanges()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":{}}"""));

        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastFailure == FetchFailure.CredentialsRejected);
        await AdvanceAsync(TimeSpan.FromSeconds(95));
        Assert.Equal(1, UsageRequests(handler));

        WriteLogin("at-2", Start.AddHours(9));
        await AdvanceAsync(TimeSpan.FromSeconds(31));
        await UntilAsync(s => s.ConsecutiveFailures == 2);
        Assert.Equal(2, UsageRequests(handler));
        Assert.Equal("at-2", handler.Requests.Last().Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task RetryAfterHoldsEvenAManualRefresh()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, n) => n == 1
            ? Json(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.TryAddWithoutValidation("Retry-After", "120"))
            : Usage(30));

        coordinator.Watch(Profile());
        var limited = await UntilAsync(s => s.LastFailure == FetchFailure.RateLimited);
        Assert.Equal(Start.AddSeconds(120), limited.RetryNotBefore);

        coordinator.Refresh(_profileId);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await AdvanceAsync(TimeSpan.FromSeconds(110));
        Assert.Equal(1, UsageRequests(handler));

        await AdvanceAsync(TimeSpan.FromSeconds(15));
        var recovered = await UntilAsync(s => s.LastSuccessAt is not null);
        Assert.Equal(2, UsageRequests(handler));
        Assert.Null(recovered.RetryNotBefore);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task ServerRetryAfterHoldsAManualRefresh()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, n) => n == 1
            ? Json(HttpStatusCode.ServiceUnavailable, "{}", r => r.Headers.TryAddWithoutValidation("Retry-After", "300"))
            : Usage(30));

        coordinator.Watch(Profile());
        var failed = await UntilAsync(s => s.LastFailure == FetchFailure.ServerError);
        Assert.Equal(Start.AddSeconds(300), failed.RetryNotBefore);

        coordinator.Refresh(_profileId);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, UsageRequests(handler));
    }

    [Fact]
    public async Task UnexpectedExceptionIsShownAndPollingContinues()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var calls = 0;
        var (coordinator, _) = Create(new ScriptedHandler((_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("synthetic failure")
            : Task.FromResult(Usage(25))));

        coordinator.Watch(Profile());
        var failed = await UntilAsync(s => s.InternalError is not null);
        Assert.Contains("synthetic failure", failed.InternalError, StringComparison.Ordinal);
        Assert.False(failed.IsRefreshing);

        coordinator.Refresh(_profileId);
        var recovered = await UntilAsync(s => s.LastSuccessAt is not null);
        Assert.Null(recovered.InternalError);
        Assert.Equal(25, recovered.Usage?.Usage.SessionPercentage);
    }

    [Fact]
    public async Task ExpiredCliTokenWaitsWithoutRequests()
    {
        WriteLogin("at-1", Start.AddSeconds(30));
        var (coordinator, handler) = Create((_, _) => Usage(12));

        coordinator.Watch(Profile());
        await UntilAsync(s => s.Link == LinkStatus.WaitingForCliRefresh);
        await AdvanceAsync(TimeSpan.FromSeconds(65));

        Assert.Equal(0, UsageRequests(handler));

        // Claude Code refreshed its login: the next check picks it up.
        WriteLogin("at-2", Start.AddHours(8));
        await AdvanceAsync(TimeSpan.FromSeconds(31));
        await UntilAsync(s => s.LastSuccessAt is not null);
        Assert.Equal("at-2", handler.Requests.Single().Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ChangedAccountIsNotPolled()
    {
        WriteLogin("at-1", Start.AddHours(8), accountUuid: "acc-other");
        var (coordinator, handler) = Create((_, _) => Usage(12));

        coordinator.Watch(Profile());
        await UntilAsync(s => s.Link == LinkStatus.AccountChanged);

        Assert.Equal(0, UsageRequests(handler));
    }

    [Fact]
    public async Task LateResultOfASupersededLinkIsDropped()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var release = new TaskCompletionSource();
        var calls = 0;
        var handler = new ScriptedHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // Ignores cancellation, like a response already on the wire.
                await release.Task;
                return Usage(11);
            }

            return Usage(22);
        });
        var (coordinator, _) = Create(handler);

        coordinator.Watch(Profile());
        await UntilAsync(s => s.IsRefreshing);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        coordinator.Watch(Profile(linkedAt: Start.AddMinutes(1)));
        release.SetResult();

        var state = await UntilAsync(s => s.Usage?.Usage.SessionPercentage == 22);
        Assert.Equal(1, state.Generation);
        Assert.DoesNotContain(_states, s => s.Usage?.Usage.SessionPercentage == 11);
    }

    [Fact]
    public async Task NotificationOfASupersededLinkIsDroppedWhenItRuns()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var posted = new ConcurrentQueue<Action>();
        var (coordinator, _) = Create(ScriptedHandler.Returning(HttpStatusCode.OK, """{"five_hour":{"utilization":12}}"""), posted.Enqueue);
        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastSuccessAt is not null);
        var beforeUnlink = posted.ToArray();
        posted.Clear();

        // The unlink's notification runs first, then the older ones, as when a result was committed before the
        // unlink but posted after it.
        coordinator.Watch(new ProfileSettings { Id = _profileId, Name = "Test" });
        foreach (var notify in posted.Concat(beforeUnlink))
        {
            notify();
        }

        var shown = Assert.Single(_states);
        Assert.False(shown.IsLinked);
        Assert.Null(shown.Usage);
    }

    [Fact]
    public async Task IntervalChangeReschedulesWithoutAnExtraRequest()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, _) => Usage(12));
        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastSuccessAt is not null);

        coordinator.Watch(Profile(intervalSeconds: 60));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await AdvanceAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(1, UsageRequests(handler));

        await AdvanceAsync(TimeSpan.FromSeconds(20));
        await UntilAsync(_ => UsageRequests(handler) == 2);
    }

    [Fact]
    public async Task ManualRefreshChecksAtOnce()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, n) => Usage(n * 10));
        coordinator.Watch(Profile());
        await UntilAsync(s => s.Usage?.Usage.SessionPercentage == 10);

        coordinator.Refresh(_profileId);

        await UntilAsync(s => s.Usage?.Usage.SessionPercentage == 20);
        Assert.Equal(2, UsageRequests(handler));
    }

    [Fact]
    public async Task UnlinkedProfileMakesNoRequests()
    {
        var (coordinator, handler) = Create((_, _) => Usage(12));

        coordinator.Watch(new ProfileSettings { Id = _profileId, Name = "Unlinked" });
        var state = await UntilAsync(s => !s.IsLinked);
        await AdvanceAsync(TimeSpan.FromSeconds(40));

        Assert.Null(state.Usage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StatusIsCheckedForTheStatusProfile()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, _) = Create((request, _) => request.RequestUri!.Host == "status.claude.com"
            ? Json(HttpStatusCode.OK, """{"status":{"indicator":"none","description":"All Systems Operational"}}""")
            : Usage(12));
        coordinator.SetStatusProfile(_profileId);

        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastSuccessAt is not null);
        for (var i = 0; i < 100 && _statuses.IsEmpty; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(new ServiceStatus(ServiceStatusIndicator.None, "All Systems Operational"), Assert.Single(_statuses));
    }
}
