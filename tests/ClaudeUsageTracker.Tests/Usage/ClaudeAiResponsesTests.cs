using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>claude.ai's organisation list and extra-usage endpoints, with synthetic bodies.</summary>
public class ClaudeAiResponsesTests
{
    [Fact]
    public void OrganisationsAreListedInOrder()
    {
        var organisations = ClaudeAiResponses.ParseOrganizations(
            """[{"uuid":"org-1","name":"Personal","capabilities":["chat"]},{"uuid":"org-2","name":"","capabilities":[]},{"name":"no id"}]""");

        Assert.Equal([new ClaudeOrganization("org-1", "Personal"), new ClaudeOrganization("org-2", "")], organisations);
        Assert.Equal("org-2", organisations[1].DisplayName);
    }

    [Theory]
    [InlineData("""{"error":{"type":"permission_error"}}""")]
    [InlineData("<html>Just a moment...</html>")]
    public void SomethingElseThanAListIsNotAnEmptyList(string body) =>
        Assert.Throws<UsageParseException>(() => ClaudeAiResponses.ParseOrganizations(body));

    [Fact]
    public void EnabledExtraUsageIsReadInMajorUnits()
    {
        var usage = ClaudeAiResponses.WithOverage(
            new UsageData { SessionPercentage = 10 },
            """{"monthly_credit_limit":500,"currency":"USD","used_credits":186,"is_enabled":true}""",
            """{"remaining_balance":1250,"currency":"usd","total_granted":2000}""");

        Assert.Equal(1.86m, usage.CostUsed);
        Assert.Equal("5.00", usage.CostLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("USD", usage.CostCurrency);
        Assert.Equal(12.50m, usage.OverageBalance);
        Assert.Equal("usd", usage.OverageBalanceCurrency);
        Assert.Equal(10, usage.SessionPercentage);
    }

    [Theory]
    [InlineData("""{"monthly_credit_limit":500,"currency":"USD","used_credits":0,"is_enabled":false}""")]
    [InlineData("""{"monthly_credit_limit":500,"currency":"USD","used_credits":0}""")]
    [InlineData("""{"error":"not found"}""")]
    [InlineData("not json")]
    [InlineData(null)]
    public void DisabledOrUnreadableExtraUsageStaysMissing(string? body)
    {
        var usage = ClaudeAiResponses.WithOverage(new UsageData(), body, null);

        Assert.Null(usage.CostUsed);
        Assert.Null(usage.CostLimit);
        Assert.Null(usage.CostCurrency);
        Assert.Null(usage.OverageBalance);
    }

    [Fact]
    public void MissingAmountsStayMissingRatherThanZero()
    {
        var usage = ClaudeAiResponses.WithOverage(new UsageData(), """{"is_enabled":true,"currency":"USD"}""", """{"currency":"USD"}""");

        Assert.Null(usage.CostUsed);
        Assert.Null(usage.CostLimit);
        Assert.Null(usage.OverageBalance);
    }
}
