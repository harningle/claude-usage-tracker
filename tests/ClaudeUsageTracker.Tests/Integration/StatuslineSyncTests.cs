using System.IO;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Statusline;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Integration;

/// <summary>
/// Which Claude Code folder gets which profile's status line cache (plan.md section 2): only a folder whose
/// login is the profile's account, with usage only from that Claude Code login, and never a credential.
/// </summary>
public sealed class StatuslineSyncTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly string _data;
    private readonly string _home;
    private readonly StatuslineFiles _files;
    private readonly StatuslineSync _sync;
    private readonly AppSettings _settings = new() { StatuslineEnabled = true };
    private readonly Dictionary<Guid, CachedUsage> _fetched = [];

    public StatuslineSyncTests()
    {
        _data = Path.Combine(_temp.Path, "data");
        _home = Path.Combine(_temp.Path, "home");
        _files = new StatuslineFiles(_data);
        _sync = new StatuslineSync(_files, directory => CliStore.Read(directory, Now), _home);
    }

    public void Dispose() => _temp.Dispose();

    private string DefaultFolder => Path.Combine(_home, ".claude");

    /// <summary>A Claude Code folder signed in to an account, as synthetic files.</summary>
    private void SignIn(string folder, string uuid, bool isDefault = false)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".credentials.json"), LinkHarness.Credentials("access-" + uuid, "refresh-" + uuid, Now.AddHours(4)));
        File.WriteAllText(Path.Combine(isDefault ? _home : folder, ".claude.json"), LinkHarness.Account(uuid, uuid + "@example.com"));
    }

    private ProfileSettings Profile(string name, string folder, string uuid, bool isDefault = false)
    {
        var profile = new ProfileSettings
        {
            Name = name,
            RefreshInterval = 30,
            CliLink = new CliLink { ConfigDirectory = folder, IsDefaultDirectory = isDefault, AccountUuid = uuid, EmailAddress = uuid + "@example.com" },
        };
        _settings.Profiles.Add(profile);
        return profile;
    }

    /// <param name="account">The account it was fetched for; the profile's linked account unless given.</param>
    private void Fetched(ProfileSettings profile, double session, DateTimeOffset? at = null, string source = "oauthUsage", string? account = null) =>
        _fetched[profile.Id] = new CachedUsage
    {
        ProfileId = profile.Id,
        ObservedAt = at ?? Now,
        Source = source,
        AccountIdentity = account ?? profile.CliLink?.Identity,
        Usage = new UsageData
        {
            SessionPercentage = session, SessionResetTime = Now.AddHours(2), WeeklyPercentage = 12, WeeklyResetTime = Now.AddDays(3),
            CostUsed = 1.5m, CostLimit = 20m, CostCurrency = "USD",
        },
    };

    private void Sync() => _sync.Sync(_settings, id => _fetched.GetValueOrDefault(id));

    private string[] CacheFiles() =>
        Directory.Exists(StatuslinePaths.Directory(_data)) ? Directory.GetFiles(StatuslinePaths.Directory(_data), "*.cache.json") : [];

    [Fact]
    public void AFolderGetsTheUsageOfTheProfileWhoseAccountIsSignedInThere()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        Fetched(profile, 42);

        Sync();

        var cache = _files.ReadCache(DefaultFolder)!;
        Assert.Equal((profile.Id, "Personal", 30d, Now), (cache.ProfileId, cache.ProfileName, cache.RefreshIntervalSeconds, cache.ObservedAt));
        Assert.Equal((42d, Now.AddHours(2), 12d, Now.AddDays(3)), (cache.SessionPercentage, cache.SessionResetsAt, cache.WeeklyPercentage, cache.WeeklyResetsAt));
        Assert.Equal((1.5m, 20m, "USD"), (cache.CostUsed, cache.CostLimit, cache.CostCurrency));
        Assert.Equal(StatuslinePaths.Normalise(DefaultFolder), cache.ConfigDirectory);
        Assert.Single(CacheFiles());
    }

    [Fact]
    public void UsageFromAnotherSourceOrForAnotherAccountIsNotPassedOn()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);

        // claude.ai's last result, from before that link was removed: the profile now uses Claude Code, not yet read.
        Fetched(profile, 77, source: "claudeAi", account: "org-1");
        Sync();
        Assert.Equal(("Personal", null, null), (_files.ReadCache(DefaultFolder)!.ProfileName, _files.ReadCache(DefaultFolder)!.SessionPercentage, _files.ReadCache(DefaultFolder)!.ObservedAt));

        // Only a Claude Code result counts, whatever account another source's result names.
        Fetched(profile, 55, source: "claudeAi");
        Sync();
        Assert.Null(_files.ReadCache(DefaultFolder)!.SessionPercentage);

        // A Claude Code result read while the profile followed another account.
        Fetched(profile, 66, account: "acct-old");
        Sync();
        Assert.Null(_files.ReadCache(DefaultFolder)!.SessionPercentage);

        // A result of the folder's own login is passed on.
        Fetched(profile, 42);
        Sync();
        Assert.Equal(42d, _files.ReadCache(DefaultFolder)!.SessionPercentage);
    }

    [Fact]
    public void RelinkingTheProfileToAnotherAccountDropsTheFormerAccountsUsage()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        Fetched(profile, 42);
        Sync();
        Assert.Equal(42d, _files.ReadCache(DefaultFolder)!.SessionPercentage);

        // The folder is signed in to another account and the profile follows it now; nothing has been fetched for it yet,
        // and the former account's result is still the profile's latest.
        SignIn(DefaultFolder, "acct-b", isDefault: true);
        profile.CliLink = new CliLink { ConfigDirectory = DefaultFolder, IsDefaultDirectory = true, AccountUuid = "acct-b", EmailAddress = "acct-b@example.com" };
        Sync();

        var cache = _files.ReadCache(DefaultFolder)!;
        Assert.Equal("Personal", cache.ProfileName);
        Assert.Equal((null, null, null, null), (cache.ObservedAt, cache.SessionPercentage, cache.WeeklyPercentage, cache.CostUsed));

        Fetched(profile, 7);
        Sync();
        Assert.Equal(7d, _files.ReadCache(DefaultFolder)!.SessionPercentage);
    }

    [Fact]
    public void ARelinkWhoseWriteFailedStillDropsTheFormerAccountsUsage()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        Fetched(profile, 42);
        Sync();

        SignIn(DefaultFolder, "acct-b", isDefault: true);
        profile.CliLink = new CliLink { ConfigDirectory = DefaultFolder, IsDefaultDirectory = true, AccountUuid = "acct-b", EmailAddress = "acct-b@example.com" };
        // The file cannot be replaced while it is held open this way, so the former account's values stay in it.
        using (new FileStream(Assert.Single(CacheFiles()), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(Record.Exception(Sync) is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(42d, _files.ReadCache(DefaultFolder)!.SessionPercentage);
        Sync();
        Assert.Null(_files.ReadCache(DefaultFolder)!.SessionPercentage);
    }

    [Fact]
    public void UsageAnEarlierRunWroteIsNotCarriedOver()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        Fetched(profile, 42);
        Sync();

        // The file does not say whose usage it holds, so a new run starts it empty until its own first fetch.
        new StatuslineSync(_files, directory => CliStore.Read(directory, Now), _home).Sync(_settings, _ => null);

        Assert.Equal(("Personal", null), (_files.ReadCache(DefaultFolder)!.ProfileName, _files.ReadCache(DefaultFolder)!.SessionPercentage));
    }

    [Fact]
    public void TheCacheHoldsNoCredentialAndNoAccountIdentity()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        Fetched(Profile("Personal", DefaultFolder, "acct-a", isDefault: true), 42);

        Sync();

        var text = File.ReadAllText(Assert.Single(CacheFiles()));
        Assert.DoesNotContain("access-", text);
        Assert.DoesNotContain("refresh-", text);
        Assert.DoesNotContain("acct-a", text);
        Assert.DoesNotContain("example.com", text);
    }

    [Fact]
    public void EachFolderFollowsItsOwnAccount()
    {
        var launcher = Path.Combine(_home, ".claude-work");
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        SignIn(launcher, "acct-b");
        Fetched(Profile("Personal", DefaultFolder, "acct-a", isDefault: true), 10);
        Fetched(Profile("Work", launcher, "acct-b"), 80);

        Sync();

        Assert.Equal(("Personal", 10d), (_files.ReadCache(DefaultFolder)!.ProfileName, _files.ReadCache(DefaultFolder)!.SessionPercentage));
        Assert.Equal(("Work", 80d), (_files.ReadCache(launcher)!.ProfileName, _files.ReadCache(launcher)!.SessionPercentage));
    }

    [Fact]
    public void AfterClaudeCodeSwitchesAccountTheFoldersCacheFollowsTheNewProfile()
    {
        // Two profiles follow accounts that have both used the default folder; it holds B's login now.
        SignIn(DefaultFolder, "acct-b", isDefault: true);
        var a = Profile("A", DefaultFolder, "acct-a", isDefault: true);
        var b = Profile("B", DefaultFolder, "acct-b", isDefault: true);
        Fetched(a, 11);
        Fetched(b, 77);

        Sync();

        var cache = _files.ReadCache(DefaultFolder)!;
        Assert.Equal((b.Id, "B", 77d), (cache.ProfileId, cache.ProfileName, cache.SessionPercentage));

        // The folder is signed in to A again: A's values replace B's entirely.
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        _fetched.Remove(a.Id);
        Sync();
        cache = _files.ReadCache(DefaultFolder)!;
        Assert.Equal((a.Id, "A"), (cache.ProfileId, cache.ProfileName));
        Assert.Null(cache.SessionPercentage);
        Assert.Null(cache.ObservedAt);
    }

    [Fact]
    public void AFolderThatIsSignedOutOrOnAnUnknownAccountGetsNoCache()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        Fetched(profile, 42);
        Sync();
        Assert.Single(CacheFiles());

        SignIn(DefaultFolder, "acct-stranger", isDefault: true);
        Sync();
        Assert.Empty(CacheFiles());

        SignIn(DefaultFolder, "acct-a", isDefault: true);
        Sync();
        Assert.Single(CacheFiles());
        File.Delete(Path.Combine(DefaultFolder, ".credentials.json"));
        Sync();
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public void AProfileThatPollsClaudeAiGivesItsNameButNoUsage()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        profile.ClaudeAi = new ClaudeAiLink { OrganizationId = "org-1" };
        Fetched(profile, 42);

        Sync();

        // Its usage comes from a browser session whose account is not checked against the folder's login.
        var cache = _files.ReadCache(DefaultFolder)!;
        Assert.Equal("Personal", cache.ProfileName);
        Assert.Null(cache.ObservedAt);
        Assert.Null(cache.SessionPercentage);
    }

    [Fact]
    public void UsageFromAnEarlierRunIsNotPassedOffAsFetched()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);

        Sync();
        Assert.Null(_files.ReadCache(DefaultFolder)!.ObservedAt);

        Fetched(profile, 42);
        Sync();
        Assert.Equal(Now, _files.ReadCache(DefaultFolder)!.ObservedAt);

        // An older result arriving late does not replace a newer one.
        Fetched(profile, 5, Now.AddMinutes(-10));
        Sync();
        Assert.Equal(42, _files.ReadCache(DefaultFolder)!.SessionPercentage);
    }

    [Fact]
    public void ARenamedProfileAndAChangedIntervalReachTheCacheWithoutNewUsage()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        Fetched(profile, 42);
        Sync();

        profile.Name = "Renamed";
        profile.RefreshInterval = 120;
        _fetched.Clear();
        Sync();

        var cache = _files.ReadCache(DefaultFolder)!;
        Assert.Equal(("Renamed", 120d, 42d), (cache.ProfileName, cache.RefreshIntervalSeconds, cache.SessionPercentage));
    }

    [Fact]
    public void NothingIsRewrittenWhenNothingChanged()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        Fetched(Profile("Personal", DefaultFolder, "acct-a", isDefault: true), 42);
        Sync();
        var file = Assert.Single(CacheFiles());
        var written = File.GetLastWriteTimeUtc(file);
        Thread.Sleep(20);

        Sync();

        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void SwitchingTheStatusLineOffOrRemovingTheProfileDeletesItsCaches()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        var profile = Profile("Personal", DefaultFolder, "acct-a", isDefault: true);
        Fetched(profile, 42);
        Sync();

        _settings.Profiles.Remove(profile);
        Sync();
        Assert.Empty(CacheFiles());

        _settings.Profiles.Add(profile);
        Sync();
        Assert.Single(CacheFiles());
        _settings.StatuslineEnabled = false;
        Sync();
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public void CodexProfilesHaveNoStatusLine()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        Profile("Codex", DefaultFolder, "acct-a", isDefault: true).Provider = Provider.Codex.RawValue();

        Sync();

        Assert.Empty(CacheFiles());
    }

    [Fact]
    public void TheHelperShowsWhatWasSynced()
    {
        SignIn(DefaultFolder, "acct-a", isDefault: true);
        Fetched(Profile("Personal", DefaultFolder, "acct-a", isDefault: true), 42);
        _files.WriteConfig(new StatuslineConfig { ShowProfile = true, ShowProgressBar = false, ShowResetTime = false, ShowContext = false });
        Sync();

        var line = StatuslineCommand.Run("{}"u8, _data, null, _home, Now.AddSeconds(20), TimeZoneInfo.Utc);

        Assert.Contains("Personal", line);
        Assert.Contains("Usage: 42%", line);
    }
}

public class ShellEnvironmentTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void GitBashIsFoundWhereClaudeCodeLooksForIt()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Exists(string path) => files.Contains(Path.GetFullPath(path));

        Assert.False(ShellEnvironment.GitBashInstalled(Environment(("PATH", @"C:\Windows"), ("ProgramFiles", @"C:\Program Files")), Exists));

        files.Add(@"C:\Program Files\Git\bin\bash.exe");
        Assert.True(ShellEnvironment.GitBashInstalled(Environment(("ProgramFiles", @"C:\Program Files")), Exists));

        files.Clear();
        files.Add(@"D:\Tools\Git\cmd\git.exe");
        files.Add(@"D:\Tools\Git\bin\bash.exe");
        Assert.True(ShellEnvironment.GitBashInstalled(Environment(("PATH", @"C:\Windows;D:\Tools\Git\cmd")), Exists));

        files.Clear();
        files.Add(@"E:\portable\bash.exe");
        Assert.True(ShellEnvironment.GitBashInstalled(Environment(("CLAUDE_CODE_GIT_BASH_PATH", @"E:\portable\bash.exe")), Exists));
        Assert.False(ShellEnvironment.GitBashInstalled(Environment(("CLAUDE_CODE_GIT_BASH_PATH", @"E:\gone\bash.exe")), Exists));
    }

    [Theory]
    [InlineData(@"C:\Windows;C:\Users\u\.local\bin", true)]
    [InlineData(@"C:\Windows;c:\users\U\.LOCAL\bin\;C:\x", true)]
    [InlineData("C:\\Windows;\"C:\\Users\\u\\.local\\bin\"", true)]
    [InlineData(@"C:\Windows;C:\Users\u\.local", false)]
    [InlineData(@"C:\Windows;C:\Users\u\.local\bin2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ThePathIsComparedAsWindowsComparesPaths(string? path, bool expected) =>
        Assert.Equal(expected, ShellEnvironment.PathContains(path, @"C:\Users\u\.local\bin"));

    [Fact]
    public void VariablesInASavedPathAreExpanded()
    {
        var profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        Assert.True(ShellEnvironment.PathContains(@"C:\Windows;%USERPROFILE%\.local\bin", Path.Combine(profile, ".local", "bin")));
    }
}
