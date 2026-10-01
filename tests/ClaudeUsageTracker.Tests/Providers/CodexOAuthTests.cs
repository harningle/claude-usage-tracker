using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Providers.Codex;

namespace ClaudeUsageTracker.Tests.Providers;

/// <summary>The Codex refresh grant (upstream <c>CodexAuthService.refresh</c>), with synthetic tokens.</summary>
public class CodexOAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestBodyNamesTheClientGrantAndScope()
    {
        var body = JsonNode.Parse(CodexOAuth.RefreshRequestBody("rt-1"))!;

        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", body["client_id"]!.GetValue<string>());
        Assert.Equal("refresh_token", body["grant_type"]!.GetValue<string>());
        Assert.Equal("rt-1", body["refresh_token"]!.GetValue<string>());
        Assert.Equal("openid profile email", body["scope"]!.GetValue<string>());
    }

    [Fact]
    public void ResponseReplacesTheTokensItCarriesAndStampsTheRefresh()
    {
        var current = new CodexCredentials("at-1", "rt-1", "id-1", "account-1", Now.AddDays(-9));

        var refreshed = CodexOAuth.ApplyRefreshResponse(current, """{"access_token":"at-2","refresh_token":"rt-2"}""", Now);

        Assert.Equal("at-2", refreshed?.AccessToken);
        Assert.Equal("rt-2", refreshed?.RefreshToken);
        Assert.Equal("id-1", refreshed?.IdToken);
        Assert.Equal("account-1", refreshed?.AccountId);
        Assert.Equal(Now, refreshed?.LastRefresh);
    }

    [Fact]
    public void ANonJsonResponseIsNotARefresh() =>
        Assert.Null(CodexOAuth.ApplyRefreshResponse(new CodexCredentials("at", "rt", null, null, null), "<html>", Now));

    [Theory]
    [InlineData(400, """{"error":{"code":"refresh_token_reused"}}""", true)]
    [InlineData(400, """{"error":"invalid_grant"}""", true)]
    [InlineData(400, """{"code":"refresh_token_expired"}""", true)]
    [InlineData(401, "", true)]
    [InlineData(400, """{"error":"invalid_request"}""", false)]
    [InlineData(503, "", false)]
    [InlineData(null, "", false)]
    public void ExpiredLoginsAreToldFromPassingFailures(int? status, string body, bool expected) =>
        Assert.Equal(expected, CodexOAuth.IsLoginExpired(status, body));
}
