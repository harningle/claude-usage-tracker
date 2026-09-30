using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HookBench;

internal sealed class StepTiming
{
    public long RequestTicks { get; set; }
    public long ResponseEndTicks { get; set; }
}

/// <summary>
/// A local stand-in for the Messages API. The main agent loop (a request offering the Read tool)
/// gets <c>toolCalls</c> successful Read calls, one Read of a missing file (to exercise
/// PostToolUseFailure), then a final text answer. Any other request gets a short text reply.
/// Timestamps let the caller measure the CLI-side gap between a tool_use response and the next
/// request, which contains the PreToolUse/PostToolUse hooks and the tool itself.
/// </summary>
internal sealed class FakeAnthropicApi(int port, string readTarget, string missingTarget, int toolCalls,
    Func<int, Task>? beforeStep = null) : IAsyncDisposable
{
    private WebApplication? app;

    public ConcurrentDictionary<int, StepTiming> Steps { get; } = new();
    public ConcurrentQueue<string> SideRequests { get; } = new();
    public ConcurrentQueue<string> Anomalies { get; } = new();

    public int ToolCalls => toolCalls;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        app = builder.Build();
        app.MapPost("/v1/messages", HandleMessagesAsync);
        app.MapPost("/v1/messages/count_tokens", () => Results.Json(new { input_tokens = 1 }));
        app.MapFallback((HttpContext context) =>
        {
            SideRequests.Enqueue($"{context.Request.Method} {context.Request.Path} (404)");
            return Results.Json(new { type = "error", error = new { type = "not_found_error", message = "bench" } },
                statusCode: 404);
        });
        await app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private async Task HandleMessagesAsync(HttpContext context)
    {
        var arrived = Clock.Now;
        using var document = await JsonDocument.ParseAsync(context.Request.Body);
        var root = document.RootElement;
        var stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "bench" : "bench";

        if (!OffersTool(root, "Read"))
        {
            SideRequests.Enqueue($"POST /v1/messages side request (model {model}, stream {stream})");
            await WriteAsync(context, stream, model, "side", TextBlock("ok"), "end_turn");
            return;
        }

        var step = CountToolResults(root);
        var timing = new StepTiming { RequestTicks = arrived };
        if (!Steps.TryAdd(step, timing))
        {
            Anomalies.Enqueue($"duplicate request for step {step}");
        }

        if (beforeStep is not null)
        {
            await beforeStep(step);
        }

        if (step <= toolCalls)
        {
            var target = step < toolCalls ? readTarget : missingTarget;
            var input = new JsonObject { ["file_path"] = target };
            await WriteAsync(context, stream, model, $"step{step}", ToolUseBlock($"toolu_bench_{step:D3}", input),
                "tool_use");
        }
        else
        {
            await WriteAsync(context, stream, model, "final", TextBlock("done"), "end_turn");
        }

        timing.ResponseEndTicks = Clock.Now;
    }

    private static bool OffersTool(JsonElement root, string name) =>
        root.TryGetProperty("tools", out var tools)
        && tools.ValueKind == JsonValueKind.Array
        && tools.EnumerateArray().Any(t => t.TryGetProperty("name", out var n) && n.GetString() == name);

    private static int CountToolResults(JsonElement root)
    {
        var count = 0;
        foreach (var message in root.GetProperty("messages").EnumerateArray())
        {
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                count += content.EnumerateArray()
                    .Count(block => block.TryGetProperty("type", out var type) && type.GetString() == "tool_result");
            }
        }

        return count;
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ToolUseBlock(string id, JsonObject input) =>
        new() { ["type"] = "tool_use", ["id"] = id, ["name"] = "Read", ["input"] = input };

    private static async Task WriteAsync(HttpContext context, bool stream, string model, string id, JsonObject block,
        string stopReason)
    {
        var usage = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 };
        if (!stream)
        {
            var message = new JsonObject
            {
                ["id"] = $"msg_bench_{id}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = new JsonArray(block),
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null,
                ["usage"] = usage,
            };
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(message.ToJsonString());
            await context.Response.CompleteAsync();
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var events = new StringBuilder();

        void Event(string name, JsonObject data) =>
            events.Append("event: ").Append(name).Append("\ndata: ").Append(data.ToJsonString()).Append("\n\n");

        Event("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_bench_{id}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = usage.DeepClone(),
            },
        });

        var isTool = (string?)block["type"] == "tool_use";
        var start = isTool
            ? new JsonObject { ["type"] = "tool_use", ["id"] = block["id"]!.DeepClone(), ["name"] = "Read", ["input"] = new JsonObject() }
            : new JsonObject { ["type"] = "text", ["text"] = "" };
        Event("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = start });
        var delta = isTool
            ? new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = block["input"]!.ToJsonString() }
            : new JsonObject { ["type"] = "text_delta", ["text"] = block["text"]!.DeepClone() };
        Event("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = delta });
        Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
        Event("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = 1 },
        });
        Event("message_stop", new JsonObject { ["type"] = "message_stop" });

        await context.Response.WriteAsync(events.ToString());
        await context.Response.CompleteAsync();
    }
}
