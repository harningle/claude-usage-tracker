using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Core.Providers.Codex;

/// <summary>
/// The API's primary and secondary windows are not guaranteed to be session and weekly in that
/// order, so they are classified by duration. Port of upstream <c>CodexRateWindowNormalizer</c>.
/// </summary>
public static class CodexRateWindowNormaliser
{
    private enum Role
    {
        Session,
        Weekly,
        Unknown,
    }

    /// <summary>
    /// Unknown-duration windows default to the session slot, a recognised session window always wins
    /// that slot, and a lone weekly window leaves the session empty.
    /// </summary>
    public static (CodexRateWindow? Session, CodexRateWindow? Weekly) Normalise(
        CodexRateWindow? primary, CodexRateWindow? secondary)
    {
        switch (primary, secondary)
        {
            case ({ } p, { } s):
                return (RoleOf(p), RoleOf(s)) switch
                {
                    (Role.Weekly, Role.Session) or (Role.Weekly, Role.Unknown) or (Role.Unknown, Role.Session) =>
                        (s, p),
                    _ => (p, s),
                };
            case ({ } p, null):
                return RoleOf(p) == Role.Weekly ? (null, p) : (p, null);
            case (null, { } s):
                return RoleOf(s) == Role.Weekly ? (null, s) : (s, null);
            default:
                return (null, null);
        }
    }

    private static Role RoleOf(CodexRateWindow window) => window.WindowMinutes switch
    {
        300 => Role.Session,
        10080 => Role.Weekly,
        _ => Role.Unknown,
    };
}

/// <summary>Codex usage URL resolution and response mapping. Port of the pure parts of upstream <c>CodexAPIService</c>.</summary>
public static class CodexApi
{
    private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";

    /// <summary>
    /// Maps a response onto <see cref="UsageData"/>. Codex reports percentages only, so token fields
    /// stay null. When <c>reset_at</c> is missing, the previous still-future reset time is reused so
    /// reset detection stays stable across refreshes; otherwise the reset time is unknown (upstream
    /// estimates it from the current time instead).
    /// </summary>
    public static UsageData MapToUsage(CodexUsageResponse response, UsageData? previous, DateTimeOffset now)
    {
        var (session, weekly) = CodexRateWindowNormaliser.Normalise(
            response.RateLimit?.PrimaryWindow, response.RateLimit?.SecondaryWindow);

        return new UsageData
        {
            SessionPercentage = session?.UsedPercent,
            SessionResetTime = session?.ResetAt ?? StillFuture(previous?.SessionResetTime, now),
            WeeklyPercentage = weekly?.UsedPercent,
            WeeklyResetTime = weekly?.ResetAt ?? StillFuture(previous?.WeeklyResetTime, now),
            PlanType = response.PlanType,
            CreditsUnlimited = response.Credits?.Unlimited,
            CreditsBalance = response.Credits?.Balance,
        };
    }

    /// <summary>
    /// Minimal line scan of <c>config.toml</c> for <c>chatgpt_base_url = "..."</c>, handling
    /// comments and quotes.
    /// </summary>
    public static string? ParseChatGptBaseUrl(string contents)
    {
        var lines = contents.Split(
            ['\n', '\v', '\f', '\r', (char)0x85, (char)0x2028, (char)0x2029], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            // Everything before the first '#'; a fully commented-out line leaves nothing.
            var hash = rawLine.IndexOf('#');
            var line = (hash >= 0 ? rawLine[..hash] : rawLine).Trim();
            var equals = line.IndexOf('=');
            if (equals <= 0 || equals == line.Length - 1 || line[..equals].Trim() != "chatgpt_base_url")
            {
                continue;
            }

            var value = line[(equals + 1)..].Trim();
            if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value.Length >= 2 ? value[1..^1] : "";
            }

            return value.Trim();
        }

        return null;
    }

    /// <summary>
    /// The usage endpoint for the configured base URL (<paramref name="configContents"/> is the
    /// contents of <c>config.toml</c>, or null without one): <c>/wham/usage</c> for backend-api bases,
    /// <c>/api/codex/usage</c> for others such as enterprise proxies.
    /// </summary>
    public static Uri UsageUrl(string? configContents)
    {
        var baseUrl = NormaliseBaseUrl(
            (configContents is null ? null : ParseChatGptBaseUrl(configContents)) ?? DefaultBaseUrl);
        var path = baseUrl.Contains("/backend-api", StringComparison.Ordinal) ? "/wham/usage" : "/api/codex/usage";
        return Uri.TryCreate(baseUrl + path, UriKind.Absolute, out var url)
            ? url
            : new Uri(DefaultBaseUrl + "/wham/usage");
    }

    private static string NormaliseBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            trimmed = DefaultBaseUrl;
        }

        trimmed = trimmed.TrimEnd('/');
        if ((trimmed.StartsWith("https://chatgpt.com", StringComparison.Ordinal)
                || trimmed.StartsWith("https://chat.openai.com", StringComparison.Ordinal))
            && !trimmed.Contains("/backend-api", StringComparison.Ordinal))
        {
            trimmed += "/backend-api";
        }

        return trimmed;
    }

    private static DateTimeOffset? StillFuture(DateTimeOffset? time, DateTimeOffset now) =>
        time > now ? time : null;
}
