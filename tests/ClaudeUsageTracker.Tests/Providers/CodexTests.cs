using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Providers;

/// <summary>
/// Port of upstream <c>CodexProviderTests</c>. All tokens are synthetic. Where upstream turns a missing
/// value into 0, false or an estimated time, the Windows port keeps it null (docs/parity.md).
/// </summary>
public class CodexTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // auth.json parsing

    [Fact]
    public void ParseOAuthTokensSnakeCase()
    {
        var credentials = CodexAuthJson.Parse("""
            {
                "tokens": {
                    "access_token": "at-123",
                    "refresh_token": "rt-456",
                    "id_token": "id-789",
                    "account_id": "acc-1"
                },
                "last_refresh": "2026-07-10T12:00:00.000Z"
            }
            """);

        Assert.NotNull(credentials);
        Assert.Equal("at-123", credentials.AccessToken);
        Assert.Equal("rt-456", credentials.RefreshToken);
        Assert.Equal("id-789", credentials.IdToken);
        Assert.Equal("acc-1", credentials.AccountId);
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero), credentials.LastRefresh);
    }

    [Fact]
    public void ParseOAuthTokensCamelCase()
    {
        var credentials = CodexAuthJson.Parse("""
            {
                "tokens": {
                    "accessToken": "at-123",
                    "refreshToken": "rt-456",
                    "idToken": "id-789",
                    "accountId": "acc-1"
                },
                "last_refresh": "2026-07-10T12:00:00Z"
            }
            """);

        Assert.NotNull(credentials);
        Assert.Equal("at-123", credentials.AccessToken);
        Assert.Equal("rt-456", credentials.RefreshToken);
        // Non-fractional ISO 8601 parses too.
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero), credentials.LastRefresh);
    }

    [Fact]
    public void ParseApiKeyOnlyShape()
    {
        var credentials = CodexAuthJson.Parse("""{"OPENAI_API_KEY": "sk-test-123"}""");

        Assert.NotNull(credentials);
        Assert.Equal("sk-test-123", credentials.AccessToken);
        Assert.Empty(credentials.RefreshToken);
        Assert.False(credentials.NeedsRefresh(Now), "API-key credentials must never be refreshed");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tokens": {}}""")]
    public void ParseGarbageReturnsNil(string json) => Assert.Null(CodexAuthJson.Parse(json));

    // Refresh policy

    [Fact]
    public void NeedsRefreshWhenLastRefreshOld() =>
        Assert.True(new CodexCredentials("at", "rt", null, null, Now.AddDays(-9)).NeedsRefresh(Now));

    [Fact]
    public void NoRefreshWhenRecent() =>
        Assert.False(new CodexCredentials("at", "rt", null, null, Now.AddDays(-1)).NeedsRefresh(Now));

    [Fact]
    public void NeedsRefreshWhenLastRefreshMissing() =>
        Assert.True(new CodexCredentials("at", "rt", null, null, null).NeedsRefresh(Now));

    // JWT claims

    [Fact]
    public void AccountIdFromNestedAuthClaim()
    {
        var jwt = MakeJwt(new JsonObject
        {
            ["email"] = "dev@example.com",
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "acc-jwt-1" },
        });
        var credentials = new CodexCredentials("at", "rt", jwt, null, null);

        Assert.Equal("acc-jwt-1", credentials.ResolvedAccountId);
        Assert.Equal("dev@example.com", credentials.Email);
    }

    [Fact]
    public void AccountIdFromTopLevelClaim()
    {
        var jwt = MakeJwt(new JsonObject { ["chatgpt_account_id"] = "acc-top" });

        Assert.Equal("acc-top", new CodexCredentials("at", "rt", jwt, null, null).ResolvedAccountId);
    }

    [Fact]
    public void ExplicitAccountIdWinsOverJwt()
    {
        var jwt = MakeJwt(new JsonObject { ["chatgpt_account_id"] = "acc-jwt" });

        Assert.Equal("acc-explicit", new CodexCredentials("at", "rt", jwt, "acc-explicit", null).ResolvedAccountId);
    }

    // Write-back serialisation

    [Fact]
    public void SerialisePreservesUnknownTopLevelKeys()
    {
        const string existing = """
            {"tokens": {"access_token": "old"}, "some_future_key": {"a": 1}, "OPENAI_API_KEY": "sk-x"}
            """;
        var credentials = new CodexCredentials("new-at", "new-rt", "new-id", "acc", Now);

        var root = JsonNode.Parse(CodexAuthJson.SerialiseAuthJson(credentials, existing, Now))!.AsObject();

        Assert.NotNull(root["some_future_key"]);
        Assert.Equal("sk-x", root["OPENAI_API_KEY"]!.GetValue<string>());
        var tokens = root["tokens"]!.AsObject();
        Assert.Equal("new-at", tokens["access_token"]!.GetValue<string>());
        Assert.Equal("new-rt", tokens["refresh_token"]!.GetValue<string>());
        Assert.Equal("2026-09-30T12:00:00Z", root["last_refresh"]!.GetValue<string>());
    }

    /// <summary>No upstream case; without existing content a new document is written.</summary>
    [Fact]
    public void SerialiseWithoutExistingJsonCreatesDocument()
    {
        var credentials = new CodexCredentials("at", "rt", null, null, Now);

        var root = JsonNode.Parse(CodexAuthJson.SerialiseAuthJson(credentials, existingJson: null, Now))!.AsObject();

        Assert.Equal(["tokens", "last_refresh"], root.Select(p => p.Key));
        Assert.Equal("at", root["tokens"]!["access_token"]!.GetValue<string>());
    }

    /// <summary>No upstream case; upstream replaces unreadable content with a new document.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"tokens": """)]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    public void SerialiseRejectsInvalidExistingJson(string existing)
    {
        var credentials = new CodexCredentials("at", "rt", null, null, Now);

        Assert.ThrowsAny<JsonException>(() => CodexAuthJson.SerialiseAuthJson(credentials, existing, Now));
    }

    // wham/usage decoding

    [Fact]
    public void DecodeFullResponse()
    {
        var response = CodexUsageResponse.Parse("""
            {
                "plan_type": "plus",
                "rate_limit": {
                    "primary_window": {"used_percent": 42, "reset_at": 1789000000, "limit_window_seconds": 18000},
                    "secondary_window": {"used_percent": 17, "reset_at": 1789500000, "limit_window_seconds": 604800}
                },
                "credits": {"has_credits": true, "unlimited": false, "balance": "12.50"}
            }
            """);

        Assert.Equal("plus", response.PlanType);
        Assert.Equal(42, response.RateLimit?.PrimaryWindow?.UsedPercent);
        Assert.Equal(604800, response.RateLimit?.SecondaryWindow?.LimitWindowSeconds);
        Assert.Equal(12.50m, response.Credits?.Balance); // string balances parse
    }

    [Fact]
    public void DecodeMissingSecondaryWindowAndCredits()
    {
        var response = CodexUsageResponse.Parse("""
            {
                "plan_type": "free",
                "rate_limit": {"primary_window": {"used_percent": 90, "reset_at": 1789000000, "limit_window_seconds": 18000}}
            }
            """);

        Assert.Null(response.RateLimit?.SecondaryWindow);
        Assert.Null(response.Credits);
    }

    [Fact]
    public void DecodeToleratesUnknownAndMalformedFields()
    {
        var response = CodexUsageResponse.Parse("""
            {
                "plan_type": "some_new_plan",
                "rate_limit": {
                    "primary_window": {"used_percent": 10, "reset_at": 1789000000, "limit_window_seconds": 18000},
                    "individual_limit": {"limit": "weird"}
                },
                "individual_limit": [1, 2],
                "additional_rate_limits": [{"limit_name": "GPT-5.3-Codex-Spark", "rate_limit": "garbage"}],
                "credits": {"has_credits": "not-a-bool"}
            }
            """);

        Assert.Equal("some_new_plan", response.PlanType); // unknown plan types pass through
        Assert.Equal(10, response.RateLimit?.PrimaryWindow?.UsedPercent);
        // Upstream degrades a malformed flag to false; here it stays unknown.
        Assert.NotNull(response.Credits);
        Assert.Null(response.Credits.HasCredits);
    }

    // Window normalisation (by duration, not position)

    [Fact]
    public void NormaliseStandardOrder()
    {
        var (session, weekly) = CodexRateWindowNormaliser.Normalise(Window(42, minutes: 300), Window(17, minutes: 10080));

        Assert.Equal(42, session?.UsedPercent);
        Assert.Equal(17, weekly?.UsedPercent);
    }

    [Fact]
    public void NormaliseSwappedOrder()
    {
        var (session, weekly) = CodexRateWindowNormaliser.Normalise(Window(17, minutes: 10080), Window(42, minutes: 300));

        Assert.Equal(42, session?.UsedPercent);
        Assert.Equal(17, weekly?.UsedPercent);
    }

    [Fact]
    public void NormaliseLoneWeeklyWindow()
    {
        var (session, weekly) = CodexRateWindowNormaliser.Normalise(Window(17, minutes: 10080), null);

        Assert.Null(session);
        Assert.Equal(17, weekly?.UsedPercent);
    }

    [Fact]
    public void NormaliseUnknownDurationDefaultsToSession()
    {
        var (session, weekly) = CodexRateWindowNormaliser.Normalise(Window(50, minutes: 123), null);

        Assert.Equal(50, session?.UsedPercent);
        Assert.Null(weekly);
    }

    [Fact]
    public void NormaliseUnknownPrimaryWithSessionSecondary()
    {
        // A recognised session window wins the session slot even beside an unrecognised duration.
        var (session, weekly) = CodexRateWindowNormaliser.Normalise(Window(50, minutes: 360), Window(42, minutes: 300));

        Assert.Equal(42, session?.UsedPercent);
        Assert.Equal(50, weekly?.UsedPercent);
    }

    // Usage mapping

    /// <summary>Upstream <c>testMapToUsagePopulatesPercentagesAndZeroTokens</c>: token fields are missing, not zero.</summary>
    [Fact]
    public void MapToUsagePopulatesPercentagesAndLeavesTokensMissing()
    {
        var response = CodexUsageResponse.Parse("""
            {
                "plan_type": "pro",
                "rate_limit": {
                    "primary_window": {"used_percent": 42, "reset_at": 1789000000, "limit_window_seconds": 18000},
                    "secondary_window": {"used_percent": 17, "reset_at": 1789500000, "limit_window_seconds": 604800}
                },
                "credits": {"has_credits": true, "unlimited": true, "balance": 3.5}
            }
            """);

        var usage = CodexApi.MapToUsage(response, previous: null, Now);

        Assert.Equal(42, usage.SessionPercentage);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789000000), usage.SessionResetTime);
        Assert.Equal(17, usage.WeeklyPercentage);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789500000), usage.WeeklyResetTime);
        Assert.Null(usage.SessionTokensUsed);
        Assert.Null(usage.WeeklyTokensUsed);
        Assert.Null(usage.WeeklyLimit);
        Assert.Null(usage.OpusWeeklyTokensUsed);
        Assert.Equal("pro", usage.PlanType);
        Assert.Equal(3.5m, usage.CreditsBalance);
        Assert.True(usage.CreditsUnlimited);
    }

    /// <summary>Upstream <c>testMapToUsageMissingWeeklyGivesZeroPercent</c>: the weekly value is missing, not 0%.</summary>
    [Fact]
    public void MapToUsageMissingWeeklyLeavesWeeklyMissing()
    {
        var response = CodexUsageResponse.Parse("""
            {"rate_limit": {"primary_window": {"used_percent": 99, "reset_at": 1789000000, "limit_window_seconds": 18000}}}
            """);

        var usage = CodexApi.MapToUsage(response, previous: null, Now);

        Assert.Equal(99, usage.SessionPercentage);
        Assert.Null(usage.WeeklyPercentage);
        Assert.Null(usage.WeeklyResetTime);
    }

    [Fact]
    public void MapToUsageMissingResetAtReusesPreviousResetTimes()
    {
        // A now-relative fallback would change on every fetch and look like a reset each time.
        var response = CodexUsageResponse.Parse("""
            {"rate_limit": {
                "primary_window": {"used_percent": 10, "limit_window_seconds": 18000},
                "secondary_window": {"used_percent": 5, "limit_window_seconds": 604800}
            }}
            """);
        var previous = new UsageData { SessionResetTime = Now.AddHours(2), WeeklyResetTime = Now.AddDays(3) };

        var usage = CodexApi.MapToUsage(response, previous, Now);

        Assert.Equal(previous.SessionResetTime, usage.SessionResetTime);
        Assert.Equal(previous.WeeklyResetTime, usage.WeeklyResetTime);
    }

    /// <summary>
    /// Upstream <c>testMapToUsageMissingResetAtIgnoresExpiredPreviousResetTimes</c>: upstream estimates a
    /// future reset from the current time; here the reset time is unknown.
    /// </summary>
    [Fact]
    public void MapToUsageMissingResetAtIgnoresExpiredPreviousResetTimes()
    {
        var response = CodexUsageResponse.Parse("""
            {"rate_limit": {"primary_window": {"used_percent": 10, "limit_window_seconds": 18000}}}
            """);
        var previous = new UsageData { SessionResetTime = Now.AddSeconds(-60) };

        var usage = CodexApi.MapToUsage(response, previous, Now);

        Assert.Null(usage.SessionResetTime);
    }

    // config.toml base URL

    [Fact]
    public void ParseChatGptBaseUrlFromConfig()
    {
        const string toml = """
            # codex config
            model = "gpt-5"
            chatgpt_base_url = "https://enterprise.example.com/proxy"  # comment
            """;

        Assert.Equal("https://enterprise.example.com/proxy", CodexApi.ParseChatGptBaseUrl(toml));
    }

    [Fact]
    public void ParseChatGptBaseUrlIgnoresCommentedOutLine()
    {
        const string toml = """
            # chatgpt_base_url = "https://old-proxy.corp.com"
            model = "gpt-5"
            """;

        Assert.Null(CodexApi.ParseChatGptBaseUrl(toml));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UsageUrlDefaultsToWhamPath(string? config) =>
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", CodexApi.UsageUrl(config).OriginalString);

    [Fact]
    public void UsageUrlNonBackendBaseUsesCodexPath() =>
        Assert.Equal(
            "https://proxy.corp.com/api/codex/usage",
            CodexApi.UsageUrl("chatgpt_base_url = \"https://proxy.corp.com\"").OriginalString);

    [Fact]
    public void UsageUrlBareChatGptHostGetsBackendApi() =>
        Assert.Equal(
            "https://chatgpt.com/backend-api/wham/usage",
            CodexApi.UsageUrl("chatgpt_base_url = \"https://chatgpt.com/\"").OriginalString);

    private static CodexRateWindow Window(double percent, int? minutes) =>
        new(percent, DateTimeOffset.FromUnixTimeSeconds(1789000000), minutes * 60);

    private static string MakeJwt(JsonObject payload)
    {
        var header = Convert.ToBase64String("""{"alg":"none"}"""u8);
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"{header}.{body}.sig";
    }
}
