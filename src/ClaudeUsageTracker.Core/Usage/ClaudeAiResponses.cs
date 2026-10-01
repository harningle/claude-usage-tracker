using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;

namespace ClaudeUsageTracker.Core.Usage;

/// <summary>An organisation of a claude.ai account. Port of upstream <c>AccountInfo</c>.</summary>
public sealed record ClaudeOrganization(string Uuid, string Name)
{
    /// <summary>The name, or the ID when the organisation has no name.</summary>
    public string DisplayName => Name.Length > 0 ? Name : Uuid;
}

/// <summary>
/// Parsers for claude.ai's own endpoints, used by the browser route: the organisation list and the two
/// extra-usage endpoints upstream reads beside the usage body.
/// </summary>
public static class ClaudeAiResponses
{
    /// <summary>
    /// Parses <c>GET /api/organizations</c>. Entries without a string <c>uuid</c> are skipped; a missing
    /// name reads as empty (upstream failed the whole list on any incomplete entry).
    /// </summary>
    /// <exception cref="UsageParseException">The body is not a JSON array.</exception>
    public static IReadOnlyList<ClaudeOrganization> ParseOrganizations(string json)
    {
        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            array = null;
        }

        if (array is null)
        {
            throw new UsageParseException("The organisations response was not a JSON array.");
        }

        return array.OfType<JsonObject>()
            .Where(o => JsonRead.NonEmptyString(o, "uuid") is not null)
            .Select(o => new ClaudeOrganization(JsonRead.NonEmptyString(o, "uuid")!, JsonRead.String(o, "name") ?? ""))
            .ToList();
    }

    /// <summary>
    /// Adds extra usage from <c>overage_spend_limit</c> (only when it reports <c>is_enabled</c> true, as
    /// upstream) and the credit balance from <c>overage_credit_grant</c>. Either body may be null or
    /// unusable, which leaves its values missing. These endpoints give amounts in minor units without an
    /// exponent, so they are divided by 100 as upstream does.
    /// </summary>
    public static UsageData WithOverage(UsageData usage, string? spendLimitJson, string? creditGrantJson)
    {
        if (ParseObject(spendLimitJson) is { } limit && JsonRead.Bool(limit, "is_enabled") == true)
        {
            usage = usage with
            {
                CostUsed = Major(JsonRead.Decimal(limit, "used_credits")),
                CostLimit = Major(JsonRead.Decimal(limit, "monthly_credit_limit")),
                CostCurrency = JsonRead.NonEmptyString(limit, "currency"),
            };
        }

        if (ParseObject(creditGrantJson) is { } grant)
        {
            usage = usage with
            {
                OverageBalance = Major(JsonRead.Decimal(grant, "remaining_balance")),
                OverageBalanceCurrency = JsonRead.NonEmptyString(grant, "currency"),
            };
        }

        return usage;
    }

    /// <summary>Minor units as a two-decimal amount; adding 0.00 keeps the scale, so 500 reads 5.00.</summary>
    private static decimal? Major(decimal? minor) =>
        minor is { } value ? Math.Round(value / 100m, 2) + 0.00m : null;

    private static JsonObject? ParseObject(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
