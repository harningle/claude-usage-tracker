using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.ClaudeCode;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>The refresh grant's request body, response parsing and error codes. All tokens are synthetic.</summary>
public class ClaudeOAuthTests
{
    [Fact]
    public void RefreshRequestBodyIsUpstreamsFormEncoding() =>
        Assert.Equal(
            "grant_type=refresh_token&refresh_token=rt%2Bwith%2Fchars%3D&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e",
            ClaudeOAuth.RefreshRequestBody("rt+with/chars="));

    [Fact]
    public void ParsesARotatingResponse()
    {
        var parsed = ClaudeOAuth.ParseRefreshResponse(
            """{"access_token":"at-2","refresh_token":"rt-2","expires_in":28800,"token_type":"Bearer","extra":1}""");

        Assert.Equal("at-2", parsed?.AccessToken);
        Assert.Equal("rt-2", parsed?.RefreshToken);
        Assert.Equal(28800, parsed?.ExpiresIn);
    }

    [Fact]
    public void ResponseWithoutARefreshTokenKeepsTheOldOne() =>
        Assert.Null(ClaudeOAuth.ParseRefreshResponse("""{"access_token":"at-2","expires_in":60}""")?.RefreshToken);

    [Theory]
    [InlineData("""{"refresh_token":"rt-2","expires_in":60}""")]
    [InlineData("""{"access_token":"","expires_in":60}""")]
    [InlineData("""{"access_token":"at-2"}""")]
    [InlineData("""{"access_token":"at-2","expires_in":0}""")]
    [InlineData("""{"access_token":"at-2","expires_in":"soon"}""")]
    [InlineData("<html>Just a moment</html>")]
    [InlineData("[]")]
    public void UnusableResponsesAreRejected(string body) =>
        Assert.Null(ClaudeOAuth.ParseRefreshResponse(body));

    [Theory]
    [InlineData("""{"error":"invalid_grant","error_description":"Refresh token not found"}""", "invalid_grant")]
    [InlineData("""{"error":{"code":"refresh_token_reused","message":"..."}}""", "refresh_token_reused")]
    [InlineData("""{"type":"error","error":{"type":"invalid_request_error","message":"..."}}""", "invalid_request_error")]
    [InlineData("""{"code":"Refresh_Token_Expired"}""", "refresh_token_expired")]
    public void ErrorCodesAreRead(string body, string expected) =>
        Assert.Equal(expected, OAuthErrors.Code(body));

    [Theory]
    [InlineData("""{"error":"a sentence, not a code"}""")]
    [InlineData("""{"error":"sk-ant-ort01-looks/like+a=token"}""")]
    [InlineData("""{"message":"no code"}""")]
    [InlineData("not json")]
    public void AnythingThatIsNotAnIdentifierIsNotACode(string body) =>
        Assert.Null(OAuthErrors.Code(body));

    [Fact]
    public void ApplyTokensWritesTheGivenExpiryAndKeepsOtherFields()
    {
        const string cliJson =
            """{"claudeAiOauth":{"accessToken":"old","refreshToken":"old-rt","expiresAt":1,"scopes":["a"]},"mcpOAuth":{"x":{"accessToken":"mcp"}}}""";

        var applied = ClaudeCliCredentials.ApplyTokens(cliJson, "new", "new-rt", 1_790_000_000_000);

        Assert.Equal(
            """{"claudeAiOauth":{"accessToken":"new","refreshToken":"new-rt","expiresAt":1790000000000,"scopes":["a"]},"mcpOAuth":{"x":{"accessToken":"mcp"}}}""",
            applied);
        Assert.Equal("old-rt", JsonNode.Parse(ClaudeCliCredentials.ApplyTokens(cliJson, "new", null, 5)!)!["claudeAiOauth"]!["refreshToken"]!.GetValue<string>());
        Assert.Null(ClaudeCliCredentials.ApplyTokens("[]", "new", null, 5));
    }
}
