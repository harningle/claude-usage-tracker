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

    /// <summary>The project folder name, or a short session label without a project path.</summary>
    public string DisplayName =>
        string.IsNullOrEmpty(ProjectPath)
            ? $"Session {TextElements.Prefix(Id, 5)}"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(ProjectPath));
}

/// <summary>
/// Reducer over <see cref="HookEvent"/>s. Port of upstream <c>NotchSessionStore</c> without its timer
/// and server status. Not thread-safe: the caller applies events on one thread.
/// </summary>
public sealed class HudSessionStore(TimeProvider timeProvider)
{
    /// <summary>A session with no events for this long is removed.</summary>
    public static readonly TimeSpan StaleSessionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>A session waiting for the user gets a longer grace, so the cue survives while they are away.</summary>
    public static readonly TimeSpan AttentionStaleTimeout = TimeSpan.FromSeconds(600);

    private const int MaxTextLength = 80;

    private readonly List<ClaudeCodeSession> _sessions = [];

    /// <summary>Sessions in start order.</summary>
    public IReadOnlyList<ClaudeCodeSession> Sessions => _sessions;

    /// <summary>The most urgent session, the latest updated on a tie.</summary>
    public ClaudeCodeSession? PrimarySession => _sessions.MaxBy(s => (s.Status.Priority(), s.LastUpdate));

    public void Apply(HookEvent hookEvent)
    {
        switch (hookEvent)
        {
            case HookEvent.SessionStart e:
                Upsert(e.SessionId, e.Cwd, session =>
                {
                    session.Status = SessionStatus.Thinking;
                    session.CurrentTask = null;
                });
                break;
            case HookEvent.SessionEnd e:
                _sessions.RemoveAll(s => s.Id == e.SessionId);
                break;
            case HookEvent.UserPromptSubmit e:
                Upsert(e.SessionId, null, session =>
                {
                    session.Status = SessionStatus.Thinking;
                    session.CurrentTask = null;
                    if (!string.IsNullOrEmpty(e.Prompt))
                    {
                        session.LastUserPrompt = TextElements.Prefix(e.Prompt, MaxTextLength);
                    }
                });
                break;
            case HookEvent.PreToolUse e:
                Upsert(e.SessionId, e.Cwd, session =>
                {
                    session.Status = e.Status;
                    session.CurrentTask = e.Task;
                });
                break;
            case HookEvent.PostToolUse e:
                Upsert(e.SessionId, null, session =>
                {
                    session.Status = SessionStatus.Thinking;
                    session.HasRecentError = false;
                });
                break;
            case HookEvent.ToolFailure e:
                Upsert(e.SessionId, null, session => session.HasRecentError = true);
                break;
            case HookEvent.Stop e:
                Upsert(e.SessionId, null, session =>
                {
                    session.Status = SessionStatus.Idle;
                    session.CurrentTask = null;
                });
                break;
            case HookEvent.Notification e:
                Upsert(e.SessionId, null, session =>
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
    }

    public void Reset() => _sessions.Clear();

    /// <summary>
    /// Updates a session, creating it for out-of-order events (tracker started mid-session or
    /// SessionStart lost). Any status-setting transition clears <see cref="SessionStatus.NeedsAttention"/>.
    /// </summary>
    private void Upsert(string id, string? cwd, Action<ClaudeCodeSession> mutate)
    {
        var now = timeProvider.GetUtcNow();
        var session = _sessions.Find(s => s.Id == id);
        if (session is null)
        {
            session = new ClaudeCodeSession
            {
                Id = id,
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

        mutate(session);
        session.LastUpdate = now;
    }
}
