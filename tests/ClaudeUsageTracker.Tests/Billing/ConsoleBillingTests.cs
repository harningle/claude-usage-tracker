using System.Globalization;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Billing;

/// <summary>
/// Console billing parsing and the monthly cost roll-up (upstream <c>fetchAPIUsageData</c>), with
/// synthetic bodies. Upstream has no tests for this code.
/// </summary>
public class ConsoleBillingTests
{
    private const string Costs = """
        {
          "costs": {
            "2026-10-01": [
              {"workspace_id":"w","key_id":"key-cli","model_name":"Claude Sonnet 5.5 Usage","total":120.5,"token_type":"input"},
              {"key_id":"key-api","model_name":"Claude Haiku 4.5 Usage","total":30}
            ],
            "2026-10-02": [
              {"key_id":"key-cli","model_name":"Claude Sonnet 5.5 Usage","total":79.5},
              {"model_name":null,"total":null}
            ]
          },
          "web_search_costs": {"2026-10-02": [{"key_id":"key-api","total":10}]},
          "code_execution_costs": {"2026-10-01": [{"key_id":"key-api","total":5}]}
        }
        """;

    [Fact]
    public void OrganisationsUseTheUuid()
    {
        var organisations = ConsoleBilling.ParseOrganizations("""[{"id":7,"uuid":"org-uuid","name":"Example"},{"id":8,"name":"no uuid"}]""");

        Assert.Equal([new ConsoleOrganization("org-uuid", "Example")], organisations);
    }

    [Fact]
    public void SpendAndCreditsAreReadInCents()
    {
        var spend = ConsoleBilling.ParseCurrentSpend("""{"amount":1234,"resets_at":"2026-11-01T00:00:00.000Z"}""");
        var credits = ConsoleBilling.ParsePrepaidCredits("""{"amount":8766.0,"currency":"USD","auto_reload_settings":null}""");

        Assert.Equal(new CurrentSpend(1234, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)), spend);
        Assert.Equal(new PrepaidCredits(8766, "USD"), credits);
    }

    [Fact]
    public void MissingResetTimeStaysMissing() =>
        Assert.Null(ConsoleBilling.ParseCurrentSpend("""{"amount":0,"resets_at":"soon"}""").ResetsAt);

    [Theory]
    [InlineData("""{"resets_at":"2026-11-01T00:00:00Z"}""")]
    [InlineData("""{"amount":12.5}""")]
    [InlineData("<html></html>")]
    public void SpendWithoutAWholeAmountIsRejected(string body) =>
        Assert.Throws<UsageParseException>(() => ConsoleBilling.ParseCurrentSpend(body));

    [Fact]
    public void CreditsWithoutACurrencyAreRejected() =>
        Assert.Throws<UsageParseException>(() => ConsoleBilling.ParsePrepaidCredits("""{"amount":100}"""));

    [Fact]
    public void ApiKeysAreReadFromEitherShape()
    {
        Assert.Equal("CLI key", ConsoleBilling.ParseApiKeys("""{"data":[{"id":"key-cli","name":"CLI key"}]}""")["key-cli"]);
        Assert.Equal("Server", ConsoleBilling.ParseApiKeys("""[{"id":"key-api","name":"Server"}]""")["key-api"]);
        Assert.Empty(ConsoleBilling.ParseApiKeys("""{"unexpected":true}"""));
    }

    [Fact]
    public void CostsRollUpByModelKeyAndDay()
    {
        var usage = ConsoleBilling.Build(
            new CurrentSpend(1234, null), new PrepaidCredits(8766, "USD"), ConsoleBilling.ParseUsageCost(Costs),
            new Dictionary<string, string> { ["key-cli"] = "claude-code laptop", ["key-api"] = "prod server" });

        Assert.Equal(245m, usage.ApiTokenCostCents);
        Assert.Equal(200m, usage.ApiCostByModel!["Claude Sonnet 5.5"]);
        Assert.Equal(30m, usage.ApiCostByModel["Claude Haiku 4.5"]);
        Assert.Equal(10m, usage.ApiCostByModel["Web Search"]);
        Assert.Equal(5m, usage.ApiCostByModel["Code Execution"]);
        Assert.Equal(0m, usage.ApiCostByModel["Unknown"]);
        Assert.Equal(155.5m, usage.DailyCostCents!["2026-10-01"]);
        Assert.Equal(89.5m, usage.DailyCostCents["2026-10-02"]);

        var sources = usage.SortedCostSources();
        Assert.Equal(["key-cli", "key-api", "unknown"], sources.Select(s => s.KeyId));
        Assert.Equal(ApiCostSourceType.Cli, sources[0].SourceType);
        Assert.Equal("claude-code laptop", sources[0].KeyName);
        Assert.Equal(200m, sources[0].TotalCents);
        Assert.Equal(ApiCostSourceType.Api, sources[1].SourceType);
        Assert.Equal(new Dictionary<string, decimal> { ["Claude Haiku 4.5"] = 30m, ["Web Search"] = 10m, ["Code Execution"] = 5m }, sources[1].CostByModel);
        Assert.Equal("unknown", sources[2].KeyName);
        Assert.True(usage.HasMultipleSources);
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)], usage.SortedDailyCosts().Select(d => d.Date));
    }

    [Fact]
    public void FailedCostRequestLeavesCostsMissingNotZero()
    {
        var usage = ConsoleBilling.Build(new CurrentSpend(1234, null), new PrepaidCredits(8766, "USD"), null, new Dictionary<string, string>());

        Assert.Null(usage.ApiTokenCostCents);
        Assert.Null(usage.ApiCostByModel);
        Assert.Null(usage.CostBySource);
        Assert.Null(usage.DailyCostCents);
        Assert.Empty(usage.SortedDailyCosts());
    }

    [Fact]
    public void SpendShareIsOfSpendPlusRemainingCredits()
    {
        var usage = new ApiUsage { CurrentSpendCents = 1234, PrepaidCreditsCents = 8766, Currency = "USD" };

        Assert.Equal(12.34m, usage.UsedAmount);
        Assert.Equal(87.66m, usage.RemainingAmount);
        Assert.Equal(100m, usage.TotalCredits);
        Assert.Equal(12.34, usage.UsagePercentage, 6);
        Assert.Equal(0, new ApiUsage { CurrentSpendCents = 0, PrepaidCreditsCents = 0, Currency = "USD" }.UsagePercentage);
    }

    [Theory]
    [InlineData("2026-10-15T12:00:00+00:00", "2026-10-01", "2026-11-01")]
    [InlineData("2026-12-31T23:30:00+00:00", "2026-12-01", "2027-01-01")]
    public void BillingMonthIsTheCalendarMonth(string now, string start, string end) =>
        Assert.Equal((start, end), ConsoleBilling.BillingMonth(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), TimeZoneInfo.Utc));

    [Fact]
    public void BillingMonthFollowsTheGivenTimeZone()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

        // 20:00 UTC on 31 October is already 1 November in Tokyo.
        Assert.Equal(("2026-11-01", "2026-12-01"),
            ConsoleBilling.BillingMonth(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero), tokyo));
    }

    [Theory]
    [InlineData("Claude Code (laptop)", ApiCostSourceType.Cli)]
    [InlineData("my-cli-key", ApiCostSourceType.Cli)]
    [InlineData("Production server", ApiCostSourceType.Api)]
    [InlineData("staging", ApiCostSourceType.Api)]
    [InlineData("Onboarding", ApiCostSourceType.Unknown)]
    public void KeyNamesSuggestTheSource(string name, ApiCostSourceType expected) =>
        Assert.Equal(expected, ApiCostSource.Detect(name));

    [Theory]
    [InlineData(12.34, "USD", "$12.34")]
    [InlineData(1234.5, "usd", "$1,234.50")]
    [InlineData(0.05, "EUR", "€0.05")]
    [InlineData(7, "CHF", "CHF 7.00")]
    [InlineData(-3.5, "GBP", "-£3.50")]
    public void MoneyIsWrittenWithTwoDecimals(double amount, string currency, string expected) =>
        Assert.Equal(expected, CurrencyText.Format((decimal)amount, currency, CultureInfo.GetCultureInfo("en-GB")));
}
