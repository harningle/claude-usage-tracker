using System.IO;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Profiles;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Setup;

/// <summary>
/// Saving tested configurations: what is persisted for each route, the re-check against the tested
/// account and non-destructive failure. Logins, keys and accounts are synthetic.
/// </summary>
public sealed class SetupServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private const string CodexAuth = """{"tokens":{"access_token":"codex-at","refresh_token":"codex-rt","account_id":"chatgpt-1"},"last_refresh":"2026-10-01T10:00:00Z"}""";

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string ClaudeDirectory => Path.Combine(UserProfile, ".claude");
    private string Data => Path.Combine(_temp.Path, "data");
    private string SettingsPath => Path.Combine(Data, "settings.json");
    private SecretStore Secrets => new(Path.Combine(Data, "secrets"));
    private UsageCacheStore Cache => new(Path.Combine(Data, "cache"));

    public SetupServiceTests() => Directory.CreateDirectory(ClaudeDirectory);

    public void Dispose() => _temp.Dispose();

    private void WriteLogin(string access, string account)
    {
        File.WriteAllText(Path.Combine(ClaudeDirectory, ".credentials.json"),
            $$$"""{"claudeAiOauth":{"accessToken":"{{{access}}}","refreshToken":"rt-{{{access}}}","expiresAt":{{{Now.AddHours(8).ToUnixTimeMilliseconds()}}},"subscriptionType":"pro"}}""");
        File.WriteAllText(Path.Combine(UserProfile, ".claude.json"),
            $$$"""{"oauthAccount":{"accountUuid":"{{{account}}}","emailAddress":"{{{account}}}@example.com"}}""");
    }

    private (SetupService Service, AppSettings Settings, LinkHarness Links) Create(AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        var links = new LinkHarness(Data, UserProfile, _time);
        return (new SetupService(new SettingsStore(SettingsPath), settings, links.Service, links.Secrets, links.Lock, Cache, _time), settings, links);
    }

    private CliSnapshot Snapshot(LinkHarness links) => links.Service.Read(CliStore.Inventory(UserProfile, null, [])[0]);

    private string Decrypted(Guid profileId) => Encoding.UTF8.GetString(Secrets.Read(profileId)!);

    [Fact]
    public async Task FirstSaveCreatesANamedActiveProfile()
    {
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create();

        var profile = await service.SaveCliAsync(service.CaptureTarget(), Snapshot(links), new UsageData { SessionPercentage = 12 }, Now, CancellationToken.None);

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
    public async Task SettingsFileHoldsNoTokens()
    {
        WriteLogin("sk-ant-oat01-synthetic-secret", "acc-1");
        var (service, _, links) = Create();

        await service.SaveCliAsync(service.CaptureTarget(), Snapshot(links), new UsageData(), Now, CancellationToken.None);

        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RerunLinksTheExistingActiveProfile()
    {
        var existing = new ProfileSettings { Name = "Work" };
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create(new AppSettings { Profiles = [existing], ActiveProfileId = existing.Id });

        var profile = await service.SaveCliAsync(service.CaptureTarget(), Snapshot(links), new UsageData(), Now, CancellationToken.None);

        Assert.Same(existing, profile);
        Assert.Single(settings.Profiles);
        Assert.Equal("Work", profile.Name);
    }

    [Fact]
    public async Task UsedElsewhereSurvivesARelinkOfTheSameAccountOnly()
    {
        var existing = new ProfileSettings
        {
            Name = "Work",
            CliLink = new CliLink { ConfigDirectory = ClaudeDirectory, AccountUuid = "acc-1", UsedElsewhere = true },
        };
        WriteLogin("at-1", "acc-1");
        var (service, _, links) = Create(new AppSettings { Profiles = [existing], ActiveProfileId = existing.Id });

        Assert.True((await service.SaveCliAsync(service.CaptureTarget(), Snapshot(links), new UsageData(), Now, CancellationToken.None)).CliLink?.UsedElsewhere);

        WriteLogin("at-2", "acc-2");
        Assert.False((await service.SaveCliAsync(service.CaptureTarget(), Snapshot(links), new UsageData(), Now, CancellationToken.None)).CliLink?.UsedElsewhere);
    }

    [Fact]
    public async Task LoginThatChangedAccountSinceTheTestIsRefusedAndNothingChanges()
    {
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create();
        var tested = Snapshot(links);
        WriteLogin("at-2", "acc-2");

        await Assert.ThrowsAsync<SetupException>(() => service.SaveCliAsync(service.CaptureTarget(), tested, new UsageData(), Now, CancellationToken.None));

        Assert.Empty(settings.Profiles);
        Assert.False(settings.HasCompletedSetup);
        Assert.False(File.Exists(SettingsPath));
        Assert.Empty(Directory.Exists(Path.Combine(Data, "secrets")) ? Directory.GetFiles(Path.Combine(Data, "secrets")) : []);
    }

    [Fact]
    public async Task RotationOfTheSameAccountSavesTheNewerLogin()
    {
        WriteLogin("at-1", "acc-1");
        var (service, _, links) = Create();
        var tested = Snapshot(links);
        WriteLogin("at-2", "acc-1");

        var profile = await service.SaveCliAsync(service.CaptureTarget(), tested, new UsageData(), Now, CancellationToken.None);

        Assert.Contains("at-2", Decrypted(profile.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyFailureRestoresTheSettings()
    {
        var existing = new ProfileSettings { Name = "Work" };
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create(new AppSettings { Profiles = [existing], ActiveProfileId = existing.Id });
        // A directory where the secret file belongs makes the encrypted write fail.
        Directory.CreateDirectory(Secrets.PathFor(existing.Id));

        await Assert.ThrowsAsync<SetupException>(() => service.SaveCliAsync(service.CaptureTarget(), Snapshot(links), new UsageData(), Now, CancellationToken.None));

        Assert.Null(existing.CliLink);
        Assert.False(settings.HasCompletedSetup);
        Assert.Null(new SettingsStore(SettingsPath).Load().ActiveProfile?.CliLink);
    }

    [Fact]
    public async Task SaveCancelledWhileWaitingToWriteTheKeyRestoresTheSettings()
    {
        var (service, settings, links) = Create();
        using var closing = new CancellationTokenSource();

        using (await links.Lock.AcquireAsync(CancellationToken.None))
        {
            // A renewal holds the credential lock, and the wizard closes while the key waits for it.
            var saving = service.SaveConsoleAsync(
                service.CaptureTarget(), new ConsoleOrganization("org-api", "API Org"), new ConsoleSecret("sk-ant-sid01-synthetic-console", null), closing.Token);
            await closing.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        }

        Assert.Empty(settings.Profiles);
        Assert.False(settings.HasCompletedSetup);
        Assert.Empty(new SettingsStore(SettingsPath).Load().Profiles);
        Assert.Empty(Directory.Exists(Path.Combine(Data, "secrets")) ? Directory.GetFiles(Path.Combine(Data, "secrets")) : []);
    }

    [Fact]
    public async Task SignedOutBeforeSavingIsRefused()
    {
        WriteLogin("at-1", "acc-1");
        var (service, settings, links) = Create();
        var tested = Snapshot(links);
        File.Delete(Path.Combine(ClaudeDirectory, ".credentials.json"));

        await Assert.ThrowsAsync<SetupException>(() => service.SaveCliAsync(service.CaptureTarget(), tested, new UsageData(), Now, CancellationToken.None));
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public async Task ClaudeAiSaveKeepsOnlyTheOrganizationAndCachesUsage()
    {
        var (service, settings, _) = Create();

        var profile = await service.SaveClaudeAiAsync(service.CaptureTarget(), new ClaudeOrganization("org-1", "Example Team"), new UsageData { SessionPercentage = 7 }, Now);

        Assert.Equal("org-1", profile.ClaudeAi?.OrganizationId);
        Assert.Equal("Example Team", profile.ClaudeAi?.OrganizationName);
        Assert.Equal(Provider.Anthropic, profile.ProviderKind);
        Assert.Equal(UsageSourceKind.ClaudeAi, profile.UsageSource);
        Assert.Equal(profile.Id, settings.ActiveProfileId);
        // The session stays in the browser; nothing secret is written.
        Assert.False(Secrets.Exists(profile.Id));
        Assert.Equal("org-1", Cache.Load(profile.Id)?.AccountIdentity);
    }

    [Fact]
    public async Task ConsoleSaveEncryptsTheKeyAndKeepsTheOtherSecrets()
    {
        WriteLogin("at-1", "acc-1");
        var (service, _, links) = Create();
        var profile = await service.SaveCliAsync(service.CaptureTarget(), Snapshot(links), new UsageData(), Now, CancellationToken.None);

        await service.SaveConsoleAsync(service.CaptureTarget(), new ConsoleOrganization("org-api", "API Org"),
            new ConsoleSecret("sk-ant-sid01-synthetic-console", Now.AddDays(28)), CancellationToken.None);

        var bundle = links.Secrets.Load(profile.Id);
        Assert.Equal("sk-ant-sid01-synthetic-console", bundle.Console?.SessionKey);
        Assert.NotNull(bundle.Cli);
        Assert.Equal("org-api", profile.Console?.OrganizationId);
        Assert.Equal(Now.AddDays(28), profile.Console?.SessionKeyExpiry);
        Assert.DoesNotContain("synthetic-console", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-console", File.ReadAllText(Secrets.PathFor(profile.Id), Encoding.Latin1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexAuthFileSaveSwitchesTheProviderAndStoresNoSecret()
    {
        var (service, _, _) = Create();
        var credentials = CodexAuthJson.Parse(CodexAuth)!;

        var profile = await service.SaveCodexAsync(service.CaptureTarget(), Path.Combine(UserProfile, ".codex"), null, credentials, new UsageData(), Now, CancellationToken.None);

        Assert.Equal(Provider.Codex, profile.ProviderKind);
        Assert.Equal(CodexLink.AuthFileSource, profile.Codex?.Source);
        Assert.Equal("chatgpt-1", profile.Codex?.AccountId);
        Assert.Equal(UsageSourceKind.Codex, profile.UsageSource);
        Assert.False(Secrets.Exists(profile.Id));
    }

    [Fact]
    public async Task PastedCodexCredentialsAreEncryptedAndRemovedWhenFollowingTheCliAgain()
    {
        var (service, _, links) = Create();
        var credentials = CodexAuthJson.Parse(CodexAuth)!;

        var profile = await service.SaveCodexAsync(service.CaptureTarget(), Path.Combine(UserProfile, ".codex"), CodexAuth, credentials, new UsageData(), Now, CancellationToken.None);

        Assert.True(profile.Codex?.IsManual);
        Assert.Equal(CodexAuth, links.Secrets.Load(profile.Id).CodexAuthJson);
        Assert.DoesNotContain("codex-rt", File.ReadAllText(SettingsPath), StringComparison.Ordinal);

        await service.SaveCodexAsync(service.CaptureTarget(), Path.Combine(UserProfile, ".codex"), null, credentials, new UsageData(), Now, CancellationToken.None);

        Assert.False(Secrets.Exists(profile.Id));
    }
}
