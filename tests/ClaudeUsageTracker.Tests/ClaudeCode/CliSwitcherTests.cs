using System.IO;
using System.Windows.Threading;
using ClaudeUsageTracker.App.ClaudeCode;
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
            _settings, new RecordingNotifier(), new AutomationStateStore(Path.Combine(_temp.Path, "automation.json")), _time, _ => null, (_, _) => { });
        var switcher = new CliSwitcher(
            _settings, links.Service, new ProfileService(store, _settings, links.Service, links.Secrets, links.Lock, cache, history, _time), automation,
            () => CliStore.Inventory(UserProfile, null, [])[0], () => { }, _time, Dispatcher.CurrentDispatcher);
        return (switcher, profiles[0], profiles[1], links);
    }

    [Fact]
    public Task TheNewestRequestReplacesOneThatWaits() => OnSta(async () =>
    {
        var (switcher, first, second, links) = await Create();
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
        Assert.Null(switcher.ViewFor(first, null).Message);
        Assert.Contains("acc-b", File.ReadAllText(AccountFile), StringComparison.Ordinal);
        Assert.Contains("at-acc-b", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
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
