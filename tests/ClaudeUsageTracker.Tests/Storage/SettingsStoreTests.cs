using System.IO;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Storage;

/// <summary>
/// Windows equivalents of upstream's profile and settings persistence cases
/// (<c>ProfileKeychainMigrationTests</c>, <c>ProfileProviderTests</c>, <c>SharedDataStoreTests</c>), plus the
/// plan's rule that damaged storage is reported, never silently reset.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string SettingsPath => Path.Combine(_temp.Path, "settings.json");

    public void Dispose() => _temp.Dispose();

    private AppSettings LoadJson(string json)
    {
        File.WriteAllText(SettingsPath, json);
        return new SettingsStore(SettingsPath).Load();
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Empty(settings.Profiles);
        Assert.Equal(PopoverTimeDisplay.ResetTime, settings.TimeDisplay);
        Assert.Equal(TimeFormatPreference.System, settings.TimeFormat);
        Assert.False(settings.HasCompletedSetup);
    }

    [Fact]
    public void HasCompletedSetup()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { HasCompletedSetup = false });
        Assert.False(store.Load().HasCompletedSetup);

        store.Save(new AppSettings { HasCompletedSetup = true });
        Assert.True(store.Load().HasCompletedSetup);
    }

    [Fact]
    public void DecodeMinimalLegacyProfileAppliesDefaults()
    {
        var settings = LoadJson("""{"version": 1, "profiles": [{"id": "11111111-2222-3333-4444-555555555555", "name": "Minimal"}]}""");

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("Minimal", profile.Name);
        Assert.Equal(30.0, profile.RefreshInterval);
        Assert.True(profile.IsSelectedForDisplay);
        Assert.Null(profile.CliLink);
        Assert.Equal(Provider.Anthropic, profile.ProviderKind);
        Assert.True(profile.IconConfig.Metric("session")!.IsEnabled);
        Assert.False(profile.IconConfig.Metric("week")!.IsEnabled);
        Assert.True(profile.IconConfig.ShowPaceMarker);
    }

    [Fact]
    public void UnknownProviderRawValueDegradesToAnthropicButIsKept()
    {
        var store = new SettingsStore(SettingsPath);
        File.WriteAllText(SettingsPath,
            """{"profiles": [{"id": "11111111-2222-3333-4444-555555555555", "name": "Future", "provider": "some-future-provider"}]}""");

        var settings = store.Load();
        Assert.Equal(Provider.Anthropic, settings.Profiles[0].ProviderKind);

        store.Save(settings);
        Assert.Contains("some-future-provider", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void NonStringProviderValueDegradesToAnthropic()
    {
        var settings = LoadJson("""{"profiles": [{"id": "11111111-2222-3333-4444-555555555555", "name": "Odd", "provider": 42}]}""");

        Assert.Equal(Provider.Anthropic, settings.Profiles[0].ProviderKind);
        Assert.Equal("anthropic", settings.Profiles[0].Provider);
    }

    [Fact]
    public void CodexProviderRoundTrips()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Profiles = [new ProfileSettings { Name = "Codex", Provider = "codex" }] });

        Assert.Equal(Provider.Codex, store.Load().Profiles[0].ProviderKind);
    }

    [Fact]
    public void UnknownFieldsSurviveASave()
    {
        var store = new SettingsStore(SettingsPath);
        File.WriteAllText(SettingsPath, """
            {"version": 1, "futureSetting": {"nested": [1, 2]},
             "profiles": [{"id": "11111111-2222-3333-4444-555555555555", "name": "P", "notificationSettings": {"enabled": true},
                           "iconConfig": {"metrics": [{"metricType": "session", "isEnabled": true, "futureFlag": "x"}]}}]}
            """);

        store.Save(store.Load());
        var text = File.ReadAllText(SettingsPath);

        Assert.Contains("futureSetting", text, StringComparison.Ordinal);
        Assert.Contains("notificationSettings", text, StringComparison.Ordinal);
        Assert.Contains("futureFlag", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("""{"profiles": [{"id": "11111111-2222-3333-4444-555555555555", "name": "P", "refreshInterval": "fast"}]}""")]
    [InlineData("""{"profiles": [{"name": "No id"}]}""")]
    [InlineData("""{"profiles": [{"id": "11111111-2222-3333-4444-555555555555", "name": "A"}, {"id": "11111111-2222-3333-4444-555555555555", "name": "B"}]}""")]
    public void DamagedSettingsAreReportedAndLeftUnchanged(string json)
    {
        File.WriteAllText(SettingsPath, json);

        Assert.Throws<SettingsStoreException>(() => new SettingsStore(SettingsPath).Load());
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void NewerFormatIsReported()
    {
        var error = Assert.Throws<SettingsStoreException>(() => LoadJson("""{"version": 2, "profiles": []}"""));

        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkRoundTripsWithoutSecrets()
    {
        var store = new SettingsStore(SettingsPath);
        var profile = new ProfileSettings
        {
            Name = "Work",
            CliLink = new CliLink
            {
                ConfigDirectory = @"C:\Users\someone\.claude",
                IsDefaultDirectory = true,
                AccountUuid = "acc-1",
                EmailAddress = "user@example.com",
                LinkedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            },
        };
        store.Save(new AppSettings { Profiles = [profile], ActiveProfileId = profile.Id });

        var loaded = store.Load();

        Assert.Equal(profile.Id, loaded.ActiveProfile?.Id);
        Assert.Equal("acc-1", loaded.ActiveProfile?.CliLink?.Identity);
        Assert.Equal(@"C:\Users\someone\.claude", loaded.ActiveProfile?.CliLink?.ConfigDirectory);
    }

    /// <summary>Windows equivalent of upstream <c>testDecodesOldPlistWithoutCustomKeychainField</c>: Milestone 2 settings load unchanged.</summary>
    [Fact]
    public void MilestoneTwoSettingsWithoutTheNewerLinksLoad()
    {
        var settings = LoadJson("""
            {"version":1,"profiles":[{"id":"11111111-2222-3333-4444-555555555555","name":"Work",
              "cliLink":{"configDirectory":"C:\\Users\\someone\\.claude","isDefaultDirectory":true,"accountUuid":"acc-1"}}],
             "activeProfileId":"11111111-2222-3333-4444-555555555555"}
            """);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("acc-1", profile.CliLink?.Identity);
        Assert.False(profile.CliLink?.UsedElsewhere);
        Assert.Null(profile.ClaudeAi);
        Assert.Null(profile.Console);
        Assert.Null(profile.Codex);
        Assert.False(settings.IsMultiProfile);
        Assert.Empty(settings.PendingCleanups);
        Assert.Equal("concentric", settings.MultiProfileDisplayConfig.IconStyle);
    }

    /// <summary>
    /// Windows equivalent of upstream <c>testHasAnyCredentialsRecognizesCustomKeychainPin</c>: a linked Claude Code
    /// folder is a usage source, and upstream's precedence picks the claude.ai session first.
    /// </summary>
    [Fact]
    public void UsageSourceFollowsUpstreamPrecedence()
    {
        var profile = new ProfileSettings { Name = "P" };
        Assert.Null(profile.UsageSource);

        profile.CliLink = new CliLink { ConfigDirectory = @"C:\Users\someone\.claude" };
        Assert.Equal(UsageSourceKind.ClaudeCode, profile.UsageSource);

        profile.ClaudeAi = new ClaudeAiLink { OrganizationId = "org-1" };
        Assert.Equal(UsageSourceKind.ClaudeAi, profile.UsageSource);

        // A Codex profile never uses Anthropic credentials, nor the other way round.
        profile.Provider = Provider.Codex.RawValue();
        Assert.Null(profile.UsageSource);
        profile.Codex = new CodexLink();
        Assert.Equal(UsageSourceKind.Codex, profile.UsageSource);
        profile.Provider = Provider.Anthropic.RawValue();
        Assert.Equal(UsageSourceKind.ClaudeAi, profile.UsageSource);
    }

    [Fact]
    public void EveryLinkAndDisplayOptionRoundTripsWithoutSecrets()
    {
        var store = new SettingsStore(SettingsPath);
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var profile = new ProfileSettings
        {
            Name = "Mixed",
            ClaudeAi = new ClaudeAiLink { OrganizationId = "org-1", OrganizationName = "Team", LinkedAt = at },
            Console = new ConsoleLink { OrganizationId = "org-api", SessionKeyExpiry = at.AddDays(28), LinkedAt = at },
            Codex = new CodexLink { Source = CodexLink.ManualSource, AccountId = "chatgpt-1", Email = "user@example.com", LinkedAt = at },
            CliLink = new CliLink { ConfigDirectory = @"C:\Users\someone\.claude", AccountUuid = "acc-1", UsedElsewhere = true },
            IsSelectedForDisplay = false,
        };
        var cleanup = new PendingCleanup { ProfileId = Guid.NewGuid(), Browser = true };
        store.Save(new AppSettings
        {
            Profiles = [profile],
            IsMultiProfile = true,
            MultiProfileDisplayConfig = new MultiProfileDisplayConfig { IconStyle = "compact", ShowWeek = false, ShowActiveProfileIndicator = true },
            PendingCleanups = [cleanup],
        });

        var loaded = store.Load();
        var round = Assert.Single(loaded.Profiles);

        Assert.Equal("Team", round.ClaudeAi?.OrganizationName);
        Assert.Equal(at.AddDays(28), round.Console?.SessionKeyExpiry);
        Assert.True(round.Codex?.IsManual);
        Assert.Equal("chatgpt-1", round.Codex?.Identity);
        Assert.True(round.CliLink?.UsedElsewhere);
        Assert.False(round.IsSelectedForDisplay);
        Assert.True(loaded.IsMultiProfile);
        Assert.Equal("compact", loaded.MultiProfileDisplayConfig.IconStyle);
        Assert.True(loaded.MultiProfileDisplayConfig.ShowActiveProfileIndicator);
        Assert.Equal(cleanup.ProfileId, Assert.Single(loaded.PendingCleanups).ProfileId);
        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("sessionKey\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Windows equivalents of upstream's <c>ClaudeUsage</c> Codable cases and the corrupt-cache case: the
/// cache is a separate, re-fetchable file whose loss never costs a profile.
/// </summary>
public sealed class UsageCacheStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly Guid _profileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public void Dispose() => _temp.Dispose();

    private UsageCacheStore Store => new(_temp.Path);

    private CachedUsage Cache(UsageData usage) => new()
    {
        ProfileId = _profileId,
        ObservedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        Source = "oauthUsage",
        AccountIdentity = "acc-1",
        Usage = usage,
    };

    [Fact]
    public void EncodeDecode()
    {
        var usage = new UsageData
        {
            SessionPercentage = 45.5,
            SessionResetTime = new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero),
            WeeklyPercentage = 50,
            WeeklyResetTime = new DateTimeOffset(2026, 10, 5, 2, 0, 0, TimeSpan.Zero),
            CostUsed = 1.86m,
            CostLimit = 5.00m,
            CostCurrency = "USD",
        };
        Store.Save(Cache(usage));

        Assert.Equal(Cache(usage), Store.Load(_profileId)! with { Extra = null });
    }

    [Fact]
    public void ExtraUsageKeepsItsDecimalPlacesAcrossARestart()
    {
        Store.Save(Cache(new UsageData { CostUsed = 0.00m, CostLimit = 5.00m, CostCurrency = "USD" }));

        var loaded = Store.Load(_profileId)!.Usage;
        Assert.Equal("0.00", loaded.CostUsed!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("5.00", loaded.CostLimit!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EncodeDecodeFableFields()
    {
        var usage = new UsageData
        {
            FableWeeklyTokensUsed = 123_456,
            FableWeeklyPercentage = 42.5,
            FableWeeklyResetTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
        };
        Store.Save(Cache(usage));

        var loaded = Store.Load(_profileId)!.Usage;
        Assert.Equal(123_456, loaded.FableWeeklyTokensUsed);
        Assert.Equal(42.5, loaded.FableWeeklyPercentage);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), loaded.FableWeeklyResetTime);
    }

    [Fact]
    public void PlanAndCreditsRoundTrip()
    {
        Store.Save(Cache(new UsageData { PlanType = "plus", CreditsBalance = 12.5m, CreditsUnlimited = false }));

        var loaded = Store.Load(_profileId)!.Usage;
        Assert.Equal("plus", loaded.PlanType);
        Assert.Equal(12.5m, loaded.CreditsBalance);
        Assert.False(loaded.CreditsUnlimited);
    }

    [Fact]
    public void OlderCacheWithoutNewerKeysLoadsWithThemMissing()
    {
        File.WriteAllText(Store.PathFor(_profileId), """
            {"version": 1, "profileId": "11111111-2222-3333-4444-555555555555", "observedAt": "2026-10-01T12:00:00+00:00",
             "source": "oauthUsage", "usage": {"sessionPercentage": 24, "weeklyPercentage": 45, "opusWeeklyPercentage": 1}}
            """);

        var loaded = Store.Load(_profileId)!.Usage;

        Assert.Equal(24, loaded.SessionPercentage);
        Assert.Equal(1, loaded.OpusWeeklyPercentage);
        Assert.Null(loaded.DesignWeeklyPercentage);
        Assert.Null(loaded.FableWeeklyResetTime);
        Assert.Null(loaded.PlanType);
        Assert.Null(loaded.CreditsBalance);
        Assert.Null(loaded.CreditsUnlimited);
    }

    [Theory]
    [InlineData("""{"version": 1, "profileId": "11111111-2222-3333-4444-555555555555", "observedAt": "2026-10-01T12:00:00Z", "source": "x", "usage": {"sessionTokensUsed": "not-a-number"}}""")]
    [InlineData("not json")]
    [InlineData("""{"version": 2, "profileId": "11111111-2222-3333-4444-555555555555", "observedAt": "2026-10-01T12:00:00Z", "source": "x", "usage": {}}""")]
    [InlineData("""{"version": 1, "profileId": "99999999-2222-3333-4444-555555555555", "observedAt": "2026-10-01T12:00:00Z", "source": "x", "usage": {}}""")]
    public void UnusableCacheDegradesToNull(string json)
    {
        File.WriteAllText(Store.PathFor(_profileId), json);

        Assert.Null(Store.Load(_profileId));
    }

    [Fact]
    public void MissingCacheIsNull() => Assert.Null(Store.Load(_profileId));
}
