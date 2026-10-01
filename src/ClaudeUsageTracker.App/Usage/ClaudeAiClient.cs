using System.Diagnostics;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>The claude.ai requests the app may send through a profile's browser. Nothing else can be sent.</summary>
public enum ClaudeAiOperation
{
    Organizations,
    Usage,
    OverageSpendLimit,
    OverageCreditGrant,
}

/// <summary>What the browser reported for one request.</summary>
/// <param name="Status">The HTTP status, or null when the request did not complete.</param>
/// <param name="Body">The response text, at most 1 MB.</param>
/// <param name="RetryAfter">The <c>Retry-After</c> header's raw value, if any.</param>
/// <param name="Error">Why no response arrived: <c>timeout</c>, <c>network</c> or <c>navigation</c>.</param>
public sealed record BrowserResponse(int? Status, string? ContentType, bool CfMitigated, string? Body, string? RetryAfter, string? Error);

/// <summary>
/// The narrow adapter between claude.ai request logic and the profile's WebView2 context (plan.md
/// section 2: claude.ai uses same-origin browser fetch, because every <c>HttpClient</c> replay was
/// challenged). It forwards allowlisted operations only, never a caller-supplied URL or script.
/// </summary>
public interface IClaudeAiTransport
{
    /// <summary>Sends one operation as a same-origin, credentialed GET in the profile's browser context.</summary>
    /// <param name="organizationId">Required for every operation except <see cref="ClaudeAiOperation.Organizations"/>.</param>
    Task<BrowserResponse> SendAsync(Guid profileId, ClaudeAiOperation operation, string? organizationId, CancellationToken cancellation);

    /// <summary>Changes whenever the profile's browser session may have changed: a sign-in, a pasted key, cleared data.</summary>
    long SessionEpoch(Guid profileId);
}

/// <param name="Organizations">The organisations of the signed-in account; null when the request failed.</param>
public sealed record OrganizationsResult(IReadOnlyList<ClaudeOrganization>? Organizations, FetchFailed? Failure);

/// <summary>
/// claude.ai usage for browser profiles: upstream's <c>fetchUsageData(sessionKey:organizationId:)</c> over
/// the WebView2 transport. All three requests are read-only GETs.
/// </summary>
public sealed class ClaudeAiClient(IClaudeAiTransport transport, TimeProvider time, RequestLog log)
{
    /// <summary>The organisations the profile's browser session can see. A signed-out session is <see cref="FetchFailure.CredentialsRejected"/>.</summary>
    public async Task<OrganizationsResult> FetchOrganizationsAsync(Guid profileId, CancellationToken cancellation)
    {
        var (response, failure) = await SendAsync(profileId, ClaudeAiOperation.Organizations, null, cancellation).ConfigureAwait(false);
        if (failure is not null)
        {
            return new OrganizationsResult(null, failure);
        }

        try
        {
            return new OrganizationsResult(ClaudeAiResponses.ParseOrganizations(response.Body ?? ""), null);
        }
        catch (UsageParseException)
        {
            return new OrganizationsResult(null, new FetchFailed(FetchFailure.InvalidResponse, response.Status, null) { At = time.GetUtcNow() });
        }
    }

    /// <summary>
    /// Usage of one organisation, with extra usage and the credit balance when their endpoints answer.
    /// Those two are optional, as upstream: a failure there leaves their values missing.
    /// </summary>
    public async Task<FetchResult> FetchUsageAsync(Guid profileId, string organizationId, CancellationToken cancellation)
    {
        var usageTask = SendAsync(profileId, ClaudeAiOperation.Usage, organizationId, cancellation);
        var limitTask = SendAsync(profileId, ClaudeAiOperation.OverageSpendLimit, organizationId, cancellation);
        var grantTask = SendAsync(profileId, ClaudeAiOperation.OverageCreditGrant, organizationId, cancellation);
        await Task.WhenAll(usageTask, limitTask, grantTask).ConfigureAwait(false);

        var (response, failure) = await usageTask.ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var usage = ClaudeUsageResponse.Parse(response.Body ?? "");
            var (limit, limitFailure) = await limitTask.ConfigureAwait(false);
            var (grant, grantFailure) = await grantTask.ConfigureAwait(false);
            return new FetchSucceeded(ClaudeAiResponses.WithOverage(
                usage, limitFailure is null ? limit.Body : null, grantFailure is null ? grant.Body : null))
            {
                At = time.GetUtcNow(),
            };
        }
        catch (UsageParseException)
        {
            return new FetchFailed(FetchFailure.InvalidResponse, response.Status, null) { At = time.GetUtcNow() };
        }
    }

    private async Task<(BrowserResponse Response, FetchFailed? Failure)> SendAsync(
        Guid profileId, ClaudeAiOperation operation, string? organizationId, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await transport.SendAsync(profileId, operation, organizationId, cancellation).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var failure = Classify(response);
        var (name, route) = operation switch
        {
            ClaudeAiOperation.Organizations => ("claudeAiOrganizations", "/api/organizations"),
            ClaudeAiOperation.Usage => ("claudeAiUsage", "/api/organizations/{org}/usage"),
            ClaudeAiOperation.OverageSpendLimit => ("claudeAiOverage", "/api/organizations/{org}/overage_spend_limit"),
            _ => ("claudeAiCreditGrant", "/api/organizations/{org}/overage_credit_grant"),
        };
        log.Append(new RequestLogEntry(
            now, name, "GET", route, response.Status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "WebView2",
            failure is { } f ? HttpResponses.Category(f) : null));
        return (response, failure is { } kind
            ? new FetchFailed(kind, response.Status, kind is FetchFailure.RateLimited or FetchFailure.ServerError
                ? RefreshPolicy.ParseRetryAfter(response.RetryAfter, now)
                : null) { At = now }
            : null);
    }

    /// <summary>
    /// A challenge is never an authentication verdict (upstream issue #277). A JSON 401 or 403 means the
    /// browser session cannot make this request: it is signed out, or signed in to an account without
    /// access to the organisation; the caller tells the two apart.
    /// </summary>
    private static FetchFailure? Classify(BrowserResponse response)
    {
        if (response.Status is not { } status)
        {
            return response.Error == "timeout" ? FetchFailure.Timeout : FetchFailure.Network;
        }

        var html = response.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
        if (response.CfMitigated || (html && response.Body?.Contains("Just a moment", StringComparison.Ordinal) == true))
        {
            return FetchFailure.Challenge;
        }

        return status switch
        {
            200 => null,
            401 or 403 => html ? FetchFailure.Challenge : FetchFailure.CredentialsRejected,
            429 => FetchFailure.RateLimited,
            >= 500 => FetchFailure.ServerError,
            _ => FetchFailure.InvalidResponse,
        };
    }
}
