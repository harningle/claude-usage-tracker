using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.Tests.Hud;

/// <summary>Port of upstream <c>ToolActivityMapperTests</c>.</summary>
public class ToolActivityMapperTests
{
    [Fact]
    public void BashMapsToRunningCommandWithTruncatedCommand()
    {
        var (status, task) = ToolActivityMapper.Map("Bash", new JsonObject { ["command"] = new string('x', 100) });

        Assert.Equal(SessionStatus.RunningCommand, status);
        Assert.Null(task.LocalisationKey);
        Assert.Equal(60, task.Argument!.Length);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    public void EditorToolsMapToWritingCode(string tool)
    {
        var (status, task) = ToolActivityMapper.Map(tool, new JsonObject { ["file_path"] = "/a/b/File.swift" });

        Assert.Equal(SessionStatus.WritingCode, status);
        Assert.Equal("File.swift", task.Argument);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Glob")]
    [InlineData("Grep")]
    public void ReadToolsMapToReadingFiles(string tool) =>
        Assert.Equal(SessionStatus.ReadingFiles, ToolActivityMapper.Map(tool, null).Status);

    [Fact]
    public void UnknownToolFallsBackToThinkingWithToolName()
    {
        var (status, task) = ToolActivityMapper.Map("SomeMCPTool", null);

        Assert.Equal(SessionStatus.Thinking, status);
        Assert.Equal(HudTaskText.Literal("SomeMCPTool"), task);
    }
}

/// <summary>Port of upstream <c>NotchHookEventTests</c>.</summary>
public class HookEventTests
{
    [Fact]
    public void EventRequiresSessionId()
    {
        Assert.Null(HookEvent.From("stop", new JsonObject()));
        Assert.Null(HookEvent.From("stop", new JsonObject { ["session_id"] = "" }));
    }

    [Fact]
    public void UnknownPathReturnsNil() =>
        Assert.Null(HookEvent.From("permission-request", new JsonObject { ["session_id"] = "s" }));

    [Fact]
    public void KnownEventsDecode()
    {
        Assert.Equal(
            new HookEvent.SessionStart("s", "/p"),
            HookEvent.From("session-start", new JsonObject { ["session_id"] = "s", ["cwd"] = "/p" }));
        Assert.Equal(new HookEvent.Stop("s"), HookEvent.From("stop", new JsonObject { ["session_id"] = "s" }));
        Assert.Equal(
            new HookEvent.Notification("s", "waiting"),
            HookEvent.From("notification", new JsonObject { ["session_id"] = "s", ["message"] = "waiting" }));
    }
}

/// <summary>Port of upstream <c>NotchSessionStoreTests</c>.</summary>
public class HudSessionStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly HudSessionStore _store;

    public HudSessionStoreTests() => _store = new HudSessionStore(_time);

    [Fact]
    public void OutOfOrderPreToolUseAutoCreatesSession()
    {
        _store.Apply(new HookEvent.PreToolUse("s1", "/proj", SessionStatus.RunningCommand, HudTaskText.Literal("git status")));

        var session = Assert.Single(_store.Sessions);
        Assert.Equal(SessionStatus.RunningCommand, session.Status);
        Assert.Equal("proj", session.DisplayName);
    }

    [Fact]
    public void NotificationSetsAttentionAndNextActivityClearsIt()
    {
        _store.Apply(new HookEvent.SessionStart("s1", "/p"));
        _store.Apply(new HookEvent.Notification("s1", "Claude needs your permission"));
        Assert.Equal(SessionStatus.NeedsAttention, _store.Sessions[0].Status);

        _store.Apply(new HookEvent.PreToolUse("s1", null, SessionStatus.RunningCommand, HudTaskText.Literal("ls")));
        Assert.Equal(SessionStatus.RunningCommand, _store.Sessions[0].Status);
    }

    [Fact]
    public void StopSetsIdleAndSessionEndRemoves()
    {
        _store.Apply(new HookEvent.SessionStart("s1", null));
        _store.Apply(new HookEvent.Stop("s1"));
        Assert.Equal(SessionStatus.Idle, _store.Sessions[0].Status);

        _store.Apply(new HookEvent.SessionEnd("s1"));
        Assert.Empty(_store.Sessions);
    }

    [Fact]
    public void ToolFailureSetsErrorFlagAndPostToolUseClearsIt()
    {
        _store.Apply(new HookEvent.SessionStart("s1", null));
        _store.Apply(new HookEvent.ToolFailure("s1"));
        Assert.True(_store.Sessions[0].HasRecentError);

        _store.Apply(new HookEvent.PostToolUse("s1"));
        Assert.False(_store.Sessions[0].HasRecentError);
    }

    [Fact]
    public void PrimarySessionPrefersAttention()
    {
        _store.Apply(new HookEvent.PreToolUse("busy", null, SessionStatus.RunningCommand, HudTaskText.Literal("build")));
        _store.Apply(new HookEvent.SessionStart("waiting", null));
        _store.Apply(new HookEvent.Notification("waiting", "input needed"));

        Assert.Equal("waiting", _store.PrimarySession?.Id);
    }

    [Fact]
    public void StaleSweepRemovesOldSessionsWithAttentionGrace()
    {
        _store.Apply(new HookEvent.SessionStart("old", null));
        _store.Apply(new HookEvent.SessionStart("attention", null));
        _store.Apply(new HookEvent.Notification("attention", "waiting"));

        // 3 minutes later: the normal session is swept (120 s); attention survives (600 s grace).
        _time.Now += TimeSpan.FromSeconds(180);
        _store.SweepStaleSessions();
        Assert.Equal(["attention"], _store.Sessions.Select(s => s.Id));

        // 11 minutes after that: attention is swept too.
        _time.Now += TimeSpan.FromMinutes(11);
        _store.SweepStaleSessions();
        Assert.Empty(_store.Sessions);
    }

    [Fact]
    public void PromptTruncatedTo80()
    {
        _store.Apply(new HookEvent.UserPromptSubmit("s1", new string('p', 200)));

        Assert.Equal(80, _store.Sessions[0].LastUserPrompt?.Length);
    }
}
