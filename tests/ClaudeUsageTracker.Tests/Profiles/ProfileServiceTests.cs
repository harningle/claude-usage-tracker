using System.IO;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.History;
using ClaudeUsageTracker.Core.Profiles;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Profiles;

/// <summary>
/// The profile lifecycle (upstream <c>ProfileManager</c>): create, rename, activate, display selection,
/// and recoverable removal of profiles and links. Activating never touches Claude Code's files.
/// </summary>
public sealed class ProfileServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _temp.Dispose();

    private string Data => Path.Combine(_temp.Path, "data");
    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string SettingsPath => Path.Combine(Data, "settings.json");
    private UsageCacheStore Cache => new(Path.Combine(Data, "cache"));
    private UsageHistoryStore HistoryStore => new(Path.Combine(Data, "history"));

    private (ProfileService Service, AppSettings Settings, LinkHarness Links) Create(params ProfileSettings[] profiles)
    {
        var settings = new AppSettings { Profiles = [.. profiles], ActiveProfileId = profiles.FirstOrDefault()?.Id };
        var links = new LinkHarness(Data, UserProfile, _time);
        var service = new ProfileService(new SettingsStore(SettingsPath), settings, links.Service, links.Secrets, links.Lock, Cache,
            new UsageHistoryService(HistoryStore, _time), _time);
        return (service, settings, links);
    }

    private static Task<bool> Cleared(Guid _) => Task.FromResult(true);

    private void SeedData(LinkHarness links, Guid profileId)
    {
        var bundle = links.Secrets.Load(profileId);
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic", null);
        links.Secrets.Save(profileId, bundle);
        Cache.Save(new CachedUsage { ProfileId = profileId, ObservedAt = Now, Source = "claudeAi", Usage = new UsageData() });
        var history = new UsageHistoryData();
        history.Snapshots.Add(new UsageSnapshot { Timestamp = Now, ResetType = ResetType.SessionReset, TriggeringResetTime = Now, SessionPercentage = 5 });
        HistoryStore.Save(profileId, history);
    }

    [Fact]
    public void CreatedProfileHasUpstreamDefaultsAndIsNotActivated()
    {
        var first = new ProfileSettings { Name = "First" };
        var (service, settings, _) = Create(first);

        var created = service.Create(null, Provider.Codex);

        Assert.Contains(created.Name, FunnyNames.Names);
        Assert.Equal(Provider.Codex, created.ProviderKind);
        Assert.Equal(30, created.RefreshInterval);
        Assert.Equal(first.Id, settings.ActiveProfileId);
        Assert.Equal(2, new SettingsStore(SettingsPath).Load().Profiles.Count);
    }

    [Fact]
    public void RenameIgnoresBlankNamesAndTrims()
    {
        var profile = new ProfileSettings { Name = "Work" };
        var (service, _, _) = Create(profile);

        service.Rename(profile, "   ");
        Assert.Equal("Work", profile.Name);

        service.Rename(profile, "  Personal ");
        Assert.Equal("Personal", new SettingsStore(SettingsPath).Load().Profiles[0].Name);
    }

    [Fact]
    public void ActivationChangesOnlyWhichProfileIsViewed()
    {
        var first = new ProfileSettings { Name = "First" };
        var second = new ProfileSettings { Name = "Second" };
        var (service, settings, _) = Create(first, second);
        var claude = Path.Combine(UserProfile, ".claude");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"untouched\"}}");

        service.Activate(second);

        Assert.Equal(second.Id, settings.ActiveProfileId);
        Assert.Equal(Now, second.LastUsedAt);
        Assert.Equal("{\"claudeAiOauth\":{\"accessToken\":\"untouched\"}}", File.ReadAllText(Path.Combine(claude, ".credentials.json")));
    }

    [Fact]
    public void AtLeastOneProfileStaysDisplayed()
    {
        var first = new ProfileSettings { Name = "First" };
        var second = new ProfileSettings { Name = "Second" };
        var (service, settings, _) = Create(first, second);
        service.SetMultiProfile(true);

        Assert.True(service.ToggleDisplayed(first));
        Assert.False(service.ToggleDisplayed(second));

        Assert.Equal([second], settings.DisplayedProfiles);
    }

    [Fact]
    public void DisplayedProfilesFollowTheMode()
    {
        var first = new ProfileSettings { Name = "First", IsSelectedForDisplay = false };
        var second = new ProfileSettings { Name = "Second" };
        var (service, settings, _) = Create(first, second);

        Assert.Equal([first], settings.DisplayedProfiles);
        service.SetMultiProfile(true);
        Assert.Equal([second], settings.DisplayedProfiles);
        Assert.Equal("multi", new SettingsStore(SettingsPath).Load().ProfileDisplayMode);
    }

    [Fact]
    public async Task LastProfileCannotBeDeleted()
    {
        var only = new ProfileSettings { Name = "Only" };
        var (service, settings, _) = Create(only);

        await Assert.ThrowsAsync<SetupException>(() => service.DeleteAsync(only, _ => Task.CompletedTask, Cleared));

        Assert.Single(settings.Profiles);
    }

    [Fact]
    public async Task DeletionStopsPollingFirstThenRemovesEverythingOfTheProfile()
    {
        var keep = new ProfileSettings { Name = "Keep" };
        var remove = new ProfileSettings { Name = "Remove" };
        var (service, settings, links) = Create(remove, keep);
        SeedData(links, remove.Id);
        var dataPresentWhenStopped = false;

        var message = await service.DeleteAsync(remove, id =>
        {
            dataPresentWhenStopped = links.Secrets.Exists(id);
            return Task.CompletedTask;
        }, Cleared);

        Assert.Null(message);
        Assert.True(dataPresentWhenStopped);
        Assert.Equal([keep], settings.Profiles);
        Assert.Equal(keep.Id, settings.ActiveProfileId);
        Assert.False(links.Secrets.Exists(remove.Id));
        Assert.Null(Cache.Load(remove.Id));
        Assert.False(File.Exists(HistoryStore.PathFor(remove.Id)));
        Assert.Empty(new SettingsStore(SettingsPath).Load().PendingCleanups);
    }

    [Fact]
    public async Task DataThatCannotBeRemovedYetIsListedAndRemovedAtTheNextStart()
    {
        var keep = new ProfileSettings { Name = "Keep" };
        var remove = new ProfileSettings { Name = "Remove" };
        var (service, _, links) = Create(remove, keep);
        SeedData(links, remove.Id);

        // The browser is still running, so its folder cannot be deleted yet.
        var message = await service.DeleteAsync(remove, _ => Task.CompletedTask, _ => Task.FromResult(false));

        Assert.NotNull(message);
        var pending = Assert.Single(new SettingsStore(SettingsPath).Load().PendingCleanups);
        Assert.Equal(remove.Id, pending.ProfileId);
        Assert.True(pending.Browser);
        Assert.False(pending.Secrets);

        // The next start, with the settings as saved: the browser folder can go now.
        var reloaded = new ProfileService(new SettingsStore(SettingsPath), new SettingsStore(SettingsPath).Load(), links.Service, links.Secrets,
            links.Lock, Cache, new UsageHistoryService(HistoryStore, _time), _time);

        Assert.Null(await reloaded.RunPendingCleanupAsync(Cleared));
        Assert.Empty(new SettingsStore(SettingsPath).Load().PendingCleanups);
    }

    [Fact]
    public async Task UnlinkingClaudeAiClearsTheBrowserButKeepsTheOtherLinks()
    {
        var profile = new ProfileSettings
        {
            Name = "Mixed",
            ClaudeAi = new ClaudeAiLink { OrganizationId = "org-1" },
            Console = new ConsoleLink { OrganizationId = "org-api" },
        };
        var (service, _, links) = Create(profile);
        SeedData(links, profile.Id);
        var cleared = new List<Guid>();

        await service.UnlinkClaudeAiAsync(profile, () => { }, id =>
        {
            cleared.Add(id);
            return Task.FromResult(true);
        });

        Assert.Null(profile.ClaudeAi);
        Assert.NotNull(profile.Console);
        Assert.Equal([profile.Id], cleared);
        Assert.Equal("sk-ant-sid01-synthetic", links.Secrets.Load(profile.Id).Console?.SessionKey);
        Assert.Null(Cache.Load(profile.Id));
    }

    [Fact]
    public async Task UnlinkingTheConsoleRemovesOnlyItsKeyAndBilling()
    {
        var profile = new ProfileSettings { Name = "Mixed", Console = new ConsoleLink { OrganizationId = "org-api" } };
        var (service, _, links) = Create(profile);
        var bundle = links.Secrets.Load(profile.Id);
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic", null);
        bundle.CodexAuthJson = "{}";
        links.Secrets.Save(profile.Id, bundle);

        await service.UnlinkConsoleAsync(profile, () => { });

        Assert.Null(profile.Console);
        Assert.Null(links.Secrets.Load(profile.Id).Console);
        Assert.Equal("{}", links.Secrets.Load(profile.Id).CodexAuthJson);
    }

    [Fact]
    public async Task UnlinkingClaudeCodeRemovesTheCopyAfterChecksStopButNotClaudeCodesLogin()
    {
        var claude = Path.Combine(UserProfile, ".claude");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, ".credentials.json"), LinkHarness.Credentials("at-1", "rt-1", Now.AddHours(8)));
        var profile = new ProfileSettings { Name = "Cli" };
        var (service, _, links) = Create(profile);
        var snapshot = links.Service.Read(ClaudeUsageTracker.App.ClaudeCode.CliStore.Inventory(UserProfile, null, [])[0]);
        profile.CliLink = links.Service.CreateLink(snapshot);
        await links.Service.WriteMirrorAsync(profile.Id, snapshot, CancellationToken.None);
        var copyKeptUntilChecksStop = false;

        await service.UnlinkCliAsync(profile, () => copyKeptUntilChecksStop = links.Secrets.Exists(profile.Id));

        Assert.True(copyKeptUntilChecksStop);
        Assert.Null(profile.CliLink);
        Assert.False(links.Secrets.Exists(profile.Id));
        Assert.True(File.Exists(Path.Combine(claude, ".credentials.json")));
    }

    [Fact]
    public async Task CancelledSetupBrowserDataIsDiscardedOnlyWithoutAClaudeAiLink()
    {
        var linked = new ProfileSettings { Name = "Linked", ClaudeAi = new ClaudeAiLink { OrganizationId = "org-1" } };
        var (service, _, _) = Create(linked);
        var cleared = new List<Guid>();
        Task<bool> Clear(Guid id)
        {
            cleared.Add(id);
            return Task.FromResult(true);
        }

        var unsaved = Guid.NewGuid();
        await service.DiscardUnlinkedBrowserAsync(linked.Id, Clear);
        await service.DiscardUnlinkedBrowserAsync(unsaved, Clear);

        Assert.Equal([unsaved], cleared);
    }

    [Fact]
    public void FailedSaveUndoesTheChange()
    {
        var profile = new ProfileSettings { Name = "Work" };
        var (service, _, _) = Create(profile);
        // A directory where the settings file belongs makes the save fail.
        Directory.CreateDirectory(SettingsPath);

        Assert.Throws<SetupException>(() => service.Rename(profile, "Personal"));

        Assert.Equal("Work", profile.Name);
    }
}
