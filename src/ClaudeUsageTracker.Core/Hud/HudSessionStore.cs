using ClaudeUsageTracker.Core.Text;

namespace ClaudeUsageTracker.Core.Hud;

/// <summary>A live Claude Code session observed through hook events. Port of upstream <c>ClaudeCodeSession</c>.</summary>
public sealed class ClaudeCodeSession
{
    /// <summary>Claude Code's own <c>session_id</c>.</summary>
    public required string Id { get; init; }

    public SessionStatus Status { get; set; }
    public HudTaskText? CurrentTask { get; set; }
    public string? ProjectPath { get; set; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset LastUpdate { get; set; }
    public string? LastUserPrompt { get; set; }

    /// <summary>Set when the latest tool use failed; cleared by the next successful one.</summary>
    public bool HasRecentError { get; set; }

    /// <summary>The Claude Code folder the session uses, when its events showed it.</summary>
    public string? ConfigDirectory { get; set; }

    /// <summary>The prompt being answered, when Claude Code names it.</summary>
    internal string? PromptId { get; set; }

    /// <summary>Tools that started and have not completed, oldest first.</summary>
    internal List<(string Id, SessionStatus Status, HudTaskText Task)> OpenTools { get; } = [];

    /// <summary>The latest completed tools, so a start that arrives after its completion is recognised.</summary>
    internal Queue<string> CompletedTools { get; } = new();

    /// <summary>The project folder name, or a short session label without a project path.</summary>
    public string DisplayName =>
        string.IsNullOrEmpty(ProjectPath)
            ? $"Session {TextElements.Prefix(Id, 5)}"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(ProjectPath));
}

/// <summary>
/// Reducer over <see cref="HookEvent"/>s. Port of upstream <c>NotchSessionStore</c> without its timer
/// and server status, with stricter ordering (plan.md section 2): a tool's start and completion are tied by
/// <c>tool_use_id</c>, so a start arriving late never reopens a completed tool; a Stop for an earlier prompt
/// does not idle a later one; and an ended session comes back only on a new session start or prompt. Events
/// without those identifiers behave as upstream. Not thread-safe: the caller applies events on one thread.
/// </summary>
public sealed class HudSessionStore(TimeProvider timeProvider)
{
    /// <summary>A session with no events for this long is removed.</summary>
    public static readonly TimeSpan StaleSessionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>A session waiting for the user gets a longer grace, so the cue survives while they are away.</summary>
    public static readonly TimeSpan AttentionStaleTimeout = TimeSpan.FromSeconds(600);

    private const int MaxTextLength = 80;
    private const int MaxCompletedTools = 64;
    private const int MaxEndedSessions = 256;

    private readonly List<ClaudeCodeSession> _sessions = [];

    // Sessions that ended, with the time: their late events must not bring them back.
    private readonly Dictionary<string, DateTimeOffset> _ended = [];

    /// <summary>Sessions in start order.</summary>
    public IReadOnlyList<ClaudeCodeSession> Sessions => _sessions;

    /// <summary>The most urgent session, the latest updated on a tie.</summary>
    public ClaudeCodeSession? PrimarySession => _sessions.MaxBy(s => (s.Status.Priority(), s.LastUpdate));

    public void Apply(HookEvent hookEvent)
    {
        if (_ended.ContainsKey(hookEvent.SessionId))
        {
            if (hookEvent is not (HookEvent.SessionStart or HookEvent.UserPromptSubmit))
            {
                // A late event of a session that ended: only new-session activity revives it.
                return;
            }

            _ended.Remove(hookEvent.SessionId);
        }

        switch (hookEvent)
        {
            case HookEvent.SessionStart e:
                Upsert(e, e.Cwd, session =>
                {
                    session.Status = SessionStatus.Thinking;
                    session.CurrentTask = null;
                    session.OpenTools.Clear();
                });
                break;
            case HookEvent.SessionEnd e:
                _sessions.RemoveAll(s => s.Id == e.SessionId);
                RememberEnded(e.SessionId);
                break;
            case HookEvent.UserPromptSubmit e:
                Upsert(e, null, session =>
                {
                    session.Status = SessionStatus.Thinking;
                    session.CurrentTask = null;
                    session.OpenTools.Clear();
                    session.PromptId = e.PromptId ?? session.PromptId;
                    if (!string.IsNullOrEmpty(e.Prompt))
                    {
                        session.LastUserPrompt = TextElements.Prefix(e.Prompt, MaxTextLength);
                    }
                });
                break;
            case HookEvent.PreToolUse e:
                Upsert(e, e.Cwd, session =>
                {
                    if (e.ToolUseId is { } id)
                    {
                        if (session.CompletedTools.Contains(id) || session.OpenTools.Exists(t => t.Id == id))
                        {
                            // The tool already completed, or this start is a duplicate.
                            return;
                        }

                        session.OpenTools.Add((id, e.Status, e.Task));
                    }

                    session.Status = e.Status;
                    session.CurrentTask = e.Task;
                });
                break;
            case HookEvent.PostToolUse e:
                Upsert(e, null, session =>
                {
                    Complete(session, e.ToolUseId);
                    session.HasRecentError = false;
                });
                break;
            case HookEvent.ToolFailure e:
                Upsert(e, null, session =>
                {
                    if (e.ToolUseId is not null)
                    {
                        Complete(session, e.ToolUseId);
                    }

                    session.HasRecentError = true;
                });
                break;
            case HookEvent.Stop e:
                Upsert(e, null, session =>
                {
                    if (e.PromptId is not null && session.PromptId is not null && e.PromptId != session.PromptId)
                    {
                        // The answer to an earlier prompt finished; a later prompt is already being worked on.
                        return;
                    }

                    session.Status = SessionStatus.Idle;
                    session.CurrentTask = null;
                    session.OpenTools.Clear();
                });
                break;
            case HookEvent.Notification e:
                Upsert(e, null, session =>
                {
                    session.Status = SessionStatus.NeedsAttention;
                    if (!string.IsNullOrEmpty(e.Message))
                    {
                        session.CurrentTask = HudTaskText.Literal(TextElements.Prefix(e.Message, MaxTextLength));
                    }
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(hookEvent), hookEvent.GetType().Name, "Unknown hook event.");
        }
    }

    public void SweepStaleSessions()
    {
        var now = timeProvider.GetUtcNow();
        _sessions.RemoveAll(session =>
        {
            var timeout = session.Status == SessionStatus.NeedsAttention
                ? AttentionStaleTimeout
                : StaleSessionTimeout;
            return now - session.LastUpdate > timeout;
        });
        foreach (var id in _ended.Where(e => now - e.Value > AttentionStaleTimeout).Select(e => e.Key).ToList())
        {
            _ended.Remove(id);
        }
    }

    public void Reset()
    {
        _sessions.Clear();
        _ended.Clear();
    }

    /// <summary>
    /// A tool finished. With its ID, the session shows the newest tool still running, or thinking when none is;
    /// without one, it is thinking, as upstream.
    /// </summary>
    private static void Complete(ClaudeCodeSession session, string? toolUseId)
    {
        if (toolUseId is not null)
        {
            session.OpenTools.RemoveAll(t => t.Id == toolUseId);
            if (!session.CompletedTools.Contains(toolUseId))
            {
                session.CompletedTools.Enqueue(toolUseId);
                if (session.CompletedTools.Count > MaxCompletedTools)
                {
                    session.CompletedTools.Dequeue();
                }
            }

            if (session.OpenTools.Count > 0)
            {
                (_, session.Status, session.CurrentTask) = session.OpenTools[^1];
                return;
            }
        }

        session.Status = SessionStatus.Thinking;
    }

    private void RememberEnded(string id)
    {
        _ended[id] = timeProvider.GetUtcNow();
        if (_ended.Count > MaxEndedSessions)
        {
            _ended.Remove(_ended.MinBy(e => e.Value).Key);
        }
    }

    /// <summary>
    /// Updates a session, creating it for out-of-order events (tracker started mid-session or
    /// SessionStart lost). Any status-setting transition clears <see cref="SessionStatus.NeedsAttention"/>.
    /// </summary>
    private void Upsert(HookEvent hookEvent, string? cwd, Action<ClaudeCodeSession> mutate)
    {
        var now = timeProvider.GetUtcNow();
        var session = _sessions.Find(s => s.Id == hookEvent.SessionId);
        if (session is null)
        {
            session = new ClaudeCodeSession
            {
                Id = hookEvent.SessionId,
                Status = SessionStatus.Thinking,
                ProjectPath = cwd,
                StartTime = now,
            };
            _sessions.Add(session);
        }
        else if (!string.IsNullOrEmpty(cwd))
        {
            session.ProjectPath = cwd;
        }

        // Upstream names a session from SessionStart or a tool start. Without either yet, any event names it.
        if (string.IsNullOrEmpty(session.ProjectPath) && !string.IsNullOrEmpty(hookEvent.WorkingDirectory))
        {
            session.ProjectPath = hookEvent.WorkingDirectory;
        }

        session.ConfigDirectory = hookEvent.ConfigDirectory ?? session.ConfigDirectory;
        mutate(session);
        session.LastUpdate = now;
    }
}
