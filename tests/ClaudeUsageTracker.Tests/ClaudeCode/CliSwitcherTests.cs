using System.IO;
using System.Windows.Threading;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Tests.Notifications;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>The queue of switches that wait for Claude Code to close: the newest request is the one that counts.</summary>
public sealed class CliSwitcherTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _temp.Dispose();

    private string UserProfile => Path.Combine(_temp.Path, "home");

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

    private async Task<(CliSwitcher Switcher, ProfileSettings First, ProfileSettings Second, LinkHarness Links)> Create()
    {
        var claude = Path.Combine(UserProfile, ".claude");
        Directory.CreateDirectory(claude);
        var links = new LinkHarness(_temp.Path, UserProfile, _time);
        var settings = new AppSettings();
        var profiles = new List<ProfileSettings>();
        foreach (var (uuid, name) in new[] { ("acc-a", "First"), ("acc-b", "Second") })
        {
            File.WriteAllText(Path.Combine(claude, ".credentials.json"), LinkHarness.Credentials($"at-{uuid}", $"rt-{uuid}", Now.AddHours(8)));
            File.WriteAllText(Path.Combine(UserProfile, ".claude.json"), LinkHarness.Account(uuid, $"{uuid}@example.com"));
            var snapshot = links.Service.Read(CliStore.Inventory(UserProfile, null, [])[0]);
            var profile = new ProfileSettings { Name = name, CliLink = links.Service.CreateLink(snapshot) };
            await links.Service.WriteMirrorAsync(profile.Id, snapshot, CancellationToken.None);
            settings.Profiles.Add(profile);
            profiles.Add(profile);
        }

        var store = new SettingsStore(Path.Combine(_temp.Path, "settings.json"));
        var cache = new UsageCacheStore(Path.Combine(_temp.Path, "cache"));
        var history = new UsageHistoryService(new UsageHistoryStore(Path.Combine(_temp.Path, "history")), _time);
        var automation = new AutomationService(
            settings, new RecordingNotifier(), new AutomationStateStore(Path.Combine(_temp.Path, "automation.json")), _time, _ => null, (_, _) => { });
        var switcher = new CliSwitcher(
            settings, links.Service, new ProfileService(store, settings, links.Service, links.Secrets, links.Lock, cache, history, _time), automation,
            () => CliStore.Inventory(UserProfile, null, [])[0], () => { }, _time, Dispatcher.CurrentDispatcher);
        return (switcher, profiles[0], profiles[1], links);
    }

    [Fact]
    public Task TheNewestRequestReplacesOneThatWaits() => OnSta(async () =>
    {
        var (switcher, first, second, links) = await Create();
        links.ClaudeRunning();

        Assert.Equal(CliSwitchOutcome.Deferred, (await switcher.RequestAsync(first, automatic: false, null)).Outcome);
        Assert.Equal(first.Id, switcher.Pending?.TargetProfileId);

        await switcher.RequestAsync(second, automatic: true, null);

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
        await switcher.RequestAsync(first, automatic: false, null);
        links.Processes.Clear();

        // Claude Code has closed and the user asks for the second profile: the older wait for the first must not switch
        // the folder away from it at the next retry.
        Assert.Equal(CliSwitchOutcome.AlreadyInUse, (await switcher.RequestAsync(second, automatic: false, null)).Outcome);
        Assert.Null(switcher.Pending);
    });

    [Fact]
    public Task AnOlderSwitchThatEndsUpWaitingNeverReplacesANewerRequest() => OnSta(async () =>
    {
        // The switch to the first profile waits for the credential lock (held, as by a renewal); meanwhile Claude Code
        // starts and the second profile is requested.
        var (switcher, first, second, links) = await Create();
        var held = links.Lock.Acquire();
        var older = switcher.RequestAsync(first, automatic: false, null);
        links.ClaudeRunning();
        Assert.Equal(CliSwitchOutcome.Deferred, (await switcher.RequestAsync(second, automatic: true, null)).Outcome);

        held.Dispose();
        Assert.Equal(CliSwitchOutcome.Deferred, (await older).Outcome);

        // The newer request is the one that waits.
        Assert.Equal(second.Id, switcher.Pending?.TargetProfileId);
        Assert.False(switcher.ViewFor(first, null).Pending);
    });

    [Fact]
    public Task CancellingDropsTheWaitingSwitch() => OnSta(async () =>
    {
        var (switcher, first, _, links) = await Create();
        links.ClaudeRunning();
        await switcher.RequestAsync(first, automatic: false, null);

        switcher.Cancel();

        Assert.Null(switcher.Pending);
        Assert.Null(switcher.ViewFor(first, null).Message);
    });
}
