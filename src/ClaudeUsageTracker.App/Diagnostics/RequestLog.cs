using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTracker.App.Diagnostics;

/// <summary>
/// One request, as allowlisted metadata only: operation, method, a fixed route, status, timing,
/// transport and error category. Never bodies, headers, cookies, tokens or account data
/// (plan.md section 2, "Diagnostics and build location").
/// </summary>
/// <param name="ProfileId">
/// For an event about one profile (a switch of Claude Code's account), that profile's local ID, which is no account data.
/// Not written for requests.
/// </param>
public sealed record RequestLogEntry(
    DateTimeOffset At,
    string Operation,
    string Method,
    string Route,
    int? Status,
    long DurationMs,
    string Transport,
    string? ErrorCategory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ProfileId = null);

/// <summary>
/// Appends <see cref="RequestLogEntry"/> lines to a bounded JSON-lines file and reads them back for the
/// network log view. Upstream's logger kept URLs and request and response bodies while it was switched
/// on; this one keeps only the fields above, so it runs all the time (docs/parity.md).
/// </summary>
public sealed class RequestLog(string directory, long maxBytes = 1024 * 1024)
{
    private static readonly JsonSerializerOptions LineOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _gate = new();

    public string FilePath => Path.Combine(directory, "requests.jsonl");

    private string PreviousPath => FilePath + ".1";

    /// <summary>Raised after an entry is written or the log is cleared, on the thread that did it.</summary>
    public event Action? Changed;

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
                    File.Move(FilePath, PreviousPath, overwrite: true);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never break polling; the next append tries again.
                return;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The newest entries, newest first. Lines that are not entries are skipped.</summary>
    public IReadOnlyList<RequestLogEntry> ReadRecent(int limit)
    {
        var entries = new List<RequestLogEntry>();
        lock (_gate)
        {
            foreach (var path in new[] { PreviousPath, FilePath })
            {
                try
                {
                    foreach (var line in File.ReadLines(path))
                    {
                        try
                        {
                            if (JsonSerializer.Deserialize<RequestLogEntry>(line, LineOptions) is { Operation: not null, Route: not null } entry)
                            {
                                entries.Add(entry);
                            }
                        }
                        catch (JsonException)
                        {
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        entries.Reverse();
        return entries.Count > limit ? entries[..limit] : entries;
    }

    /// <summary>Deletes every logged entry.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            File.Delete(FilePath);
            File.Delete(PreviousPath);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Writes the log to <paramref name="path"/> as JSON lines, oldest first. The export holds exactly the
    /// fields of <see cref="RequestLogEntry"/>, re-serialised, so nothing else in the files can leave with it.
    /// </summary>
    public int Export(string path)
    {
        var entries = ReadRecent(int.MaxValue);
        var text = new StringBuilder();
        foreach (var entry in entries.Reverse())
        {
            text.Append(JsonSerializer.Serialize(entry, LineOptions)).Append('\n');
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return entries.Count;
    }
}
