using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;

namespace ClaudeUsageTracker.Core.ClaudeCode;

/// <summary>
/// An OAuth <c>refresh_token</c> grant response. A class rather than a record so no generated
/// <c>ToString</c> can print the tokens.
/// </summary>
public sealed class OAuthRefreshResponse(string accessToken, string? refreshToken, long expiresIn, string? tokenType)
{
    public string AccessToken { get; } = accessToken;

    /// <summary>Null when the server did not rotate the refresh token.</summary>
    public string? RefreshToken { get; } = refreshToken;

    public long ExpiresIn { get; } = expiresIn;
    public string? TokenType { get; } = tokenType;
}

/// <summary>Claude Code credential JSON handling. Port of the pure parts of upstream <c>ClaudeCodeSyncService</c>.</summary>
public static class ClaudeCliCredentials
{
    private static readonly JsonSerializerOptions CompactOutput = new()
    {
        // Keep token characters such as '+' literal rather than +; the file is not embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Merges a refresh response into the <c>claudeAiOauth</c> object, preserving every field the
    /// response does not replace. <c>expiresAt</c> is written in milliseconds since the Unix epoch, as
    /// Claude Code stores it. Returns null when the input has no <c>claudeAiOauth</c> object.
    /// </summary>
    public static string? MergeRefreshedCredentials(string cliJson, OAuthRefreshResponse refreshed, DateTimeOffset now) =>
        ApplyTokens(cliJson, refreshed.AccessToken, refreshed.RefreshToken, now.ToUnixTimeMilliseconds() + refreshed.ExpiresIn * 1000);

    /// <summary>
    /// Writes an access token, an optional rotated refresh token and an expiry (milliseconds since the
    /// Unix epoch) into the <c>claudeAiOauth</c> object, preserving every other field and the key order.
    /// Returns null when the input has no <c>claudeAiOauth</c> object.
    /// </summary>
    public static string? ApplyTokens(string cliJson, string accessToken, string? refreshToken, long expiresAtMilliseconds)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(cliJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["claudeAiOauth"] is not JsonObject oauth)
        {
            return null;
        }

        oauth["accessToken"] = accessToken;
        if (refreshToken is not null)
        {
            oauth["refreshToken"] = refreshToken;
        }

        oauth["expiresAt"] = expiresAtMilliseconds;
        return root.ToJsonString(CompactOutput);
    }
}

/// <summary>
/// Claude Code's OAuth <c>refresh_token</c> grant, as upstream <c>ClaudeCodeSyncService.performTokenRefresh</c>
/// sends it. The client ID is Claude Code's public one. The request shape is upstream's; it first renewed a
/// login live on 2026-10-03 (docs/milestone-4.md).
/// </summary>
public static class ClaudeOAuth
{
    public static readonly Uri TokenUrl = new("https://platform.claude.com/v1/oauth/token");

    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>The <c>application/x-www-form-urlencoded</c> request body.</summary>
    public static string RefreshRequestBody(string refreshToken) =>
        $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}&client_id={Uri.EscapeDataString(ClientId)}";

    /// <summary>
    /// Parses a successful grant response. Null unless it holds a non-empty <c>access_token</c> and a
    /// positive integer <c>expires_in</c>.
    /// </summary>
    public static OAuthRefreshResponse? ParseRefreshResponse(string json)
    {
        if (OAuthErrors.ParseObject(json) is not { } root
            || JsonRead.NonEmptyString(root, "access_token") is not { } accessToken
            || JsonRead.Integer(root, "expires_in") is not { } expiresIn
            || expiresIn <= 0)
        {
            return null;
        }

        return new OAuthRefreshResponse(
            accessToken, JsonRead.NonEmptyString(root, "refresh_token"), expiresIn, JsonRead.NonEmptyString(root, "token_type"));
    }
}

/// <summary>Error codes in OAuth and API error bodies. Codes are identifiers such as <c>invalid_grant</c>, never secrets.</summary>
public static class OAuthErrors
{
    /// <summary>
    /// The error code of an error body: a string <c>error</c>, or the <c>code</c> or <c>type</c> of an
    /// <c>error</c> object, or a top-level <c>code</c>. Lower-cased, and null unless it looks like an
    /// identifier, so response text can never leak into messages or logs through it.
    /// </summary>
    public static string? Code(string json)
    {
        if (ParseObject(json) is not { } root)
        {
            return null;
        }

        var code = JsonRead.String(root, "error")
                   ?? (root["error"] is JsonObject error
                       ? JsonRead.String(error, "code") ?? JsonRead.String(error, "type")
                       : null)
                   ?? JsonRead.String(root, "code");
        return code is { Length: > 0 and <= 60 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? code.ToLowerInvariant()
            : null;
    }

    internal static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
