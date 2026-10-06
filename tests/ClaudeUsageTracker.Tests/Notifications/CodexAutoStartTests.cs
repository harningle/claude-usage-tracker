using System.IO;
using System.Net;
using System.Net.Http;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Notifications;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;
using ClaudeUsageTracker.Tests.Usage;
using TestTime = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace ClaudeUsageTracker.Tests.Notifications;

/// <summary>
/// The Codex session auto-start, which upstream does not have: only for opted-in profiles that read the Codex CLI's
/// login, only on fresh usage showing an unstarted window (0 % with a reset time that moves on with the clock), through
/// one run of the CLI, once per window, with a wait after a failure. The usage answers and the CLI's runs are scripted;
/// nothing starts a process or reaches an account.
/// </summary>
public sealed class CodexAutoStartTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly TestTime _time = new(Now);
    private readonly RecordingNotifier _notifier = new();
    private readonly AppSettings _settings = new();
    private readonly ProfileSettings _profile;
    private readonly string _codexHome;
    private readonly ScriptedHandler _http;
    private readonly List<string> _runs = [];
    private CodexExecResult _run = new(CodexExecOutcome.Completed, 0);
    private ProfileUsageState? _current;
    private Action? _duringUsage;

    // Codex as seen while idle: 0 %, resetting one session length after each read.
    private Func<string> _usage;

    public CodexAutoStartTests()
    {
        _codexHome = Path.Combine(_temp.Path, "codex");
        Directory.CreateDirectory(_codexHome);
        SignIn("chatgpt-1");
        _profile = new ProfileSettings
        {
            Name = "Codex",
            Provider = Provider.Codex.RawValue(),
            AutoStartSessionEnabled = true,
            Codex = new CodexLink { CodexHome = _codexHome, AccountId = "chatgpt-1" },
        };
        _settings.Profiles.Add(_profile);
        _usage = () => Usage(0, _time.GetUtcNow() + SessionAutoStart.SessionLength);
        _http = new ScriptedHandler((_, _) =>
        {
            _duringUsage?.Invoke();
            return Task.FromResult(LinkHarness.Json(HttpStatusCode.OK, _usage()));
        });
    }

    public void Dispose() => _temp.Dispose();

    private static string Usage(int used, DateTimeOffset reset) =>
        $$"""{"plan_type":"team","rate_limit":{"primary_window":{"used_percent":{{used}},"limit_window_seconds":18000,"reset_at":{{reset.ToUnixTimeSeconds()}}},"secondary_window":{"used_percent":12,"limit_window_seconds":604800,"reset_at":{{Now.AddDays(3).ToUnixTimeSeconds()}} } } }""";

    /// <summary>The Codex CLI's login in the profile's Codex home, for the given ChatGPT account.</summary>
    private void SignIn(string account) => File.WriteAllText(Path.Combine(_codexHome, "auth.json"),
        $$"""{"tokens":{"access_token":"codex-at","refresh_token":"codex-rt","account_id":"{{account}}"},"last_refresh":"2026-10-06T10:00:00Z"}""");

    private AutomationService Automation() => new(
        _settings, _notifier, new AutomationStateStore(Path.Combine(_temp.Path, "automation.json")), _time, _ => null, (_, _) => { });

    private string LogText => File.ReadAllText(Path.Combine(_temp.Path, "diagnostics", "requests.jsonl"));

    private ProfileSecretStore Secrets => new(new SecretStore(Path.Combine(_temp.Path, "secrets")));

    /// <summary>The services as the app has them; the profile's Codex home is also the CLI's default one.</summary>
    private AutoStartService Service()
    {
        var log = new RequestLog(Path.Combine(_temp.Path, "diagnostics"));
        var http = new HttpClient(_http);
        var source = new CodexUsageSource(
            Secrets, new CredentialLock(), new OAuthTokenClient(http, _time, log), new CodexClient(http, _time, log), _time, () => _codexHome);
        var starter = new CodexSessionStarter(
            (home, _) =>
            {
                _runs.Add(home);
                return Task.FromResult(_run);
            },
            () => _codexHome, _time, log);
        var claudeAi = new ClaudeAiClient(
            new ScriptedTransport((_, _, _) => throw new InvalidOperationException("No claude.ai request was expected.")), _time, log);
        return new AutoStartService(_settings, claudeAi, source, starter, Automation(), _time, _ => _current, _ => { });
    }

    [Fact]
    public async Task AnUnstartedWindowIsStartedOnceThroughTheCli()
    {
        var service = Service();

        await service.CheckAllAsync(CancellationToken.None);

        Assert.Equal([_codexHome], _runs);
        Assert.Equal("Codex - Session Auto-Started", Assert.Single(_notifier.Shown).Title);
        Assert.Equal(Now + SessionAutoStart.SessionLength, Automation().StateOf(_profile.Id).AutoStartHoldUntil);
        Assert.Contains("\"operation\":\"codexAutoStart\"", LogText, StringComparison.Ordinal);

        // The started window keeps its reset time; even at 0 % it is not started again, and the wait holds anyway.
        var started = _time.GetUtcNow();
        _usage = () => Usage(0, started + SessionAutoStart.SessionLength);
        _time.Advance(SessionAutoStart.CheckInterval);
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Single(_runs);
    }

    [Theory]
    [InlineData(0, 0, true)] // the moving reset time of an unstarted window
    [InlineData(0, -2, true)]
    [InlineData(0, 2, true)]
    [InlineData(0, -5, false)] // started five minutes ago, still at 0 %
    [InlineData(0, -120, false)]
    [InlineData(3, 0, false)]
    public void ACodexWindowIsUnstartedOnlyAtZeroWithAResetOneSessionAfterTheRead(int used, int minutesFromIdle, bool unstarted)
    {
        var usage = new UsageData { SessionPercentage = used, SessionResetTime = Now + SessionAutoStart.SessionLength + TimeSpan.FromMinutes(minutesFromIdle) };

        Assert.Equal(unstarted, SessionAutoStart.ShouldStartCodex(usage, Now));
    }

    [Fact]
    public void AMissingOrPastResetAtZeroIsUnstartedAndMissingUsageIsNot()
    {
        Assert.True(SessionAutoStart.ShouldStartCodex(new UsageData { SessionPercentage = 0 }, Now));
        Assert.True(SessionAutoStart.ShouldStartCodex(new UsageData { SessionPercentage = 0, SessionResetTime = Now.AddMinutes(-1) }, Now));
        Assert.False(SessionAutoStart.ShouldStartCodex(new UsageData { SessionResetTime = Now + SessionAutoStart.SessionLength }, Now));
    }

    [Fact]
    public async Task AnOpenWindowIsNotStarted()
    {
        _usage = () => Usage(0, Now.AddHours(3));

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Single(_http.Requests);
        Assert.Empty(_runs);
        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task PastedCredentialsOrNoLoginNeverStartAnything()
    {
        // Usable pasted credentials, for the same account the Codex CLI is signed in to: their usage could be read and a run
        // would go through the CLI's own login, which this profile does not follow.
        var secrets = Secrets;
        var bundle = secrets.Load(_profile.Id);
        bundle.CodexAuthJson = """{"tokens":{"access_token":"pasted-at","refresh_token":"pasted-rt","account_id":"chatgpt-1"},"last_refresh":"2026-10-06T10:00:00Z"}""";
        secrets.Save(_profile.Id, bundle);
        _profile.Codex = new CodexLink { Source = CodexLink.ManualSource };
        Assert.False(AutoStartService.CanSend(_profile));
        await Service().CheckAllAsync(CancellationToken.None);

        _profile.Codex = null;
        Assert.False(AutoStartService.CanSend(_profile));
        _time.Advance(TimeSpan.FromMinutes(1));
        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task NothingRunsWhileTheCliIsSignedInToAnotherAccount()
    {
        SignIn("chatgpt-2");

        await Service().CheckAllAsync(CancellationToken.None);

        // The usage of that account is not read either.
        Assert.Empty(_http.Requests);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task NothingRunsWhenTheCliSignsInToAnotherAccountWhileTheUsageIsRead()
    {
        _duringUsage = () => SignIn("chatgpt-2");

        await Service().CheckAllAsync(CancellationToken.None);

        // The CLI's login is shared: the message would start the other account's window. Nothing counts as a start or a failure.
        Assert.Single(_http.Requests);
        Assert.Empty(_runs);
        Assert.Empty(_notifier.Shown);
        Assert.Null(Automation().StateOf(_profile.Id).AutoStartHoldUntil);
    }

    [Theory]
    [InlineData("switched off")]
    [InlineData("pasted credentials")]
    [InlineData("another home")]
    [InlineData("deleted")]
    public async Task NothingRunsWhenTheProfileChangesWhileItsUsageIsRead(string change)
    {
        _duringUsage = () =>
        {
            switch (change)
            {
                case "switched off":
                    _profile.AutoStartSessionEnabled = false;
                    break;
                case "pasted credentials":
                    _profile.Codex = new CodexLink { Source = CodexLink.ManualSource };
                    break;
                case "another home":
                    _profile.Codex = new CodexLink { CodexHome = Path.Combine(_temp.Path, "other"), AccountId = "chatgpt-1" };
                    break;
                default:
                    _settings.Profiles.Remove(_profile);
                    break;
            }
        };

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Single(_http.Requests);
        Assert.Empty(_runs);
    }

    [Theory]
    [InlineData(CodexExecOutcome.Failed, 1, "Codex exited with code 1.")]
    [InlineData(CodexExecOutcome.Failed, 0, "Codex finished without a reply.")]
    [InlineData(CodexExecOutcome.NotFound, null, "The Codex CLI (codex.exe) was not found on PATH.")]
    [InlineData(CodexExecOutcome.TimedOut, null, "Codex did not finish within two minutes.")]
    public async Task AFailureIsNotifiedOnceAndTriedAgainOnlyAfterTheWait(CodexExecOutcome outcome, int? exitCode, string reason)
    {
        _run = new CodexExecResult(outcome, exitCode);
        var service = Service();

        await service.CheckAllAsync(CancellationToken.None);
        var failure = Assert.Single(_notifier.Shown);
        Assert.Equal("Codex - Auto-Start Failed", failure.Title);
        Assert.EndsWith(reason, failure.Body, StringComparison.Ordinal);

        _time.Advance(SessionAutoStart.FailureHold - TimeSpan.FromMinutes(1));
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Single(_runs);

        _time.Advance(TimeSpan.FromMinutes(2));
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(2, _runs.Count);
        Assert.Single(_notifier.Shown);
    }

    [Fact]
    public async Task FreshUsageOfThePollingLoopIsUsedWithoutAnotherRequest()
    {
        // Four minutes old: its reset time is judged against when it was read, not against now.
        var at = Now.AddMinutes(-4);
        _current = new ProfileUsageState
        {
            ProfileId = _profile.Id, Source = UsageSourceKind.Codex, LastAttemptAt = at, LastSuccessAt = at,
            Usage = new CachedUsage
            {
                ProfileId = _profile.Id, ObservedAt = at, Source = "codex",
                Usage = new UsageData { SessionPercentage = 0, SessionResetTime = at + SessionAutoStart.SessionLength },
            },
        };

        await Service().CheckAllAsync(CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.Single(_runs);
    }
}
