using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.ClaudeCode;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>Port of the credential-merge cases of upstream <c>ClaudeCodeSyncServiceLogicTests</c>. All tokens are synthetic.</summary>
public class ClaudeCliCredentialsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MergeRefreshedCredentialsUpdatesAccessTokenAndExpiresAt()
    {
        const string cliJson = """
            {
                "claudeAiOauth": {
                    "accessToken": "OLD",
                    "refreshToken": "OLD-RT",
                    "expiresAt": 1000,
                    "scopes": ["a", "b"],
                    "subscriptionType": "max"
                },
                "organizationUuid": "org-1"
            }
            """;

        var merged = ClaudeCliCredentials.MergeRefreshedCredentials(
            cliJson, new OAuthRefreshResponse("NEW", null, 3600, "Bearer"), Now);

        var root = JsonNode.Parse(merged!)!.AsObject();
        var oauth = root["claudeAiOauth"]!.AsObject();
        Assert.Equal("NEW", oauth["accessToken"]!.GetValue<string>());
        Assert.Equal("OLD-RT", oauth["refreshToken"]!.GetValue<string>()); // kept when the response omits it
        Assert.Equal(Now.ToUnixTimeMilliseconds() + 3_600_000, oauth["expiresAt"]!.GetValue<long>());
        Assert.Equal("org-1", root["organizationUuid"]!.GetValue<string>());
        Assert.Equal("max", oauth["subscriptionType"]!.GetValue<string>());
        Assert.Equal(["a", "b"], oauth["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public void MergeRefreshedCredentialsRotatesRefreshTokenWhenProvided()
    {
        const string cliJson = """{"claudeAiOauth":{"accessToken":"old","refreshToken":"old-rt","expiresAt":0}}""";

        var merged = ClaudeCliCredentials.MergeRefreshedCredentials(
            cliJson, new OAuthRefreshResponse("new", "new-rt", 60, null), Now);

        Assert.Equal("new-rt", JsonNode.Parse(merged!)!["claudeAiOauth"]!["refreshToken"]!.GetValue<string>());
    }

    [Fact]
    public void MergeRefreshedCredentialsRejectsInputWithoutClaudeAiOauth() =>
        Assert.Null(ClaudeCliCredentials.MergeRefreshedCredentials(
            """{"unknownRoot": {}}""", new OAuthRefreshResponse("x", null, 60, null), Now));
}
