using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.Tests.Hud;

/// <summary>
/// The hook listener over real loopback sockets. Port of upstream <c>NotchHookServerTests</c>, on a free port
/// of the test's own instead of a fixed one.
/// </summary>
public sealed class HookServerTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly BlockingCollection<HookEvent> _events = [];
    private readonly HookServer _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly int _port;

    public HookServerTests()
    {
        _server = new HookServer(Token, _events.Add);
        _port = FreePort();
        Assert.True(_server.TryStart(_port));
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }

    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string Url(string path) => $"http://127.0.0.1:{_port}{path}";

    private async Task<HttpResponseMessage> PostAsync(string path, string json) =>
        await _http.PostAsync(Url(path), new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private HookEvent? Next(int milliseconds = 5000) => _events.TryTake(out var hookEvent, milliseconds) ? hookEvent : null;

    [Fact]
    public async Task TokenedEventReachesStore()
    {
        var response = await PostAsync($"/hook/{Token}/session-start", """{"session_id":"itest-1","cwd":"C:/tmp/proj"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new HookEvent.SessionStart("itest-1", "C:/tmp/proj") { WorkingDirectory = "C:/tmp/proj" }, Next());
    }

    [Fact]
    public async Task TheAnswerIsAnEmptyObjectThatDecidesNothing()
    {
        foreach (var suffix in HookEvent.PathSuffixes)
        {
            var response = await PostAsync($"/hook/{Token}/{suffix}", """{"session_id":"s","tool_name":"Bash","tool_input":{"command":"rm -rf /"}}""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // An empty JSON object: no permission decision, no blocking, no added context.
            Assert.Equal("{}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task LegacyTokenlessPathGets404AndIsDropped()
    {
        var response = await PostAsync("/hook/session-start", """{"session_id":"legacy-spoof"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(Next(300));
    }

    [Theory]
    [InlineData("/hook/wrongtoken/stop")]
    [InlineData("/hook/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdee/stop")]
    [InlineData("/other/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/stop")]
    [InlineData("/hook/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/permission-request")]
    [InlineData("/hook/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/stop/extra")]
    public async Task WrongTokenOrPathGets404(string path)
    {
        var response = await PostAsync(path, """{"session_id":"x"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(Next(200));
    }

    [Fact]
    public async Task GetRejected405()
    {
        var response = await _http.GetAsync(Url($"/hook/{Token}/stop"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonStillGets200()
    {
        // A malformed body must never show as a hook error in Claude Code.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/hook/{Token}/stop", "{not json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/hook/{Token}/stop", """["an","array"]""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/hook/{Token}/stop", """{"session_id":"a","session_id":"b"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/hook/{Token}/stop", """{"no_session":true}""")).StatusCode);

        Assert.Null(Next(300));
    }

    [Fact]
    public async Task OversizeBodyGets200AndIsDropped()
    {
        // Over the cap: the listener drains the body and answers 200, and the event does not land.
        var padding = new string('a', HookLimits.MaxBodyBytes + 65_536);

        var response = await PostAsync($"/hook/{Token}/session-start", $$"""{"session_id":"oversize","junk":"{{padding}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Next(300));
    }

    [Fact]
    public async Task ABodyJustUnderTheCapIsDelivered()
    {
        var padding = new string('a', HookLimits.MaxBodyBytes - 200);

        var response = await PostAsync($"/hook/{Token}/stop", $$"""{"session_id":"large","junk":"{{padding}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new HookEvent.Stop("large"), Next());
    }

    [Fact]
    public async Task EventsCarryOnlyWhatTheHudShows()
    {
        await PostAsync($"/hook/{Token}/pre-tool-use",
            """{"session_id":"s1","transcript_path":"C:\\Users\\u\\.claude\\projects\\p\\s1.jsonl","cwd":"C:\\work","tool_name":"Read","tool_input":{"file_path":"C:\\work\\secret-plans.txt","content":"private"},"tool_use_id":"toolu_1"}""");

        var pre = Assert.IsType<HookEvent.PreToolUse>(Next());
        Assert.Equal(SessionStatus.ReadingFiles, pre.Status);
        Assert.Equal(new HudTaskText("notch.task.reading", "secret-plans.txt"), pre.Task);
        Assert.Equal("toolu_1", pre.ToolUseId);
        Assert.Equal(@"C:\Users\u\.claude", pre.ConfigDirectory);
    }

    [Fact]
    public void TheListenerIsLoopbackOnlyAndHoldsItsPortExclusively()
    {
        Assert.Equal(_port, _server.Port);

        // Nothing else can take the loopback port while the listener runs, not even by asking to share it.
        using var again = new HookServer(Token, _ => { });
        Assert.False(again.TryStart(_port));
        Assert.Null(again.Port);
        using var sharer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sharer.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        Assert.Throws<SocketException>(() => sharer.Bind(new IPEndPoint(IPAddress.Loopback, _port)));

        // The listener itself holds only the loopback address: the same port on the other addresses is still free.
        var elsewhere = new TcpListener(IPAddress.Any, _port);
        elsewhere.Start();
        elsewhere.Stop();
    }

    [Fact]
    public async Task AStoppedListenerRefusesConnectionsAtOnce()
    {
        _server.Dispose();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => PostAsync($"/hook/{Token}/stop", """{"session_id":"x"}"""));

        Assert.Equal(SocketError.ConnectionRefused, (error.InnerException as SocketException)?.SocketErrorCode);
        Assert.Null(_server.Port);
    }

    [Fact]
    public async Task AnUnauthorisedPeerIsAnsweredBeforeItSendsItsBody()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();

        // The header announces a body that never comes; the 404 must not wait for it.
        await stream.WriteAsync("POST /hook/evil/stop HTTP/1.1\r\nHost: h\r\nContent-Length: 500000\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.Equal("HTTP/1.1 404 Not Found", status);
    }

    [Fact]
    public async Task ASilentConnectionDoesNotStopOthersFromBeingServed()
    {
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, _port, TestContext.Current.CancellationToken);

        var response = await PostAsync($"/hook/{Token}/stop", """{"session_id":"busy"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new HookEvent.Stop("busy"), Next());
    }
}

/// <summary>The choice of the listener's port (plan.md section 2): outside the machine's dynamic and excluded ranges, never hard-coded.</summary>
public class HookPortsTests
{
    private const string EnglishDynamic = "\r\nProtocol tcp Dynamic Port Range\r\n---------------------------------\r\nStart Port      : 1024\r\nNumber of Ports : 13977\r\n\r\n";

    // A Chinese Windows, read as Latin-1: only the numbers are legible, which is all that is read.
    private const string LocalisedDynamic = "\r\nÐ­Òé tcp ¶¯Ì¬¶Ë¿Ú·¶Î§\r\n---------------------------------\r\nÆô¶¯¶Ë¿Ú        : 49152\r\n¶Ë¿ÚÊý          : 16384\r\n\r\n";

    private const string Excluded =
        "\r\nProtocol tcp Port Exclusion Ranges\r\n\r\nStart Port    End Port\r\n----------    --------\r\n     27339       27339\r\n     50000       50059     *\r\n\r\n* - Administered port exclusions.\r\n\r\n";

    [Fact]
    public void TheDynamicRangeIsReadFromItsTwoNumbers()
    {
        Assert.Equal(new PortRange(1024, 15000), HookPorts.ParseDynamicRange(EnglishDynamic));
        Assert.Equal(new PortRange(49152, 65535), HookPorts.ParseDynamicRange(LocalisedDynamic));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Start Port : 1024\r\n")]
    [InlineData("Start Port : 1024\r\nNumber of Ports : 13977\r\nAnother : 5\r\n")]
    [InlineData("Start Port : 60000\r\nNumber of Ports : 9000\r\n")]
    [InlineData("Start Port : 0\r\nNumber of Ports : 10\r\n")]
    public void AnUnexpectedAnswerIsNotGuessedAt(string output) =>
        Assert.Throws<PortQueryException>(() => HookPorts.ParseDynamicRange(output));

    [Fact]
    public void ExcludedRangesAreReadWithAndWithoutTheAdministeredMark()
    {
        Assert.Equal([new PortRange(27339, 27339), new PortRange(50000, 50059)], HookPorts.ParseExcludedRanges(Excluded));
        Assert.Empty(HookPorts.ParseExcludedRanges("\r\nProtocol tcp Port Exclusion Ranges\r\n\r\nStart Port    End Port\r\n----------    --------\r\n\r\n"));
    }

    [Theory]
    [InlineData(1023, false)]     // well-known ports
    [InlineData(1024, false)]     // dynamic range start
    [InlineData(15000, false)]    // dynamic range end
    [InlineData(15001, true)]
    [InlineData(27339, false)]    // excluded
    [InlineData(27340, true)]
    [InlineData(50059, false)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void EligiblePortsAreOutsideEveryRange(int port, bool eligible) =>
        Assert.Equal(eligible,
            new PortReservations(HookPorts.ParseDynamicRange(EnglishDynamic), HookPorts.ParseExcludedRanges(Excluded)).IsEligible(port));

    [Fact]
    public void AChosenPortIsEligibleAndWasActuallyBound()
    {
        var reservations = new PortReservations(new PortRange(1024, 15000), [new PortRange(50000, 50059)]);
        var tried = new List<int>();

        var port = HookPorts.Choose(reservations, new Random(7), candidate =>
        {
            tried.Add(candidate);
            // The first three candidates are in use by something else.
            return tried.Count > 3;
        });

        Assert.Equal(4, tried.Count);
        Assert.Equal(tried[^1], port);
        Assert.All(tried, candidate => Assert.True(reservations.IsEligible(candidate)));
        Assert.Equal(4, tried.Distinct().Count());
    }

    [Fact]
    public void ChoicesDifferBetweenInstallations() =>
        Assert.NotEqual(
            HookPorts.Choose(new PortReservations(new PortRange(49152, 65535), []), new Random(1), _ => true),
            HookPorts.Choose(new PortReservations(new PortRange(49152, 65535), []), new Random(2), _ => true));

    [Fact]
    public void NoPortIsChosenWhenNoneIsEligibleOrFree()
    {
        Assert.Null(HookPorts.Choose(new PortReservations(new PortRange(1024, 65535), []), new Random(1), _ => true));

        var attempts = 0;
        Assert.Null(HookPorts.Choose(new PortReservations(new PortRange(49152, 65535), []), new Random(1), _ =>
        {
            attempts++;
            return false;
        }));
        // Bounded: it gives up instead of trying every port.
        Assert.InRange(attempts, 1, 64);
    }

    [Fact]
    public void ThisMachinesRangesCanBeRead()
    {
        PortReservations reservations;
        try
        {
            reservations = HookPorts.Query();
        }
        catch (PortQueryException e) when (e.Message == "netsh did not answer in time." && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            // On 2026-11-22 netsh ran past its 10 seconds on GitHub's Windows runners four times in a row, where it
            // answers in under 50 ms on a desktop PC. The parsing is covered by the canned outputs above.
            Assert.Skip("netsh did not answer within 10 seconds on this GitHub Actions runner.");
            return;
        }

        Assert.InRange(reservations.Dynamic.Start, 1, 65535);
        Assert.True(reservations.Dynamic.End >= reservations.Dynamic.Start);
        Assert.All(reservations.Excluded, range => Assert.True(range.End >= range.Start));
    }
}
