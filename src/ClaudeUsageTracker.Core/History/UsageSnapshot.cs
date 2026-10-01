using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Core.History;

/// <summary>Reset type that triggers a usage snapshot.</summary>
public enum ResetType
{
    SessionReset,
    WeeklyReset,
    BillingCycle,
}

/// <summary>
/// Usage recorded at a reset, or sampled periodically for the charts. Port of upstream
/// <c>UsageSnapshot</c>.
/// </summary>
public sealed record UsageSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DateTimeOffset Timestamp { get; init; }
    public required ResetType ResetType { get; init; }

    /// <summary>Provider raw value, kept as a string so history from newer versions still loads.</summary>
    public string? Provider { get; init; }

    public long? SessionTokensUsed { get; init; }
    public double? SessionPercentage { get; init; }
    public long? WeeklyTokensUsed { get; init; }
    public double? WeeklyPercentage { get; init; }
    public long? OpusWeeklyTokensUsed { get; init; }
    public double? OpusWeeklyPercentage { get; init; }
    public long? SonnetWeeklyTokensUsed { get; init; }
    public double? SonnetWeeklyPercentage { get; init; }
    public long? DesignWeeklyTokensUsed { get; init; }
    public double? DesignWeeklyPercentage { get; init; }
    public long? FableWeeklyTokensUsed { get; init; }
    public double? FableWeeklyPercentage { get; init; }

    // API billing, captured before a billing-cycle reset
    public long? ApiSpendCents { get; init; }
    public long? ApiPrepaidCreditsCents { get; init; }
    public string? ApiCurrency { get; init; }

    public required DateTimeOffset TriggeringResetTime { get; init; }

    /// <summary>
    /// Snapshot for a session reset. Token counts are omitted for percentage-only providers so
    /// exports show blanks instead of misleading zeros.
    /// </summary>
    public static UsageSnapshot FromSessionReset(
        UsageData usage, DateTimeOffset resetTime, Provider provider, DateTimeOffset recordedAt)
    {
        var hasTokens = provider.Descriptor().Capabilities.TokenCounts;
        return new UsageSnapshot
        {
            Timestamp = recordedAt,
            ResetType = ResetType.SessionReset,
            Provider = provider.RawValue(),
            SessionTokensUsed = hasTokens ? usage.SessionTokensUsed : null,
            SessionPercentage = usage.SessionPercentage,
            TriggeringResetTime = resetTime,
        };
    }

    /// <summary>Snapshot for a weekly reset, gated by token and per-model capabilities.</summary>
    public static UsageSnapshot FromWeeklyReset(
        UsageData usage, DateTimeOffset resetTime, Provider provider, DateTimeOffset recordedAt)
    {
        var capabilities = provider.Descriptor().Capabilities;
        var hasTokens = capabilities.TokenCounts;
        var hasModels = capabilities.PerModelBreakdown;
        return new UsageSnapshot
        {
            Timestamp = recordedAt,
            ResetType = ResetType.WeeklyReset,
            Provider = provider.RawValue(),
            WeeklyTokensUsed = hasTokens ? usage.WeeklyTokensUsed : null,
            WeeklyPercentage = usage.WeeklyPercentage,
            OpusWeeklyTokensUsed = hasModels ? usage.OpusWeeklyTokensUsed : null,
            OpusWeeklyPercentage = hasModels ? usage.OpusWeeklyPercentage : null,
            SonnetWeeklyTokensUsed = hasModels ? usage.SonnetWeeklyTokensUsed : null,
            SonnetWeeklyPercentage = hasModels ? usage.SonnetWeeklyPercentage : null,
            DesignWeeklyTokensUsed = hasModels ? usage.DesignWeeklyTokensUsed : null,
            DesignWeeklyPercentage = hasModels ? usage.DesignWeeklyPercentage : null,
            FableWeeklyTokensUsed = hasModels ? usage.FableWeeklyTokensUsed : null,
            FableWeeklyPercentage = hasModels ? usage.FableWeeklyPercentage : null,
            TriggeringResetTime = resetTime,
        };
    }

    /// <summary>Snapshot of Console billing before a billing-cycle reset. It has no provider, as upstream.</summary>
    public static UsageSnapshot FromBillingCycleReset(ApiUsage usage, DateTimeOffset resetTime, DateTimeOffset recordedAt) =>
        new()
        {
            Timestamp = recordedAt,
            ResetType = ResetType.BillingCycle,
            ApiSpendCents = usage.CurrentSpendCents,
            ApiPrepaidCreditsCents = usage.PrepaidCreditsCents,
            ApiCurrency = usage.Currency,
            TriggeringResetTime = resetTime,
        };
}
