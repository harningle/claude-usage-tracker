using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Usage;

/// <param name="Value">The parsed answer; null when the request failed.</param>
/// <param name="RetryAfter">The server's retry guidance, when it gave any.</param>
/// <param name="RenewedKey">A session cookie the Console issued in its answer, to be saved in place of the one sent.</param>
public sealed record ConsoleResult<T>(T? Value, FetchFailure? Failure, int? Status, TimeSpan? RetryAfter, ConsoleSecret? RenewedKey)
    where T : class;

/// <summary>
/// Anthropic Console billing over <c>HttpClient</c> with the profile's own session cookie (plan.md
/// section 2: replay against <c>platform.claude.com</c> was not challenged in Milestone 1). Port of
/// upstream <c>ClaudeAPIService+ConsoleAPI</c>; upstream's <c>console.anthropic.com</c> base is not used
/// because the Console's cookie is scoped to <c>platform.claude.com</c>. Every request is a read-only GET
/// carrying only the <c>sessionKey</c> cookie, so no profile's cookie can reach another's request.
/// </summary>
/// <param name="userAgent">The WebView2 user agent, which the Milestone 1 replay used; null sends none.</param>
public sealed class ConsoleClient(HttpClient http, TimeProvider time, RequestLog log, string? userAgent = null)
{
    public const string Origin = "https://platform.claude.com";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<ConsoleResult<IReadOnlyList<ConsoleOrganization>>> FetchOrganizationsAsync(string sessionKey, CancellationToken cancellation)
    {
        var response = await GetAsync("consoleOrganizations", "/api/organizations", "/api/organizations", sessionKey, cancellation).ConfigureAwait(false);
        return Parsed(response, ConsoleBilling.ParseOrganizations);
    }

    /// <summary>
    /// Spend and prepaid credits (both required), then this month's cost by API key and the key names (both
    /// optional, as upstream: without them the cost fields stay missing).
    /// </summary>
    public async Task<ConsoleResult<ApiUsage>> FetchApiUsageAsync(string sessionKey, string organizationId, CancellationToken cancellation)
    {
        var org = Uri.EscapeDataString(organizationId);
        var spendTask = GetAsync("consoleCurrentSpend", $"/api/organizations/{org}/current_spend", "/api/organizations/{org}/current_spend", sessionKey, cancellation);
        var creditsTask = GetAsync("consolePrepaidCredits", $"/api/organizations/{org}/prepaid/credits", "/api/organizations/{org}/prepaid/credits", sessionKey, cancellation);
        var spendResponse = await spendTask.ConfigureAwait(false);
        var creditsResponse = await creditsTask.ConfigureAwait(false);
        var spend = Parsed(spendResponse, ConsoleBilling.ParseCurrentSpend);
        var credits = Parsed(creditsResponse, ConsoleBilling.ParsePrepaidCredits);
        var renewed = credits.RenewedKey ?? spend.RenewedKey;
        if (spend.Value is null || credits.Value is null)
        {
            var failed = spend.Value is null ? (spend.Failure, spend.Status, spend.RetryAfter) : (credits.Failure, credits.Status, credits.RetryAfter);
            return new ConsoleResult<ApiUsage>(null, failed.Failure, failed.Status, failed.RetryAfter, renewed);
        }

        var key = renewed?.SessionKey ?? sessionKey;
        var (startingOn, endingBefore) = ConsoleBilling.BillingMonth(time.GetUtcNow(), TimeZoneInfo.Local);
        var costs = Parsed(
            await GetAsync(
                "consoleUsageCost",
                $"/api/organizations/{org}/workspaces/default/usage_cost?starting_on={startingOn}&ending_before={endingBefore}&group_by=api_key_id",
                "/api/organizations/{org}/workspaces/default/usage_cost", key, cancellation).ConfigureAwait(false),
            ConsoleBilling.ParseUsageCost);
        IReadOnlyDictionary<string, string> keyNames = new Dictionary<string, string>();
        if (costs.Value is not null)
        {
            var keys = await GetAsync(
                "consoleApiKeys", $"/api/organizations/{org}/api_keys?status=active", "/api/organizations/{org}/api_keys", key, cancellation).ConfigureAwait(false);
            if (keys.Failure is null)
            {
                keyNames = ConsoleBilling.ParseApiKeys(keys.Body!);
            }

            renewed = keys.RenewedKey ?? costs.RenewedKey ?? renewed;
        }

        return new ConsoleResult<ApiUsage>(ConsoleBilling.Build(spend.Value, credits.Value, costs.Value, keyNames), null, 200, null, renewed);
    }

    private ConsoleResult<T> Parsed<T>(Response response, Func<string, T> parse) where T : class
    {
        if (response.Failure is not null)
        {
            return new ConsoleResult<T>(null, response.Failure, response.Status, response.RetryAfter, response.RenewedKey);
        }

        try
        {
            return new ConsoleResult<T>(parse(response.Body!), null, response.Status, null, response.RenewedKey);
        }
        catch (UsageParseException)
        {
            return new ConsoleResult<T>(null, FetchFailure.InvalidResponse, response.Status, null, response.RenewedKey);
        }
    }

    private async Task<Response> GetAsync(string operation, string pathAndQuery, string route, string sessionKey, CancellationToken cancellation)
    {
        var url = new Uri(Origin + pathAndQuery);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The cookie is set per request; the shared handler keeps no cookie container.
        request.Headers.TryAddWithoutValidation("Cookie", "sessionKey=" + sessionKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(RequestTimeout);
        var result = new Response(FetchFailure.Network, null, null, null, null);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var body = await HttpResponses.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var now = time.GetUtcNow();
            FetchFailure? failure = HttpResponses.IsChallenge(response, body) ? FetchFailure.Challenge
                : response.StatusCode switch
                {
                    HttpStatusCode.OK => null,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        HttpResponses.IsHtml(response) ? FetchFailure.Challenge : FetchFailure.CredentialsRejected,
                    HttpStatusCode.TooManyRequests => FetchFailure.RateLimited,
                    >= HttpStatusCode.InternalServerError => FetchFailure.ServerError,
                    _ => FetchFailure.InvalidResponse,
                };
            result = new Response(failure, (int)response.StatusCode, body, HttpResponses.RetryAfter(response, now), RenewedKey(response, url, sessionKey));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            result = result with { Failure = FetchFailure.Timeout };
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
        }

        log.Append(new RequestLogEntry(
            time.GetUtcNow(), operation, "GET", route, result.Status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            "HttpClient", result.Failure is { } f ? HttpResponses.Category(f) : null));
        return result;
    }

    /// <summary>A different, non-empty <c>sessionKey</c> the Console set in this answer, with its expiry.</summary>
    private static ConsoleSecret? RenewedKey(HttpResponseMessage response, Uri url, string sent)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var container = new CookieContainer();
        foreach (var value in values)
        {
            try
            {
                container.SetCookies(url, value);
            }
            catch (CookieException)
            {
            }
        }

        var cookie = container.GetCookies(url)["sessionKey"];
        return cookie is { Value.Length: > 0, Expired: false } && cookie.Value != sent
            ? new ConsoleSecret(cookie.Value, cookie.Expires == DateTime.MinValue ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()))
            : null;
    }

    private sealed record Response(FetchFailure? Failure, int? Status, string? Body, TimeSpan? RetryAfter, ConsoleSecret? RenewedKey);
}
