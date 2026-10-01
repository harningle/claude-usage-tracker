using System.IO;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.Core.Refresh;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>What every HTTP client here needs to read and classify a response without trusting its size or type.</summary>
internal static class HttpResponses
{
    /// <summary>API bodies are a few kilobytes; anything far larger is not the API's answer.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>Reads at most <see cref="MaxBodyBytes"/> of a body as UTF-8.</summary>
    public static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellation)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static bool IsHtml(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A Cloudflare challenge instead of the API's answer: the mitigation header or the challenge page.</summary>
    public static bool IsChallenge(HttpResponseMessage response, string body) =>
        response.Headers.Contains("cf-mitigated") || (IsHtml(response) && body.Contains("Just a moment", StringComparison.Ordinal));

    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now) =>
        response.Headers.TryGetValues("Retry-After", out var values)
            ? RefreshPolicy.ParseRetryAfter(values.FirstOrDefault(), now)
            : null;

    /// <summary>A failure as the log's error category, e.g. <c>rateLimited</c>.</summary>
    public static string Category(FetchFailure failure)
    {
        var name = failure.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
