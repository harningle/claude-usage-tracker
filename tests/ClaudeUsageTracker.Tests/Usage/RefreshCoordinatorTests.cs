using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.History;
using ClaudeUsageTracker.Core.Providers;
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
    private readonly ScriptedTransport _transport;
    private volatile bool _signedIn = true;
    private RefreshCoordinator? _coordinator;
    private LinkHarness _links = null!;

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string ClaudeDirectory => Path.Combine(UserProfile, ".claude");
    private UsageCacheStore Cache => new(Path.Combine(_temp.Path, "cache"));
    private UsageHistoryStore History => new(Path.Combine(_temp.Path, "history"));

    public RefreshCoordinatorTests()
    {
        Directory.CreateDirectory(ClaudeDirectory);
        _transport = new((_, operation, _) => _signedIn ? ScriptedTransport.SignedIn(operation) : ScriptedTransport.Status(401));
    }

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
        _links = new LinkHarness(_temp.Path, UserProfile, _time);
        var log = new RequestLog(Path.Combine(_temp.Path, "diagnostics"));
        var http = new HttpClient(handler);
        var client = new UsageClient(http, _time, log);
        var sources = new Dictionary<UsageSourceKind, IUsageSource>
        {
            [UsageSourceKind.ClaudeCode] = new CliUsageSource(_links.Service, client),
            [UsageSourceKind.ClaudeAi] = new ClaudeAiUsageSource(new ClaudeAiClient(_transport, _time, log), _transport),
            [UsageSourceKind.Codex] = new CodexUsageSource(
                _links.Secrets, _links.Lock, _links.Tokens, new CodexClient(http, _time, log), _time, () => Path.Combine(UserProfile, ".codex")),
        };
        _coordinator = new RefreshCoordinator(
            kind => sources[kind], client, new ConsoleBillingSource(_links.Secrets, _links.Lock, new ConsoleClient(http, _time, log)), Cache,
            new UsageHistoryService(History, _time), _time, post ?? (action => action()));
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

    private Task<ProfileUsageState> UntilAsync(Func<ProfileUsageState, bool> condition) => UntilAsync(_profileId, condition);

    private async Task<ProfileUsageState> UntilAsync(Guid profileId, Func<ProfileUsageState, bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (_coordinator?.Current(profileId) is { } state && condition(state))
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
    public async Task ExpiredCliTokenWaitsWithoutRequestsWhileClaudeCodeRuns()
    {
        WriteLogin("at-1", Start.AddSeconds(30));
        var (coordinator, handler) = Create((_, _) => Usage(12));
        _links.ClaudeRunning();

        coordinator.Watch(Profile());
        await UntilAsync(s => s.Link == LinkStatus.WaitingForCliRefresh);
        await AdvanceAsync(TimeSpan.FromSeconds(65));

        Assert.Equal(0, UsageRequests(handler));
        Assert.Equal(0, _links.TokenRequests);

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

    private ProfileSettings ClaudeAiProfile(Guid id, ConsoleLink? console = null) => new()
    {
        Id = id,
        Name = "Browser",
        ClaudeAi = new ClaudeAiLink { OrganizationId = "org-1", LinkedAt = Start },
        Console = console,
    };

    [Fact]
    public async Task ProfilesArePolledTogetherEachWithItsOwnCredentials()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var browserId = Guid.NewGuid();

        coordinator.Watch(Profile());
        coordinator.Watch(ClaudeAiProfile(browserId));
        var browser = await UntilAsync(browserId, s => s.LastSuccessAt is not null);
        var cli = await UntilAsync(s => s.LastFailure is not null);

        // The browser profile has usage while the CLI profile's service fails; neither borrows the other.
        Assert.Equal(21, browser.Usage?.Usage.SessionPercentage);
        Assert.Equal(UsageSourceKind.ClaudeAi, browser.Source);
        Assert.Null(cli.Usage);
        Assert.Equal(FetchFailure.ServerError, cli.LastFailure);
        Assert.All(_transport.Requests, r => Assert.Equal(browserId, r.ProfileId));
        Assert.All(handler.Requests.Where(r => r.RequestUri?.AbsolutePath == "/api/oauth/usage"),
            r => Assert.Equal("at-1", r.Headers.Authorization?.Parameter));
        Assert.Equal("org-1", Cache.Load(browserId)?.AccountIdentity);
        Assert.Null(Cache.Load(_profileId));
        Assert.Equal(2, coordinator.Watched.Count);
    }

    [Fact]
    public async Task UnwatchedProfileStopsPolling()
    {
        var (coordinator, _) = Create((_, _) => Usage(12));
        var browserId = Guid.NewGuid();
        coordinator.Watch(ClaudeAiProfile(browserId));
        await UntilAsync(browserId, s => s.LastSuccessAt is not null);
        var before = _transport.Requests.Count;

        await coordinator.UnwatchAsync(browserId);
        await AdvanceAsync(TimeSpan.FromSeconds(65));

        Assert.Null(coordinator.Current(browserId));
        Assert.Equal(before, _transport.Requests.Count);
    }

    [Fact]
    public async Task SignedOutBrowserSessionIsNotAskedAgainUntilItChanges()
    {
        _signedIn = false;
        var (coordinator, _) = Create((_, _) => Usage(12));
        var browserId = Guid.NewGuid();
        int UsageAsks() => _transport.Requests.Count(r => r.Operation == ClaudeAiOperation.Usage);

        coordinator.Watch(ClaudeAiProfile(browserId));
        var signedOut = await UntilAsync(browserId, s => s.Link == LinkStatus.NotSignedIn);
        Assert.Equal(FetchFailure.CredentialsRejected, signedOut.LastFailure);
        await AdvanceAsync(TimeSpan.FromSeconds(95));
        Assert.Equal(1, UsageAsks());

        // A sign-in changes the session: the next check asks again.
        _signedIn = true;
        _transport.SessionChanged();
        await AdvanceAsync(TimeSpan.FromSeconds(31));
        var state = await UntilAsync(browserId, s => s.LastSuccessAt is not null);
        Assert.Equal(LinkStatus.Ready, state.Link);
        Assert.Equal(2, UsageAsks());
    }

    [Fact]
    public async Task ConsoleBillingIsFetchedAtMostEveryFiveMinutesUnlessRefreshedByHand()
    {
        var (coordinator, handler) = Create((request, _) =>
            ConsoleEndpoint.Answer(request.RequestUri!) is { } console ? Json(console.Status, console.Body) : Usage(12));
        var browserId = Guid.NewGuid();
        var bundle = _links.Secrets.Load(browserId);
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic-console", null);
        _links.Secrets.Save(browserId, bundle);
        static int Spend(ScriptedHandler h) => h.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/current_spend", StringComparison.Ordinal));

        coordinator.Watch(ClaudeAiProfile(browserId, new ConsoleLink { OrganizationId = "org-api", LinkedAt = Start }));
        var state = await UntilAsync(browserId, s => s.Api is not null && !s.ApiFromCache);

        Assert.Equal(1234, state.Api?.Usage.CurrentSpendCents);
        Assert.Equal(230m, state.Api?.Usage.ApiTokenCostCents);
        Assert.Equal("CLI key", state.Api?.Usage.SortedCostSources()[0].KeyName);
        Assert.Equal("org-api", Cache.LoadApi(browserId)?.OrganizationId);
        Assert.All(handler.Requests, r => Assert.Equal("sessionKey=sk-ant-sid01-synthetic-console", r.Headers.GetValues("Cookie").Single()));

        await AdvanceAsync(TimeSpan.FromSeconds(95));
        Assert.Equal(1, Spend(handler));

        coordinator.Refresh(browserId);
        await UntilAsync(browserId, _ => Spend(handler) == 2);

        await AdvanceAsync(RefreshCoordinator.BillingInterval + TimeSpan.FromSeconds(31));
        await UntilAsync(browserId, _ => Spend(handler) == 3);
    }

    [Fact]
    public async Task RenewedConsoleCookieIsAnnouncedWithTheLinkItWasFetchedFor()
    {
        var (coordinator, _) = Create((request, _) =>
        {
            if (ConsoleEndpoint.Answer(request.RequestUri!) is not { } console)
            {
                return Usage(12);
            }

            return Json(console.Status, console.Body, response => response.Headers.TryAddWithoutValidation(
                "Set-Cookie", "sessionKey=sk-ant-sid01-synthetic-renewed; Path=/; Secure; HttpOnly; Expires=Wed, 11 Nov 2026 12:00:00 GMT"));
        });
        var renewals = new ConcurrentQueue<(Guid ProfileId, DateTimeOffset LinkedAt, DateTimeOffset? Expiry)>();
        coordinator.ConsoleKeyRenewed += (profileId, linkedAt, expiry) => renewals.Enqueue((profileId, linkedAt, expiry));
        var browserId = Guid.NewGuid();
        var bundle = _links.Secrets.Load(browserId);
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic-console", Start.AddDays(7));
        _links.Secrets.Save(browserId, bundle);
        var linkedAt = Start.AddDays(-1);

        coordinator.Watch(ClaudeAiProfile(browserId, new ConsoleLink { OrganizationId = "org-api", LinkedAt = linkedAt }));
        await UntilAsync(browserId, s => s.Api is not null && !s.ApiFromCache);

        Assert.Equal((browserId, linkedAt, new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero)), Assert.Single(renewals));
    }

    [Fact]
    public async Task BillingFailureNeverStopsUsage()
    {
        var (coordinator, _) = Create((_, _) => Json(HttpStatusCode.Unauthorized, "{}"));
        var browserId = Guid.NewGuid();
        var bundle = _links.Secrets.Load(browserId);
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic-console", null);
        _links.Secrets.Save(browserId, bundle);

        coordinator.Watch(ClaudeAiProfile(browserId, new ConsoleLink { OrganizationId = "org-api", LinkedAt = Start }));
        var state = await UntilAsync(browserId, s => s.ApiFailure is not null && s.LastSuccessAt is not null);

        Assert.Equal(FetchFailure.CredentialsRejected, state.ApiFailure);
        Assert.Equal(21, state.Usage?.Usage.SessionPercentage);
        Assert.Null(state.LastFailure);
    }

    [Fact]
    public async Task SessionResetIsRecordedInTheHistory()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var resetAt = new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);
        var (coordinator, _) = Create((_, _) => _time.GetUtcNow() < resetAt
            ? Json(HttpStatusCode.OK, """{"five_hour":{"utilization":80,"resets_at":"2026-10-01T15:00:00Z"}}""")
            : Json(HttpStatusCode.OK, """{"five_hour":{"utilization":2,"resets_at":"2026-10-01T20:00:00Z"}}"""));
        coordinator.Watch(Profile());
        await UntilAsync(s => s.Usage?.Usage.SessionPercentage == 80);

        // The window resets at 15:00; the next check after it sees the new window.
        _time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        var state = await UntilAsync(s => s.Usage?.Usage.SessionPercentage == 2);

        Assert.Null(state.HistoryError);
        var reset = Assert.Single(History.Load(_profileId).SessionSnapshots, s => s.TriggeringResetTime == resetAt);
        Assert.Equal(80, reset.SessionPercentage);
    }

    [Fact]
    public async Task DamagedHistoryIsReportedAndNeverOverwritten()
    {
        WriteLogin("at-1", Start.AddHours(8));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "history"));
        File.WriteAllText(History.PathFor(_profileId), "{ not json");
        var (coordinator, _) = Create((_, _) => Usage(12));

        coordinator.Watch(Profile());
        var state = await UntilAsync(s => s.LastSuccessAt is not null);

        Assert.NotNull(state.HistoryError);
        Assert.Equal("{ not json", File.ReadAllText(History.PathFor(_profileId)));
    }

    /// <summary>
    /// Windows equivalent of upstream <c>testEncodeExcludesSecretsByDefault</c>: after polling a Claude Code
    /// profile and Console billing, no token or session key is in any file outside the encrypted secrets.
    /// </summary>
    [Fact]
    public async Task NoSecretReachesSettingsHistoryCacheOrLogFiles()
    {
        WriteLogin("sk-ant-oat01-synthetic-access", Start.AddHours(8));
        var (coordinator, _) = Create((request, _) =>
            ConsoleEndpoint.Answer(request.RequestUri!) is { } console ? Json(console.Status, console.Body) : Usage(12));
        var profile = Profile();
        profile.Console = new ConsoleLink { OrganizationId = "org-api", LinkedAt = Start };
        var bundle = _links.Secrets.Load(_profileId);
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic-console", null);
        _links.Secrets.Save(_profileId, bundle);
        new SettingsStore(Path.Combine(_temp.Path, "settings.json")).Save(new AppSettings { Profiles = [profile] });

        coordinator.Watch(profile);
        await UntilAsync(s => s.LastSuccessAt is not null && s.Api is not null);

        var secretsDirectory = Path.Combine(_temp.Path, "secrets");
        var plainFiles = Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(secretsDirectory, StringComparison.OrdinalIgnoreCase)
                        && !f.StartsWith(UserProfile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(plainFiles, f => f.EndsWith("requests.jsonl", StringComparison.Ordinal));
        Assert.Contains(plainFiles, f => f.EndsWith(".usage.json", StringComparison.Ordinal));
        Assert.Contains(plainFiles, f => f.EndsWith(".api.json", StringComparison.Ordinal));
        Assert.Contains(plainFiles, f => f.EndsWith(".history.json", StringComparison.Ordinal));
        foreach (var file in plainFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("synthetic-access", text, StringComparison.Ordinal);
            Assert.DoesNotContain("rt-sk-ant", text, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-console", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CodexProfileReadsTheCliFileAndNeverRenewsIt()
    {
        var home = Path.Combine(UserProfile, ".codex");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "auth.json"),
            """{"tokens":{"access_token":"codex-at","refresh_token":"codex-rt","account_id":"chatgpt-1"},"last_refresh":"2026-09-01T10:00:00Z"}""");
        var (coordinator, handler) = Create((_, _) => Json(HttpStatusCode.Unauthorized, "{}"));
        var codexId = Guid.NewGuid();

        coordinator.Watch(new ProfileSettings
        {
            Id = codexId,
            Name = "Codex",
            Provider = Provider.Codex.RawValue(),
            Codex = new CodexLink { CodexHome = home, AccountId = "chatgpt-1", LinkedAt = Start },
        });
        var state = await UntilAsync(codexId, s => s.Link == LinkStatus.WaitingForCliRefresh);

        Assert.Equal(UsageSourceKind.Codex, state.Source);
        Assert.Equal(0, _links.TokenRequests);
        Assert.Equal("Bearer codex-at", handler.Requests.Single().Headers.Authorization?.ToString());
        Assert.Contains("codex-rt", File.ReadAllText(Path.Combine(home, "auth.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageFetchedIsRaisedForFreshUsageOnlyNeverForTheCache()
    {
        WriteLogin("at-1", Start.AddHours(8));
        Cache.Save(new CachedUsage
        {
            ProfileId = _profileId, ObservedAt = Start.AddHours(-3), Source = "oauthUsage", AccountIdentity = "acc-1",
            Usage = new Core.Usage.UsageData { SessionPercentage = 99 },
        });
        var (coordinator, _) = Create((_, n) => n == 1 ? Json(HttpStatusCode.ServiceUnavailable, "{}") : Usage(12));
        var fetched = new ConcurrentQueue<(Guid Id, double? Session)>();
        coordinator.UsageFetched += (id, usage) => fetched.Enqueue((id, usage.Usage.SessionPercentage));

        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastFailure is not null);
        Assert.Empty(fetched);

        coordinator.Refresh(_profileId);
        await UntilAsync(s => s.LastSuccessAt is not null);
        Assert.Equal([(_profileId, 12.0)], fetched.Select(f => (f.Id, f.Session ?? -1)));
    }

    [Fact]
    public async Task CheckSoonChecksAtOnceAndIsDebounced()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, _) => Usage(12));

        coordinator.Watch(Profile(intervalSeconds: 300));
        await UntilAsync(s => s.LastSuccessAt is not null && !s.IsRefreshing);

        // Within the debounce, as when a resume and a network change arrive together.
        coordinator.CheckSoon();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, UsageRequests(handler));

        _time.Advance(RefreshCoordinator.CheckSoonDebounce);
        coordinator.CheckSoon();
        await UntilAsync(_ => UsageRequests(handler) == 2);
        Assert.Equal(2, UsageRequests(handler));
    }

    [Fact]
    public async Task CheckSoonNeverResendsARejectedLogin()
    {
        WriteLogin("at-1", Start.AddHours(8));
        var (coordinator, handler) = Create((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":{}}"""));

        coordinator.Watch(Profile());
        await UntilAsync(s => s.LastFailure == FetchFailure.CredentialsRejected && !s.IsRefreshing);
        _time.Advance(RefreshCoordinator.CheckSoonDebounce);
        coordinator.CheckSoon();
        await UntilAsync(s => s.LastAttemptAt > Start);

        // The login was looked at again, but the rejected token was not sent; a manual refresh may still retry it.
        Assert.Equal(1, UsageRequests(handler));
        coordinator.Refresh(_profileId);
        await UntilAsync(_ => UsageRequests(handler) == 2);
    }
}
