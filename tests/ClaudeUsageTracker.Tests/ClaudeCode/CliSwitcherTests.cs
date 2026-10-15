using System.IO;
using System.Windows.Threading;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Tests.Notifications;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>The queue of switches that wait for Claude Code to close: the newest request is the one that counts.</summary>
public sealed class CliSwitcherTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly AppSettings _settings = new();
    private readonly RecordingNotifier _notifier = new();

    public void Dispose() => _temp.Dispose();

    private string UserProfile => Path.Combine(_temp.Path, "home");

    private string CredentialsFile => Path.Combine(UserProfile, ".claude", ".credentials.json");

    private string AccountFile => Path.Combine(UserProfile, ".claude.json");

    /// <summary>Signs the folder in to an account and adds a profile that follows it.</summary>
    private async Task<ProfileSettings> LinkProfile(LinkHarness links, string uuid, string name)
    {
        File.WriteAllText(CredentialsFile, LinkHarness.Credentials($"at-{uuid}", $"rt-{uuid}", Now.AddHours(8)));
        File.WriteAllText(AccountFile, LinkHarness.Account(uuid, $"{uuid}@example.com"));
        var snapshot = links.Service.Read(CliStore.Inventory(UserProfile, null, [])[0]);
        var profile = new ProfileSettings { Name = name, CliLink = links.Service.CreateLink(snapshot) };
        await links.Service.WriteMirrorAsync(profile.Id, snapshot, CancellationToken.None);
        _settings.Profiles.Add(profile);
        return profile;
    }

    /// <summary>The switcher's retry timer belongs to a dispatcher, so the test runs on an STA thread with one.</summary>
    private static Task OnSta(Func<Task> test)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                test().GetAwaiter().GetResult();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private async Task<(CliSwitcher Switcher, ProfileSettings First, ProfileSettings Second, LinkHarness Links)> Create(ScriptedHandler? tokens = null)
    {
        Directory.CreateDirectory(Path.Combine(UserProfile, ".claude"));
        var links = new LinkHarness(_temp.Path, UserProfile, _time, tokens);
        var profiles = new List<ProfileSettings> { await LinkProfile(links, "acc-a", "First"), await LinkProfile(links, "acc-b", "Second") };

        var store = new SettingsStore(Path.Combine(_temp.Path, "settings.json"));
        var cache = new UsageCacheStore(Path.Combine(_temp.Path, "cache"));
        var history = new UsageHistoryService(new UsageHistoryStore(Path.Combine(_temp.Path, "history")), _time);
        var automation = new AutomationService(
            _settings, _notifier, new AutomationStateStore(Path.Combine(_temp.Path, "automation.json")), _time, _ => null, (_, _) => { });
        var switcher = new CliSwitcher(
            _settings, links.Service, new ProfileService(store, _settings, links.Service, links.Secrets, links.Lock, cache, history, _time), automation,
            () => CliStore.Inventory(UserProfile, null, [])[0], () => { }, _time, Dispatcher.CurrentDispatcher, links.Log);
        return (switcher, profiles[0], profiles[1], links);
    }

    /// <summary>The switcher's entries of the network log, oldest first, as "method, who asked, reason or outcome".</summary>
    private static List<string> Logged(LinkHarness links) =>
    [
        .. links.Log.ReadRecent(100).Reverse().Where(e => e.Operation == "cliSwitch").Select(e => $"{e.Method} {e.Route} {e.ErrorCategory ?? "-"}"),
    ];

    [Fact]
    public Task EveryChangeOfTheWaitingSwitchIsLogged() => OnSta(async () =>
    {
        // Whether a switch waits, and how it stopped waiting, shows in Debug Tools afterwards (the user's request of 2026-10-15).
        // The folder holds the second profile's account. Retries while Claude Code runs, and a cancel with nothing
        // waiting, change nothing and log nothing.
        var (switcher, first, _, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);
        await switcher.TickAsync();
        switcher.Cancel();
        switcher.Cancel();
        await switcher.RequestAsync(first, automaticFrom: null, null);
        await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);
        switcher.DropOnExit();
        await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);
        links.Processes.Clear();
        await switcher.TickAsync();

        Assert.Equal(
            [
                "QUEUE picked claudeCodeRunning", "CANCEL picked -",
                "QUEUE manual claudeCodeRunning", "DROP manual superseded", "QUEUE picked claudeCodeRunning", "DROP picked appClosed",
                "QUEUE picked claudeCodeRunning", "RUN picked -",
            ],
            Logged(links));
        Assert.All(links.Log.ReadRecent(100).Where(e => e.Operation == "cliSwitch"), e => Assert.Equal(first.Id, e.ProfileId));
    });

    [Theory]
    [InlineData("cancelled")]
    [InlineData("replaced")]
    public Task AnAttemptThatSwitchesAfterItsSwitchWasCancelledOrReplacedIsStillLogged(string change) => OnSta(async () =>
    {
        // The waiting switch to the first profile is tried once Claude Code has closed, but its attempt waits for the
        // credential lock (held, as by a renewal). Meanwhile the user cancels it or picks another profile: the attempt
        // runs on and switches Claude Code, which the log must not hide behind the cancel or the drop.
        var (switcher, first, second, links) = await Create();
        await LinkProfile(links, "acc-c", "Third");
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);
        links.Processes.Clear();
        var held = links.Lock.Acquire();
        var attempt = switcher.TickAsync();

        if (change == "cancelled")
        {
            switcher.Cancel();
        }
        else
        {
            await switcher.RequestAsync(second, automaticFrom: null, null);
        }

        held.Dispose();
        await attempt;

        Assert.Contains("acc-a", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        Assert.Equal(
            change == "cancelled"
                ? ["QUEUE picked claudeCodeRunning", "CANCEL picked -", "RUN picked -"]
                : ["QUEUE picked claudeCodeRunning", "DROP picked superseded", "QUEUE manual busy", "RUN picked -"],
            Logged(links));
        Assert.Equal(first.Id, links.Log.ReadRecent(1).Single().ProfileId);
    });

    [Fact]
    public Task AWaitingSwitchToADeletedProfileIsLoggedAsDropped() => OnSta(async () =>
    {
        var (switcher, first, _, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);
        _settings.Profiles.Remove(first);

        await switcher.TickAsync();

        Assert.Null(switcher.Pending);
        Assert.Equal(["QUEUE picked claudeCodeRunning", "DROP picked profileUnlinked"], Logged(links));
    });

    [Fact]
    public Task TheNewestRequestReplacesOneThatWaits() => OnSta(async () =>
    {
        // Claude Code is on a third account, so both switches would change it.
        var (switcher, first, second, links) = await Create();
        await LinkProfile(links, "acc-c", "Third");
        links.ClaudeRunning();

        Assert.Equal(CliSwitchOutcome.Deferred, (await switcher.RequestAsync(first, automaticFrom: null, null)).Outcome);
        Assert.Equal(first.Id, switcher.Pending?.TargetProfileId);

        await switcher.RequestAsync(second, automaticFrom: first.Id, null);

        Assert.Equal(second.Id, switcher.Pending?.TargetProfileId);
        Assert.True(switcher.Pending?.Automatic);
        Assert.True(switcher.ViewFor(second, null).Pending);
        Assert.False(switcher.ViewFor(first, null).Pending);
    });

    [Fact]
    public Task ANewerSwitchThatSucceedsAtOnceDropsTheOlderWaitingOne() => OnSta(async () =>
    {
        // The folder holds the second profile's account. A switch to the first waits while Claude Code runs.
        var (switcher, first, second, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null);
        links.Processes.Clear();

        // Claude Code has closed and the user asks for the second profile: the older wait for the first must not switch
        // the folder away from it at the next retry.
        Assert.Equal(CliSwitchOutcome.AlreadyInUse, (await switcher.RequestAsync(second, automaticFrom: null, null)).Outcome);
        Assert.Null(switcher.Pending);
    });

    [Fact]
    public Task PickingTheAccountClaudeCodeIsOnWhileItRunsDropsTheWaitingSwitch() => OnSta(async () =>
    {
        // The folder holds the second profile's account. While Claude Code runs, the first is picked and then the second
        // again: nothing is left waiting, and the popover must not say that Claude Code switches to the account it is on.
        // Only the first pick, which was queued, was announced.
        var (switcher, first, second, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);

        Assert.Equal(CliSwitchOutcome.AlreadyInUse, (await switcher.RequestAsync(second, automaticFrom: null, null, fromActivation: true)).Outcome);

        Assert.Null(switcher.Pending);
        Assert.Equal([Strings.Format("windows.cli.switch.deferred", "First")], _notifier.Shown.Select(a => a.Body));
        Assert.Equal(["QUEUE picked claudeCodeRunning", "DROP picked superseded", "RUN picked alreadyInUse"], Logged(links));
    });

    [Fact]
    public Task AWaitingSwitchEndsOnceClaudeCodeIsSignedInToItsAccount() => OnSta(async () =>
    {
        // A switch to the first profile waits; meanwhile the user signs Claude Code in to that account with /login. Only its
        // queueing was announced: the tracker switched nothing.
        var (switcher, first, _, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);
        File.WriteAllText(CredentialsFile, LinkHarness.Credentials("at-acc-a-new", "rt-acc-a-new", Now.AddHours(8)));
        File.WriteAllText(AccountFile, LinkHarness.Account("acc-a", "acc-a@example.com"));

        await switcher.TickAsync();

        Assert.Null(switcher.Pending);
        Assert.Contains("at-acc-a-new", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
        Assert.Equal([Strings.Format("windows.cli.switch.deferred", "First")], _notifier.Shown.Select(a => a.Body));
    });

    [Fact]
    public Task AnOlderSwitchThatEndsUpWaitingNeverReplacesANewerRequest() => OnSta(async () =>
    {
        // The switch to the first profile waits for the credential lock (held, as by a renewal); meanwhile Claude Code
        // starts and the second profile is requested.
        var (switcher, first, second, links) = await Create();
        var held = links.Lock.Acquire();
        var older = switcher.RequestAsync(first, automaticFrom: null, null);
        links.ClaudeRunning();
        Assert.Equal(CliSwitchOutcome.Deferred, (await switcher.RequestAsync(second, automaticFrom: first.Id, null)).Outcome);

        held.Dispose();
        Assert.Equal(CliSwitchOutcome.Deferred, (await older).Outcome);

        // The newer request is the one that waits.
        Assert.Equal(second.Id, switcher.Pending?.TargetProfileId);
        Assert.False(switcher.ViewFor(first, null).Pending);
    });

    [Theory]
    [InlineData("auto-switch turned off")]
    [InlineData("signed in to a third account")]
    public Task AQueuedAutomaticSwitchIsDroppedWhenItNoLongerApplies(string change) => OnSta(async () =>
    {
        // Claude Code is on the second profile's account, which reached its limit: auto-switch queues the first.
        var (switcher, first, second, links) = await Create();
        _settings.AutoSwitchProfileEnabled = true;
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: second.Id, null);
        Assert.Equal(first.Id, switcher.Pending?.TargetProfileId);

        if (change == "auto-switch turned off")
        {
            _settings.AutoSwitchProfileEnabled = false;
        }
        else
        {
            // The user signs Claude Code in to another account that a profile follows, so no confirmation would stop it.
            await LinkProfile(links, "acc-c", "Third");
        }

        links.Processes.Clear();
        await switcher.TickAsync();

        Assert.Null(switcher.Pending);
        Assert.DoesNotContain("acc-a", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        Assert.Equal(["QUEUE automatic claudeCodeRunning", "DROP automatic noLongerApplies"], Logged(links));
    });

    [Fact]
    public Task AQueuedAutomaticSwitchThatStillAppliesIsMadeOnceTheFolderCanBeRead() => OnSta(async () =>
    {
        var (switcher, first, second, links) = await Create();
        _settings.AutoSwitchProfileEnabled = true;
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: second.Id, null);
        links.Processes.Clear();

        // A file caught half-written says nothing about the account: the switch keeps waiting.
        var credentials = File.ReadAllText(CredentialsFile);
        File.WriteAllText(CredentialsFile, "{ torn");
        await switcher.TickAsync();
        Assert.Equal(first.Id, switcher.Pending?.TargetProfileId);

        File.WriteAllText(CredentialsFile, credentials);
        await switcher.TickAsync();

        Assert.Null(switcher.Pending);
        Assert.Contains("acc-a", File.ReadAllText(AccountFile), StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AnAutomaticSwitchAsksAgainAfterItsRenewal(bool queued) => OnSta(async () =>
    {
        // The first profile's saved login has expired, so the switch renews it first, under the credential lock. While
        // that request is out the user turns auto-switch off: nothing may be written to Claude Code's folder afterwards.
        var (switcher, first, second, links) = await Create(new ScriptedHandler((_, _) =>
        {
            _settings.AutoSwitchProfileEnabled = false;
            return Task.FromResult(LinkHarness.Grant("at-renewed", "rt-renewed"));
        }));
        _settings.AutoSwitchProfileEnabled = true;
        if (queued)
        {
            links.ClaudeRunning();
            await switcher.RequestAsync(first, automaticFrom: second.Id, null);
            links.Processes.Clear();
        }

        _time.Now += TimeSpan.FromHours(9);
        if (queued)
        {
            await switcher.TickAsync();
        }
        else
        {
            Assert.Equal(CliSwitchRefusal.NoLongerApplies, (await switcher.RequestAsync(first, automaticFrom: second.Id, null)).Refusal);
        }

        Assert.Equal(1, links.TokenRequests);
        Assert.Null(switcher.Pending);
        Assert.Equal(queued ? ["QUEUE automatic claudeCodeRunning", "DROP automatic noLongerApplies"] : ["RUN automatic noLongerApplies"], Logged(links));
        Assert.Null(switcher.ViewFor(first, null).Message);
        Assert.Contains("acc-b", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        Assert.Contains("at-acc-b", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task OnlyAPickedSwitchIsAnnouncedWhenItHappensAtOnce(bool fromActivation) => OnSta(async () =>
    {
        // The folder holds the second profile's account and no Claude Code runs. A switch made on the CLI Account page is
        // watched there; one asked for by picking the profile in the popover is not.
        var (switcher, first, _, _) = await Create();

        Assert.Equal(CliSwitchOutcome.Switched, (await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation)).Outcome);

        Assert.Contains("acc-a", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        Assert.Equal(fromActivation ? [Strings.Format("windows.cli.switch.done", "First")] : [], _notifier.Shown.Select(a => a.Body));
    });

    [Fact]
    public Task APickedSwitchThatWaitsIsAnnouncedWhenQueuedAndWhenMade() => OnSta(async () =>
    {
        var (switcher, first, _, links) = await Create();
        links.ClaudeRunning();

        Assert.Equal(CliSwitchOutcome.Deferred, (await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true)).Outcome);
        Assert.True(switcher.Pending?.FromActivation);
        Assert.Equal([Strings.Format("windows.cli.switch.deferred", "First")], _notifier.Shown.Select(a => a.Body));

        // Each retry while Claude Code still runs is not announced again.
        await switcher.TickAsync();
        Assert.Single(_notifier.Shown);

        links.Processes.Clear();
        await switcher.TickAsync();

        Assert.Null(switcher.Pending);
        Assert.Contains("acc-a", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        Assert.Equal(
            [Strings.Format("windows.cli.switch.deferred", "First"), Strings.Format("windows.cli.switch.done", "First")],
            _notifier.Shown.Select(a => a.Body));
    });

    [Fact]
    public Task APickMadeWhileAnotherSwitchRunsIsAnnouncedOnceItWaitsForClaudeCode() => OnSta(async () =>
    {
        // A switch to the first profile holds the switcher (here: waiting for the credential lock, as during a renewal)
        // when the first profile is picked again; meanwhile Claude Code starts.
        var (switcher, first, _, links) = await Create();
        var held = links.Lock.Acquire();
        var older = switcher.RequestAsync(first, automaticFrom: null, null);
        Assert.Equal(CliSwitchOutcome.Deferred, (await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true)).Outcome);
        links.ClaudeRunning();
        held.Dispose();
        await older;
        Assert.True(switcher.Pending?.FromActivation);

        // Its first attempt finds Claude Code running: the wait is announced, once.
        await switcher.TickAsync();
        Assert.Equal([Strings.Format("windows.cli.switch.deferred", "First")], _notifier.Shown.Select(a => a.Body));
        await switcher.TickAsync();
        Assert.Single(_notifier.Shown);

        links.Processes.Clear();
        await switcher.TickAsync();

        Assert.Null(switcher.Pending);
        Assert.Contains("acc-a", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        Assert.Equal(
            [Strings.Format("windows.cli.switch.deferred", "First"), Strings.Format("windows.cli.switch.done", "First")],
            _notifier.Shown.Select(a => a.Body));
        Assert.Equal(["QUEUE picked busy", "DROP manual superseded", "RUN picked -"], Logged(links));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task APickedSwitchNeverSignsOutALoginNoProfileFollows(bool queued) => OnSta(async () =>
    {
        // Claude Code was signed in to an account no profile follows. Picking a profile must not sign it out, now or once
        // Claude Code closes; it says so instead of asking.
        var (switcher, first, _, links) = await Create();
        File.WriteAllText(CredentialsFile, LinkHarness.Credentials("at-acc-x", "rt-acc-x", Now.AddHours(8)));
        File.WriteAllText(AccountFile, LinkHarness.Account("acc-x", "acc-x@example.com"));
        var label = links.Service.Read(CliStore.Inventory(UserProfile, null, [])[0]).Label;
        if (queued)
        {
            links.ClaudeRunning();
            await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true);
            links.Processes.Clear();
            await switcher.TickAsync();
        }
        else
        {
            Assert.Equal(CliSwitchOutcome.NeedsConfirmation, (await switcher.RequestAsync(first, automaticFrom: null, null, fromActivation: true)).Outcome);
        }

        Assert.Null(switcher.Pending);
        Assert.Contains("at-acc-x", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
        Assert.Contains("acc-x", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        var kept = Strings.Format("windows.cli.switch.untracked_kept", label);
        Assert.Equal(queued ? [Strings.Format("windows.cli.switch.deferred", "First"), kept] : [kept], _notifier.Shown.Select(a => a.Body));
        Assert.Equal(kept, switcher.ViewFor(first, null).Message);
    });

    [Fact]
    public Task AnAutomaticRequestThatNoLongerAppliesRedrawsTheWaitingSwitchItDropped() => OnSta(async () =>
    {
        var (switcher, first, second, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null);
        links.Processes.Clear();
        var changes = 0;
        switcher.Changed += () => changes++;

        // Auto-switch asks after it was switched off: nothing is switched, and the waiting switch it replaced is no
        // longer shown in the popover.
        Assert.Equal(CliSwitchRefusal.NoLongerApplies, (await switcher.RequestAsync(first, automaticFrom: second.Id, null)).Refusal);

        Assert.Null(switcher.Pending);
        Assert.True(changes > 0);
    });

    [Fact]
    public Task CancellingDropsTheWaitingSwitch() => OnSta(async () =>
    {
        var (switcher, first, _, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automaticFrom: null, null);

        switcher.Cancel();

        Assert.Null(switcher.Pending);
        Assert.Null(switcher.ViewFor(first, null).Message);
    });
}
