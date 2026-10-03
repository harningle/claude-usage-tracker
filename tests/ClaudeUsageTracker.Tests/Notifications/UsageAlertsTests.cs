using ClaudeUsageTracker.Core.Notifications;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Notifications;

public class UsageAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddHours(3);
    private static readonly AlertThresholds Defaults = new(true, true, true, true, []);

    private static UsageData Session(double? percentage, DateTimeOffset? reset = null) =>
        new() { SessionPercentage = percentage, SessionResetTime = reset ?? Reset };

    /// <summary>A session with no open window: no reset time is reported.</summary>
    private static UsageData NoWindow(double percentage) => new() { SessionPercentage = percentage };

    [Fact]
    public void SortedThresholdsMergeBuiltInAndCustomDistinct() =>
        Assert.Equal([50, 75, 95], new AlertThresholds(true, true, false, true, [95, 50]).Sorted);

    [Theory]
    [InlineData(50, true)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(75, false)]
    [InlineData(90, false)]
    [InlineData(95, false)]
    [InlineData(60, false)] // already listed
    public void CustomThresholdValidationFollowsUpstream(int value, bool valid) =>
        Assert.Equal(valid, AlertThresholds.IsValidCustom(value, [60]));

    [Theory]
    [InlineData(74.9, null)]
    [InlineData(75, UsageAlertKind.SessionInfo)]
    [InlineData(91, UsageAlertKind.SessionWarning)]
    [InlineData(95, UsageAlertKind.SessionCritical)]
    [InlineData(100, UsageAlertKind.SessionCritical)]
    public void TheHighestThresholdReachedChoosesTheAlert(double percentage, UsageAlertKind? expected)
    {
        var alerts = UsageAlerts.Evaluate(Defaults, null, null, Session(percentage), Now).Alerts;
        Assert.Equal(expected, alerts.SingleOrDefault()?.Kind);
    }

    [Fact]
    public void AJumpPastSeveralThresholdsSendsOneAlertForTheHighest()
    {
        var result = UsageAlerts.Evaluate(Defaults, null, 10, Session(96), Now);
        var alert = Assert.Single(result.Alerts);
        Assert.Equal(95, alert.Level);
        Assert.Equal([95], result.State.NotifiedLevels);
    }

    [Fact]
    public void EachThresholdFiresOncePerWindow()
    {
        var first = UsageAlerts.Evaluate(Defaults, null, null, Session(76), Now);
        var again = UsageAlerts.Evaluate(Defaults, first.State, 76, Session(80), Now.AddMinutes(1));
        var next = UsageAlerts.Evaluate(Defaults, again.State, 80, Session(90), Now.AddMinutes(2));
        Assert.Single(first.Alerts);
        Assert.Empty(again.Alerts);
        Assert.Equal(90, Assert.Single(next.Alerts).Level);
    }

    [Fact]
    public void ARestartInTheSameWindowDoesNotRepeatAnAlert()
    {
        // The state is what the store kept; the previous percentage is unknown after a restart.
        var saved = UsageAlerts.Evaluate(Defaults, null, null, Session(80), Now).State;
        Assert.Empty(UsageAlerts.Evaluate(Defaults, saved, null, Session(81, Reset.AddSeconds(20)), Now.AddHours(1)).Alerts);
    }

    [Fact]
    public void ANewWindowAfterARestartNotifiesAgain()
    {
        // Upstream kept the old identifiers until it saw a reset itself, so this alert never came.
        var saved = UsageAlerts.Evaluate(Defaults, null, null, Session(80), Now).State;
        var later = Reset.AddHours(1);
        var alerts = UsageAlerts.Evaluate(Defaults, saved, null, Session(77, later.AddHours(4)), later).Alerts;
        Assert.Equal(75, Assert.Single(alerts).Level);
    }

    [Fact]
    public void AResetAnnouncesItselfAndClearsTheWindow()
    {
        var before = UsageAlerts.Evaluate(Defaults, null, null, Session(92), Now);
        var reset = UsageAlerts.Evaluate(Defaults, before.State, before.Effective, NoWindow(0), Now.AddHours(4));
        Assert.Equal(UsageAlertKind.SessionReset, Assert.Single(reset.Alerts).Kind);
        Assert.Empty(reset.State.NotifiedLevels);
        Assert.Null(reset.State.WindowResetAt);
    }

    [Fact]
    public void APassedResetTimeCountsAsAReset()
    {
        // Upstream's effective percentage is 0 once the reset time has passed, whatever the stale value says.
        var reset = UsageAlerts.Evaluate(Defaults, null, 60, Session(60, Now.AddMinutes(-1)), Now);
        Assert.Equal(UsageAlertKind.SessionReset, Assert.Single(reset.Alerts).Kind);
        Assert.Equal(0, reset.Effective);
    }

    [Fact]
    public void NoResetAlertWithoutAPreviousObservationOfThisRun() =>
        Assert.Empty(UsageAlerts.Evaluate(Defaults, new SessionAlertState(Now, [75]), null, NoWindow(0), Now).Alerts);

    [Fact]
    public void AMissingPercentageCausesNothingAndKeepsTheState()
    {
        var state = new SessionAlertState(Reset, [75]);
        var result = UsageAlerts.Evaluate(Defaults, state, 80, Session(null), Now);
        Assert.Empty(result.Alerts);
        Assert.Same(state, result.State);
        Assert.Null(result.Effective);
    }

    [Fact]
    public void DisabledNotificationsSendNothing()
    {
        var disabled = Defaults with { Enabled = false };
        Assert.Empty(UsageAlerts.Evaluate(disabled, null, null, Session(99), Now).Alerts);
        Assert.Empty(UsageAlerts.Evaluate(disabled, null, 50, NoWindow(0), Now).Alerts);
    }

    [Fact]
    public void CustomThresholdsAreInfoAlerts()
    {
        var custom = new AlertThresholds(true, false, false, false, [50]);
        var alert = Assert.Single(UsageAlerts.Evaluate(custom, null, null, Session(55), Now).Alerts);
        Assert.Equal((UsageAlertKind.SessionInfo, 50), (alert.Kind, alert.Level));
    }

    [Fact]
    public void AReportedResetTimeThatMovesBelowAMinuteIsTheSameWindow()
    {
        var saved = UsageAlerts.Evaluate(Defaults, null, null, Session(80), Now).State;
        var result = UsageAlerts.Evaluate(Defaults, saved, 80, Session(85, Reset.AddSeconds(-40)), Now.AddMinutes(1));
        Assert.Empty(result.Alerts);
        Assert.Equal([75], result.State.NotifiedLevels);
    }
}

public class AutoSwitchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed record Candidate(string Name, bool HasCredentials, UsageData? Usage);

    private static int? Next(IReadOnlyList<Candidate> profiles, int current) =>
        AutoSwitch.FindNext(profiles, current, p => p.HasCredentials, p => p.Usage, Now);

    private static UsageData At(double percentage, DateTimeOffset? reset = null) =>
        new() { SessionPercentage = percentage, SessionResetTime = reset ?? Now.AddHours(2) };

    [Fact]
    public void TheNextProfileBelowTheLimitIsChosenWrappingRound()
    {
        Candidate[] profiles = [new("a", true, At(10)), new("b", true, At(100)), new("c", true, At(100))];
        Assert.Equal(0, Next(profiles, 2));
        Assert.Equal(0, Next(profiles, 1));
    }

    [Fact]
    public void ProfilesWithoutCredentialsAreSkipped()
    {
        Candidate[] profiles = [new("a", true, At(100)), new("b", false, null), new("c", true, At(40))];
        Assert.Equal(2, Next(profiles, 0));
    }

    [Fact]
    public void AProfileWithoutKnownUsageCountsAsAvailable()
    {
        Candidate[] profiles = [new("a", true, At(100)), new("b", true, null)];
        Assert.Equal(1, Next(profiles, 0));
    }

    [Fact]
    public void AFullProfileWhoseWindowHasResetCountsAsAvailable()
    {
        Candidate[] profiles = [new("a", true, At(100)), new("b", true, At(100, Now.AddMinutes(-5)))];
        Assert.Equal(1, Next(profiles, 0));
    }

    [Fact]
    public void NoneWhenEveryOtherProfileIsFull()
    {
        Candidate[] profiles = [new("a", true, At(100)), new("b", true, At(100)), new("c", false, null)];
        Assert.Null(Next(profiles, 0));
        Assert.Null(Next([new Candidate("only", true, At(100))], 0));
    }
}
