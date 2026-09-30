using ClaudeUsageTracker.Core.History;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Providers;

/// <summary>
/// Port of the registry and snapshot cases of upstream <c>ProfileProviderTests</c>. Its profile and
/// usage-cache persistence cases are scheduled with Windows persistence (docs/parity.md).
/// </summary>
public class ProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryProviderHasADescriptor()
    {
        foreach (var provider in Enum.GetValues<Provider>())
        {
            var descriptor = provider.Descriptor();
            Assert.Equal(provider, descriptor.Id);
            Assert.NotEmpty(descriptor.DisplayName);
        }
    }

    [Fact]
    public void CapabilityExpectations()
    {
        Assert.True(Provider.Anthropic.Descriptor().Capabilities.TokenCounts);
        Assert.True(Provider.Anthropic.Descriptor().Capabilities.CliAccountSync);
        Assert.False(Provider.Codex.Descriptor().Capabilities.TokenCounts);
        Assert.False(Provider.Codex.Descriptor().Capabilities.CliAccountSync);
        Assert.False(Provider.Codex.Descriptor().Capabilities.AutoStartSession);
        Assert.True(Provider.Codex.Descriptor().Capabilities.Credits);
    }

    [Fact]
    public void CodexSnapshotSuppressesTokenCounts()
    {
        var usage = new UsageData { SessionTokensUsed = 4242, SessionPercentage = 42 };

        var snapshot = UsageSnapshot.FromSessionReset(usage, Now, Provider.Codex, Now);

        Assert.Equal("codex", snapshot.Provider);
        Assert.Null(snapshot.SessionTokensUsed);
        Assert.Equal(42, snapshot.SessionPercentage);
    }

    [Fact]
    public void AnthropicSnapshotKeepsTokenCounts()
    {
        var usage = new UsageData { SessionTokensUsed = 4242, SessionPercentage = 42 };

        var snapshot = UsageSnapshot.FromSessionReset(usage, Now, Provider.Anthropic, Now);

        Assert.Equal("anthropic", snapshot.Provider);
        Assert.Equal(4242, snapshot.SessionTokensUsed);
    }

    /// <summary>No upstream case; characterises upstream <c>fromWeeklyReset</c>.</summary>
    [Fact]
    public void WeeklySnapshotGatesTokensAndModelsByCapability()
    {
        var usage = new UsageData
        {
            WeeklyTokensUsed = 900,
            WeeklyPercentage = 45,
            OpusWeeklyTokensUsed = 10,
            OpusWeeklyPercentage = 1,
            SonnetWeeklyPercentage = 2,
        };

        var anthropic = UsageSnapshot.FromWeeklyReset(usage, Now, Provider.Anthropic, Now);
        var codex = UsageSnapshot.FromWeeklyReset(usage, Now, Provider.Codex, Now);

        Assert.Equal((900L, 45.0, 10L, 1.0, 2.0), (
            anthropic.WeeklyTokensUsed, anthropic.WeeklyPercentage, anthropic.OpusWeeklyTokensUsed,
            anthropic.OpusWeeklyPercentage, anthropic.SonnetWeeklyPercentage));
        Assert.Equal(45, codex.WeeklyPercentage);
        Assert.Null(codex.WeeklyTokensUsed);
        Assert.Null(codex.OpusWeeklyTokensUsed);
        Assert.Null(codex.OpusWeeklyPercentage);
        Assert.Null(codex.SonnetWeeklyPercentage);
    }
}
