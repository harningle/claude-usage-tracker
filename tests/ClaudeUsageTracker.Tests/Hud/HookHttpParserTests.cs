using System.Text;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.Tests.Hud;

/// <summary>Port of upstream <c>HookHTTPParserTests</c>.</summary>
public class HookHttpParserTests
{
    private static byte[] Request(string method = "POST", string path = "/hook/x/stop", string body = "{}", int? contentLength = null) =>
        Encoding.UTF8.GetBytes(
            $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {contentLength ?? Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

    private static void AssertRequest(HookHttpParseResult result, string path, string body)
    {
        var request = Assert.IsType<HookHttpParseResult.Request>(result);
        Assert.Equal("POST", request.Method);
        Assert.Equal(path, request.Path);
        Assert.Equal(body, Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public void CompleteRequestSinglePacket() =>
        AssertRequest(new HookHttpParser().Feed(Request(body: """{"session_id":"s1"}""")), "/hook/x/stop", """{"session_id":"s1"}""");

    [Fact]
    public void MultiPacketBody()
    {
        var parser = new HookHttpParser();
        var full = Request(body: """{"session_id":"s1","prompt":"hello world"}""");

        // Fed byte by byte, it must only complete at the end.
        HookHttpParseResult last = new HookHttpParseResult.NeedMoreData();
        for (var i = 0; i < full.Length; i++)
        {
            last = parser.Feed(full.AsSpan(i, 1));
            if (i < full.Length - 1)
            {
                Assert.IsType<HookHttpParseResult.NeedMoreData>(last);
            }
        }

        AssertRequest(last, "/hook/x/stop", """{"session_id":"s1","prompt":"hello world"}""");
    }

    [Fact]
    public void NonPostRejected() =>
        Assert.Equal(new HookHttpParseResult.Error(405), new HookHttpParser().Feed(Request("GET", body: "")));

    [Fact]
    public void MissingContentLengthRejected() =>
        Assert.Equal(new HookHttpParseResult.Error(400),
            new HookHttpParser().Feed("POST /hook/x/stop HTTP/1.1\r\nHost: h\r\n\r\n{}"u8));

    [Fact]
    public void OversizedBodyReportedWithRemainingCount() =>
        // The two body bytes ("{}") arrived with the header; the rest is unread.
        Assert.Equal(new HookHttpParseResult.OversizeRequest("/hook/x/stop", 4_000_000 - 2),
            new HookHttpParser().Feed(Request(body: "{}", contentLength: 4_000_000)));

    [Fact]
    public void UnauthorisedPathRejectedBeforeBodyArrives()
    {
        var parser = new HookHttpParser(pathAuthoriser: path => path == "/hook/good/stop");

        // Header only, no body bytes yet: the 404 must not wait for them.
        Assert.Equal(new HookHttpParseResult.Error(404),
            parser.Feed("POST /hook/evil/stop HTTP/1.1\r\nHost: h\r\nContent-Length: 500000\r\n\r\n"u8));
    }

    [Fact]
    public void UnauthorisedPathBeatsOversizeStatus() =>
        // A peer without the secret must learn nothing about the size cap.
        Assert.Equal(new HookHttpParseResult.Error(404),
            new HookHttpParser(maxBodyBytes: 1024, pathAuthoriser: _ => false).Feed(Request(body: "{}", contentLength: 1_000_000)));

    [Fact]
    public void AuthorisedPathStillParses() =>
        AssertRequest(
            new HookHttpParser(pathAuthoriser: path => path == "/hook/x/stop").Feed(Request(body: """{"session_id":"s1"}""")),
            "/hook/x/stop", """{"session_id":"s1"}""");

    [Fact]
    public void OversizedHeaderRejected() =>
        Assert.Equal(new HookHttpParseResult.Error(431),
            new HookHttpParser(maxHeaderBytes: 128, maxBodyBytes: 1024).Feed(
                Encoding.UTF8.GetBytes("POST /hook HTTP/1.1\r\nX-Junk: " + new string('a', 300))));

    // Bounds upstream's parser has but does not test (plan.md section 4, "Hooks": framing and parser bounds).

    [Fact]
    public void AHeaderBlockThatEndsBeyondTheCapIsRejected() =>
        Assert.Equal(new HookHttpParseResult.Error(431),
            new HookHttpParser(maxHeaderBytes: 64).Feed(
                Encoding.UTF8.GetBytes("POST /hook HTTP/1.1\r\nX-Junk: " + new string('a', 100) + "\r\nContent-Length: 0\r\n\r\n")));

    [Theory]
    [InlineData("-1")]
    [InlineData("12x")]
    [InlineData("")]
    [InlineData("99999999999999999999")]
    public void AContentLengthThatIsNotANumberIsRejected(string value) =>
        Assert.Equal(new HookHttpParseResult.Error(400),
            new HookHttpParser().Feed(Encoding.UTF8.GetBytes($"POST /hook/x/stop HTTP/1.1\r\nContent-Length: {value}\r\n\r\n")));

    [Fact]
    public void AMalformedRequestLineIsRejected()
    {
        Assert.Equal(new HookHttpParseResult.Error(400), new HookHttpParser().Feed("POST\r\nContent-Length: 0\r\n\r\n"u8));
        Assert.Equal(new HookHttpParseResult.Error(400), new HookHttpParser().Feed([0xFF, 0xFE, 0x20, 0x2F, 0x0D, 0x0A, 0x0D, 0x0A]));
    }

    [Fact]
    public void HeaderNamesAreCaseInsensitiveAndTheBodyIsCutAtContentLength()
    {
        var result = new HookHttpParser().Feed("POST /hook/x/stop HTTP/1.1\r\ncontent-LENGTH:  2 \r\n\r\n{}trailing"u8);

        AssertRequest(result, "/hook/x/stop", "{}");
    }

    [Fact]
    public void AnEmptyBodyIsAComplete()
    {
        var request = Assert.IsType<HookHttpParseResult.Request>(new HookHttpParser().Feed(Request(body: "")));

        Assert.Empty(request.Body);
    }

    [Fact]
    public void NothingIsAcceptedAfterAResult()
    {
        var parser = new HookHttpParser();
        parser.Feed(Request());

        Assert.Equal(new HookHttpParseResult.Error(400), parser.Feed(Request()));
    }

    [Fact]
    public void ABodyAtTheCapIsARequestAndOneByteMoreIsOversize()
    {
        AssertRequest(new HookHttpParser(maxBodyBytes: 4).Feed(Request(body: "abcd")), "/hook/x/stop", "abcd");
        Assert.Equal(new HookHttpParseResult.OversizeRequest("/hook/x/stop", 0),
            new HookHttpParser(maxBodyBytes: 4).Feed(Request(body: "abcde")));
    }
}
