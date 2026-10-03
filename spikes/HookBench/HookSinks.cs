using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Text.Json;

namespace HookBench;

/// <param name="Route">The HTTP path secret or "pipe".</param>
/// <param name="ReceivedTicks">Arrival at the bench (HTTP) or at the helper (pipe).</param>
/// <param name="Detail">A Notification's <c>notification_type</c> or a SessionEnd's <c>reason</c> (HTTP only).</param>
internal sealed record HookReceipt(string Route, string Event, string? ToolUseId, long ReceivedTicks, long ArrivedTicks, string? Detail = null);

/// <summary>
/// A loopback HTTP hook sink answering <c>200 {}</c> without decisions, like upstream's listener.
/// Each start builds a new host so the benchmark can stop and restart it on one port.
/// </summary>
internal sealed class HttpHookListener(int port, IReadOnlyCollection<string> secrets)
{
    private WebApplication? app;

    public ConcurrentQueue<HookReceipt> Receipts { get; } = new();
    public bool Running => app is not null;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        app = builder.Build();
        app.MapPost("/hook/{secret}/{suffix}", async (HttpContext context, string secret, string suffix) =>
        {
            var arrived = Clock.Now;
            if (!secrets.Contains(secret))
            {
                return Results.NotFound();
            }

            string? toolUseId = null;
            string? detail = null;
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                if (document.RootElement.TryGetProperty("tool_use_id", out var id))
                {
                    toolUseId = id.GetString();
                }

                if (document.RootElement.TryGetProperty("notification_type", out var type)
                    || document.RootElement.TryGetProperty("reason", out type))
                {
                    detail = type.GetString();
                }
            }
            catch (JsonException)
            {
            }

            Receipts.Enqueue(new HookReceipt(secret, suffix, toolUseId, arrived, arrived, detail));
            return Results.Text("{}", "application/json");
        });
        await app.StartAsync();
    }

    /// <summary>Stops the listener; <paramref name="abort"/> drops connections like a crash would.</summary>
    public async Task StopAsync(bool abort)
    {
        if (app is null)
        {
            return;
        }

        using var cancelled = new CancellationTokenSource();
        if (abort)
        {
            cancelled.Cancel();
        }

        await app.StopAsync(cancelled.Token);
        await app.DisposeAsync();
        app = null;
    }
}

/// <summary>A current-user named-pipe sink for the helper's forwarded identifiers.</summary>
internal sealed class PipeHookServer(string name) : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> loops = [];

    public ConcurrentQueue<HookReceipt> Receipts { get; } = new();

    public void Start()
    {
        for (var i = 0; i < 4; i++)
        {
            loops.Add(Task.Run(AcceptLoopAsync));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(name, PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(stopping.Token);
            using var reader = new StreamReader(server);
            var line = await reader.ReadLineAsync(stopping.Token);
            var arrived = Clock.Now;
            if (line is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Receipts.Enqueue(new HookReceipt("pipe", root.GetProperty("event").GetString() ?? "?",
                root.GetProperty("tool_use_id").GetString(), root.GetProperty("received_ticks").GetInt64(), arrived));
        }
    }
}
