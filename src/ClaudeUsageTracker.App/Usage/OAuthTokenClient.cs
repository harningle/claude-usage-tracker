using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.Core.ClaudeCode;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Refresh;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>How a refresh grant ended.</summary>
public enum RefreshResult
{
    /// <summary>New tokens were issued. The refresh token that was sent may no longer work.</summary>
    Succeeded,

    /// <summary>The login service says this login is no longer valid; only signing in again helps.</summary>
    LoginRejected,

    /// <summary>No usable answer (offline, timeout, a server error, a refused request). The login may still be valid.</summary>
    Failed,
}

/// <summary>
/// The outcome of a Claude Code refresh grant. A class rather than a record so no generated
/// <c>ToString</c> can print the tokens.
/// </summary>
public sealed class ClaudeRefreshOutcome(
    RefreshResult result, OAuthRefreshResponse? response, DateTimeOffset at, FetchFailure? failure, int? status, string? errorCode)
{
    public RefreshResult Result { get; } = result;

    /// <summary>Set only for <see cref="RefreshResult.Succeeded"/>.</summary>
    public OAuthRefreshResponse? Response { get; } = response;

    /// <summary>When the answer arrived; expiry is counted from here.</summary>
    public DateTimeOffset At { get; } = at;

    public FetchFailure? Failure { get; } = failure;

    public int? Status { get; } = status;

    /// <summary>The service's error code, e.g. <c>invalid_grant</c>; an identifier, never response text.</summary>
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>The outcome of a Codex refresh grant.</summary>
public sealed class CodexRefreshOutcome(RefreshResult result, CodexCredentials? credentials, FetchFailure? failure, int? status)
{
    public RefreshResult Result { get; } = result;

    /// <summary>The renewed credentials; set only for <see cref="RefreshResult.Succeeded"/>.</summary>
    public CodexCredentials? Credentials { get; } = credentials;

    public FetchFailure? Failure { get; } = failure;

    public int? Status { get; } = status;
}

/// <summary>
/// Sends OAuth <c>refresh_token</c> grants. Callers hold the app-wide credential lock, and decide
/// beforehand whether the tracker may renew the login at all (ownership and the process guard): a
/// successful grant uses the refresh token up. Nothing of the request or response is logged except the
/// usual allowlisted metadata.
/// </summary>
public sealed class OAuthTokenClient(HttpClient http, TimeProvider time, RequestLog log)
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Renews a Claude Code login (upstream <c>performTokenRefresh</c>).</summary>
    public async Task<ClaudeRefreshOutcome> RefreshClaudeAsync(string refreshToken, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaudeOAuth.TokenUrl)
        {
            Content = new StringContent(ClaudeOAuth.RefreshRequestBody(refreshToken), Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UsageClient.CliUserAgent);
        request.Headers.Accept.ParseAdd("application/json");

        var (failure, status, body) = await SendAsync(request, "claudeTokenRefresh", "/v1/oauth/token", cancellation).ConfigureAwait(false);
        var at = time.GetUtcNow();
        if (failure is null && ClaudeOAuth.ParseRefreshResponse(body!) is { } response)
        {
            return new ClaudeRefreshOutcome(RefreshResult.Succeeded, response, at, null, status, null);
        }

        var code = body is null ? null : OAuthErrors.Code(body);
        return code == "invalid_grant"
            ? new ClaudeRefreshOutcome(RefreshResult.LoginRejected, null, at, FetchFailure.CredentialsRejected, status, code)
            : new ClaudeRefreshOutcome(RefreshResult.Failed, null, at, failure ?? FetchFailure.InvalidResponse, status, code);
    }

    /// <summary>Renews pasted Codex credentials (upstream <c>CodexAuthService.refresh</c>).</summary>
    public async Task<CodexRefreshOutcome> RefreshCodexAsync(CodexCredentials credentials, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CodexOAuth.TokenUrl)
        {
            Content = new StringContent(CodexOAuth.RefreshRequestBody(credentials.RefreshToken), Encoding.UTF8, "application/json"),
        };

        var (failure, status, body) = await SendAsync(request, "codexTokenRefresh", "/oauth/token", cancellation).ConfigureAwait(false);
        if (failure is null && CodexOAuth.ApplyRefreshResponse(credentials, body!, time.GetUtcNow()) is { } refreshed)
        {
            return new CodexRefreshOutcome(RefreshResult.Succeeded, refreshed, null, status);
        }

        return body is not null && CodexOAuth.IsLoginExpired(status, body)
            ? new CodexRefreshOutcome(RefreshResult.LoginRejected, null, FetchFailure.CredentialsRejected, status)
            : new CodexRefreshOutcome(RefreshResult.Failed, null, failure ?? FetchFailure.InvalidResponse, status);
    }

    /// <summary>
    /// Sends a grant. A null failure means a 200 answer whose body is returned; otherwise the body is
    /// returned when one arrived, so the caller can read the service's error code.
    /// </summary>
    private async Task<(FetchFailure? Failure, int? Status, string? Body)> SendAsync(
        HttpRequestMessage request, string operation, string route, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(RequestTimeout);
        FetchFailure? failure;
        int? status = null;
        string? body = null;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await HttpResponses.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            failure = HttpResponses.IsChallenge(response, body) ? FetchFailure.Challenge
                : response.StatusCode == HttpStatusCode.OK ? null
                : response.StatusCode == HttpStatusCode.TooManyRequests ? FetchFailure.RateLimited
                : response.StatusCode >= HttpStatusCode.InternalServerError ? FetchFailure.ServerError
                : FetchFailure.InvalidResponse;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            failure = FetchFailure.Timeout;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            failure = FetchFailure.Network;
        }

        log.Append(new RequestLogEntry(
            time.GetUtcNow(), operation, "POST", route, status,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "HttpClient", failure is { } f ? HttpResponses.Category(f) : null));
        return (failure, status, body);
    }
}
