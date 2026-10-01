using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTracker.Core.Usage;

/// <summary>The usage body was not a JSON object, e.g. an HTML challenge page or truncated output.</summary>
public sealed class UsageParseException(string message) : Exception(message);

/// <summary>
/// Parses the usage body shared by <c>GET /api/oauth/usage</c> and claude.ai's organisation usage
/// endpoint. Port of upstream <c>ClaudeAPIService.parseUsageResponse</c>, except that missing, null or
/// unparseable values stay null instead of becoming 0 %, a synthesised reset time or fabricated
/// token counts (docs/parity.md). Unknown and code-named windows are ignored, so windows that come
/// and go between polls never break parsing.
/// </summary>
public static class ClaudeUsageResponse
{
    public static UsageData Parse(string json)
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

        if (root is null)
        {
            throw new UsageParseException("The usage response was not a JSON object.");
        }

        var session = Window(root, "five_hour");
        var weekly = Window(root, "seven_day");
        var opus = Window(root, "seven_day_opus");
        var sonnet = Window(root, "seven_day_sonnet");
        // Upstream reads design usage from the code-named seven_day_omelette window.
        var design = Window(root, "seven_day_omelette");
        var fable = Window(root, "seven_day_fable");

        // Newer responses null the legacy per-model windows and report them in limits[] instead, so a
        // matching entry overrides the legacy value. Upstream discards Opus reset times.
        if (root["limits"] is JsonArray limits)
        {
            foreach (var limit in limits.OfType<JsonObject>())
            {
                if (String(limit, "kind") != "weekly_scoped"
                    || limit["scope"] is not JsonObject scope
                    || scope["model"] is not JsonObject model
                    || Utilisation(limit["percent"]) is not { } percent)
                {
                    continue;
                }

                var reset = Timestamp(limit["resets_at"]);
                var modelId = String(model, "id")?.ToLowerInvariant() ?? "";
                var name = String(model, "display_name")?.ToLowerInvariant() ?? "";
                bool Matches(params string[] keys) => keys.Any(k => name == k || modelId.Contains(k, StringComparison.Ordinal));

                if (Matches("fable", "mythos"))
                {
                    fable = (percent, reset ?? fable.Reset);
                }
                else if (Matches("opus"))
                {
                    opus = (percent, opus.Reset);
                }
                else if (Matches("sonnet"))
                {
                    sonnet = (percent, reset ?? sonnet.Reset);
                }
                else if (Matches("design", "omelette"))
                {
                    design = (percent, reset ?? design.Reset);
                }
            }
        }

        return new UsageData
        {
            SessionPercentage = session.Percentage,
            SessionResetTime = session.Reset,
            WeeklyPercentage = weekly.Percentage,
            WeeklyResetTime = weekly.Reset,
            OpusWeeklyPercentage = opus.Percentage,
            SonnetWeeklyPercentage = sonnet.Percentage,
            SonnetWeeklyResetTime = sonnet.Reset,
            DesignWeeklyPercentage = design.Percentage,
            DesignWeeklyResetTime = design.Reset,
            FableWeeklyPercentage = fable.Percentage,
            FableWeeklyResetTime = fable.Reset,
        };
    }

    /// <summary>
    /// An RFC 3339 timestamp with or without fractional seconds (upstream's formatter required them and
    /// silently replaced whole-second times with placeholders). More than seven fractional digits are
    /// truncated. Anything else is null.
    /// </summary>
    public static DateTimeOffset? Timestamp(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue(out string? text))
        {
            return null;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end - dot - 1 > 7)
            {
                text = text[..(dot + 8)] + text[end..];
            }
        }

        return DateTimeOffset.TryParseExact(
            text,
            ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>A 0-100 percentage given as a number or as a string such as "42" or "42%".</summary>
    private static double? Utilisation(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out double number))
        {
            return double.IsFinite(number) ? number : null;
        }

        return value.TryGetValue(out string? text)
               && double.TryParse(text.Trim().Replace("%", "", StringComparison.Ordinal),
                   NumberStyles.Float, CultureInfo.InvariantCulture, out number)
               && double.IsFinite(number)
            ? number
            : null;
    }

    private static (double? Percentage, DateTimeOffset? Reset) Window(JsonObject root, string key) =>
        root[key] is JsonObject window ? (Utilisation(window["utilization"]), Timestamp(window["resets_at"])) : (null, null);

    private static string? String(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
