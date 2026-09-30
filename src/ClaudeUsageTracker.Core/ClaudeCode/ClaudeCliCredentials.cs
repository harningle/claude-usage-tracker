using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    public static string? MergeRefreshedCredentials(string cliJson, OAuthRefreshResponse refreshed, DateTimeOffset now)
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

        oauth["accessToken"] = refreshed.AccessToken;
        if (refreshed.RefreshToken is not null)
        {
            oauth["refreshToken"] = refreshed.RefreshToken;
        }

        oauth["expiresAt"] = now.ToUnixTimeMilliseconds() + refreshed.ExpiresIn * 1000;
        return root.ToJsonString(CompactOutput);
    }
}
