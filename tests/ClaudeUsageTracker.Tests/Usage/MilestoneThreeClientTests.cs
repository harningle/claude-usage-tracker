using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.Browser;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Refresh;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>claude.ai over the browser transport: classification, and that a challenge is never an authentication verdict.</summary>
public sealed class ClaudeAiClientTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Guid _profileId = Guid.NewGuid();

    public void Dispose() => _temp.Dispose();

    private ClaudeAiClient Client(Func<ClaudeAiOperation, BrowserResponse> respond) =>
        new(new ScriptedTransport((_, operation, _) => respond(operation)), _time, new RequestLog(_temp.Path));

    [Fact]
    public async Task UsageCombinesExtraUsageAndTheCreditBalance()
    {
        var client = Client(operation => operation switch
        {
            ClaudeAiOperation.Usage => ScriptedTransport.Ok("""{"five_hour":{"utilization":21},"seven_day":{"utilization":40}}"""),
            ClaudeAiOperation.OverageSpendLimit => ScriptedTransport.Ok("""{"is_enabled":true,"monthly_credit_limit":5000,"used_credits":186,"currency":"USD"}"""),
            _ => ScriptedTransport.Ok("""{"remaining_balance":1200,"currency":"usd"}"""),
        });

        var result = Assert.IsType<FetchSucceeded>(await client.FetchUsageAsync(_profileId, "org-1", CancellationToken.None));

        Assert.Equal(21, result.Usage.SessionPercentage);
        Assert.Equal(40, result.Usage.WeeklyPercentage);
    }

    [Fact]
    public async Task OptionalEndpointsThatFailLeaveTheirValuesMissing()
    {
        var client = Client(operation => operation == ClaudeAiOperation.Usage
            ? ScriptedTransport.Ok("""{"five_hour":{"utilization":21}}""")
            : ScriptedTransport.Status(500));

        var result = Assert.IsType<FetchSucceeded>(await client.FetchUsageAsync(_profileId, "org-1", CancellationToken.None));

        Assert.Null(result.Usage.CostLimit);
        Assert.Null(result.Usage.OverageBalance);
    }

    [Theory]
    [InlineData(401, "application/json", false, null, FetchFailure.CredentialsRejected)]
    [InlineData(403, "application/json", false, null, FetchFailure.CredentialsRejected)]
    [InlineData(403, "text/html", false, "<html>blocked</html>", FetchFailure.Challenge)]
    [InlineData(200, "text/html", false, "<title>Just a moment...</title>", FetchFailure.Challenge)]
    [InlineData(403, "application/json", true, null, FetchFailure.Challenge)]
    [InlineData(429, "application/json", false, null, FetchFailure.RateLimited)]
    [InlineData(502, "application/json", false, null, FetchFailure.ServerError)]
    public async Task ResponsesAreClassified(int status, string contentType, bool mitigated, string? body, FetchFailure expected)
    {
        var client = Client(_ => new BrowserResponse(status, contentType, mitigated, body ?? "{}", null, null));

        var result = Assert.IsType<FetchFailed>(await client.FetchUsageAsync(_profileId, "org-1", CancellationToken.None));

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task BrowserThatCouldNotSendIsANetworkFailure()
    {
        var client = Client(_ => new BrowserResponse(null, null, false, null, null, "navigation"));

        Assert.Equal(FetchFailure.Network, Assert.IsType<FetchFailed>(await client.FetchUsageAsync(_profileId, "org-1", CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task RateLimitKeepsTheServersWait()
    {
        var client = Client(_ => new BrowserResponse(429, "application/json", false, "{}", "120", null));

        Assert.Equal(TimeSpan.FromSeconds(120), Assert.IsType<FetchFailed>(await client.FetchUsageAsync(_profileId, "org-1", CancellationToken.None)).RetryAfter);
    }

    [Fact]
    public async Task OrganizationsAreListedAndAnythingElseIsInvalid()
    {
        Assert.Equal(["org-1", "org-2"], (await Client(ScriptedTransport.SignedIn).FetchOrganizationsAsync(_profileId, CancellationToken.None))
            .Organizations?.Select(o => o.Uuid));
        Assert.Equal(FetchFailure.InvalidResponse,
            (await Client(_ => ScriptedTransport.Ok("""{"error":"x"}""")).FetchOrganizationsAsync(_profileId, CancellationToken.None)).Failure?.Failure);
    }

    [Theory]
    [InlineData("org-1")]
    [InlineData("0f2b8a51-1c7e-4d2a-9a4e-8f1b2c3d4e5f")]
    public void AllowlistedPathsTakeOnlyWellFormedOrganizationIds(string id) =>
        Assert.Equal($"/api/organizations/{id}/usage", BrowserSessions.PathFor(ClaudeAiOperation.Usage, id));

    [Theory]
    [InlineData("../admin")]
    [InlineData("org-1?x=1")]
    [InlineData("org/1")]
    [InlineData("")]
    public void MalformedOrganizationIdsNeverReachAUrl(string id) =>
        Assert.Throws<ArgumentException>(() => BrowserSessions.PathFor(ClaudeAiOperation.Usage, id));
}

/// <summary>The Anthropic Console over <c>HttpClient</c>: the session cookie per request, renewal and classification.</summary>
public sealed class ConsoleClientTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Key = "sk-ant-sid01-synthetic-console-key";
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _temp.Dispose();

    private RequestLog Log => new(_temp.Path);

    private (ConsoleClient Client, ScriptedHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new ScriptedHandler((request, _) => Task.FromResult(respond?.Invoke(request)
            ?? (ConsoleEndpoint.Answer(request.RequestUri!) is { } answer ? LinkHarness.Json(answer.Status, answer.Body) : LinkHarness.Json(HttpStatusCode.NotFound, "{}"))));
        return (new ConsoleClient(new HttpClient(handler), _time, Log, "Mozilla/5.0 test"), handler);
    }

    [Fact]
    public async Task BillingIsBuiltFromFourReadOnlyRequests()
    {
        var (client, handler) = Create();

        var result = await client.FetchApiUsageAsync(Key, "org-api", CancellationToken.None);

        Assert.Equal(1234, result.Value?.CurrentSpendCents);
        Assert.Equal(8766, result.Value?.PrepaidCreditsCents);
        Assert.Equal(230m, result.Value?.ApiTokenCostCents);
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("platform.claude.com", r.RequestUri?.Host);
            Assert.Equal("sessionKey=" + Key, r.Headers.GetValues("Cookie").Single());
        });
        Assert.Contains(handler.Requests, r => r.RequestUri!.Query.Contains("starting_on=2026-10-01", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingCostsLeaveTheCostFieldsMissing()
    {
        var (client, _) = Create(request => request.RequestUri!.AbsolutePath.EndsWith("/usage_cost", StringComparison.Ordinal)
            ? LinkHarness.Json(HttpStatusCode.InternalServerError, "{}")
            : LinkHarness.Json(ConsoleEndpoint.Answer(request.RequestUri)!.Value.Status, ConsoleEndpoint.Answer(request.RequestUri)!.Value.Body));

        var result = await client.FetchApiUsageAsync(Key, "org-api", CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Null(result.Value.ApiTokenCostCents);
    }

    [Fact]
    public async Task RenewedSessionCookieIsReturnedForSaving()
    {
        var (client, _) = Create(request =>
        {
            var response = LinkHarness.Json(HttpStatusCode.OK, ConsoleEndpoint.Answer(request.RequestUri!)!.Value.Body);
            response.Headers.TryAddWithoutValidation("Set-Cookie", "sessionKey=sk-ant-sid01-synthetic-renewed; Path=/; Secure; HttpOnly; Expires=Wed, 11 Nov 2026 12:00:00 GMT");
            return response;
        });

        var result = await client.FetchOrganizationsAsync(Key, CancellationToken.None);

        Assert.Equal("sk-ant-sid01-synthetic-renewed", result.RenewedKey?.SessionKey);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero), result.RenewedKey?.Expires);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "application/json", FetchFailure.CredentialsRejected)]
    [InlineData(HttpStatusCode.Forbidden, "text/html", FetchFailure.Challenge)]
    [InlineData(HttpStatusCode.TooManyRequests, "application/json", FetchFailure.RateLimited)]
    public async Task FailuresAreClassified(HttpStatusCode status, string mediaType, FetchFailure expected)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, mediaType) });

        Assert.Equal(expected, (await client.FetchOrganizationsAsync(Key, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task LogHoldsRoutesWithoutTheKeyOrOrganization()
    {
        var (client, _) = Create();

        await client.FetchApiUsageAsync(Key, "org-api", CancellationToken.None);

        var log = File.ReadAllText(Log.FilePath);
        Assert.DoesNotContain("synthetic-console-key", log, StringComparison.Ordinal);
        Assert.DoesNotContain("org-api", log, StringComparison.Ordinal);
        Assert.Contains("/api/organizations/{org}/current_spend", log, StringComparison.Ordinal);
    }
}

/// <summary>Refresh grants: upstream's request shapes, and the error codes that decide whether a login is rejected.</summary>
public sealed class OAuthTokenClientTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _temp.Dispose();

    private RequestLog Log => new(_temp.Path);

    private OAuthTokenClient Client(ScriptedHandler handler) => new(new HttpClient(handler), _time, Log);

    [Fact]
    public async Task ClaudeGrantIsFormEncodedAndParsed()
    {
        string? body = null;
        var handler = new ScriptedHandler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return LinkHarness.Grant("at-2", "rt-2");
        });

        var outcome = await Client(handler).RefreshClaudeAsync("rt-synthetic-1", CancellationToken.None);

        Assert.Equal(RefreshResult.Succeeded, outcome.Result);
        Assert.Equal("at-2", outcome.Response?.AccessToken);
        Assert.Equal("application/x-www-form-urlencoded", handler.Requests.Single().Content?.Headers.ContentType?.MediaType);
        Assert.Equal("grant_type=refresh_token&refresh_token=rt-synthetic-1&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e", body);
        // Upstream sends no User-Agent here, and the endpoint refuses the usage request's one with a 429.
        Assert.Empty(handler.Requests.Single().Headers.UserAgent);
        Assert.DoesNotContain("rt-synthetic-1", File.ReadAllText(Log.FilePath), StringComparison.Ordinal);
        Assert.DoesNotContain("at-2", File.ReadAllText(Log.FilePath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""", RefreshResult.LoginRejected)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_request"}""", RefreshResult.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", RefreshResult.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"type":"rate_limit_error","message":"Rate limited. Please try again later."}}""", RefreshResult.Failed)]
    [InlineData(HttpStatusCode.OK, """{"access_token":""}""", RefreshResult.Failed)]
    [InlineData(HttpStatusCode.Forbidden, "<html>Just a moment...</html>", RefreshResult.Failed)]
    public async Task ClaudeOutcomesAreClassified(HttpStatusCode status, string body, RefreshResult expected) =>
        Assert.Equal(expected, (await Client(ScriptedHandler.Returning(status, body)).RefreshClaudeAsync("rt-1", CancellationToken.None)).Result);

    [Fact]
    public async Task NetworkFailureIsAFailedRenewalNotARejection()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("offline"));

        var outcome = await Client(handler).RefreshClaudeAsync("rt-1", CancellationToken.None);

        Assert.Equal(RefreshResult.Failed, outcome.Result);
        Assert.Equal(FetchFailure.Network, outcome.Failure);
    }

    [Fact]
    public async Task CodexGrantIsJsonAndRenewsTheCredentials()
    {
        var credentials = CodexAuthJson.Parse("""{"tokens":{"access_token":"codex-at","refresh_token":"codex-rt","account_id":"chatgpt-1"}}""")!;
        var handler = ScriptedHandler.Returning(HttpStatusCode.OK, """{"access_token":"codex-at-2","refresh_token":"codex-rt-2"}""");

        var outcome = await Client(handler).RefreshCodexAsync(credentials, CancellationToken.None);

        Assert.Equal(RefreshResult.Succeeded, outcome.Result);
        Assert.Equal("codex-at-2", outcome.Credentials?.AccessToken);
        Assert.Equal("https://auth.openai.com/oauth/token", handler.Requests.Single().RequestUri?.ToString());
        Assert.Equal("application/json", handler.Requests.Single().Content?.Headers.ContentType?.MediaType);
    }
}

/// <summary>The network log: allowlisted fields only, bounded, with clear and export.</summary>
public sealed class RequestLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static RequestLogEntry Entry(int n) => new(Now.AddSeconds(n), "claudeAiUsage", "GET", "/api/organizations/{org}/usage", 200, n, "WebView2", null);

    [Fact]
    public void ReadsNewestFirstAndSkipsForeignLines()
    {
        var log = new RequestLog(_temp.Path);
        log.Append(Entry(1));
        File.AppendAllText(log.FilePath, "not json\n{\"other\":1}\n");
        log.Append(Entry(2));

        Assert.Equal([2L, 1L], log.ReadRecent(10).Select(e => e.DurationMs));
        Assert.Single(log.ReadRecent(1));
    }

    [Fact]
    public void ClearRemovesEveryEntryAndNotifies()
    {
        var log = new RequestLog(_temp.Path, maxBytes: 200);
        for (var i = 0; i < 10; i++)
        {
            log.Append(Entry(i));
        }

        var changed = 0;
        log.Changed += () => changed++;
        log.Clear();

        Assert.Empty(log.ReadRecent(100));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ExportHoldsOnlyTheLoggedFieldsOldestFirst()
    {
        var log = new RequestLog(_temp.Path);
        log.Append(Entry(1));
        log.Append(Entry(2));
        // A line someone added by hand with extra members never leaves with the export.
        File.AppendAllText(log.FilePath, """{"at":"2026-10-01T12:00:03+00:00","operation":"x","method":"GET","route":"/r","durationMs":3,"transport":"HttpClient","cookie":"sessionKey=secret"}""" + "\n");
        var path = Path.Combine(_temp.Path, "export.jsonl");

        var count = log.Export(path);

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, count);
        Assert.Contains("\"durationMs\":1", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void LogStaysBounded()
    {
        var log = new RequestLog(_temp.Path, maxBytes: 1000);
        for (var i = 0; i < 200; i++)
        {
            log.Append(Entry(i));
        }

        Assert.True(new FileInfo(log.FilePath).Length <= 1200);
        Assert.True(log.ReadRecent(1000).Count < 200);
    }
}
