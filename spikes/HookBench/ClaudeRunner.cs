using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HookBench;

internal sealed record ClaudeRun(int ExitCode, bool TimedOut, long StartedTicks, long ExitedTicks, string? ResultSubtype,
    bool? IsError, int? NumTurns, string StderrTail);

internal static class ClaudeRunner
{
    /// <summary>The eight baseline events and upstream's URL suffixes (NotchHookInstaller.swift).</summary>
    public static readonly (string Event, string Suffix)[] Events =
    [
        ("SessionStart", "session-start"),
        ("SessionEnd", "session-end"),
        ("UserPromptSubmit", "user-prompt-submit"),
        ("PreToolUse", "pre-tool-use"),
        ("PostToolUse", "post-tool-use"),
        ("PostToolUseFailure", "post-tool-use-failure"),
        ("Stop", "stop"),
        ("Notification", "notification"),
    ];

    public static string EventForSuffix(string suffix) =>
        Events.FirstOrDefault(e => e.Suffix == suffix).Event ?? suffix;

    /// <summary>Upstream's HTTP hook entries: matcher "", type http, timeout 3.</summary>
    public static JsonObject HttpHooks(int port, string secret) => Hooks(suffix => new JsonObject
    {
        ["type"] = "http",
        ["url"] = $"http://127.0.0.1:{port}/hook/{secret}/{suffix}",
        ["timeout"] = 3,
    });

    /// <summary>The plan's alternative: direct-exec async command hooks forwarding over a pipe.</summary>
    public static JsonObject AsyncCommandHooks(string helper, string pipe) => Hooks(_ => new JsonObject
    {
        ["type"] = "command",
        ["command"] = helper,
        ["args"] = new JsonArray("hook-pipe", pipe),
        ["async"] = true,
    });

    public static JsonObject NoHooks() => new();

    private static JsonObject Hooks(Func<string, JsonObject> handler)
    {
        var hooks = new JsonObject();
        foreach (var (name, suffix) in Events)
        {
            hooks[name] = new JsonArray(new JsonObject { ["matcher"] = "", ["hooks"] = new JsonArray(handler(suffix)) });
        }

        return new JsonObject { ["hooks"] = hooks };
    }

    /// <summary>Atomically replaces a settings file, as the production installer will.</summary>
    public static void WriteSettings(string path, JsonObject settings)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, settings.ToJsonString(Json.Indented));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Runs <c>claude -p</c> with an isolated config directory, a dummy API key and the fake API as
    /// base URL: no account credentials are loaded and no usage is consumed. Inherited CLAUDE* and
    /// ANTHROPIC* variables are removed so the parent session cannot leak into the child.
    /// </summary>
    public static async Task<ClaudeRun> RunAsync(string claude, string workDirectory, string configDirectory,
        int apiPort, IReadOnlyList<string> extraArguments, string? debugFile = null)
    {
        var start = new ProcessStartInfo(claude)
        {
            WorkingDirectory = workDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-p", "Read the file you are asked to read, then answer.", "--model", "claude-haiku-4-5",
                     "--output-format", "json", "--no-session-persistence", "--strict-mcp-config",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in extraArguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (debugFile is not null)
        {
            start.ArgumentList.Add("--debug-file");
            start.ArgumentList.Add(debugFile);
        }

        foreach (var key in start.Environment.Keys.ToList())
        {
            if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(key);
            }
        }

        start.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        start.Environment["ANTHROPIC_BASE_URL"] = $"http://127.0.0.1:{apiPort}";
        start.Environment["ANTHROPIC_API_KEY"] = "bench-not-a-real-key";
        start.Environment["DISABLE_AUTOUPDATER"] = "1";
        start.Environment["DISABLE_TELEMETRY"] = "1";
        start.Environment["DISABLE_ERROR_REPORTING"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        var started = Clock.Now;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var exited = Clock.Now;
        string? subtype = null;
        bool? isError = null;
        int? turns = null;
        try
        {
            if (JsonNode.Parse(await stdout) is JsonObject result)
            {
                subtype = (string?)result["subtype"];
                isError = (bool?)result["is_error"];
                turns = (int?)result["num_turns"];
            }
        }
        catch (JsonException)
        {
        }

        var errorText = await stderr;
        return new ClaudeRun(process.ExitCode, timedOut, started, exited, subtype, isError, turns,
            errorText.Length > 2000 ? errorText[^2000..] : errorText);
    }
}
