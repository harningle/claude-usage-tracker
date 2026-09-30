using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;

namespace ClaudeUsageTracker.Core.Providers.Codex;

/// <summary>
/// Reads and writes the Codex CLI's <c>auth.json</c> shapes. Port of the pure parts of upstream
/// <c>CodexAuthService</c>; file access and refresh arrive with Codex profiles.
/// </summary>
public static class CodexAuthJson
{
    private static readonly string[] LastRefreshFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
    ];

    private static readonly JsonSerializerOptions IndentedOutput = new()
    {
        WriteIndented = true,
        // Keep token characters such as '+' literal rather than +; the file is not embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Parses either shape: OAuth <c>tokens</c> (snake_case or camelCase keys) with a top-level
    /// <c>last_refresh</c>, or a top-level <c>OPENAI_API_KEY</c>. Returns null when neither yields a
    /// usable access token.
    /// </summary>
    public static CodexCredentials? Parse(string json)
    {
        if (ParseObject(json) is not { } root)
        {
            return null;
        }

        var apiKey = JsonRead.String(root, "OPENAI_API_KEY") is { } key && !string.IsNullOrWhiteSpace(key)
            ? key
            : null;

        if (apiKey is not null && !root.ContainsKey("tokens"))
        {
            return ApiKeyCredentials(apiKey);
        }

        if (root["tokens"] is not JsonObject tokens || TokenString(tokens, "access_token", "accessToken") is not { } accessToken)
        {
            // API key fallback even when an unusable tokens object exists.
            return apiKey is null ? null : ApiKeyCredentials(apiKey);
        }

        return new CodexCredentials(
            accessToken,
            TokenString(tokens, "refresh_token", "refreshToken") ?? "",
            TokenString(tokens, "id_token", "idToken"),
            TokenString(tokens, "account_id", "accountId"),
            ParseLastRefresh(JsonRead.String(root, "last_refresh")));
    }

    /// <summary>
    /// Serialises credentials into the auth.json shape, merged into the existing JSON so unknown
    /// top-level keys written by the Codex CLI survive. The <c>tokens</c> object is replaced. A new
    /// document is written only when there is no existing content (null).
    /// </summary>
    /// <exception cref="JsonException">
    /// <paramref name="existingJson"/> is not a JSON object; it is never silently replaced.
    /// </exception>
    public static string SerialiseAuthJson(CodexCredentials credentials, string? existingJson, DateTimeOffset now)
    {
        var root = existingJson is null
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject
                ?? throw new JsonException("Existing auth.json content is not a JSON object.");

        var tokens = new JsonObject
        {
            ["access_token"] = credentials.AccessToken,
            ["refresh_token"] = credentials.RefreshToken,
        };
        if (credentials.IdToken is not null)
        {
            tokens["id_token"] = credentials.IdToken;
        }

        if (credentials.AccountId is not null)
        {
            tokens["account_id"] = credentials.AccountId;
        }

        root["tokens"] = tokens;
        root["last_refresh"] = (credentials.LastRefresh ?? now).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return root.ToJsonString(IndentedOutput);
    }

    /// <summary>
    /// Base64url-decodes a JWT payload without verifying the signature; only display metadata is read
    /// from tokens already trusted locally.
    /// </summary>
    public static JsonObject? DecodeJwtPayload(string jwt)
    {
        var segments = jwt.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        var base64 = segments[1].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CodexCredentials ApiKeyCredentials(string apiKey) => new(apiKey, "", null, null, null);

    private static string? TokenString(JsonObject tokens, string snakeKey, string camelKey) =>
        JsonRead.NonEmptyString(tokens, snakeKey) ?? JsonRead.NonEmptyString(tokens, camelKey);

    private static DateTimeOffset? ParseLastRefresh(string? value) =>
        !string.IsNullOrEmpty(value)
        && DateTimeOffset.TryParseExact(
            value, LastRefreshFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static JsonObject? ParseObject(string json)
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
