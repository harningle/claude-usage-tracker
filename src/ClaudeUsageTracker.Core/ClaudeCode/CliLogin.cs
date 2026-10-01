using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;

namespace ClaudeUsageTracker.Core.ClaudeCode;

/// <summary>
/// The <c>claudeAiOauth</c> login in a Claude Code <c>.credentials.json</c>. A class rather than a
/// record so no generated <c>ToString</c> can print the tokens. Port of upstream
/// <c>ClaudeCodeSyncService</c>'s extractors.
/// </summary>
public sealed class CliCredentials
{
    private CliCredentials(
        string rawJson, string accessToken, string? refreshToken, DateTimeOffset? expiresAt,
        IReadOnlyList<string> scopes, string? subscriptionType, string? rateLimitTier)
    {
        RawJson = rawJson;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAt = expiresAt;
        Scopes = scopes;
        SubscriptionType = subscriptionType;
        RateLimitTier = rateLimitTier;
    }

    /// <summary>
    /// How close to expiry a CLI-owned token stops being used. Claude Code refreshes about a minute
    /// before expiry while it is in use, and upstream uses the same 60 s leeway.
    /// </summary>
    public static readonly TimeSpan ExpiryLeeway = TimeSpan.FromSeconds(60);

    /// <summary>The whole file as read, kept verbatim so unknown fields survive a mirror.</summary>
    public string RawJson { get; }

    public string AccessToken { get; }
    public string? RefreshToken { get; }

    /// <summary>Null when the file gives no expiry; the token is then used until the server rejects it.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    public IReadOnlyList<string> Scopes { get; }
    public string? SubscriptionType { get; }
    public string? RateLimitTier { get; }

    /// <summary>Expired, or expiring within <see cref="ExpiryLeeway"/>.</summary>
    public bool IsExpiredOrExpiring(DateTimeOffset now) => ExpiresAt is { } expiry && expiry - ExpiryLeeway <= now;

    /// <summary>
    /// Parses credential JSON. Returns null unless the root is an object with a <c>claudeAiOauth</c>
    /// object holding a non-empty string <c>accessToken</c>. <c>expiresAt</c> is a JSON number in
    /// milliseconds, or in seconds when it is at most 10^12 (upstream's heuristic).
    /// </summary>
    public static CliCredentials? Parse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json.Trim()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["claudeAiOauth"] is not JsonObject oauth || JsonRead.NonEmptyString(oauth, "accessToken") is not { } access)
        {
            return null;
        }

        DateTimeOffset? expiresAt = null;
        if (JsonRead.Double(oauth, "expiresAt") is { } raw && double.IsFinite(raw) && raw > 0)
        {
            var milliseconds = raw > 1e12 ? raw : raw * 1000;
            if (milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
            }
        }

        var scopes = oauth["scopes"] is JsonArray array
            ? array.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList()
            : [];

        return new CliCredentials(
            json,
            access,
            JsonRead.NonEmptyString(oauth, "refreshToken"),
            expiresAt,
            scopes,
            JsonRead.NonEmptyString(oauth, "subscriptionType"),
            JsonRead.NonEmptyString(oauth, "rateLimitTier"));
    }
}

/// <summary>
/// The <c>oauthAccount</c> object Claude Code keeps in <c>.claude.json</c>: who is logged in. Not a
/// secret, but personal data, so it never goes into logs.
/// </summary>
public sealed record CliAccount(
    string? AccountUuid, string? EmailAddress, string? OrganizationName, string? OrganizationUuid)
{
    /// <summary>Upstream's account identity: <c>accountUuid</c>, else <c>emailAddress</c>.</summary>
    public string? Identity => AccountUuid ?? EmailAddress;

    /// <summary>Reads <c>oauthAccount</c> from a whole <c>.claude.json</c>; null when absent or unreadable.</summary>
    public static CliAccount? FromClaudeJson(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        return root?["oauthAccount"] is JsonObject account
            ? new CliAccount(
                JsonRead.NonEmptyString(account, "accountUuid"),
                JsonRead.NonEmptyString(account, "emailAddress"),
                JsonRead.NonEmptyString(account, "organizationName"),
                JsonRead.NonEmptyString(account, "organizationUuid"))
            : null;
    }
}

public enum CliAccountMatch
{
    SameAccount,
    DifferentAccount,

    /// <summary>Identity is missing on one side and token lineage cannot prove the account either.</summary>
    Unresolved,
}

public static class CliLoginLabels
{
    /// <summary>
    /// Upstream's <c>KeychainEntryDescription.displayLabel</c> with a config-directory shorthand in place
    /// of the keychain service suffix: <c>"default"</c> for the default directory, otherwise the folder
    /// name; <paramref name="rawFallback"/> (the full path) replaces the full service name.
    /// </summary>
    public static string DisplayLabel(
        string shorthand, string rawFallback, string? emailAddress, string? organizationName, string? subscriptionType)
    {
        if (emailAddress is not null)
        {
            return organizationName is { Length: > 0 }
                ? $"{emailAddress} — {organizationName} · {shorthand}"
                : $"{emailAddress} · {shorthand}";
        }

        return subscriptionType is { Length: > 0 } ? $"{shorthand} — {subscriptionType}" : rawFallback;
    }

    /// <summary>
    /// Whether the current CLI login is the account a profile was linked to. Both identities present:
    /// they decide. Otherwise only an unchanged, non-empty refresh token proves the same lineage
    /// (upstream also matched two missing tokens, which proves nothing).
    /// </summary>
    public static CliAccountMatch Compare(
        string? linkedIdentity, string? currentIdentity, string? linkedRefreshToken, string? currentRefreshToken)
    {
        if (linkedIdentity is not null && currentIdentity is not null)
        {
            return string.Equals(linkedIdentity, currentIdentity, StringComparison.OrdinalIgnoreCase)
                ? CliAccountMatch.SameAccount
                : CliAccountMatch.DifferentAccount;
        }

        return linkedRefreshToken is { Length: > 0 } && linkedRefreshToken == currentRefreshToken
            ? CliAccountMatch.SameAccount
            : CliAccountMatch.Unresolved;
    }
}
