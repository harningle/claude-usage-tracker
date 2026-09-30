using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;

namespace ClaudeUsageTracker.Core.Providers.Codex;

/// <summary>
/// Parsed Codex CLI credentials. Port of upstream <c>CodexCredentials</c>.
/// A class rather than a record so no generated <c>ToString</c> can print the tokens.
/// </summary>
public sealed class CodexCredentials(
    string accessToken, string refreshToken, string? idToken, string? accountId, DateTimeOffset? lastRefresh)
{
    /// <summary>The Codex CLI refreshes when the last refresh is older than this, not on JWT expiry.</summary>
    private static readonly TimeSpan RefreshAge = TimeSpan.FromDays(8);

    public string AccessToken { get; } = accessToken;

    /// <summary>Empty for API-key credentials, which are never refreshed.</summary>
    public string RefreshToken { get; } = refreshToken;

    public string? IdToken { get; } = idToken;
    public string? AccountId { get; } = accountId;
    public DateTimeOffset? LastRefresh { get; } = lastRefresh;

    /// <summary>
    /// Account ID for the <c>ChatGPT-Account-Id</c> header: the explicit account ID, else the
    /// <c>chatgpt_account_id</c> claim from the ID or access token.
    /// </summary>
    public string? ResolvedAccountId
    {
        get
        {
            if (!string.IsNullOrEmpty(AccountId))
            {
                return AccountId;
            }

            foreach (var claims in TokenClaims())
            {
                if (JsonRead.NonEmptyString(claims, "chatgpt_account_id") is { } id)
                {
                    return id;
                }

                if (claims["https://api.openai.com/auth"] is JsonObject auth
                    && JsonRead.NonEmptyString(auth, "chatgpt_account_id") is { } nestedId)
                {
                    return nestedId;
                }
            }

            return null;
        }
    }

    /// <summary>Signed-in email from the tokens, for display.</summary>
    public string? Email =>
        TokenClaims().Select(claims => JsonRead.NonEmptyString(claims, "email")).FirstOrDefault(e => e is not null);

    public bool NeedsRefresh(DateTimeOffset now) =>
        RefreshToken.Length > 0 && (LastRefresh is not { } last || now - last > RefreshAge);

    private IEnumerable<JsonObject> TokenClaims()
    {
        foreach (var token in new[] { IdToken, AccessToken })
        {
            if (token is not null && CodexAuthJson.DecodeJwtPayload(token) is { } claims)
            {
                yield return claims;
            }
        }
    }
}
