using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.ClaudeCode;
using ClaudeUsageTracker.Core.Json;

namespace ClaudeUsageTracker.Core.Providers.Codex;

/// <summary>
/// The Codex CLI's OAuth <c>refresh_token</c> grant. Port of the request and response handling of upstream
/// <c>CodexAuthService.refresh</c>; the client ID is the Codex CLI's public one.
/// </summary>
public static class CodexOAuth
{
    public static readonly Uri TokenUrl = new("https://auth.openai.com/oauth/token");

    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    /// <summary>Error codes that mean the login itself is no longer valid, as upstream lists them.</summary>
    private static readonly string[] ExpiredCodes =
        ["refresh_token_expired", "refresh_token_reused", "invalid_grant", "refresh_token_invalidated"];

    /// <summary>The JSON request body.</summary>
    public static string RefreshRequestBody(string refreshToken) =>
        new JsonObject
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = "openid profile email",
        }.ToJsonString();

    /// <summary>
    /// The credentials after a successful grant: tokens the response omits are kept, and the refresh time
    /// becomes <paramref name="now"/>. Null when the body is not a JSON object.
    /// </summary>
    public static CodexCredentials? ApplyRefreshResponse(CodexCredentials current, string json, DateTimeOffset now)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        return root is null
            ? null
            : new CodexCredentials(
                JsonRead.NonEmptyString(root, "access_token") ?? current.AccessToken,
                JsonRead.NonEmptyString(root, "refresh_token") ?? current.RefreshToken,
                JsonRead.NonEmptyString(root, "id_token") ?? current.IdToken,
                current.AccountId,
                now);
    }

    /// <summary>Whether a failed grant means the login expired or was revoked, rather than a passing failure.</summary>
    public static bool IsLoginExpired(int? status, string body) =>
        (OAuthErrors.Code(body) is { } code && ExpiredCodes.Contains(code)) || status == 401;
}
