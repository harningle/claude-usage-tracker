using System.IO;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Profiles;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Setup;

/// <summary>
/// Saving a tested CLI link: what is persisted, the re-check against the tested account and
/// non-destructive failure. Logins and accounts are synthetic.
/// </summary>
public sealed class SetupServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string ClaudeDirectory => Path.Combine(UserProfile, ".claude");
    private string SettingsPath => Path.Combine(_temp.Path, "data", "settings.json");
    private SecretStore Secrets => new(Path.Combine(_temp.Path, "data", "secrets"));
    private UsageCacheStore Cache => new(Path.Combine(_temp.Path, "data", "cache"));

    public SetupServiceTests() => Directory.CreateDirectory(ClaudeDirectory);

    public void Dispose() => _temp.Dispose();

    private void WriteLogin(string access, string account)
    {
        File.WriteAllText(Path.Combine(ClaudeDirectory, ".credentials.json"),
            $$$"""{"claudeAiOauth":{"accessToken":"{{{access}}}","refreshToken":"rt-{{{access}}}","expiresAt":{{{Now.AddHours(8).ToUnixTimeMilliseconds()}}},"subscriptionType":"pro"}}""");
        File.WriteAllText(Path.Combine(UserProfile, ".claude.json"),
            $$$"""{"oauthAccount":{"accountUuid":"{{{account}}}","emailAddress":"{{{account}}}@example.com"}}""");
    }

    private (SetupService Service, AppSettings Settings, CliLinkService Links) Create(AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        var links = new CliLinkService(Secrets, _time, UserProfile);
        return (new SetupService(new SettingsStore(SettingsPath), settings, links, Cache, _time), settings, links);
    }

    private CliSnapshot Snapshot(CliLinkService links) => links.Read(CliStore.Inventory(UserProfile, null, [])[0]);

    [Fact]
    public void FirstSaveCreatesANamedActiveProfile()
    {
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create();

        var profile = service.Save(Snapshot(links), new UsageData { SessionPercentage = 12 }, Now);

        Assert.Contains(profile.Name, FunnyNames.Names);
        Assert.Equal(profile.Id, settings.ActiveProfileId);
        Assert.True(settings.HasCompletedSetup);
        Assert.Equal("acc-1", profile.CliLink?.Identity);
        var saved = new SettingsStore(SettingsPath).Load();
        Assert.Equal(profile.Id, saved.ActiveProfile?.Id);
        Assert.Equal(ClaudeDirectory, saved.ActiveProfile?.CliLink?.ConfigDirectory);
        Assert.True(Secrets.Exists(profile.Id));
        Assert.Equal(12, Cache.Load(profile.Id)?.Usage.SessionPercentage);
    }

    [Fact]
    public void SettingsFileHoldsNoTokens()
    {
        WriteLogin("sk-ant-oat01-synthetic-secret", "acc-1");
        var (service, _, links) = Create();

        service.Save(Snapshot(links), new UsageData(), Now);

        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RerunLinksTheExistingActiveProfile()
    {
        var existing = new ProfileSettings { Name = "Work" };
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create(new AppSettings { Profiles = [existing], ActiveProfileId = existing.Id });

        var profile = service.Save(Snapshot(links), new UsageData(), Now);

        Assert.Same(existing, profile);
        Assert.Single(settings.Profiles);
        Assert.Equal("Work", profile.Name);
    }

    [Fact]
    public void LoginThatChangedAccountSinceTheTestIsRefusedAndNothingChanges()
    {
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create();
        var tested = Snapshot(links);
        WriteLogin("at-2", "acc-2");

        Assert.Throws<SetupException>(() => service.Save(tested, new UsageData(), Now));

        Assert.Empty(settings.Profiles);
        Assert.False(settings.HasCompletedSetup);
        Assert.False(File.Exists(SettingsPath));
        Assert.Empty(Directory.Exists(Path.Combine(_temp.Path, "data", "secrets"))
            ? Directory.GetFiles(Path.Combine(_temp.Path, "data", "secrets")) : []);
    }

    [Fact]
    public void RotationOfTheSameAccountSavesTheNewerLogin()
    {
        WriteLogin("at-1", "acc-1");
        var (service, _, links) = Create();
        var tested = Snapshot(links);
        WriteLogin("at-2", "acc-1");

        var profile = service.Save(tested, new UsageData(), Now);

        Assert.Contains("at-2", Encoding.UTF8.GetString(Secrets.Read(profile.Id)!), StringComparison.Ordinal);
    }

    [Fact]
    public void CopyFailureRestoresTheSettings()
    {
        var existing = new ProfileSettings { Name = "Work" };
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create(new AppSettings { Profiles = [existing], ActiveProfileId = existing.Id });
        // A directory where the secret file belongs makes the encrypted write fail.
        Directory.CreateDirectory(Secrets.PathFor(existing.Id));

        Assert.Throws<SetupException>(() => service.Save(Snapshot(links), new UsageData(), Now));

        Assert.Null(existing.CliLink);
        Assert.False(settings.HasCompletedSetup);
        Assert.Null(new SettingsStore(SettingsPath).Load().ActiveProfile?.CliLink);
    }

    [Fact]
    public void SignedOutBeforeSavingIsRefused()
    {
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create();
        var tested = Snapshot(links);
        File.Delete(Path.Combine(ClaudeDirectory, ".credentials.json"));

        Assert.Throws<SetupException>(() => service.Save(tested, new UsageData(), Now));
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public void UnlinkRemovesTheLinkCopyAndCacheButNotClaudeCodesLogin()
    {
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create();
        var profile = service.Save(Snapshot(links), new UsageData { SessionPercentage = 1 }, Now);

        var copyKeptUntilChecksStop = false;
        service.Unlink(profile, () => copyKeptUntilChecksStop = Secrets.Exists(profile.Id));

        Assert.True(copyKeptUntilChecksStop);

        Assert.Null(profile.CliLink);
        Assert.Null(new SettingsStore(SettingsPath).Load().ActiveProfile?.CliLink);
        Assert.False(Secrets.Exists(profile.Id));
        Assert.Null(Cache.Load(profile.Id));
        Assert.Single(settings.Profiles);
        Assert.True(File.Exists(Path.Combine(ClaudeDirectory, ".credentials.json")));
    }
}
