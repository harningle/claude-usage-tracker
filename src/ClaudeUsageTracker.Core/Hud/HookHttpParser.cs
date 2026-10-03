using System.Globalization;
using System.Text;

namespace ClaudeUsageTracker.Core.Hud;

/// <summary>Bounds of the hook listener. Upstream's values (<c>Constants.NotchHUD</c>).</summary>
public static class HookLimits
{
    /// <summary>
    /// Hook body cap. Upstream sized it on real Claude Code hook traffic (image reads reach several hundred
    /// kilobytes); a larger body from an authorised sender is drained and answered 200, never an error.
    /// </summary>
    public const int MaxBodyBytes = 1_048_576;

    public const int MaxHeaderBytes = 8_192;

    /// <summary>The most that is read and discarded of an over-cap body before answering.</summary>
    public const int MaxDrainBytes = 8_388_608;

    /// <summary>A connection lives at most this long, whatever state it is in.</summary>
    public static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(10);

    /// <summary>Real hook connections live for milliseconds; a client holding sockets open must not exhaust the listener.</summary>
    public const int MaxConcurrentConnections = 64;
}

public abstract record HookHttpParseResult
{
    /// <summary>Keep feeding bytes.</summary>
    public sealed record NeedMoreData : HookHttpParseResult;

    /// <summary>A complete request was framed.</summary>
    public sealed record Request(string Method, string Path, byte[] Body) : HookHttpParseResult;

    /// <summary>
    /// The body exceeds the cap on an otherwise well-formed request. The caller owns the answer: for an
    /// authorised path that is a drained 200-and-drop. <see cref="RemainingBytes"/> is how much body is still
    /// on the wire.
    /// </summary>
    public sealed record OversizeRequest(string Path, int RemainingBytes) : HookHttpParseResult;

    /// <summary>A protocol violation: answer with this HTTP status and close.</summary>
    public sealed record Error(int Status) : HookHttpParseResult;
}

/// <summary>
/// A minimal incremental HTTP/1.1 request parser for the hook listener: POST only, Content-Length framing
/// and hard size caps. No I/O. Port of upstream <c>HookHTTPParser</c>.
/// </summary>
/// <param name="pathAuthoriser">
/// Asked when the header is framed, before any body byte is kept, so a peer without the path secret can
/// neither park a payload in the buffer nor learn the size cap. Null skips the check.
/// </param>
public sealed class HookHttpParser(
    int maxHeaderBytes = HookLimits.MaxHeaderBytes, int maxBodyBytes = HookLimits.MaxBodyBytes, Func<string, bool>? pathAuthoriser = null)
{
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly MemoryStream _buffer = new();
    private int? _headerEnd;
    private string? _method;
    private string? _path;
    private int? _contentLength;
    private bool _finished;

    /// <summary>Feeds the next bytes read from the connection.</summary>
    public HookHttpParseResult Feed(ReadOnlySpan<byte> data)
    {
        if (_finished)
        {
            return new HookHttpParseResult.Error(400);
        }

        _buffer.Write(data);
        var received = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);

        // Phase 1: frame the header block.
        if (_headerEnd is null)
        {
            var index = received.IndexOf(HeaderEnd);
            if (index < 0)
            {
                return received.Length > maxHeaderBytes ? Finish(new HookHttpParseResult.Error(431)) : new HookHttpParseResult.NeedMoreData();
            }

            _headerEnd = index + HeaderEnd.Length;
            if (_headerEnd > maxHeaderBytes)
            {
                return Finish(new HookHttpParseResult.Error(431));
            }

            if (ParseHeader(received[..index]) is { } status)
            {
                return Finish(new HookHttpParseResult.Error(status));
            }

            if (_contentLength > maxBodyBytes)
            {
                // The body bytes that arrived with the header are already off the wire.
                var arrived = received.Length - _headerEnd.Value;
                return Finish(new HookHttpParseResult.OversizeRequest(_path!, Math.Max(0, _contentLength.Value - arrived)));
            }
        }

        // Phase 2: wait until the body is complete.
        var headerEnd = _headerEnd!.Value;
        var contentLength = _contentLength!.Value;
        return received.Length - headerEnd < contentLength
            ? new HookHttpParseResult.NeedMoreData()
            : Finish(new HookHttpParseResult.Request(_method!, _path!, received.Slice(headerEnd, contentLength).ToArray()));
    }

    private HookHttpParseResult Finish(HookHttpParseResult result)
    {
        _finished = true;
        return result;
    }

    /// <summary>Reads the request line and Content-Length; returns the HTTP error status, or null when the header is acceptable.</summary>
    private int? ParseHeader(ReadOnlySpan<byte> header)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(header);
        }
        catch (DecoderFallbackException)
        {
            return 400;
        }

        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return 400;
        }

        if (parts[0] != "POST")
        {
            return 405;
        }

        // The path is authorised before Content-Length is looked at: always 404 for a peer without the secret.
        if (pathAuthoriser is not null && !pathAuthoriser(parts[1]))
        {
            return 404;
        }

        int? length = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon >= 0 && line[..colon].Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                length = int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
            }
        }

        // Hooks always send Content-Length; anything else is malformed here.
        if (length is null)
        {
            return 400;
        }

        _method = parts[0];
        _path = parts[1];
        _contentLength = length;
        return null;
    }
}
