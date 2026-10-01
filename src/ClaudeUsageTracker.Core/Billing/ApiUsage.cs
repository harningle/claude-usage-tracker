using System.Globalization;
using System.Text.Json.Serialization;

namespace ClaudeUsageTracker.Core.Billing;

/// <summary>What kind of client an API key's name suggests. Port of upstream <c>APICostSourceType</c>.</summary>
public enum ApiCostSourceType
{
    Cli,
    Api,
    Unknown,
}

/// <summary>This month's cost of one API key. Port of upstream <c>APICostSource</c>; amounts are in cents.</summary>
public sealed record ApiCostSource
{
    public required string KeyId { get; init; }

    public required string KeyName { get; init; }

    public ApiCostSourceType SourceType { get; init; }

    public decimal TotalCents { get; init; }

    public Dictionary<string, decimal> CostByModel { get; init; } = [];

    /// <summary>Models by cost, most expensive first.</summary>
    public IReadOnlyList<KeyValuePair<string, decimal>> SortedModelCosts() =>
        [.. CostByModel.OrderByDescending(m => m.Value)];

    /// <summary>Upstream's guess from the key's name.</summary>
    public static ApiCostSourceType Detect(string keyName)
    {
        var lower = keyName.ToLowerInvariant();
        if (lower.Contains("claude code", StringComparison.Ordinal) || lower.Contains("claude-code", StringComparison.Ordinal)
            || lower.Contains("cli", StringComparison.Ordinal))
        {
            return ApiCostSourceType.Cli;
        }

        string[] apiWords = ["api", "sdk", "server", "bot", "app", "prod", "dev", "staging", "test"];
        return apiWords.Any(word => lower.Contains(word, StringComparison.Ordinal)) ? ApiCostSourceType.Api : ApiCostSourceType.Unknown;
    }
}

/// <summary>
/// Anthropic Console billing for one organisation. Port of upstream <c>APIUsage</c>, with decimal money
/// and a reset time that stays missing when the Console gives none (upstream substituted the current time).
/// </summary>
public sealed record ApiUsage
{
    public required long CurrentSpendCents { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    public required long PrepaidCreditsCents { get; init; }

    public required string Currency { get; init; }

    /// <summary>This month's cost of API tokens and tools; null when the cost request failed.</summary>
    public decimal? ApiTokenCostCents { get; init; }

    public Dictionary<string, decimal>? ApiCostByModel { get; init; }

    public List<ApiCostSource>? CostBySource { get; init; }

    /// <summary>Cost per day, keyed <c>yyyy-MM-dd</c>.</summary>
    public Dictionary<string, decimal>? DailyCostCents { get; init; }

    [JsonIgnore]
    public decimal UsedAmount => CurrentSpendCents / 100m;

    [JsonIgnore]
    public decimal RemainingAmount => PrepaidCreditsCents / 100m;

    [JsonIgnore]
    public decimal TotalCredits => UsedAmount + RemainingAmount;

    /// <summary>Spend as a percentage of spend plus remaining credits; 0 when both are zero, as upstream.</summary>
    [JsonIgnore]
    public double UsagePercentage => TotalCredits > 0 ? (double)(UsedAmount / TotalCredits * 100m) : 0;

    [JsonIgnore]
    public bool HasMultipleSources => CostBySource is { Count: > 1 };

    /// <summary>Daily costs in date order; keys that are not dates are dropped.</summary>
    public IReadOnlyList<(DateOnly Date, decimal Cents)> SortedDailyCosts() =>
        DailyCostCents is null
            ? []
            : [.. DailyCostCents
                .Select(d => DateOnly.TryParseExact(d.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (Date: date, Cents: d.Value)
                    : ((DateOnly Date, decimal Cents)?)null)
                .OfType<(DateOnly Date, decimal Cents)>()
                .OrderBy(d => d.Date)];

    /// <summary>Models by cost, most expensive first.</summary>
    public IReadOnlyList<KeyValuePair<string, decimal>> SortedModelCosts() =>
        ApiCostByModel is null ? [] : [.. ApiCostByModel.OrderByDescending(m => m.Value)];

    /// <summary>API keys by cost, most expensive first.</summary>
    public IReadOnlyList<ApiCostSource> SortedCostSources() =>
        CostBySource is null ? [] : [.. CostBySource.OrderByDescending(s => s.TotalCents)];
}

/// <summary>
/// Money as text. Upstream used the system currency formatter; Windows has none for an arbitrary
/// currency code, so the common symbols are used and any other code is written out.
/// </summary>
public static class CurrencyText
{
    /// <summary>For example "$12.34" or "CHF 12.34", always with two decimals as upstream.</summary>
    public static string Format(decimal amount, string currencyCode, CultureInfo culture)
    {
        var number = Math.Abs(amount).ToString("N2", culture);
        var sign = amount < 0 ? "-" : "";
        return currencyCode.ToUpperInvariant() switch
        {
            "USD" => $"{sign}${number}",
            "EUR" => $"{sign}€{number}",
            "GBP" => $"{sign}£{number}",
            "JPY" => $"{sign}¥{number}",
            var code => $"{sign}{code} {number}",
        };
    }

    public static string FormatCents(decimal cents, string currencyCode, CultureInfo culture) =>
        Format(cents / 100m, currencyCode, culture);
}
