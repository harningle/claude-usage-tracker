using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.App.Hud;

/// <summary>
/// The loopback listener that receives Claude Code's hook events for the HUD. Port of upstream
/// <c>NotchHookServer</c>. It only observes: every accepted request is answered with <c>200 {}</c> before its
/// JSON is even parsed, no connection is held open, and no code path can answer a hook with a decision or
/// with context. Nothing about a request is logged, so neither a payload nor the path secret reaches a file.
/// </summary>
public sealed class HookServer : IDisposable
{
    private readonly string _token;
    private readonly Action<HookEvent> _deliver;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private int _connections;

    /// <param name="token">The path secret: only <c>/hook/&lt;token&gt;/&lt;event&gt;</c> is accepted.</param>
    /// <param name="deliver">Receives each decoded event, on a thread-pool thread.</param>
    public HookServer(string token, Action<HookEvent> deliver)
    {
        _token = token;
        _deliver = deliver;
    }

    public int? Port { get; private set; }

    /// <summary>
    /// Binds 127.0.0.1 on <paramref name="port"/> exclusively and starts accepting. False when the port is in
    /// use or reserved; the listener is then not running.
    /// </summary>
    public bool TryStart(int port)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The hook listener is already running.");
        }

        // Loopback only: the listener is never reachable from the network.
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Dispose();
            return false;
        }

        _listener = listener;
        Port = port;
        _ = AcceptAsync(listener);
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        _listener = null;
        Port = null;
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            // A hard cap on concurrent peers: real hook connections live for milliseconds.
            if (Interlocked.Increment(ref _connections) > HookLimits.MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(HookLimits.ConnectionDeadline);
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                // The path is authorised when the header is framed, so a peer without the secret never occupies body memory.
                var parser = new HookHttpParser(pathAuthoriser: path => EventSuffix(path) is not null);
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    switch (parser.Feed(buffer.AsSpan(0, read)))
                    {
                        case HookHttpParseResult.NeedMoreData:
                            continue;
                        case HookHttpParseResult.Error error:
                            await RespondAsync(stream, error.Status, deadline.Token).ConfigureAwait(false);
                            return;
                        case HookHttpParseResult.OversizeRequest oversize:
                            // An authorised sender with a body over the cap: that is this listener's limit, not the
                            // sender's mistake. The body is drained and acknowledged, and the event is dropped.
                            await DrainAsync(stream, oversize.RemainingBytes, deadline.Token).ConfigureAwait(false);
                            await RespondAsync(stream, 200, deadline.Token).ConfigureAwait(false);
                            return;
                        case HookHttpParseResult.Request request:
                            if (EventSuffix(request.Path) is not { } suffix)
                            {
                                await RespondAsync(stream, 404, deadline.Token).ConfigureAwait(false);
                                return;
                            }

                            // Answer first: the hook never waits for the tracker, and a malformed body never fails it.
                            await RespondAsync(stream, 200, deadline.Token).ConfigureAwait(false);
                            Deliver(suffix, request.Body);
                            return;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A peer that went away or ran into the deadline; there is nothing to answer.
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }

    /// <summary>Reads and discards the rest of a body so the sender's write completes; absurd lengths are not read.</summary>
    private static async Task DrainAsync(NetworkStream stream, int remaining, CancellationToken cancellation)
    {
        if (remaining > HookLimits.MaxDrainBytes)
        {
            return;
        }

        var buffer = new byte[65_536];
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellation).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            remaining -= read;
        }
    }

    private static async Task RespondAsync(NetworkStream stream, int status, CancellationToken cancellation)
    {
        var reason = status switch
        {
            200 => "OK",
            404 => "Not Found",
            405 => "Method Not Allowed",
            431 => "Request Header Fields Too Large",
            _ => "Bad Request",
        };
        // An empty JSON object: no decision, no context, nothing for Claude Code to act on.
        var body = status == 200 ? "{}" : "";
        var response = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellation).ConfigureAwait(false);
    }

    /// <summary>The event of <c>/hook/&lt;token&gt;/&lt;event&gt;</c>, or null for any other path or a wrong secret.</summary>
    private string? EventSuffix(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["hook", var token, var suffix]
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_token))
               && HookEvent.PathSuffixes.Contains(suffix)
            ? suffix
            : null;
    }

    private void Deliver(string suffix, byte[] body)
    {
        HookEvent? hookEvent;
        try
        {
            hookEvent = JsonNode.Parse(body) is JsonObject payload ? HookEvent.From(suffix, payload) : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            // Not JSON, or JSON of a shape no hook sends (a repeated member name): dropped.
            return;
        }

        if (hookEvent is not null)
        {
            _deliver(hookEvent);
        }
    }
}
