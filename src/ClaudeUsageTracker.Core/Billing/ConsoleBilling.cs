using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Core.Billing;

/// <summary>An Anthropic Console organisation. Port of upstream <c>APIOrganization</c>.</summary>
public sealed record ConsoleOrganization(string Uuid, string Name)
{
    public string DisplayName => Name.Length > 0 ? Name : Uuid;
}

public sealed record CurrentSpend(long AmountCents, DateTimeOffset? ResetsAt);

public sealed record PrepaidCredits(long AmountCents, string Currency);

/// <summary>One cost line of a day: an API key, a model (or tool) and an amount in cents.</summary>
public sealed record UsageCostEntry(string DateKey, string KeyId, string ModelName, decimal TotalCents);

/// <summary>
/// Parsers for the Anthropic Console billing endpoints and the monthly cost roll-up. Port of upstream
/// <c>ClaudeAPIService+ConsoleAPI</c> and its response types. A body that is not the expected JSON shape
/// throws <see cref="UsageParseException"/>; optional fields read as upstream's fallbacks.
/// </summary>
public static class ConsoleBilling
{
    /// <summary>Parses <c>GET /api/organizations</c>; entries without a string <c>uuid</c> are skipped.</summary>
    public static IReadOnlyList<ConsoleOrganization> ParseOrganizations(string json)
    {
        if (Parse(json) is not JsonArray array)
        {
            throw new UsageParseException("The Console organisations response was not a JSON array.");
        }

        return array.OfType<JsonObject>()
            .Where(o => JsonRead.NonEmptyString(o, "uuid") is not null)
            .Select(o => new ConsoleOrganization(JsonRead.NonEmptyString(o, "uuid")!, JsonRead.String(o, "name") ?? ""))
            .ToList();
    }

    /// <summary>Parses <c>current_spend</c>: an integer <c>amount</c> in cents and a <c>resets_at</c> timestamp.</summary>
    public static CurrentSpend ParseCurrentSpend(string json)
    {
        if (Parse(json) is not JsonObject root || Cents(root, "amount") is not { } amount)
        {
            throw new UsageParseException("The Console spend response had no amount.");
        }

        return new CurrentSpend(amount, ClaudeUsageResponse.Timestamp(root["resets_at"]));
    }

    /// <summary>Parses <c>prepaid/credits</c>: an integer <c>amount</c> in cents and a currency code.</summary>
    public static PrepaidCredits ParsePrepaidCredits(string json)
    {
        if (Parse(json) is not JsonObject root
            || Cents(root, "amount") is not { } amount
            || JsonRead.NonEmptyString(root, "currency") is not { } currency)
        {
            throw new UsageParseException("The Console credits response had no amount or currency.");
        }

        return new PrepaidCredits(amount, currency);
    }

    /// <summary>
    /// Parses <c>usage_cost</c> grouped by API key: <c>costs</c>, <c>web_search_costs</c> and
    /// <c>code_execution_costs</c>, each a map of day to entries. Model names lose upstream's " Usage"
    /// suffix; tool costs are named "Web Search" and "Code Execution". A missing key ID reads "unknown", a
    /// missing model "Unknown" and a missing total 0, as upstream.
    /// </summary>
    public static IReadOnlyList<UsageCostEntry> ParseUsageCost(string json)
    {
        if (Parse(json) is not JsonObject root)
        {
            throw new UsageParseException("The Console cost response was not a JSON object.");
        }

        var entries = new List<UsageCostEntry>();
        Collect(root["costs"], null);
        Collect(root["web_search_costs"], "Web Search");
        Collect(root["code_execution_costs"], "Code Execution");
        return entries;

        void Collect(JsonNode? node, string? fixedName)
        {
            if (node is not JsonObject days)
            {
                return;
            }

            foreach (var (day, value) in days)
            {
                foreach (var entry in (value as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var model = fixedName
                        ?? (JsonRead.String(entry, "model_name") ?? "Unknown").Replace(" Usage", "", StringComparison.Ordinal);
                    entries.Add(new UsageCostEntry(
                        day, JsonRead.String(entry, "key_id") ?? "unknown", model, JsonRead.Decimal(entry, "total") ?? 0m));
                }
            }
        }
    }

    /// <summary>Parses <c>api_keys</c> into key ID to name, from <c>{"data": [...]}</c> or a bare array; anything else is empty.</summary>
    public static IReadOnlyDictionary<string, string> ParseApiKeys(string json)
    {
        var parsed = Parse(json);
        var array = parsed as JsonArray ?? (parsed as JsonObject)?["data"] as JsonArray;
        var names = new Dictionary<string, string>();
        foreach (var key in array?.OfType<JsonObject>() ?? [])
        {
            if (JsonRead.NonEmptyString(key, "id") is { } id && JsonRead.String(key, "name") is { } name)
            {
                names[id] = name;
            }
        }

        return names;
    }

    /// <summary>
    /// Rolls the responses up into <see cref="ApiUsage"/>. <paramref name="costs"/> is null when the cost
    /// request failed, which leaves the cost fields missing rather than zero.
    /// </summary>
    public static ApiUsage Build(
        CurrentSpend spend, PrepaidCredits credits, IReadOnlyList<UsageCostEntry>? costs, IReadOnlyDictionary<string, string> keyNames)
    {
        if (costs is null)
        {
            return new ApiUsage
            {
                CurrentSpendCents = spend.AmountCents,
                ResetsAt = spend.ResetsAt,
                PrepaidCreditsCents = credits.AmountCents,
                Currency = credits.Currency,
            };
        }

        var byModel = new Dictionary<string, decimal>();
        var byKey = new Dictionary<string, decimal>();
        var byKeyAndModel = new Dictionary<string, Dictionary<string, decimal>>();
        var byDay = new Dictionary<string, decimal>();
        foreach (var entry in costs)
        {
            Add(byModel, entry.ModelName, entry.TotalCents);
            Add(byKey, entry.KeyId, entry.TotalCents);
            if (!byKeyAndModel.TryGetValue(entry.KeyId, out var models))
            {
                byKeyAndModel[entry.KeyId] = models = [];
            }

            Add(models, entry.ModelName, entry.TotalCents);
            Add(byDay, entry.DateKey, entry.TotalCents);
        }

        return new ApiUsage
        {
            CurrentSpendCents = spend.AmountCents,
            ResetsAt = spend.ResetsAt,
            PrepaidCreditsCents = credits.AmountCents,
            Currency = credits.Currency,
            ApiTokenCostCents = costs.Sum(c => c.TotalCents),
            ApiCostByModel = byModel.Count > 0 ? byModel : null,
            CostBySource = byKey.Count > 0
                ? byKey.Select(k =>
                {
                    var name = keyNames.GetValueOrDefault(k.Key, k.Key);
                    return new ApiCostSource
                    {
                        KeyId = k.Key,
                        KeyName = name,
                        SourceType = ApiCostSource.Detect(name),
                        TotalCents = k.Value,
                        CostByModel = byKeyAndModel[k.Key],
                    };
                }).ToList()
                : null,
            DailyCostCents = byDay.Count > 0 ? byDay : null,
        };
    }

    /// <summary>
    /// The current calendar month in <paramref name="zone"/> as <c>starting_on</c> and <c>ending_before</c>
    /// query values, as upstream's local-calendar month.
    /// </summary>
    public static (string StartingOn, string EndingBefore) BillingMonth(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = new DateOnly(local.Year, local.Month, 1);
        return (start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            start.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>A whole number of cents, also when written with a fraction part such as 1234.0.</summary>
    private static long? Cents(JsonObject obj, string key) =>
        JsonRead.Decimal(obj, key) is { } value && decimal.Truncate(value) == value && Math.Abs(value) <= long.MaxValue
            ? (long)value
            : null;

    private static void Add(Dictionary<string, decimal> totals, string key, decimal amount) =>
        totals[key] = totals.GetValueOrDefault(key) + amount;

    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
