using System.Text.Json;

namespace ClaudeUsageTracker.Core.Statusline;

/// <summary>One rate-limit window as Claude Code reports it on the status line's stdin.</summary>
public sealed record StatuslineWindow(double UsedPercentage, DateTimeOffset? ResetsAt);

/// <summary>
/// The fields of Claude Code's status line input that the status line shows
/// (https://code.claude.com/docs/en/statusline). Every field is optional: Claude Code leaves out
/// <c>rate_limits</c> before the first response and for accounts without such limits, and each window on its own.
/// </summary>
public sealed record StatuslineInput(
    string? CurrentDirectory,
    string? ModelName,
    long ContextTokens,
    long? ContextWindowSize,
    StatuslineWindow? FiveHour,
    StatuslineWindow? SevenDay)
{
    public static StatuslineInput Empty { get; } = new(null, null, 0, null, null, null);

    /// <summary>Reads the input; text that is not a JSON object reads as <see cref="Empty"/>, as upstream's script treats it.</summary>
    public static StatuslineInput Parse(ReadOnlySpan<byte> utf8)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            var usage = Walk(root, "context_window", "current_usage");
            // Upstream's context share: input plus cache tokens of the last response, 0 before the first one.
            var tokens = (Integer(usage, "input_tokens") ?? 0)
                         + (Integer(usage, "cache_creation_input_tokens") ?? 0)
                         + (Integer(usage, "cache_read_input_tokens") ?? 0);
            return new StatuslineInput(
                Text(Walk(root, "workspace"), "current_dir") ?? Text(root, "cwd"),
                Text(Walk(root, "model"), "display_name"),
                tokens,
                Integer(Walk(root, "context_window"), "context_window_size") is { } size and > 0 ? size : null,
                Window(Walk(root, "rate_limits", "five_hour")),
                Window(Walk(root, "rate_limits", "seven_day")));
        }
    }

    private static StatuslineWindow? Window(JsonElement? element)
    {
        if (Number(element, "used_percentage") is not { } used)
        {
            return null;
        }

        // Unix epoch seconds.
        DateTimeOffset? resets = Number(element, "resets_at") is { } seconds
                                 && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds()
                                 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            : null;
        return new StatuslineWindow(used, resets);
    }

    private static JsonElement? Walk(JsonElement element, params ReadOnlySpan<string> path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element;
    }

    private static string? Text(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static long? Integer(JsonElement? parent, string name) =>
        Number(parent, name) is { } number && number >= 0 && number <= long.MaxValue ? (long)number : null;
}
