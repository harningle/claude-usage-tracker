using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using ClaudeUsageTracker.App.Browser;
using ClaudeUsageTracker.App.Usage;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>
/// The claude.ai browser route with a real WebView2 runtime, parked on a loopback page instead of claude.ai, so
/// no account or network is involved. The background view runs on its own dispatcher thread, as on the app's.
/// </summary>
public sealed class BrowserSessionsTests : IDisposable
{
    private const string Organisation = "org-1";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A usage check sends its three requests at once. On a view that is not parked yet each of them used to start
    /// its own navigation, and the second cancelled the first, so the first check after every start failed with
    /// "network" (found live since Milestone 3).
    /// </summary>
    [Fact]
    public async Task TheFirstCheckOnANewViewSucceedsAndNavigatesOnce()
    {
        Assert.SkipWhen(BrowserSessions.RuntimeVersion() is null, "The WebView2 Runtime is not installed.");
        using var server = new LoopbackSite();
        var (dispatcher, thread) = StartDispatcher();
        var sessions = new BrowserSessions(_temp.Path, dispatcher, server.Parked);
        var profile = Guid.NewGuid();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var responses = await Task.WhenAll(
                sessions.SendAsync(profile, ClaudeAiOperation.Usage, Organisation, timeout.Token),
                sessions.SendAsync(profile, ClaudeAiOperation.OverageSpendLimit, Organisation, timeout.Token),
                sessions.SendAsync(profile, ClaudeAiOperation.OverageCreditGrant, Organisation, timeout.Token));

            Assert.All(responses, r => Assert.Equal((200, (string?)null), (r.Status ?? 0, r.Error)));
            Assert.Equal(1, server.Requests("/robots.txt"));
            Assert.Equal(1, server.Requests($"/api/organizations/{Organisation}/usage"));
        }
        finally
        {
            await dispatcher.InvokeAsync(() => sessions.ClearAsync(profile)).Task.Unwrap();
            dispatcher.InvokeShutdown();
            thread.Join();
        }
    }

    private static (Dispatcher Dispatcher, Thread Thread) StartDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return (ready.Task.GetAwaiter().GetResult(), thread);
    }

    /// <summary>A minimal HTTP/1.1 site on 127.0.0.1: the parked page, JSON for the API paths, 404 otherwise.</summary>
    private sealed class LoopbackSite : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<string, int> _requests = new();

        public LoopbackSite()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public Uri Parked => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/robots.txt");

        public int Requests(string path) => _requests.GetValueOrDefault(path);

        public void Dispose() => _listener.Stop();

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = AnswerAsync(client);
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        return;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var path = head.ToString().Split(' ')[1];
                _requests.AddOrUpdate(path, 1, (_, n) => n + 1);
                var (status, type, body) = path switch
                {
                    "/robots.txt" => ("200 OK", "text/plain", "User-agent: *\n"),
                    _ when path.StartsWith("/api/", StringComparison.Ordinal) => ("200 OK", "application/json", "{}"),
                    _ => ("404 Not Found", "text/plain", ""),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                var response = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                await stream.WriteAsync(bytes);
            }
        }
    }
}
