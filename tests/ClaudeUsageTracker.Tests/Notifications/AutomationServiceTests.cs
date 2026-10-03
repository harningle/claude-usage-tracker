using System.IO;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Usage;
using TestTime = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace ClaudeUsageTracker.Tests.Notifications;

/// <summary>Records what would be posted, in place of Windows notifications.</summary>
internal sealed class RecordingNotifier : INotifier
{
    public List<AppAlert> Shown { get; } = [];

    public List<string> Removed { get; } = [];

    public string? Problem { get; set; }

    public void Show(AppAlert alert) => Shown.Add(alert);

    public void Remove(string tag) => Removed.Add(tag);
}

/// <summary>
/// Notifications and auto-switch from fresh usage (upstream <c>NotificationManager</c> and
/// <c>checkAutoSwitchIfNeeded</c>): each alert once, across a restart, and never more than the rules allow.
/// </summary>
public sealed class AutomationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly TestTime _time = new(Now);
    private readonly RecordingNotifier _notifier = new();
    private readonly List<(string From, string To)> _switches = [];
    private readonly Dictionary<Guid, UsageData> _known = [];
    private readonly AppSettings _settings = new();
    private readonly ProfileSettings _first;
    private readonly ProfileSettings _second;

    public AutomationServiceTests()
    {
        _first = new ProfileSettings { Name = "Work", CliLink = new CliLink { ConfigDirectory = @"C:\x", AccountUuid = "acc-1" } };
        _second = new ProfileSettings { Name = "Personal", ClaudeAi = new ClaudeAiLink { OrganizationId = "org-2" } };
        _settings.Profiles.AddRange([_first, _second]);
        _settings.ActiveProfileId = _first.Id;
    }

    public void Dispose() => _temp.Dispose();

    private string StateFile => Path.Combine(_temp.Path, "automation.json");

    private AutomationService Service() => new(
        _settings, _notifier, new AutomationStateStore(StateFile), _time, id => _known.GetValueOrDefault(id),
        (from, to) => _switches.Add((from.Name, to.Name)));

    private static UsageData Session(double percentage, DateTimeOffset? reset = null) =>
        new() { SessionPercentage = percentage, SessionResetTime = reset ?? Now.AddHours(3) };

    [Fact]
    public void AThresholdIsNotifiedOnceEvenAcrossARestart()
    {
        var service = Service();
        service.UsageFetched(_first, Session(80));
        service.UsageFetched(_first, Session(81));
        Service().UsageFetched(_first, Session(82));

        var alert = Assert.Single(_notifier.Shown);
        Assert.Equal("Work - Usage Info", alert.Title);
        Assert.Equal("You've used 80.0% of your session limit. Resets in 3h", alert.Body);
        Assert.Equal(_first.Id, alert.ProfileId);
        Assert.Equal("default", alert.SoundName);
    }

    [Fact]
    public void ThePersistedStateHoldsNoUsageData()
    {
        Service().UsageFetched(_first, Session(91));

        var saved = new AutomationStateStore(StateFile).Load().Profiles[_first.Id];
        Assert.Equal([90], saved.NotifiedLevels);
        Assert.Equal(Now.AddHours(3), saved.SessionWindowResetAt);
        // Only what was notified and when the window resets: no percentage or other usage value.
        var members = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StateFile))!["profiles"]![_first.Id.ToString()]!.AsObject().Select(p => p.Key);
        Assert.Equal(["sessionWindowResetAt", "notifiedLevels", "autoStartHoldUntil", "autoStartFailureNotified", "consoleExpiryNotifiedFor"], members);
    }

    [Fact]
    public void TheProfilesSoundAndSwitchAreUsed()
    {
        _first.NotificationSettings.SoundName = "reminder";
        var service = Service();
        service.UsageFetched(_first, Session(96));
        _second.NotificationSettings.Enabled = false;
        service.UsageFetched(_second, Session(99));

        var alert = Assert.Single(_notifier.Shown);
        Assert.Equal("Work - Session Usage Critical", alert.Title);
        Assert.Equal("reminder", alert.SoundName);
    }

    [Fact]
    public void AResetIsAnnouncedOnlyAfterAnObservationOfThisRun()
    {
        Service().UsageFetched(_first, Session(50));
        var restarted = Service();
        restarted.UsageFetched(_first, new UsageData { SessionPercentage = 0 });
        Assert.Empty(_notifier.Shown);

        restarted.UsageFetched(_first, Session(20));
        restarted.UsageFetched(_first, new UsageData { SessionPercentage = 0 });
        Assert.Equal("Work - Session Reset", Assert.Single(_notifier.Shown).Title);
    }

    [Fact]
    public void ADamagedStateFileIsReportedAndNeverOverwritten()
    {
        File.WriteAllText(StateFile, "{ damaged");
        var service = Service();

        service.UsageFetched(_first, Session(80));

        Assert.NotNull(service.StateProblem);
        Assert.Single(_notifier.Shown);
        Assert.Equal("{ damaged", File.ReadAllText(StateFile));
    }

    [Fact]
    public void AutoSwitchActivatesTheNextProfileOnceUntilTheSessionDropsAgain()
    {
        _settings.AutoSwitchProfileEnabled = true;
        var service = Service();

        service.UsageFetched(_first, Session(100));
        service.UsageFetched(_first, Session(100));
        Assert.Equal([("Work", "Personal")], _switches);
        Assert.Contains(_notifier.Shown, a => a.Title == "Profile Auto-Switched" && a.Body == "Work → Personal due to session limit reached");

        service.UsageFetched(_first, Session(40, Now.AddHours(5)));
        service.UsageFetched(_first, Session(100, Now.AddHours(5)));
        Assert.Equal(2, _switches.Count);
    }

    [Fact]
    public void AutoSwitchIsOffByDefaultAndOnlyFollowsTheActiveProfile()
    {
        var service = Service();
        service.UsageFetched(_first, Session(100));
        Assert.Empty(_switches);

        _settings.AutoSwitchProfileEnabled = true;
        service.UsageFetched(_second, Session(100));
        Assert.Empty(_switches);
    }

    [Fact]
    public void AutoSwitchStaysWhenEveryOtherProfileIsFull()
    {
        _settings.AutoSwitchProfileEnabled = true;
        _known[_second.Id] = Session(100);

        Service().UsageFetched(_first, Session(100));

        Assert.Empty(_switches);
    }

    [Fact]
    public void AutoSwitchTriesAgainOnceAnotherProfileHasRoom()
    {
        _settings.AutoSwitchProfileEnabled = true;
        _known[_second.Id] = Session(100, Now.AddHours(1));
        var service = Service();

        service.UsageFetched(_first, Session(100));
        Assert.Empty(_switches);

        // The other profile's window resets while this one is still at its limit: the next check switches.
        _time.Advance(TimeSpan.FromMinutes(61));
        service.UsageFetched(_first, Session(100));
        Assert.Equal([("Work", "Personal")], _switches);
    }

    [Fact]
    public void TheConsoleExpiryIsWarnedOnceInItsLastDay()
    {
        _first.Console = new ConsoleLink { OrganizationId = "org-api", SessionKeyExpiry = Now.AddHours(30) };
        var service = Service();
        service.Tick();
        Assert.Empty(_notifier.Shown);

        _time.Advance(TimeSpan.FromHours(7));
        service.Tick();
        service.Tick();
        Service().Tick();

        var alert = Assert.Single(_notifier.Shown);
        Assert.Equal("Work - API Session Expiring", alert.Title);
        Assert.Contains("23h", alert.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageUpdatedFollowsOnlyAUserTriggeredRefreshSoonAfter()
    {
        var service = Service();
        service.UserTriggered(_first.Id);
        service.UsageFetched(_first, Session(10));
        var alert = Assert.Single(_notifier.Shown);
        Assert.Equal(("Usage Updated", "none"), (alert.Title, alert.SoundName));
        _time.Advance(AutomationService.UsageUpdatedLifetime);
        Assert.True(SpinWait.SpinUntil(() => _notifier.Removed.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(alert.Tag, _notifier.Removed[0]);

        service.UserTriggered(_first.Id);
        _time.Advance(TimeSpan.FromSeconds(6));
        service.UsageFetched(_first, Session(11));
        Assert.Single(_notifier.Shown);
    }
}
