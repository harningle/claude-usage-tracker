using ClaudeUsageTracker.Core.ClaudeCode;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>
/// Windows equivalents of upstream <c>ClaudeCodeSyncServiceLogicTests.testDisplayLabel*</c>: a config
/// directory replaces the keychain service, so "default" names the default directory, a folder name
/// replaces the service hash suffix and the full path replaces the raw service name. All tokens and
/// accounts are synthetic.
/// </summary>
public class CliLoginTests
{
    private const string WorkDirectory = @"C:\Users\someone\.claude-abcd1234";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisplayLabelEmailAndOrg() => Assert.Equal(
        "user@example.com \u2014 ExampleOrg \u00B7 .claude-abcd1234",
        CliLoginLabels.DisplayLabel(".claude-abcd1234", WorkDirectory, "user@example.com", "ExampleOrg", "max"));

    [Fact]
    public void DisplayLabelEmailOnly() => Assert.Equal(
        "user@example.com \u00B7 .claude-abcd1234",
        CliLoginLabels.DisplayLabel(".claude-abcd1234", WorkDirectory, "user@example.com", null, null));

    [Fact]
    public void DisplayLabelDefaultShorthand() => Assert.Equal(
        "user@example.com \u00B7 default",
        CliLoginLabels.DisplayLabel("default", @"C:\Users\someone\.claude", "user@example.com", null, null));

    [Fact]
    public void DisplayLabelSubscriptionFallback() => Assert.Equal(
        ".claude-abcd1234 \u2014 team",
        CliLoginLabels.DisplayLabel(".claude-abcd1234", WorkDirectory, null, null, "team"));

    [Fact]
    public void DisplayLabelRawFallback() => Assert.Equal(
        WorkDirectory, CliLoginLabels.DisplayLabel(".claude-abcd1234", WorkDirectory, null, null, null));

    [Fact]
    public void DisplayLabelEmptyOrganisationAndSubscriptionFallThrough()
    {
        Assert.Equal("a@b.c \u00B7 default", CliLoginLabels.DisplayLabel("default", "raw", "a@b.c", "", null));
        Assert.Equal("raw", CliLoginLabels.DisplayLabel("default", "raw", null, null, ""));
    }

    [Fact]
    public void ParsesClaudeCodeCredentials()
    {
        var credentials = CliCredentials.Parse("""
            {"claudeAiOauth":{"accessToken":"sk-ant-oat01-synthetic","refreshToken":"sk-ant-ort01-synthetic",
             "expiresAt":1790000000000,"scopes":["user:inference","user:profile"],
             "subscriptionType":"team","rateLimitTier":"default_claude_max_5x"}}
            """);

        Assert.NotNull(credentials);
        Assert.Equal("sk-ant-oat01-synthetic", credentials.AccessToken);
        Assert.Equal("sk-ant-ort01-synthetic", credentials.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790000000000), credentials.ExpiresAt);
        Assert.Equal(["user:inference", "user:profile"], credentials.Scopes);
        Assert.Equal("team", credentials.SubscriptionType);
        Assert.Equal("default_claude_max_5x", credentials.RateLimitTier);
    }

    [Fact]
    public void ExpiryInSecondsIsAccepted()
    {
        var credentials = CliCredentials.Parse("""{"claudeAiOauth":{"accessToken":"a","expiresAt":1790000000}}""");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), credentials?.ExpiresAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"claudeAiOauth":"text"}""")]
    [InlineData("""{"claudeAiOauth":{}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":42}}""")]
    public void UnusableCredentialsParseAsNull(string json) => Assert.Null(CliCredentials.Parse(json));

    [Fact]
    public void ToStringNeverShowsTokens()
    {
        var credentials = CliCredentials.Parse("""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-secret-value"}}""")!;

        Assert.DoesNotContain("secret-value", credentials.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(-5, true)]
    public void ExpiringWithinTheLeewayCountsAsExpired(int minutesUntilExpiry, bool expected)
    {
        var expiry = Now.AddMinutes(minutesUntilExpiry).ToUnixTimeMilliseconds();
        var credentials = CliCredentials.Parse($$$"""{"claudeAiOauth":{"accessToken":"a","expiresAt":{{{expiry}}}}}""")!;

        Assert.Equal(expected, credentials.IsExpiredOrExpiring(Now));
    }

    [Fact]
    public void MissingExpiryIsNotExpired()
    {
        var credentials = CliCredentials.Parse("""{"claudeAiOauth":{"accessToken":"a"}}""")!;

        Assert.Null(credentials.ExpiresAt);
        Assert.False(credentials.IsExpiredOrExpiring(Now));
    }

    [Fact]
    public void ReadsOAuthAccountFromClaudeJson()
    {
        var account = CliAccount.FromClaudeJson("""
            {"numStartups":3,"oauthAccount":{"accountUuid":"acc-1","emailAddress":"user@example.com",
             "organizationUuid":"org-1","organizationName":"ExampleOrg","billingType":"stripe"}}
            """);

        Assert.Equal(new CliAccount("acc-1", "user@example.com", "ExampleOrg", "org-1"), account);
        Assert.Equal("acc-1", account?.Identity);
    }

    [Fact]
    public void IdentityFallsBackToEmail() =>
        Assert.Equal("user@example.com", new CliAccount(null, "user@example.com", null, null).Identity);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"oauthAccount":null}""")]
    [InlineData("not json")]
    public void MissingOAuthAccountReadsAsNull(string json) => Assert.Null(CliAccount.FromClaudeJson(json));

    [Theory]
    [InlineData("acc-1", "acc-1", null, null, CliAccountMatch.SameAccount)]
    [InlineData("user@example.com", "USER@example.com", null, null, CliAccountMatch.SameAccount)]
    [InlineData("acc-1", "acc-2", "rt", "rt", CliAccountMatch.DifferentAccount)]
    [InlineData(null, "acc-1", "rt", "rt", CliAccountMatch.SameAccount)]
    [InlineData(null, null, "rt", "rt-rotated", CliAccountMatch.Unresolved)]
    [InlineData(null, null, null, null, CliAccountMatch.Unresolved)]
    [InlineData("acc-1", null, "", "", CliAccountMatch.Unresolved)]
    public void AccountComparison(
        string? linked, string? current, string? linkedRefresh, string? currentRefresh, CliAccountMatch expected) =>
        Assert.Equal(expected, CliLoginLabels.Compare(linked, current, linkedRefresh, currentRefresh));
}
