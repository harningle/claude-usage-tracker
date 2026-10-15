using System.IO;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Notifications;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;
using TestTime = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace ClaudeUsageTracker.Tests.Notifications;

/// <summary>
/// Opt-in session auto-start (upstream <c>AutoStartSessionService</c>), the only model request the app sends: only for
/// opted-in claude.ai profiles, only on fresh usage showing an unstarted window, once per window, with a wait after
/// a failure. The claude.ai answers are scripted; nothing reaches a real account.
/// </summary>
public sealed class AutoStartServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServerConversation = Guid.Parse("6f2b1a7e-3c4d-4e5f-8a9b-0c1d2e3f4a5b");
    private static readonly DateTimeOffset NewWindowReset = Now.AddHours(5).AddMinutes(-1);

    private readonly TempDirectory _temp = new();
    private readonly TestTime _time = new(Now);
    private readonly RecordingNotifier _notifier = new();
    private readonly AppSettings _settings = new();
    private readonly ProfileSettings _profile;
    private readonly ScriptedTransport _transport;
    private readonly List<Guid> _released = [];
    private string _usage = """{"five_hour":{"utilization":0,"resets_at":null},"seven_day":{"utilization":12}}""";
    private int _completionStatus = 200;
    private ProfileUsageState? _current;
    private Action? _duringUsage;
    private BrowserResponse? _usageResponse;

    public AutoStartServiceTests()
    {
        _profile = new ProfileSettings
        {
            Name = "Personal",
            AutoStartSessionEnabled = true,
            ClaudeAi = new ClaudeAiLink { OrganizationId = "org-1" },
        };
        _settings.Profiles.Add(_profile);
        _transport = new ScriptedTransport((_, operation, _) =>
        {
            if (operation != ClaudeAiOperation.Usage)
            {
                return ScriptedTransport.Status(404);
            }

            _duringUsage?.Invoke();
            return _usageResponse ?? ScriptedTransport.Ok(_usage);
        })
        {
            AutoStart = (step, _) => step switch
            {
                AutoStartStep.CreateConversation => ScriptedTransport.Status(201, $$"""{"uuid":"{{ServerConversation}}","name":""}"""),
                AutoStartStep.Completion => ScriptedTransport.Status(_completionStatus,
                    "event: completion\ndata: {\"type\":\"completion\",\"completion\":\"Hello\"}\n\n"
                    + "event: message_limit\ndata: {\"type\":\"message_limit\",\"messageLimit\":{\"type\":\"within_limit\",\"windows\":"
                    + "{\"5h\":{\"status\":\"within_limit\",\"resets_at\":" + NewWindowReset.ToUnixTimeSeconds() + "}}}}\n\n"),
                _ => ScriptedTransport.Status(204, ""),
            },
        };
    }

    public void Dispose() => _temp.Dispose();

    private AutomationService Automation() => new(
        _settings, _notifier, new AutomationStateStore(Path.Combine(_temp.Path, "automation.json")), _time, _ => null, (_, _) => { });

    private AutoStartService Service(AutomationService? automation = null)
    {
        var log = new RequestLog(Path.Combine(_temp.Path, "diagnostics"));
        var codex = new CodexSessionStarter(
            (_, _) => throw new InvalidOperationException("No Codex run was expected."), () => throw new InvalidOperationException("No Codex home was expected."),
            _time, log);
        return new(
            _settings, new ClaudeAiClient(_transport, _time, log), new NoCodexUsage(), codex, automation ?? Automation(), _time, _ => _current,
            (_, _) => throw new InvalidOperationException("No check of a polling loop was expected."), _released.Add);
    }

    /// <summary>These profiles are claude.ai ones: no Codex usage is read.</summary>
    private sealed class NoCodexUsage : IUsageSource
    {
        public Task<SourceCredentials> ResolveAsync(ProfileWatch watch, CancellationToken cancellation) =>
            throw new InvalidOperationException("No Codex usage was expected.");

        public Task<SourceFetch> FetchAsync(ProfileWatch watch, SourceCredentials credentials, UsageData? previous, CancellationToken cancellation) =>
            throw new InvalidOperationException("No Codex usage was expected.");
    }

    private int Starts => _transport.AutoStartRequests.Count(r => r.Step == AutoStartStep.Completion);

    private int UsageRequests => _transport.Requests.Count(r => r.Operation == ClaudeAiOperation.Usage);

    /// <summary>What the user may do while an auto-start request is out.</summary>
    private void Change(string change)
    {
        switch (change)
        {
            case "switched off":
                _profile.AutoStartSessionEnabled = false;
                break;
            case "signed out":
                _profile.ClaudeAi = null;
                break;
            default:
                _settings.Profiles.Remove(_profile);
                break;
        }
    }

    [Fact]
    public async Task AnUnstartedSessionIsStartedOnceAndTheConversationDeleted()
    {
        var service = Service();

        await service.CheckAllAsync(CancellationToken.None);

        Assert.Equal(
            [AutoStartStep.CreateConversation, AutoStartStep.Completion, AutoStartStep.DeleteConversation],
            _transport.AutoStartRequests.Select(r => r.Step));
        // The conversation the server created is the one written to and deleted.
        Assert.Equal(ServerConversation, _transport.AutoStartRequests[1].ConversationId);
        Assert.Equal(ServerConversation, _transport.AutoStartRequests[2].ConversationId);
        Assert.Equal("Personal - Session Auto-Started", Assert.Single(_notifier.Shown).Title);
        Assert.Contains(_profile.Id, _released);

        // The next check, even after the window still reads 0 %, waits for the new window's reset.
        _time.Advance(SessionAutoStart.CheckInterval);
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(1, Starts);
    }

    [Fact]
    public async Task TheWaitSurvivesARestart()
    {
        await Service().CheckAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Equal(1, Starts);
        Assert.Equal(NewWindowReset, Automation().StateOf(_profile.Id).AutoStartHoldUntil);
    }

    [Theory]
    [InlineData("""{"five_hour":{"utilization":0,"resets_at":"2026-10-02T14:00:00Z"}}""")] // open but unused
    [InlineData("""{"five_hour":{"utilization":3,"resets_at":"2026-10-02T14:00:00Z"}}""")]
    [InlineData("""{"seven_day":{"utilization":12}}""")] // no session reported: missing is not 0 %
    [InlineData("""{"five_hour":{"resets_at":null}}""")]
    public async Task NothingIsSentUnlessTheWindowIsReportedUnstarted(string usage)
    {
        _usage = usage;

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Empty(_transport.AutoStartRequests);
        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task OnlyOptedInProfilesWithAClaudeAiSignInAreChecked()
    {
        _profile.AutoStartSessionEnabled = false;
        await Service().CheckAllAsync(CancellationToken.None);
        _profile.AutoStartSessionEnabled = true;
        _profile.ClaudeAi = null;
        _profile.CliLink = new CliLink { ConfigDirectory = @"C:\x" };
        _time.Advance(TimeSpan.FromMinutes(1));
        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Empty(_transport.Requests);
        Assert.Empty(_transport.AutoStartRequests);
    }

    [Theory]
    [InlineData("switched off")]
    [InlineData("signed out")]
    [InlineData("deleted")]
    public async Task NothingIsSentWhenTheProfileChangesWhileItsUsageIsFetched(string change)
    {
        // The user acts while the read-only usage request is on its way; the message is the request that uses usage.
        _duringUsage = () => Change(change);

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Equal(1, UsageRequests);
        Assert.Empty(_transport.AutoStartRequests);
        Assert.Empty(_notifier.Shown);
    }

    [Theory]
    [InlineData("switched off")]
    [InlineData("signed out")]
    [InlineData("deleted")]
    public async Task NothingIsSentWhenTheProfileChangesWhileItsConversationIsCreated(string change)
    {
        var answer = _transport.AutoStart;
        _transport.AutoStart = (step, conversation) =>
        {
            if (step == AutoStartStep.CreateConversation)
            {
                Change(change);
            }

            return answer(step, conversation);
        };

        await Service().CheckAllAsync(CancellationToken.None);

        // The conversation is deleted unsent, and nothing counts as a start or a failure.
        Assert.Equal([AutoStartStep.CreateConversation, AutoStartStep.DeleteConversation], _transport.AutoStartRequests.Select(r => r.Step));
        Assert.Equal(ServerConversation, _transport.AutoStartRequests[1].ConversationId);
        Assert.Empty(_notifier.Shown);
        Assert.Null(Automation().StateOf(_profile.Id).AutoStartHoldUntil);
    }

    [Fact]
    public async Task AProfileSignedOutWhileAnEarlierOneIsCheckedIsSkipped()
    {
        var later = new ProfileSettings { Name = "Later", AutoStartSessionEnabled = true, ClaudeAi = new ClaudeAiLink { OrganizationId = "org-2" } };
        _settings.Profiles.Add(later);
        _duringUsage = () => later.ClaudeAi = null;

        await Service().CheckAllAsync(CancellationToken.None);

        // The first profile is started; the later one is passed over without a request, and nothing throws.
        Assert.All(_transport.Requests, r => Assert.Equal(_profile.Id, r.ProfileId));
        Assert.Equal(1, Starts);
    }

    [Fact]
    public async Task AFailureIsNotifiedOnceAndTriedAgainOnlyAfterTheWait()
    {
        _completionStatus = 500;
        var service = Service();

        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(AutoStartStep.DeleteConversation, _transport.AutoStartRequests.Last().Step);
        var failure = Assert.Single(_notifier.Shown);
        Assert.Equal("Personal - Auto-Start Failed", failure.Title);
        Assert.EndsWith("claude.ai answered with HTTP 500.", failure.Body, StringComparison.Ordinal);

        _time.Advance(SessionAutoStart.FailureHold - TimeSpan.FromMinutes(1));
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(1, Starts);

        _time.Advance(TimeSpan.FromMinutes(2));
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(2, Starts);
        Assert.Single(_notifier.Shown);
    }

    [Fact]
    public async Task FreshUsageOfThePollingLoopIsUsedWithoutAnotherRequest()
    {
        _current = new ProfileUsageState
        {
            ProfileId = _profile.Id, Source = UsageSourceKind.ClaudeAi, LastAttemptAt = Now.AddMinutes(-1), LastSuccessAt = Now.AddMinutes(-1),
            Usage = new CachedUsage { ProfileId = _profile.Id, ObservedAt = Now.AddMinutes(-1), Source = "claudeAi", Usage = new UsageData { SessionPercentage = 0 } },
        };

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Equal(0, UsageRequests);
        Assert.Equal(1, Starts);
        Assert.Empty(_released);
    }

    /// <summary>A polling loop's fresh result, read half a minute ago.</summary>
    private ProfileUsageState PolledNow(UsageData usage)
    {
        var at = _time.GetUtcNow().AddSeconds(-30);
        return new ProfileUsageState
        {
            ProfileId = _profile.Id, Source = UsageSourceKind.ClaudeAi, LastAttemptAt = at, LastSuccessAt = at,
            Usage = new CachedUsage { ProfileId = _profile.Id, ObservedAt = at, Source = "claudeAi", Usage = usage },
        };
    }

    [Fact]
    public async Task APolledWindowThatEndsEarlyIsStartedAgainBeforeTheWaitIsOver()
    {
        var service = Service();
        _current = PolledNow(new UsageData { SessionPercentage = 0 });
        await service.CheckAllAsync(CancellationToken.None);

        // The loop shows the new window open, then, as when a provider resets its limits early, unstarted again.
        _time.Advance(SessionAutoStart.CheckInterval);
        _current = PolledNow(new UsageData { SessionPercentage = 0, SessionResetTime = NewWindowReset });
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(1, Starts);
        _time.Advance(SessionAutoStart.CheckInterval);
        _current = PolledNow(new UsageData { SessionPercentage = 0 });
        await service.CheckAllAsync(CancellationToken.None);

        Assert.Equal(2, Starts);
        Assert.Equal(0, UsageRequests);
        Assert.Equal(2, _notifier.Shown.Count);
    }

    [Fact]
    public async Task AStartWhoseWindowNeverShowsKeepsItsWait()
    {
        // The start succeeded, but the loop never shows its window: starting again would repeat a message the provider did
        // not count, at every check. A check soon after the start, on a read from before it, sends nothing either.
        var service = Service();
        _current = PolledNow(new UsageData { SessionPercentage = 0 });
        await service.CheckAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(20));
        await service.CheckAllAsync(CancellationToken.None);

        for (var check = 0; check < 6; check++)
        {
            _time.Advance(SessionAutoStart.CheckInterval);
            _current = PolledNow(new UsageData { SessionPercentage = 0 });
            await service.CheckAllAsync(CancellationToken.None);
        }

        Assert.Equal(1, Starts);
    }

    [Fact]
    public async Task TheWaitAfterAFailureIsKeptInFullForAPolledProfile()
    {
        _completionStatus = 500;
        var service = Service();
        _current = PolledNow(new UsageData { SessionPercentage = 0 });
        await service.CheckAllAsync(CancellationToken.None);

        // Even a read whose reset time matches the wait's end does not count as a started window.
        _time.Advance(SessionAutoStart.CheckInterval);
        _current = PolledNow(new UsageData { SessionPercentage = 0, SessionResetTime = Now + SessionAutoStart.FailureHold });
        await service.CheckAllAsync(CancellationToken.None);
        _time.Advance(SessionAutoStart.CheckInterval);
        _current = PolledNow(new UsageData { SessionPercentage = 0 });
        await service.CheckAllAsync(CancellationToken.None);

        Assert.Equal(1, Starts);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("internal error")]
    [InlineData("nothing sent")]
    public async Task ASuccessFollowedByAnUnsuccessfulCheckNeverDecides(string latest)
    {
        // A 0 % read a minute ago, then a later check of the loop that brought nothing: the 0 % is no longer current.
        var success = new ProfileUsageState
        {
            ProfileId = _profile.Id, Source = UsageSourceKind.ClaudeAi, LastAttemptAt = Now.AddMinutes(-2), LastSuccessAt = Now.AddMinutes(-2),
            Usage = new CachedUsage { ProfileId = _profile.Id, ObservedAt = Now.AddMinutes(-2), Source = "claudeAi", Usage = new UsageData { SessionPercentage = 0 } },
        };
        _current = latest switch
        {
            "failed" => success with { LastAttemptAt = Now.AddMinutes(-1), LastFailure = FetchFailure.Network, ConsecutiveFailures = 1 },
            "internal error" => success with { LastAttemptAt = Now.AddMinutes(-1), InternalError = "IOException: test" },
            _ => success with { LastAttemptAt = Now.AddMinutes(-1) },
        };

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Equal(0, UsageRequests);
        Assert.Equal(0, Starts);
    }

    [Fact]
    public async Task CachedUsageNeverDecides()
    {
        // The previous run's cache says 0 %, which proves nothing about the window now.
        // Even with a recent success time, values marked as cached are never used to decide.
        _current = new ProfileUsageState
        {
            ProfileId = _profile.Id, Source = UsageSourceKind.ClaudeAi, UsageFromCache = true, LastSuccessAt = Now.AddMinutes(-1),
            Usage = new CachedUsage { ProfileId = _profile.Id, ObservedAt = Now.AddHours(-6), Source = "claudeAi", Usage = new UsageData { SessionPercentage = 0 } },
        };

        await Service().CheckAllAsync(CancellationToken.None);

        // A polled profile is not fetched beside its polling loop either: without fresh usage the round is skipped.
        Assert.Equal(0, UsageRequests);
        Assert.Equal(0, Starts);
    }

    [Fact]
    public async Task AServerRequestedWaitIsHonoured()
    {
        _current = new ProfileUsageState { ProfileId = _profile.Id, Source = UsageSourceKind.ClaudeAi, RetryNotBefore = Now.AddMinutes(10) };

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Empty(_transport.Requests);
        Assert.Empty(_transport.AutoStartRequests);
    }

    [Theory]
    [InlineData("3600", 60)]
    [InlineData(null, 5)] // a rate limit without guidance waits as the polling loop does
    public async Task AServerRequestedWaitIsHonouredForAProfileThatIsNotPolled(string? retryAfter, int minutes)
    {
        _usageResponse = new BrowserResponse(429, "application/json", false, "{}", retryAfter, null);
        var service = Service();
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(1, UsageRequests);

        // Checks before the wait is over, such as one after a wake, send nothing.
        _time.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(30));
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(1, UsageRequests);

        _time.Advance(TimeSpan.FromSeconds(30));
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(2, UsageRequests);
    }

    [Fact]
    public void TheCompletionsResetTimeIsTheLastReportedWindow()
    {
        var body = $"data: {{\"messageLimit\":{{\"windows\":{{\"5h\":{{\"resets_at\":100}}}}}}}}\n\ndata: {{\"messageLimit\":{{\"windows\":{{\"5h\":{{\"resets_at\":{NewWindowReset.ToUnixTimeSeconds()}}}}}}}}}\n";
        Assert.Equal(NewWindowReset, SessionAutoStart.ResetTimeFromCompletion(body));
        Assert.Null(SessionAutoStart.ResetTimeFromCompletion("data: {\"completion\":\"Hi\"}\n"));
        Assert.Null(SessionAutoStart.ResetTimeFromCompletion("data: not json\n"));
    }
}
