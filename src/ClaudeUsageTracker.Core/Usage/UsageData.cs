namespace ClaudeUsageTracker.Core.Usage;

/// <summary>
/// Usage values reported by a provider. Port of upstream <c>ClaudeUsage</c>, except that a value the
/// provider did not report stays null instead of becoming zero or an estimate.
/// </summary>
public sealed record UsageData
{
    // Session (5-hour rolling window)
    public long? SessionTokensUsed { get; init; }
    public long? SessionLimit { get; init; }
    public double? SessionPercentage { get; init; }
    public DateTimeOffset? SessionResetTime { get; init; }

    // Weekly, all models
    public long? WeeklyTokensUsed { get; init; }
    public long? WeeklyLimit { get; init; }
    public double? WeeklyPercentage { get; init; }
    public DateTimeOffset? WeeklyResetTime { get; init; }

    // Weekly, per model
    public long? OpusWeeklyTokensUsed { get; init; }
    public double? OpusWeeklyPercentage { get; init; }
    public long? SonnetWeeklyTokensUsed { get; init; }
    public double? SonnetWeeklyPercentage { get; init; }
    public DateTimeOffset? SonnetWeeklyResetTime { get; init; }
    public long? DesignWeeklyTokensUsed { get; init; }
    public double? DesignWeeklyPercentage { get; init; }
    public DateTimeOffset? DesignWeeklyResetTime { get; init; }
    public long? FableWeeklyTokensUsed { get; init; }
    public double? FableWeeklyPercentage { get; init; }
    public DateTimeOffset? FableWeeklyResetTime { get; init; }

    // Extra usage and overage credit grant
    public decimal? CostUsed { get; init; }
    public decimal? CostLimit { get; init; }
    public string? CostCurrency { get; init; }
    public decimal? OverageBalance { get; init; }
    public string? OverageBalanceCurrency { get; init; }

    // Provider plan and credits (e.g. Codex)
    public string? PlanType { get; init; }
    public decimal? CreditsBalance { get; init; }
    public bool? CreditsUnlimited { get; init; }
}
