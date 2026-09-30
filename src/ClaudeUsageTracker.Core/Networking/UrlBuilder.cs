using System.Globalization;
using System.Text;

namespace ClaudeUsageTracker.Core.Networking;

public enum UrlBuilderError
{
    InvalidBaseUrl,
    InvalidPath,
    InvalidQueryParameter,
    MalformedUrl,
}

public sealed class UrlBuilderException(UrlBuilderError error, string message) : Exception(message)
{
    public UrlBuilderError Error { get; } = error;
}

/// <summary>Immutable URL builder with validated paths and escaped queries. Port of upstream <c>URLBuilder</c>.</summary>
public sealed class UrlBuilder
{
    /// <summary>Path characters besides ASCII letters and digits: unreserved, sub-delims, ':', '@' and '/'.</summary>
    private const string PathCharacters = "-._~!$&'()*+,;=:@/";

    private readonly string _origin;
    private readonly string _path;
    private readonly string _query;

    public UrlBuilder(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            throw new UrlBuilderException(UrlBuilderError.InvalidBaseUrl, $"Invalid base URL: {baseUrl}");
        }

        _origin = uri.GetLeftPart(UriPartial.Authority);
        // System.Uri reports an empty path as "/"; keep it empty so a bare origin builds without a slash.
        _path = uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
        _query = uri.Query.TrimStart('?');
    }

    private UrlBuilder(string origin, string path, string query)
    {
        _origin = origin;
        _path = path;
        _query = query;
    }

    public static UrlBuilder ClaudeApi(string endpoint = "") => WithEndpoint("https://claude.ai/api", endpoint);

    public static UrlBuilder ConsoleApi(string endpoint = "") =>
        WithEndpoint("https://console.anthropic.com/api", endpoint);

    public static UrlBuilder ClaudeStatus(string endpoint = "") =>
        WithEndpoint("https://status.claude.com/api/v2", endpoint);

    /// <summary>
    /// Appends an unescaped path, normalising surrounding slashes and percent-encoding it. Rejects any path
    /// containing "..".
    /// </summary>
    public UrlBuilder AppendingPath(string path)
    {
        var cleanPath = path.Trim();
        if (cleanPath.Contains("..", StringComparison.Ordinal))
        {
            throw new UrlBuilderException(UrlBuilderError.InvalidPath, "Invalid URL path: Path contains '..'");
        }

        var trimmedPath = EscapePath(cleanPath.Trim('/'));
        var newPath = _path.Length == 0
            ? "/" + trimmedPath
            : _path + (_path.EndsWith('/') ? "" : "/") + trimmedPath;
        return new UrlBuilder(_origin, newPath, _query);
    }

    public UrlBuilder AppendingPathComponents(IEnumerable<string> paths)
    {
        var builder = this;
        foreach (var path in paths)
        {
            builder = builder.AppendingPath(path);
        }

        return builder;
    }

    public UrlBuilder AddingQueryParameter(string name, string value)
    {
        if (name.Length == 0)
        {
            throw new UrlBuilderException(
                UrlBuilderError.InvalidQueryParameter, "Invalid query parameter: =Empty parameter name");
        }

        var item = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        return new UrlBuilder(_origin, _path, _query.Length == 0 ? item : _query + "&" + item);
    }

    /// <summary>Builds the URL; only http and https are accepted.</summary>
    public Uri Build()
    {
        var text = _origin + _path + (_query.Length == 0 ? "" : "?" + _query);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new UrlBuilderException(
                UrlBuilderError.MalformedUrl, "Malformed URL: Failed to construct URL from components");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new UrlBuilderException(UrlBuilderError.MalformedUrl, "Malformed URL: Invalid or missing URL scheme");
        }

        return uri;
    }

    /// <summary>
    /// Percent-encodes every byte outside the RFC 3986 path characters, including '%', as upstream's
    /// <c>URLComponents.path</c> does. A '#' or '?' therefore stays in the path, and "%2e%2e" stays literal.
    /// </summary>
    private static string EscapePath(string path)
    {
        var escaped = new StringBuilder(path.Length);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            if (b < 0x80 && (char.IsAsciiLetterOrDigit((char)b) || PathCharacters.Contains((char)b)))
            {
                escaped.Append((char)b);
            }
            else
            {
                escaped.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return escaped.ToString();
    }

    private static UrlBuilder WithEndpoint(string baseUrl, string endpoint)
    {
        var builder = new UrlBuilder(baseUrl);
        return endpoint.Length == 0 ? builder : builder.AppendingPath(endpoint);
    }
}
