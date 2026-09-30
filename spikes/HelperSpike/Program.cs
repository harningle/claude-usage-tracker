// Milestone 1 spike (plan.md section 3): a minimal NativeAOT helper used only to time statusline
// and async hook-forwarding invocations. It performs no authentication, credential reads or
// network requests. Not production code; the real helper arrives in Milestone 5.
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

return args.FirstOrDefault() switch
{
    "noop" => 0,
    "statusline" => Statusline(args.Skip(1).FirstOrDefault()),
    "hook-pipe" when args.Length == 2 => HookPipe(args[1]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: HelperSpike noop | statusline [cache.json] | hook-pipe <pipe-name>");
    return 2;
}

// Renders a one-line status from Claude Code stdin, preferring a non-secret cache file when given.
static int Statusline(string? cachePath)
{
    var input = ReadBounded(Console.OpenStandardInput(), 1024 * 1024);
    string model = "?", directory = "?";
    double? session = null, weekly = null;
    try
    {
        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        model = Text(root, "model", "display_name") ?? model;
        directory = Path.GetFileName(Text(root, "workspace", "current_dir") ?? directory);
        session = Number(root, "rate_limits", "five_hour", "used_percentage");
        weekly = Number(root, "rate_limits", "seven_day", "used_percentage");
    }
    catch (JsonException)
    {
    }

    if (cachePath is not null && File.Exists(cachePath))
    {
        using var cache = JsonDocument.Parse(File.ReadAllBytes(cachePath));
        session = Number(cache.RootElement, "session_percentage") ?? session;
        weekly = Number(cache.RootElement, "weekly_percentage") ?? weekly;
    }

    var line = new StringBuilder();
    line.Append("\e[36m").Append(model).Append("\e[0m | ").Append(directory);
    line.Append(" | 5h ").Append(Percent(session)).Append(" | 7d ").Append(Percent(weekly));
    Console.Out.Write(line.ToString());
    return 0;
}

// Forwards event identifiers only (never prompts, tool input or transcripts) to a local pipe.
// A missing server is an expected dropped observation: exit quietly.
static int HookPipe(string pipeName)
{
    var receivedTicks = DateTime.UtcNow.Ticks;
    var input = ReadBounded(Console.OpenStandardInput(), 256 * 1024);
    string? eventName = null, sessionId = null, toolUseId = null, toolName = null;
    try
    {
        using var doc = JsonDocument.Parse(input);
        eventName = Text(doc.RootElement, "hook_event_name");
        sessionId = Text(doc.RootElement, "session_id");
        toolUseId = Text(doc.RootElement, "tool_use_id");
        toolName = Text(doc.RootElement, "tool_name");
    }
    catch (JsonException)
    {
        eventName = "malformed";
    }

    var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        writer.WriteStartObject();
        writer.WriteNumber("received_ticks", receivedTicks);
        writer.WriteString("event", eventName);
        writer.WriteString("session_id", sessionId);
        writer.WriteString("tool_use_id", toolUseId);
        writer.WriteString("tool_name", toolName);
        writer.WriteEndObject();
    }

    buffer.WriteByte((byte)'\n');
    try
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        pipe.Connect(50);
        pipe.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        pipe.Flush();
    }
    catch (Exception e) when (e is TimeoutException or IOException)
    {
    }

    return 0;
}

static byte[] ReadBounded(Stream stream, int limit)
{
    var buffer = new byte[limit];
    var total = 0;
    int read;
    while (total < limit && (read = stream.Read(buffer, total, limit - total)) > 0)
    {
        total += read;
    }

    return buffer[..total];
}

static string? Text(JsonElement element, params string[] path) =>
    Walk(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

static double? Number(JsonElement element, params string[] path) =>
    Walk(element, path) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

static JsonElement? Walk(JsonElement element, string[] path)
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

static string Percent(double? value) =>
    value is { } v ? v.ToString("0", CultureInfo.InvariantCulture) + "%" : "--";
