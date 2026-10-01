using System.IO;
using System.Text.Json;

namespace ClaudeUsageTracker.App.Diagnostics;

/// <summary>
/// One request, as allowlisted metadata only: operation, method, a fixed route, status, timing,
/// transport and error category. Never bodies, headers, cookies, tokens or account data
/// (plan.md section 2, "Diagnostics and build location").
/// </summary>
public sealed record RequestLogEntry(
    DateTimeOffset At,
    string Operation,
    string Method,
    string Route,
    int? Status,
    long DurationMs,
    string Transport,
    string? ErrorCategory);

/// <summary>
/// Appends <see cref="RequestLogEntry"/> lines to a bounded JSON-lines file. The network log view and
/// its clear/export actions arrive in Milestone 3; Milestone 2 records the polling evidence.
/// </summary>
public sealed class RequestLog(string directory, long maxBytes = 1024 * 1024)
{
    private static readonly JsonSerializerOptions LineOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _gate = new();

    public string FilePath => Path.Combine(directory, "requests.jsonl");

    public void Append(RequestLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, LineOptions);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > maxBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never break polling; the next append tries again.
            }
        }
    }
}
