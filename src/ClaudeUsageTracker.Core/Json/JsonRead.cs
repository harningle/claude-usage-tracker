using System.Globalization;
using System.Text.Json.Nodes;

namespace ClaudeUsageTracker.Core.Json;

/// <summary>
/// Tolerant field readers for external JSON: a missing or wrongly typed field reads as null
/// instead of failing the whole document, matching upstream's per-field <c>try?</c> decoding.
/// </summary>
internal static class JsonRead
{
    public static string? String(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public static string? NonEmptyString(JsonObject obj, string key) =>
        String(obj, key) is { Length: > 0 } text ? text : null;

    public static bool? Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    public static long? Integer(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out long number) ? number : null;

    /// <summary>A number, or a string holding one.</summary>
    public static double? Double(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        return value.TryGetValue(out string? text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    /// <summary>A number, or a string holding one.</summary>
    public static decimal? Decimal(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out decimal number))
        {
            return number;
        }

        return value.TryGetValue(out string? text)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    /// <summary>Unix seconds as a timestamp; out-of-range values read as null.</summary>
    public static DateTimeOffset? UnixSeconds(JsonObject obj, string key) =>
        Integer(obj, key) is { } seconds
        && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds()
        && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
