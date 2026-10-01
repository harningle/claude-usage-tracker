using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Status;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>Records requests and answers each with a scripted response. No real network access.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static ScriptedHandler Returning(HttpStatusCode status, string body, string mediaType = "application/json",
        Action<HttpResponseMessage>? configure = null) =>
        new((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}

public sealed class UsageClientTests : IDisposable
{
    private const string Token = "sk-ant-oat01-synthetic-token-value";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _temp.Dispose();

    private RequestLog Log => new(_temp.Path);

    private (UsageClient Client, ScriptedHandler Handler) Client(ScriptedHandler handler, TimeSpan? timeout = null) =>
        (new UsageClient(new HttpClient(handler), _time, Log, timeout), handler);

    [Fact]
    public async Task SendsTheReadOnlyRequestWithUpstreamHeaders()
    {
        var (client, handler) = Client(ScriptedHandler.Returning(HttpStatusCode.OK, """{"five_hour":{"utilization":12}}"""));

        var result = await client.FetchUsageAsync(Token, CancellationToken.None);

        var success = Assert.IsType<FetchSucceeded>(result);
        Assert.Equal(12, success.Usage.SessionPercentage);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(Token, request.Headers.Authorization?.Parameter);
        Assert.Equal("oauth-2025-04-20", Assert.Single(request.Headers.GetValues("anthropic-beta")));
        Assert.Equal("claude-code/2.1.5", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FetchFailure.CredentialsRejected)]
    [InlineData(HttpStatusCode.Forbidden, FetchFailure.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, FetchFailure.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FetchFailure.ServerError)]
    [InlineData(HttpStatusCode.NotFound, FetchFailure.InvalidResponse)]
    public async Task StatusCodesAreClassified(HttpStatusCode status, FetchFailure expected)
    {
        var (client, _) = Client(ScriptedHandler.Returning(status, """{"error":{"type":"x"}}"""));

        var failed = Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None));

        Assert.Equal(expected, failed.Failure);
        Assert.Equal((int)status, failed.Status);
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"type":"authentication_error","message":"x"}}""", "application/json", FetchFailure.CredentialsRejected)]
    [InlineData("""{"type":"error","error":{"type":"permission_error","message":"x"}}""", "application/json", FetchFailure.Forbidden)]
    [InlineData("<html><body>Access denied</body></html>", "text/html", FetchFailure.Challenge)]
    public async Task ForbiddenIsRejectedOnlyForAuthenticationErrors(string body, string mediaType, FetchFailure expected)
    {
        var (client, _) = Client(ScriptedHandler.Returning(HttpStatusCode.Forbidden, body, mediaType));

        Assert.Equal(expected, Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task ConnectionLostMidBodyIsNetwork()
    {
        var (client, _) = Client(new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BreakingStream()),
        })));

        Assert.Equal(FetchFailure.Network,
            Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task ServerErrorCarriesRetryAfter()
    {
        var (client, _) = Client(ScriptedHandler.Returning(HttpStatusCode.ServiceUnavailable, "{}",
            configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "600")));

        Assert.Equal(TimeSpan.FromSeconds(600),
            Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None)).RetryAfter);
    }

    /// <summary>A body whose connection drops after the headers, as .NET reports it (an IOException).</summary>
    private sealed class BreakingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("connection reset");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset");
    }

    [Fact]
    public async Task RateLimitCarriesRetryAfter()
    {
        var (client, _) = Client(ScriptedHandler.Returning(HttpStatusCode.TooManyRequests, "{}",
            configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "120")));

        var failed = Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None));

        Assert.Equal(FetchFailure.RateLimited, failed.Failure);
        Assert.Equal(TimeSpan.FromSeconds(120), failed.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "120", 120)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "120", 120)]
    [InlineData(HttpStatusCode.TooManyRequests, null, 300)]
    public async Task ServerWaitHoldsEveryLaterRequest(HttpStatusCode status, string? retryAfter, int holdSeconds)
    {
        var calls = 0;
        var (client, handler) = Client(new ScriptedHandler((_, _) =>
        {
            var response = Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(status) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"five_hour":{"utilization":12}}""") };
            if (calls == 1 && retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return Task.FromResult(response);
        }));
        await client.FetchUsageAsync(Token, CancellationToken.None);

        _time.Now += TimeSpan.FromSeconds(holdSeconds - 10);
        var held = Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None));
        Assert.Equal(FetchFailure.RateLimited, held.Failure);
        Assert.Null(held.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), held.RetryAfter);
        Assert.Single(handler.Requests);

        _time.Now += TimeSpan.FromSeconds(10);
        Assert.IsType<FetchSucceeded>(await client.FetchUsageAsync(Token, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CloudflareHeaderIsAChallengeNotRejectedCredentials()
    {
        var (client, _) = Client(ScriptedHandler.Returning(HttpStatusCode.Forbidden, "<html>blocked</html>", "text/html",
            r => r.Headers.TryAddWithoutValidation("cf-mitigated", "challenge")));

        Assert.Equal(FetchFailure.Challenge,
            Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task ChallengePageIsAChallenge()
    {
        var (client, _) = Client(ScriptedHandler.Returning(HttpStatusCode.Forbidden,
            "<!DOCTYPE html><title>Just a moment...</title>", "text/html"));

        Assert.Equal(FetchFailure.Challenge,
            Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task SuccessStatusWithoutUsageJsonIsInvalid()
    {
        var (client, _) = Client(ScriptedHandler.Returning(HttpStatusCode.OK, "<html>portal</html>", "text/html"));

        Assert.Equal(FetchFailure.InvalidResponse,
            Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task ConnectionFailureIsNetwork()
    {
        var (client, _) = Client(new ScriptedHandler((_, _) => throw new HttpRequestException("refused")));

        var failed = Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None));

        Assert.Equal(FetchFailure.Network, failed.Failure);
        Assert.Null(failed.Status);
    }

    [Fact]
    public async Task SlowServerTimesOut()
    {
        var (client, _) = Client(new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), TimeSpan.FromMilliseconds(50));

        Assert.Equal(FetchFailure.Timeout,
            Assert.IsType<FetchFailed>(await client.FetchUsageAsync(Token, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAFailure()
    {
        var (client, _) = Client(new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchUsageAsync(Token, cancellation.Token));
    }

    [Fact]
    public async Task LogHoldsOnlyAllowlistedMetadata()
    {
        var (client, _) = Client(ScriptedHandler.Returning(HttpStatusCode.Unauthorized,
            """{"error":{"message":"token sk-ant-oat01-synthetic-token-value invalid"}}"""));

        await client.FetchUsageAsync(Token, CancellationToken.None);

        var line = Assert.Single(File.ReadAllLines(Log.FilePath));
        Assert.DoesNotContain("synthetic-token", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", line, StringComparison.Ordinal);
        Assert.Contains("\"operation\":\"oauthUsage\"", line, StringComparison.Ordinal);
        Assert.Contains("\"route\":\"/api/oauth/usage\"", line, StringComparison.Ordinal);
        Assert.Contains("\"status\":401", line, StringComparison.Ordinal);
        Assert.Contains("\"errorCategory\":\"credentialsRejected\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusPageIsParsed()
    {
        var (client, handler) = Client(ScriptedHandler.Returning(HttpStatusCode.OK,
            """{"status":{"indicator":"minor","description":"Partially Degraded Service"}}"""));

        var status = await client.FetchStatusAsync(CancellationToken.None);

        Assert.Equal(new ServiceStatus(ServiceStatusIndicator.Minor, "Partially Degraded Service"), status);
        Assert.Equal(ServiceStatus.ClaudeStatusUrl, handler.Requests[0].RequestUri?.ToString());
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task UnavailableStatusPageIsNull()
    {
        var (client, _) = Client(new ScriptedHandler((_, _) => throw new HttpRequestException("offline")));

        Assert.Null(await client.FetchStatusAsync(CancellationToken.None));
    }
}
