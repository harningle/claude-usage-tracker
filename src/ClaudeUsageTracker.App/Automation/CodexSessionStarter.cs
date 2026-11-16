using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.CodexCli;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Notifications;

namespace ClaudeUsageTracker.App.Automation;

public enum CodexExecOutcome
{
    /// <summary>The CLI exited with 0 after reporting a finished turn: the request reached the model.</summary>
    Completed,

    /// <summary>It exited otherwise. The request may still have reached the model.</summary>
    Failed,

    /// <summary>No <c>codex.exe</c> on PATH.</summary>
    NotFound,

    /// <summary>A <c>codex.exe</c> was found, but Windows did not start it.</summary>
    StartFailed,

    /// <summary>It did not finish in time and was stopped.</summary>
    TimedOut,
}

/// <summary>Why Windows did not start a <c>codex.exe</c> that was found.</summary>
/// <param name="Path">The <c>codex.exe</c> that was found.</param>
/// <param name="Code">The Windows error code.</param>
/// <param name="Message">Windows' text for the error.</param>
public sealed record CodexStartError(string Path, int Code, string Message);

/// <param name="ExitCode">Null when the CLI was not found, did not start or did not finish.</param>
/// <param name="StartError">Why Windows did not start the CLI, for <see cref="CodexExecOutcome.StartFailed"/>.</param>
public sealed record CodexExecResult(CodexExecOutcome Outcome, int? ExitCode, CodexStartError? StartError = null);

/// <summary>
/// One run of the Codex CLI that sends a single short message, the Codex counterpart of the claude.ai conversation
/// (not an upstream feature). It runs as the user's own CLI with the profile's Codex home, so the CLI uses and, if it
/// must, renews its own login; the tracker never touches <c>auth.json</c> (plan.md section 2).
/// </summary>
public static class CodexExec
{
    public const string FileName = "codex.exe";

    /// <summary>A "Hi" request is answered within seconds; one that takes longer is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>How long the output may stay open after the CLI exited.</summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    /// <summary>Each run gets a new empty folder, <c>%TEMP%\ClaudeUsageTracker\codex-auto-start-&lt;id&gt;</c>.</summary>
    private const string FolderPrefix = "codex-auto-start-";

    /// <summary>
    /// <c>codex exec</c> with machine-readable events, no saved session, none of the user's <c>config.toml</c> (so no MCP
    /// servers, other model provider or project settings), and a read-only sandbox. It runs in a new empty folder.
    /// </summary>
    public static readonly IReadOnlyList<string> Arguments =
        ["exec", "--json", "--ephemeral", "--skip-git-repo-check", "--ignore-user-config", "--sandbox", "read-only", SessionAutoStart.Prompt];

    /// <summary>
    /// The first <c>codex.exe</c> in the PATH values, in order, with environment variables expanded; null when there is none.
    /// An npm install provides only <c>codex.cmd</c>, which is not run.
    /// </summary>
    public static string? Find(IEnumerable<string?> pathValues)
    {
        foreach (var entry in pathValues.SelectMany(value => (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(entry.Trim('"')), FileName));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Runs the CLI once and waits for it, at most <paramref name="timeout"/>; a run that takes longer is stopped with its children.</summary>
    /// <param name="executable">The CLI, or null when it was not found.</param>
    public static async Task<CodexExecResult> RunAsync(string? executable, string codexHome, TimeSpan timeout, CancellationToken cancellation)
    {
        if (executable is null)
        {
            return new CodexExecResult(CodexExecOutcome.NotFound, null);
        }

        var parent = Path.Combine(Path.GetTempPath(), "ClaudeUsageTracker");
        // Earlier runs' folders that something the CLI started was still using when they ended; one still in use stays.
        foreach (var stale in Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, FolderPrefix + "*").ToList() : [])
        {
            TryDelete(stale);
        }

        var folder = Path.Combine(parent, FolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in Arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["CODEX_HOME"] = codexHome;
            // With an API key in the environment the CLI would bill the key instead of starting the plan's window.
            start.Environment.Remove("CODEX_API_KEY");
            start.Environment.Remove("OPENAI_API_KEY");

            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception e)
            {
                // Found, but not started: removed since, blocked, or not a program. Windows' error says which.
                return new CodexExecResult(CodexExecOutcome.StartFailed, null, new CodexStartError(executable, e.NativeErrorCode, e.Message));
            }

            using (process)
            {
                if (process is null)
                {
                    return new CodexExecResult(CodexExecOutcome.NotFound, null);
                }

                // An open input would be read and appended to the prompt.
                process.StandardInput.Close();
                var turnCompleted = new StrongBox<bool>();
                var reading = Task.WhenAll(ReadEventsAsync(process.StandardOutput, turnCompleted), DrainAsync(process.StandardError));
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                limit.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // It exited meanwhile.
                    }

                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    return new CodexExecResult(CodexExecOutcome.TimedOut, null);
                }

                // The output ends with the process, unless something it started holds it open; that must not hold auto-start up.
                await Task.WhenAny(reading, Task.Delay(OutputGrace, CancellationToken.None)).ConfigureAwait(false);
                return Judge(process.ExitCode, Volatile.Read(ref turnCompleted.Value));
            }
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>Removes a run's folder. One that cannot be removed yet does not change any outcome; the next run removes it.</summary>
    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static CodexExecResult Judge(int exitCode, bool turnCompleted) =>
        new(exitCode == 0 && turnCompleted ? CodexExecOutcome.Completed : CodexExecOutcome.Failed, exitCode);

    /// <summary>Whether one line of <c>codex exec --json</c> output is its <c>turn.completed</c> event.</summary>
    public static bool IsTurnCompleted(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject { } item && item["type"] is JsonValue type
                   && type.TryGetValue(out string? name) && name == "turn.completed";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Notes a <c>turn.completed</c> event as soon as it is read. Reading stops when the output closes or is disposed.</summary>
    private static async Task ReadEventsAsync(StreamReader output, StrongBox<bool> turnCompleted)
    {
        try
        {
            while (await output.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (IsTurnCompleted(line))
                {
                    Volatile.Write(ref turnCompleted.Value, true);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Reads the error output to its end so the CLI never blocks on a full pipe; what it says is not kept.</summary>
    private static async Task DrainAsync(StreamReader errors)
    {
        try
        {
            await errors.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// Starts a Codex session window for a profile that reads the Codex CLI's login, through <see cref="CodexExec"/>. Only the
/// opt-in auto-start calls it.
/// </summary>
/// <param name="exec">Runs the CLI with a Codex home; fixture mode answers from its files instead.</param>
/// <param name="defaultHome">The Codex home of a link that names none.</param>
public sealed class CodexSessionStarter(
    Func<string, CancellationToken, Task<CodexExecResult>> exec, Func<string> defaultHome, TimeProvider time, RequestLog log)
{
    /// <summary>
    /// Runs the CLI once. Returns null, having run nothing, when the CLI's login is not the account the link was made for (or
    /// cannot be told), since the login is shared and the message would start that account's window.
    /// </summary>
    public async Task<AutoStartResult?> StartSessionAsync(CodexLink link, CancellationToken cancellation)
    {
        var home = link.CodexHome ?? defaultHome();
        var login = CodexStore.Read(home);
        if (login.State != CodexLoginState.Usable
            || (link.Identity is { } linked && !string.Equals(linked, login.Identity, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var started = Stopwatch.GetTimestamp();
        var result = await exec(home, cancellation).ConfigureAwait(true);
        log.Append(new RequestLogEntry(
            time.GetUtcNow(), "codexAutoStart", "RUN", "codex exec", result.ExitCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            "Codex CLI", result.Outcome switch
            {
                CodexExecOutcome.Completed => null,
                CodexExecOutcome.NotFound => "notFound",
                // The error code is metadata, never the path or Windows' text.
                CodexExecOutcome.StartFailed => $"startFailed:{result.StartError!.Code}",
                CodexExecOutcome.TimedOut => "timeout",
                _ => "failed",
            }));
        return result.Outcome switch
        {
            CodexExecOutcome.Completed => new AutoStartResult(true, null, null),
            CodexExecOutcome.NotFound => new AutoStartResult(false, null, Strings.Get("windows.autostart.codex_not_found")),
            CodexExecOutcome.StartFailed => new AutoStartResult(false, null, Strings.Format(
                "windows.autostart.codex_start_failed", result.StartError!.Path, result.StartError.Code, result.StartError.Message)),
            CodexExecOutcome.TimedOut => new AutoStartResult(false, null, Strings.Get("windows.autostart.codex_timed_out")),
            _ when result.ExitCode is 0 => new AutoStartResult(false, null, Strings.Get("windows.autostart.codex_no_reply")),
            _ => new AutoStartResult(false, null, Strings.Format("windows.autostart.codex_failed", result.ExitCode ?? -1)),
        };
    }
}
