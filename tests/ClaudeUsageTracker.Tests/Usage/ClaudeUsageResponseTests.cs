using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>
/// The usage body parser. Upstream has no tests for it; the fixtures are synthetic values in the
/// shape observed live from <c>/api/oauth/usage</c> on 2026-09-30/10-01 (docs/milestone-1.md section 2).
/// </summary>
public class ClaudeUsageResponseTests
{
    private const string ObservedShape = """
        {
          "five_hour": {"utilization": 39.0, "resets_at": "2026-10-01T07:00:00.412345+00:00"},
          "seven_day": {"utilization": 60.0, "resets_at": "2026-10-05T02:00:00+00:00"},
          "seven_day_oauth_apps": null,
          "seven_day_opus": null,
          "seven_day_sonnet": null,
          "seven_day_omelette": null,
          "seven_day_fable": null,
          "nimbus_quill": {"utilization": 3.0, "resets_at": null},
          "extra_usage": {"is_enabled": false, "utilization": null},
          "limits": [
            {"kind": "five_hour", "group": "core", "percent": 39, "severity": "normal",
             "resets_at": "2026-10-01T07:00:00Z", "scope": {}, "is_active": true},
            {"kind": "weekly_scoped", "group": "models", "percent": 12, "severity": "normal",
             "resets_at": "2026-10-05T02:00:00Z", "scope": {"model": {"id": null, "display_name": "Sonnet"}},
             "is_active": true}
          ],
          "spend": {"limit": null},
          "seven_day_breakdown": {"by_surface": []}
        }
        """;

    [Fact]
    public void ParsesObservedShape()
    {
        var usage = ClaudeUsageResponse.Parse(ObservedShape);

        Assert.Equal(39.0, usage.SessionPercentage);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero).AddTicks(4123450), usage.SessionResetTime);
        Assert.Equal(60.0, usage.WeeklyPercentage);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 2, 0, 0, TimeSpan.Zero), usage.WeeklyResetTime);
        Assert.Equal(12, usage.SonnetWeeklyPercentage);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 2, 0, 0, TimeSpan.Zero), usage.SonnetWeeklyResetTime);
    }

    [Fact]
    public void MissingAndNullWindowsStayMissing()
    {
        var usage = ClaudeUsageResponse.Parse(ObservedShape);

        Assert.Null(usage.OpusWeeklyPercentage);
        Assert.Null(usage.DesignWeeklyPercentage);
        Assert.Null(usage.DesignWeeklyResetTime);
        Assert.Null(usage.FableWeeklyPercentage);
        Assert.Null(usage.FableWeeklyResetTime);
        // The endpoint reports percentages only; upstream fabricated token counts from a 1,000,000 limit.
        Assert.Null(usage.SessionTokensUsed);
        Assert.Null(usage.WeeklyTokensUsed);
        Assert.Null(usage.WeeklyLimit);
    }

    [Fact]
    public void EmptyObjectHasNoValues()
    {
        var usage = ClaudeUsageResponse.Parse("{}");

        Assert.Equal(new UsageData(), usage);
    }

    [Theory]
    [InlineData("""{"five_hour": {"utilization": null, "resets_at": null}}""")]
    [InlineData("""{"five_hour": {"utilization": "n/a", "resets_at": "tomorrow"}}""")]
    [InlineData("""{"five_hour": {"utilization": true, "resets_at": 1790000000}}""")]
    [InlineData("""{"five_hour": {}}""")]
    [InlineData("""{"five_hour": null}""")]
    public void UnusableValuesAreNullNotZeroOrSynthesised(string json)
    {
        var usage = ClaudeUsageResponse.Parse(json);

        Assert.Null(usage.SessionPercentage);
        Assert.Null(usage.SessionResetTime);
    }

    [Theory]
    [InlineData("42", 42.0)]
    [InlineData("\" 42.5% \"", 42.5)]
    [InlineData("0", 0.0)]
    public void UtilisationAcceptsNumbersAndPercentStrings(string value, double expected) =>
        Assert.Equal(expected, ClaudeUsageResponse.Parse($$$"""{"seven_day": {"utilization": {{{value}}}}}""").WeeklyPercentage);

    [Theory]
    [InlineData("2026-10-01T07:00:00Z")]
    [InlineData("2026-10-01T07:00:00+00:00")]
    [InlineData("2026-10-01T09:00:00+02:00")]
    [InlineData("2026-10-01T07:00:00.000Z")]
    [InlineData("2026-10-01T07:00:00.000000000Z")]
    public void TimestampsParseWithOrWithoutFractions(string text) =>
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero), ClaudeUsageResponse.Timestamp(JsonValue.Create(text)));

    [Fact]
    public void WeeklyScopedLimitsOverrideLegacyModelWindows()
    {
        var usage = ClaudeUsageResponse.Parse("""
            {
              "seven_day_opus": {"utilization": 5, "resets_at": null},
              "seven_day_sonnet": {"utilization": 6, "resets_at": "2026-10-04T00:00:00Z"},
              "limits": [
                {"kind": "weekly_scoped", "percent": 73, "resets_at": "2026-10-06T00:00:00Z",
                 "scope": {"model": {"id": null, "display_name": "Fable"}}},
                {"kind": "weekly_scoped", "percent": 20, "resets_at": "2026-10-06T00:00:00Z",
                 "scope": {"model": {"id": "claude-opus-4-6", "display_name": "Claude Opus"}}},
                {"kind": "weekly_scoped", "percent": "8%",
                 "scope": {"model": {"id": "claude-sonnet-4-6"}}},
                {"kind": "weekly_scoped", "percent": 31, "resets_at": "2026-10-07T00:00:00Z",
                 "scope": {"model": {"display_name": "Design"}}}
              ]
            }
            """);

        Assert.Equal(73, usage.FableWeeklyPercentage);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), usage.FableWeeklyResetTime);
        Assert.Equal(20, usage.OpusWeeklyPercentage);
        Assert.Equal(8, usage.SonnetWeeklyPercentage);
        // No reset in the entry keeps the legacy reset.
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), usage.SonnetWeeklyResetTime);
        Assert.Equal(31, usage.DesignWeeklyPercentage);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero), usage.DesignWeeklyResetTime);
    }

    [Fact]
    public void LimitsThatDoNotMatchAreIgnored()
    {
        var usage = ClaudeUsageResponse.Parse("""
            {
              "seven_day_opus": {"utilization": 5},
              "limits": [
                {"kind": "five_hour", "percent": 99, "scope": {"model": {"display_name": "Opus"}}},
                {"kind": "weekly_scoped", "percent": 99, "scope": {}},
                {"kind": "weekly_scoped", "percent": 99, "scope": {"model": {"display_name": "Claude Opus"}}},
                {"kind": "weekly_scoped", "percent": 99, "scope": {"model": {"display_name": "Haiku"}}},
                {"kind": "weekly_scoped", "percent": null, "scope": {"model": {"display_name": "Opus"}}},
                "not an object"
              ]
            }
            """);

        // A null percent keeps the legacy value rather than overriding it with 0 as upstream does.
        Assert.Equal(5, usage.OpusWeeklyPercentage);
        Assert.Null(usage.FableWeeklyPercentage);
    }

    private static string SpendBody(string enabled, string used, string limit) =>
        $$$"""{"five_hour": {"utilization": 1}, "spend": {"used": {{{used}}}, "limit": {{{limit}}}, "percent": 0, "severity": "normal", "enabled": {{{enabled}}}, "disabled_reason": "x", "cap": {"money": null, "credits": {"amount_minor": 0, "exponent": 2}}, "balance": null, "auto_reload": null, "disclaimer": "x", "can_purchase_credits": true, "can_toggle": true}}""";

    private const string UsdZero = """{"amount_minor": 0, "currency": "USD", "exponent": 2}""";
    private const string UsdFive = """{"amount_minor": 500, "currency": "USD", "exponent": 2}""";

    [Fact]
    public void ExtraUsageComesFromSpendInExactMinorUnits()
    {
        var usage = ClaudeUsageResponse.Parse(SpendBody("true", """{"amount_minor": 186, "currency": "USD", "exponent": 2}""", UsdFive));

        Assert.Equal(1.86m, usage.CostUsed);
        Assert.Equal(5.00m, usage.CostLimit);
        Assert.Equal("USD", usage.CostCurrency);
        Assert.Equal("5.00", usage.CostLimit!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ZeroSpendKeepsItsDecimalPlaces()
    {
        var usage = ClaudeUsageResponse.Parse(SpendBody("true", UsdZero, UsdFive));

        Assert.Equal("0.00", usage.CostUsed!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CurrencyWithoutMinorUnitsHasNoDecimals()
    {
        var usage = ClaudeUsageResponse.Parse(SpendBody("true",
            """{"amount_minor": 120, "currency": "JPY", "exponent": 0}""", """{"amount_minor": 3000, "currency": "JPY", "exponent": 0}"""));

        Assert.Equal("3000", usage.CostLimit!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("false", UsdZero, UsdFive)]
    [InlineData("null", UsdZero, UsdFive)]
    [InlineData("true", UsdZero, "null")]
    [InlineData("true", UsdZero, """{"amount_minor": 500, "currency": "EUR", "exponent": 2}""")]
    [InlineData("true", """{"amount_minor": 1.5, "currency": "USD", "exponent": 2}""", UsdFive)]
    [InlineData("true", """{"amount_minor": 1, "currency": "USD"}""", UsdFive)]
    public void DisabledOrIncompleteSpendLeavesExtraUsageMissing(string enabled, string used, string limit)
    {
        var usage = ClaudeUsageResponse.Parse(SpendBody(enabled, used, limit));

        Assert.Null(usage.CostUsed);
        Assert.Null(usage.CostLimit);
        Assert.Null(usage.CostCurrency);
    }

    [Fact]
    public void CodeNamedWindowsCanComeAndGo()
    {
        var with = ClaudeUsageResponse.Parse("""{"five_hour": {"utilization": 1}, "nimbus_quill": {"utilization": 3}}""");
        var without = ClaudeUsageResponse.Parse("""{"five_hour": {"utilization": 1}}""");

        Assert.Equal(with, without);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><title>Just a moment...</title></html>")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{\"five_hour\":")]
    public void NonObjectBodiesThrow(string body) => Assert.Throws<UsageParseException>(() => ClaudeUsageResponse.Parse(body));
}
