using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>
/// The Codex usage request, <c>GET {base}/wham/usage</c> (upstream <c>CodexAPIService.fetchUsage</c>): a
/// read-only request with the Codex login's bearer token and account ID.
/// </summary>
public sealed class CodexClient(HttpClient http, TimeProvider time, RequestLog log)
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <param name="configContents">The contents of the Codex <c>config.toml</c>, which may name another base URL; null without one.</param>
    /// <param name="previous">The profile's last usage, whose still-future reset times are reused when the answer has none.</param>
    public async Task<FetchResult> FetchUsageAsync(
        CodexCredentials credentials, string? configContents, UsageData? previous, CancellationToken cancellation)
    {
        var url = CodexApi.UsageUrl(configContents);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        if (credentials.ResolvedAccountId is { Length: > 0 } accountId)
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(RequestTimeout);
        // Only the fixed path is logged: the host may be a private proxy from config.toml.
        var route = url.AbsolutePath.EndsWith("/wham/usage", StringComparison.Ordinal) ? "/wham/usage" : "/api/codex/usage";
        FetchResult Done(FetchFailure? failure, int? status, TimeSpan? retryAfter, UsageData? usage)
        {
            var now = time.GetUtcNow();
            log.Append(new RequestLogEntry(
                now, "codexUsage", "GET", route, status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "HttpClient",
                failure is { } f ? HttpResponses.Category(f) : null));
            return failure is { } kind ? new FetchFailed(kind, status, retryAfter) { At = now } : new FetchSucceeded(usage!) { At = now };
        }

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await HttpResponses.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (HttpResponses.IsChallenge(response, body))
            {
                return Done(FetchFailure.Challenge, status, null, null);
            }

            switch (response.StatusCode)
            {
                case >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices:
                    try
                    {
                        return Done(null, status, null, CodexApi.MapToUsage(CodexUsageResponse.Parse(body), previous, time.GetUtcNow()));
                    }
                    catch (JsonException)
                    {
                        return Done(FetchFailure.InvalidResponse, status, null, null);
                    }

                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return Done(HttpResponses.IsHtml(response) ? FetchFailure.Challenge : FetchFailure.CredentialsRejected, status, null, null);
                case HttpStatusCode.TooManyRequests:
                    return Done(FetchFailure.RateLimited, status, HttpResponses.RetryAfter(response, time.GetUtcNow()), null);
                case >= HttpStatusCode.InternalServerError:
                    return Done(FetchFailure.ServerError, status, HttpResponses.RetryAfter(response, time.GetUtcNow()), null);
                default:
                    return Done(FetchFailure.InvalidResponse, status, null, null);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return Done(FetchFailure.Timeout, null, null, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return Done(FetchFailure.Network, null, null, null);
        }
    }
}
