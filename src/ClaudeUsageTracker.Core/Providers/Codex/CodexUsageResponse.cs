using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;

namespace ClaudeUsageTracker.Core.Providers.Codex;

/// <param name="UsedPercent">Percent of the window consumed; null when missing or malformed.</param>
/// <param name="ResetAt">When the window resets.</param>
/// <param name="LimitWindowSeconds">Window length, e.g. 18000 (5 hours) or 604800 (7 days).</param>
public sealed record CodexRateWindow(double? UsedPercent, DateTimeOffset? ResetAt, long? LimitWindowSeconds)
{
    public long? WindowMinutes => LimitWindowSeconds / 60;
}

public sealed record CodexRateLimitDetails(CodexRateWindow? PrimaryWindow, CodexRateWindow? SecondaryWindow);

public sealed record CodexCreditDetails(bool? HasCredits, bool? Unlimited, decimal? Balance);

/// <summary>
/// Response of <c>GET wham/usage</c>. Port of upstream <c>CodexUsageResponse</c>: every field
/// decodes tolerantly, so a new or malformed field never fails the whole response. Unknown plan
/// types pass through as raw strings. Unlike upstream, missing or malformed values stay null
/// instead of defaulting to 0 or false.
/// </summary>
public sealed record CodexUsageResponse(string? PlanType, CodexRateLimitDetails? RateLimit, CodexCreditDetails? Credits)
{
    /// <exception cref="JsonException">The body is not a JSON object.</exception>
    public static CodexUsageResponse Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("wham/usage response is not a JSON object.");

        return new CodexUsageResponse(
            JsonRead.String(root, "plan_type"),
            root["rate_limit"] is JsonObject rateLimit
                ? new CodexRateLimitDetails(
                    ParseWindow(rateLimit["primary_window"]),
                    ParseWindow(rateLimit["secondary_window"]))
                : null,
            root["credits"] is JsonObject credits
                ? new CodexCreditDetails(
                    JsonRead.Bool(credits, "has_credits"),
                    JsonRead.Bool(credits, "unlimited"),
                    JsonRead.Decimal(credits, "balance"))
                : null);
    }

    private static CodexRateWindow? ParseWindow(JsonNode? node) =>
        node is JsonObject window
            ? new CodexRateWindow(
                JsonRead.Double(window, "used_percent"),
                JsonRead.UnixSeconds(window, "reset_at"),
                JsonRead.Integer(window, "limit_window_seconds"))
            : null;
}
