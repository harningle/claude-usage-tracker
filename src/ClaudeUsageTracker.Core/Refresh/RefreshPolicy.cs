using System.Globalization;

namespace ClaudeUsageTracker.Core.Refresh;

/// <summary>Why a usage fetch failed, as far as the response shows.</summary>
public enum FetchFailure
{
    /// <summary>No response: offline, DNS, TLS or connection failure.</summary>
    Network,

    Timeout,

    /// <summary>HTTP 429.</summary>
    RateLimited,

    /// <summary>HTTP 5xx.</summary>
    ServerError,

    /// <summary>An HTML challenge page or <c>cf-mitigated</c> header instead of the API's answer.</summary>
    Challenge,

    /// <summary>HTTP 401, or a 403 authentication error: the server rejected the login itself.</summary>
    CredentialsRejected,

    /// <summary>
    /// Another HTTP 403 from the API, e.g. a permission error for an account type. The login is not
    /// treated as rejected.
    /// </summary>
    Forbidden,

    /// <summary>A success status whose body is not usage JSON, or an unexpected status.</summary>
    InvalidResponse,
}

/// <summary>
/// Polling cadence after a success or failure. Upstream polls at a fixed interval whatever happens and
/// never reads <c>Retry-After</c>; the plan requires backoff that honours server retry guidance.
/// </summary>
public static class RefreshPolicy
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait after repeated failures, so recovery is noticed within minutes.</summary>
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);

    /// <summary>The wait after a 429 without usable retry guidance.</summary>
    public static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(5);

    /// <summary>Server guidance beyond this is treated as a day, not obeyed indefinitely.</summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(24);

    /// <summary>The configured interval clamped to the settings range (10-300 s).</summary>
    public static TimeSpan ClampInterval(TimeSpan interval) =>
        interval < MinimumInterval ? MinimumInterval : interval > MaximumInterval ? MaximumInterval : interval;

    /// <summary>
    /// How long to wait before the next request. After a success: the interval. After failures:
    /// the interval doubled per consecutive failure, capped at <see cref="MaximumBackoff"/>; after a
    /// 429, at least the server's <c>Retry-After</c> (or <see cref="DefaultRateLimitWait"/>). Never
    /// shorter than the interval. Server guidance after a 503 or other 5xx is honoured the same way.
    /// Rejected credentials are not retried on a timer at all; callers wait for the credentials to change.
    /// </summary>
    public static TimeSpan NextDelay(TimeSpan interval, int consecutiveFailures, FetchFailure? failure, TimeSpan? retryAfter)
    {
        interval = ClampInterval(interval);
        if (failure is null || consecutiveFailures <= 0)
        {
            return interval;
        }

        var exponent = Math.Min(consecutiveFailures, 16);
        var backoff = TimeSpan.FromTicks(Math.Min(interval.Ticks << exponent, MaximumBackoff.Ticks));
        if (backoff < interval)
        {
            backoff = interval;
        }

        var guidance = retryAfter is { } wait ? (wait > MaximumRetryAfter ? MaximumRetryAfter : wait)
            : failure == FetchFailure.RateLimited ? DefaultRateLimitWait
            : (TimeSpan?)null;
        return guidance > backoff ? guidance.Value : backoff;
    }

    /// <summary>
    /// Parses a <c>Retry-After</c> value: delay seconds, or an HTTP date relative to
    /// <paramref name="now"/>. Past dates give zero; anything else is null.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > MaximumRetryAfter.TotalSeconds ? MaximumRetryAfter : TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaximumRetryAfter ? MaximumRetryAfter : wait;
        }

        return null;
    }
}
