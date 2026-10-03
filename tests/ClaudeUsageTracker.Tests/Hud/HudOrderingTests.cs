using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.Tests.Hud;

/// <summary>
/// The ordering rules the port adds to upstream's reducer (plan.md section 2): completion before start,
/// duplicates, a late Stop or SessionEnd, missing events and a session whose process is gone.
/// </summary>
public class HudOrderingTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly HudSessionStore _store;

    public HudOrderingTests() => _store = new HudSessionStore(_time);

    private static HookEvent.PreToolUse Pre(string tool, SessionStatus status = SessionStatus.RunningCommand, string task = "build") =>
        new("s1", null, status, HudTaskText.Literal(task)) { ToolUseId = tool };

    private static HookEvent.PostToolUse Post(string tool) => new("s1") { ToolUseId = tool };

    private ClaudeCodeSession Session => Assert.Single(_store.Sessions);

    [Fact]
    public void ALateStartNeverReopensACompletedTool()
    {
        _store.Apply(new HookEvent.UserPromptSubmit("s1", "go"));
        _store.Apply(Post("t1"));
        _store.Apply(Pre("t1"));

        Assert.Equal(SessionStatus.Thinking, Session.Status);
    }

    [Fact]
    public void AFailedToolIsCompletedToo()
    {
        _store.Apply(Pre("t1"));
        _store.Apply(new HookEvent.ToolFailure("s1") { ToolUseId = "t1" });
        Assert.Equal(SessionStatus.Thinking, Session.Status);
        Assert.True(Session.HasRecentError);

        _store.Apply(Pre("t1"));
        Assert.Equal(SessionStatus.Thinking, Session.Status);
    }

    [Fact]
    public void DuplicateEventsChangeNothing()
    {
        _store.Apply(Pre("t1", SessionStatus.ReadingFiles, "first"));
        _store.Apply(Pre("t2", SessionStatus.RunningCommand, "second"));
        _store.Apply(Pre("t1", SessionStatus.WritingCode, "duplicate"));
        Assert.Equal(SessionStatus.RunningCommand, Session.Status);
        Assert.Equal(HudTaskText.Literal("second"), Session.CurrentTask);

        _store.Apply(Post("t2"));
        _store.Apply(Post("t2"));
        // The first tool is still running, and is shown again.
        Assert.Equal(SessionStatus.ReadingFiles, Session.Status);
        Assert.Equal(HudTaskText.Literal("first"), Session.CurrentTask);

        _store.Apply(Post("t1"));
        Assert.Equal(SessionStatus.Thinking, Session.Status);
    }

    [Fact]
    public void ParallelToolsCompletingOutOfOrderEndInThinking()
    {
        _store.Apply(Pre("t1"));
        _store.Apply(Pre("t2"));
        _store.Apply(Post("t1"));
        Assert.Equal(SessionStatus.RunningCommand, Session.Status);

        _store.Apply(Post("t2"));
        Assert.Equal(SessionStatus.Thinking, Session.Status);
    }

    [Fact]
    public void AnEndedSessionIsNotRevivedByItsLateEvents()
    {
        _store.Apply(new HookEvent.UserPromptSubmit("s1", "go"));
        _store.Apply(new HookEvent.SessionEnd("s1"));

        _store.Apply(new HookEvent.Stop("s1"));
        _store.Apply(Post("t1"));
        _store.Apply(Pre("t2"));
        _store.Apply(new HookEvent.Notification("s1", "waiting"));

        Assert.Empty(_store.Sessions);
    }

    [Fact]
    public void AnEndedSessionComesBackOnNewSessionActivity()
    {
        _store.Apply(new HookEvent.SessionEnd("s1"));
        _store.Apply(new HookEvent.UserPromptSubmit("s1", "resumed"));
        Assert.Equal(SessionStatus.Thinking, Session.Status);

        _store.Apply(new HookEvent.SessionEnd("s1"));
        _store.Apply(new HookEvent.SessionStart("s1", "/p"));
        _store.Apply(Pre("t1"));
        Assert.Equal(SessionStatus.RunningCommand, Session.Status);
    }

    [Fact]
    public void ALateSessionEndStillRemovesTheSession()
    {
        _store.Apply(new HookEvent.UserPromptSubmit("s1", "go"));
        _store.Apply(new HookEvent.Stop("s1"));
        _time.Now += TimeSpan.FromSeconds(90);

        _store.Apply(new HookEvent.SessionEnd("s1"));

        Assert.Empty(_store.Sessions);
    }

    [Fact]
    public void AStopForAnEarlierPromptDoesNotIdleALaterOne()
    {
        _store.Apply(new HookEvent.UserPromptSubmit("s1", "first") { PromptId = "p1" });
        _store.Apply(new HookEvent.UserPromptSubmit("s1", "second") { PromptId = "p2" });
        _store.Apply(Pre("t1"));

        _store.Apply(new HookEvent.Stop("s1") { PromptId = "p1" });
        Assert.Equal(SessionStatus.RunningCommand, Session.Status);

        _store.Apply(new HookEvent.Stop("s1") { PromptId = "p2" });
        Assert.Equal(SessionStatus.Idle, Session.Status);
    }

    [Fact]
    public void AMissingCompletionIsClearedByTheNextStopOrPrompt()
    {
        _store.Apply(Pre("lost"));
        _store.Apply(new HookEvent.Stop("s1"));
        Assert.Equal(SessionStatus.Idle, Session.Status);

        // The tool whose completion never arrived must not come back when another one completes.
        _store.Apply(new HookEvent.UserPromptSubmit("s1", "next"));
        _store.Apply(Pre("t2", SessionStatus.ReadingFiles));
        _store.Apply(Post("t2"));
        Assert.Equal(SessionStatus.Thinking, Session.Status);
    }

    [Fact]
    public void ASessionWhoseProcessIsGoneIsSweptAndItsLateEventsStayOut()
    {
        _store.Apply(Pre("t1"));
        _time.Now += HudSessionStore.StaleSessionTimeout + TimeSpan.FromSeconds(1);
        _store.SweepStaleSessions();
        Assert.Empty(_store.Sessions);

        // Without a SessionEnd nothing says the session ended, so real activity shows it again, as upstream.
        _store.Apply(Pre("t3"));
        Assert.Equal(SessionStatus.RunningCommand, Session.Status);
    }

    [Fact]
    public void EndedSessionsAreForgottenAfterTheLongestGrace()
    {
        _store.Apply(new HookEvent.SessionEnd("s1"));
        _time.Now += HudSessionStore.AttentionStaleTimeout + TimeSpan.FromSeconds(1);
        _store.SweepStaleSessions();

        _store.Apply(Pre("t1"));

        Assert.Equal(SessionStatus.RunningCommand, Session.Status);
    }

    [Fact]
    public void EventsWithoutIdentifiersBehaveAsUpstream()
    {
        _store.Apply(new HookEvent.PreToolUse("s1", null, SessionStatus.RunningCommand, HudTaskText.Literal("a")));
        _store.Apply(new HookEvent.PostToolUse("s1"));
        _store.Apply(new HookEvent.PreToolUse("s1", null, SessionStatus.ReadingFiles, HudTaskText.Literal("b")));

        Assert.Equal(SessionStatus.ReadingFiles, Session.Status);
    }

    [Fact]
    public void PayloadsCarryTheIdentifiersAndTheSessionsFolder()
    {
        var payload = new JsonObject
        {
            ["session_id"] = "s1",
            ["transcript_path"] = @"C:\Users\someone\.claude-work\projects\C--work-app\0f3c.jsonl",
            ["cwd"] = @"C:\work\app",
            ["tool_name"] = "Read",
            ["tool_input"] = new JsonObject { ["file_path"] = @"C:\work\app\a.cs" },
            ["tool_use_id"] = "toolu_1",
            ["prompt_id"] = "p1",
        };

        var pre = Assert.IsType<HookEvent.PreToolUse>(HookEvent.From("pre-tool-use", payload));
        Assert.Equal("toolu_1", pre.ToolUseId);
        Assert.Equal(@"C:\Users\someone\.claude-work", pre.ConfigDirectory);
        Assert.Equal("toolu_1", Assert.IsType<HookEvent.PostToolUse>(HookEvent.From("post-tool-use", payload)).ToolUseId);
        Assert.Equal("toolu_1", Assert.IsType<HookEvent.ToolFailure>(HookEvent.From("post-tool-use-failure", payload)).ToolUseId);
        Assert.Equal("p1", Assert.IsType<HookEvent.Stop>(HookEvent.From("stop", payload)).PromptId);
        Assert.Equal("p1", Assert.IsType<HookEvent.UserPromptSubmit>(HookEvent.From("user-prompt-submit", payload)).PromptId);

        _store.Apply(pre);
        Assert.Equal(@"C:\Users\someone\.claude-work", Session.ConfigDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\somewhere\else\0f3c.jsonl")]
    [InlineData("0f3c.jsonl")]
    public void ATranscriptPathOfAnotherShapeNamesNoFolder(string? path) =>
        Assert.Null(HookEvent.ConfigDirectoryOf(path));
}
