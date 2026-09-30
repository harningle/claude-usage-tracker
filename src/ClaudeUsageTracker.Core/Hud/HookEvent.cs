using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;
using ClaudeUsageTracker.Core.Text;

namespace ClaudeUsageTracker.Core.Hud;

/// <summary>The observed state of a Claude Code session. The HUD only displays it; it never answers.</summary>
public enum SessionStatus
{
    Thinking,
    WritingCode,
    ReadingFiles,
    RunningCommand,

    /// <summary>Claude Code is waiting for the user in the terminal.</summary>
    NeedsAttention,
    Idle,
}

public static class SessionStatusExtensions
{
    /// <summary>Priority for choosing the session shown in the compact HUD (higher is more urgent).</summary>
    public static int Priority(this SessionStatus status) => status switch
    {
        SessionStatus.NeedsAttention => 100,
        SessionStatus.RunningCommand => 80,
        SessionStatus.WritingCode => 70,
        SessionStatus.ReadingFiles => 60,
        SessionStatus.Thinking => 50,
        SessionStatus.Idle => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown session status."),
    };
}

/// <summary>
/// Task text for display. With a <see cref="LocalisationKey"/>, the UI formats that upstream string
/// resource with <see cref="Argument"/>; without one, <see cref="Argument"/> is literal text.
/// </summary>
public sealed record HudTaskText(string? LocalisationKey, string? Argument)
{
    public static HudTaskText Literal(string text) => new(null, text);
}

/// <summary>
/// A passive observation decoded from a Claude Code hook payload. Port of upstream
/// <c>NotchHookEvent</c>; events only ever update local display state.
/// </summary>
public abstract record HookEvent(string SessionId)
{
    /// <summary>The event identifiers, as upstream's hook URL path suffixes.</summary>
    public static readonly IReadOnlyList<string> PathSuffixes =
    [
        "session-start", "session-end", "user-prompt-submit", "pre-tool-use",
        "post-tool-use", "post-tool-use-failure", "stop", "notification",
    ];

    public sealed record SessionStart(string SessionId, string? Cwd) : HookEvent(SessionId);

    public sealed record SessionEnd(string SessionId) : HookEvent(SessionId);

    public sealed record UserPromptSubmit(string SessionId, string? Prompt) : HookEvent(SessionId);

    public sealed record PreToolUse(string SessionId, string? Cwd, SessionStatus Status, HudTaskText Task)
        : HookEvent(SessionId);

    public sealed record PostToolUse(string SessionId) : HookEvent(SessionId);

    public sealed record ToolFailure(string SessionId) : HookEvent(SessionId);

    public sealed record Stop(string SessionId) : HookEvent(SessionId);

    public sealed record Notification(string SessionId, string? Message) : HookEvent(SessionId);

    /// <summary>Decodes an event; null for an unknown event or a payload without a session ID.</summary>
    public static HookEvent? From(string pathSuffix, JsonObject payload)
    {
        if (JsonRead.NonEmptyString(payload, "session_id") is not { } sessionId)
        {
            return null;
        }

        var cwd = JsonRead.String(payload, "cwd");
        switch (pathSuffix)
        {
            case "session-start":
                return new SessionStart(sessionId, cwd);
            case "session-end":
                return new SessionEnd(sessionId);
            case "user-prompt-submit":
                return new UserPromptSubmit(sessionId, JsonRead.String(payload, "prompt"));
            case "pre-tool-use":
                var (status, task) = ToolActivityMapper.Map(
                    JsonRead.String(payload, "tool_name") ?? "", payload["tool_input"] as JsonObject);
                return new PreToolUse(sessionId, cwd, status, task);
            case "post-tool-use":
                return new PostToolUse(sessionId);
            case "post-tool-use-failure":
                return new ToolFailure(sessionId);
            case "stop":
                return new Stop(sessionId);
            case "notification":
                return new Notification(sessionId, JsonRead.String(payload, "message"));
            default:
                return null;
        }
    }
}

/// <summary>Maps a Claude Code tool invocation to a HUD status and short task. Port of upstream <c>ToolActivityMapper</c>.</summary>
public static class ToolActivityMapper
{
    private const int MaxCommandLength = 60;

    public static (SessionStatus Status, HudTaskText Task) Map(string toolName, JsonObject? toolInput)
    {
        switch (toolName)
        {
            case "Bash":
                var command = toolInput is null ? "" : JsonRead.String(toolInput, "command") ?? "";
                return (SessionStatus.RunningCommand, command.Length == 0
                    ? new HudTaskText("notch.task.running_command", null)
                    : HudTaskText.Literal(TextElements.Prefix(command, MaxCommandLength)));
            case "Write":
                return (SessionStatus.WritingCode, new HudTaskText("notch.task.writing", FileName(toolInput)));
            case "Edit" or "MultiEdit" or "NotebookEdit":
                return (SessionStatus.WritingCode, new HudTaskText("notch.task.editing", FileName(toolInput)));
            case "Read":
                return (SessionStatus.ReadingFiles, new HudTaskText("notch.task.reading", FileName(toolInput)));
            case "Glob" or "Grep":
                return (SessionStatus.ReadingFiles, new HudTaskText("notch.task.searching", null));
            case "Task" or "Agent":
                return (SessionStatus.Thinking, new HudTaskText("notch.task.subagent", null));
            case "WebFetch" or "WebSearch":
                return (SessionStatus.ReadingFiles, new HudTaskText("notch.task.web", null));
            default:
                return (SessionStatus.Thinking, HudTaskText.Literal(toolName));
        }
    }

    private static string FileName(JsonObject? toolInput) =>
        Path.GetFileName(toolInput is null ? "" : JsonRead.String(toolInput, "file_path") ?? "");
}
