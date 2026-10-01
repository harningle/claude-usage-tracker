using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Status;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Usage;

public abstract record FetchResult
{
    public required DateTimeOffset At { get; init; }
}

public sealed record FetchSucceeded(UsageData Usage) : FetchResult;

/// <param name="Status">The HTTP status, or null when no response arrived.</param>
/// <param name="RetryAfter">The server's retry guidance, when it gave any.</param>
public sealed record FetchFailed(FetchFailure Failure, int? Status, TimeSpan? RetryAfter) : FetchResult;

/// <summary>
/// The read-only CLI usage request, <c>GET https://api.anthropic.com/api/oauth/usage</c>, with upstream's
/// headers (verified live in Milestone 1). This is the only usage request the app makes for CLI
/// profiles: it never posts to <c>/v1/messages</c>, so polling generates no model requests (docs/parity.md
/// difference 11).
/// </summary>
public sealed class UsageClient(HttpClient http, TimeProvider time, RequestLog log, TimeSpan? requestTimeout = null)
{
    public static readonly Uri OAuthUsageUrl = new("https://api.anthropic.com/api/oauth/usage");

    /// <summary>Upstream's CLI OAuth user agent (<c>ClaudeAPIService.buildAuthenticatedRequest</c>).</summary>
    public const string CliUserAgent = "claude-code/2.1.5";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // The server's requested wait binds every usage request for the same account from this client: polling,
    // a manual refresh and a setup test alike. Other accounts are not held back by it.
    private readonly Lock _holdGate = new();
    private readonly Dictionary<string, DateTimeOffset> _notBefore = [];

    /// <param name="holdKey">The account the request is for; a wait the server asked for applies per account.</param>
    public async Task<FetchResult> FetchUsageAsync(string accessToken, CancellationToken cancellation, string holdKey = "")
    {
        var now = time.GetUtcNow();
        lock (_holdGate)
        {
            if (_notBefore.TryGetValue(holdKey, out var notBefore) && now < notBefore)
            {
                // Nothing is sent until the wait is over; the caller learns how long remains.
                return new FetchFailed(FetchFailure.RateLimited, null, notBefore - now) { At = now };
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, OAuthUsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", CliUserAgent);
        request.Headers.Accept.ParseAdd("application/json");

        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(requestTimeout ?? RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return Fail(FetchFailure.Timeout, null, null, started, holdKey);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return Fail(FetchFailure.Network, null, null, started, holdKey);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string body;
            try
            {
                body = await HttpResponses.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return Fail(FetchFailure.Timeout, status, null, started, holdKey);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                // A connection lost mid-body surfaces as HttpIOException, an IOException.
                return Fail(FetchFailure.Network, status, null, started, holdKey);
            }

            if (HttpResponses.IsChallenge(response, body))
            {
                return Fail(FetchFailure.Challenge, status, null, started, holdKey);
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    try
                    {
                        var usage = ClaudeUsageResponse.Parse(body);
                        Log(status, started, null);
                        return new FetchSucceeded(usage) { At = time.GetUtcNow() };
                    }
                    catch (UsageParseException)
                    {
                        return Fail(FetchFailure.InvalidResponse, status, null, started, holdKey);
                    }

                case HttpStatusCode.Unauthorized:
                    return Fail(FetchFailure.CredentialsRejected, status, null, started, holdKey);

                case HttpStatusCode.Forbidden:
                    // Only an API authentication error means the login itself was refused. An HTML 403 is a
                    // block in front of the API; other API errors (such as a permission error for an account
                    // type) leave the token usable.
                    return Fail(HttpResponses.IsHtml(response) ? FetchFailure.Challenge
                        : ErrorType(body) == "authentication_error" ? FetchFailure.CredentialsRejected
                        : FetchFailure.Forbidden, status, null, started, holdKey);

                case HttpStatusCode.TooManyRequests:
                    return Fail(FetchFailure.RateLimited, status, HttpResponses.RetryAfter(response, time.GetUtcNow()), started, holdKey);

                case >= HttpStatusCode.InternalServerError:
                    return Fail(FetchFailure.ServerError, status, HttpResponses.RetryAfter(response, time.GetUtcNow()), started, holdKey);

                default:
                    return Fail(FetchFailure.InvalidResponse, status, null, started, holdKey);
            }
        }
    }

    /// <summary>A provider's Statuspage summary; null when it cannot be fetched or read.</summary>
    public async Task<ServiceStatus?> FetchStatusAsync(CancellationToken cancellation, string statusUrl = ServiceStatus.ClaudeStatusUrl)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await http.GetAsync(statusUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var body = await HttpResponses.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var parsed = response.IsSuccessStatusCode ? ServiceStatus.Parse(body) : null;
            LogEntry("status", "/api/v2/status.json", (int)response.StatusCode, started, parsed is null ? "invalidResponse" : null);
            return parsed;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            LogEntry("status", "/api/v2/status.json", null, started, "timeout");
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            LogEntry("status", "/api/v2/status.json", null, started, "network");
            return null;
        }
    }

    /// <summary>The API error type, e.g. <c>authentication_error</c>, from an error body; null otherwise.</summary>
    private static string? ErrorType(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error
                   && error["type"] is JsonValue type && type.TryGetValue(out string? text)
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private FetchFailed Fail(FetchFailure failure, int? status, TimeSpan? retryAfter, long started, string holdKey)
    {
        Log(status, started, failure);
        var now = time.GetUtcNow();
        if (retryAfter is not null || failure == FetchFailure.RateLimited)
        {
            // A 429 without guidance waits as long as polling would (RefreshPolicy.DefaultRateLimitWait).
            var until = now + (retryAfter ?? RefreshPolicy.DefaultRateLimitWait);
            lock (_holdGate)
            {
                if (!_notBefore.TryGetValue(holdKey, out var held) || until > held)
                {
                    _notBefore[holdKey] = until;
                }
            }
        }

        return new FetchFailed(failure, status, retryAfter) { At = now };
    }

    private void Log(int? status, long started, FetchFailure? failure) =>
        LogEntry("oauthUsage", "/api/oauth/usage", status, started, failure is { } f ? HttpResponses.Category(f) : null);

    private void LogEntry(string operation, string route, int? status, long started, string? error) =>
        log.Append(new RequestLogEntry(
            time.GetUtcNow(), operation, "GET", route, status,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "HttpClient", error));
}
