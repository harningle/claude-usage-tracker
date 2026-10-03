using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.Tests.Statusline;

/// <summary>A whole status line invocation: stdin and the tracker's files in, one line out, nothing written.</summary>
public sealed partial class StatuslineCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly string _data;
    private readonly string _home;

    public StatuslineCommandTests()
    {
        _data = Path.Combine(_temp.Path, "data");
        _home = Path.Combine(_temp.Path, "home");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_home);
    }

    public void Dispose() => _temp.Dispose();

    private string DefaultFolder => Path.Combine(_home, ".claude");

    private string Run(string stdin, string? configDirOverride = null) =>
        Plain().Replace(StatuslineCommand.Run(Encoding.UTF8.GetBytes(stdin), _data, configDirOverride, _home, Now, TimeZoneInfo.Utc), "");

    private void Apply(StatuslineConfig config)
    {
        Directory.CreateDirectory(StatuslinePaths.Directory(_data));
        File.WriteAllBytes(StatuslinePaths.ConfigFile(_data), JsonSerializer.SerializeToUtf8Bytes(config, StatuslineJson.Default.StatuslineConfig));
    }

    private void Cache(string folder, string profile, double session, double ageSeconds = 0)
    {
        Directory.CreateDirectory(StatuslinePaths.Directory(_data));
        File.WriteAllBytes(StatuslinePaths.CacheFile(_data, folder), JsonSerializer.SerializeToUtf8Bytes(new StatuslineCache
        {
            ConfigDirectory = folder,
            ProfileId = Guid.NewGuid(),
            ProfileName = profile,
            RefreshIntervalSeconds = 30,
            ObservedAt = Now.AddSeconds(-ageSeconds),
            SessionPercentage = session,
        }, StatuslineJson.Default.StatuslineCache));
    }

    private static string Stdin(string directory, double? fiveHour = null)
    {
        var limits = fiveHour is { } used
            ? ",\"rate_limits\":{\"five_hour\":{\"used_percentage\":" + used.ToString(CultureInfo.InvariantCulture) + "}}"
            : "";
        return "{\"model\":{\"display_name\":\"Opus\"},\"workspace\":{\"current_dir\":" + JsonSerializer.Serialize(directory)
               + "},\"context_window\":{\"context_window_size\":200000}" + limits + "}";
    }

    [Fact]
    public void WithoutAnyFilesTheLineUsesUpstreamsDefaultsAndStdin()
    {
        Assert.Equal("project │ Opus │ Ctx: 0% │ Usage: 23% ▓▓░░░░░░░░", Run(Stdin(@"C:\work\my\project", 23.5)));
        Assert.Equal("project │ Opus │ Ctx: 0% │ Usage: ~", Run(Stdin("C:/work/my/project/")));
        Assert.Equal("Usage: ~", Run("not json"));
    }

    [Fact]
    public void TheAppliedConfigurationIsUsed()
    {
        Apply(new StatuslineConfig { ShowModel = false, ShowContext = false, ShowProfile = true, ShowProgressBar = false, ShowUsageLabel = false });
        Cache(DefaultFolder, "Work", 61);

        Assert.Equal("project │ Work │ 61%", Run(Stdin(@"C:\work\project", 5)));
    }

    [Fact]
    public void EachFolderGetsItsOwnCacheAndNeverAnothers()
    {
        var launcher = Path.Combine(_home, ".claude-work");
        Apply(new StatuslineConfig { ShowModel = false, ShowContext = false, ShowDirectory = false, ShowProfile = true, ShowProgressBar = false });
        Cache(DefaultFolder, "Personal", 10);
        Cache(launcher, "Work", 80);

        Assert.Equal("Personal │ Usage: 10%", Run(Stdin("C:/p")));
        Assert.Equal("Work │ Usage: 80%", Run(Stdin("C:/p"), launcher));
        // A session of a folder the tracker has no cache for shows only what Claude Code itself reports.
        Assert.Equal("Usage: 7%", Run(Stdin("C:/p", 7), Path.Combine(_home, ".claude-other")));
        Assert.Equal("Usage: ~", Run(Stdin("C:/p"), Path.Combine(_home, ".claude-other")));
    }

    [Fact]
    public void ACacheFileCopiedUnderAnotherFoldersKeyIsNotUsed()
    {
        var launcher = Path.Combine(_home, ".claude-work");
        Cache(DefaultFolder, "Personal", 10);
        File.Copy(StatuslinePaths.CacheFile(_data, DefaultFolder), StatuslinePaths.CacheFile(_data, launcher));

        Assert.EndsWith("Usage: ~", Run(Stdin("C:/p"), launcher));
    }

    [Fact]
    public void AnUnreadableCacheIsNoCache()
    {
        Directory.CreateDirectory(StatuslinePaths.Directory(_data));
        File.WriteAllText(StatuslinePaths.CacheFile(_data, DefaultFolder), "{ torn");

        Assert.EndsWith("Usage: 9% ▓░░░░░░░░░", Run(Stdin("C:/p", 9)));
    }

    [Fact]
    public void ADamagedConfigurationFailsLoudly()
    {
        Directory.CreateDirectory(StatuslinePaths.Directory(_data));
        File.WriteAllText(StatuslinePaths.ConfigFile(_data), "{ torn");

        Assert.Throws<InvalidDataException>(() => Run(Stdin("C:/p")));
    }

    [Fact]
    public void AConfigurationFromANewerBuildKeepsWorking()
    {
        Directory.CreateDirectory(StatuslinePaths.Directory(_data));
        File.WriteAllText(StatuslinePaths.ConfigFile(_data),
            """{"version":9,"showModel":false,"showContext":false,"showDirectory":false,"somethingNew":{"a":1},"elementColors":{"sparkleHex":"#fff"}}""");

        Assert.Equal("Usage: 9% ▓░░░░░░░░░", Run(Stdin("C:/p", 9)));
    }

    [Fact]
    public void TheCacheIsReadWhileTheTrackerHasItOpen()
    {
        Cache(DefaultFolder, "Work", 10);

        using var writer = new FileStream(
            StatuslinePaths.CacheFile(_data, DefaultFolder), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Contains("Usage: 10%", Run(Stdin("C:/p")));
    }

    [Fact]
    public void NothingIsWritten()
    {
        Cache(DefaultFolder, "Work", 10, ageSeconds: 3600);
        string[] Files() => [.. Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories).Select(f => f + File.GetLastWriteTimeUtc(f).Ticks)];
        var before = Files();

        Run(Stdin("C:/p", 55));

        Assert.Equal(before, Files());
    }

    [Fact]
    public void TheBranchComesFromTheRepositoryOfTheSessionsDirectory()
    {
        var repository = Path.Combine(_temp.Path, "repo");
        var nested = Path.Combine(repository, "src", "deep");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        File.WriteAllText(Path.Combine(repository, ".git", "HEAD"), "ref: refs/heads/feature/status-line\n");

        Assert.Equal("feature/status-line", GitBranch.Current(nested));
        Assert.StartsWith("deep │ ⎇ feature/status-line │ Opus", Run(Stdin(nested)));

        // A detached HEAD has no current branch, as "git branch --show-current" prints nothing.
        File.WriteAllText(Path.Combine(repository, ".git", "HEAD"), "4b825dc642cb6eb9a060e54bf8d69288fbee4904\n");
        Assert.Null(GitBranch.Current(nested));
        Assert.StartsWith("deep │ Opus", Run(Stdin(nested)));
    }

    [Fact]
    public void ALinkedWorktreeIsFollowedToItsGitDirectory()
    {
        var gitDirectory = Path.Combine(_temp.Path, "main", ".git", "worktrees", "wt");
        var worktree = Path.Combine(_temp.Path, "wt");
        Directory.CreateDirectory(gitDirectory);
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/wt-branch\n");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDirectory.Replace('\\', '/')}\n");

        Assert.Equal("wt-branch", GitBranch.Current(worktree));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Z:\no\such\folder\anywhere")]
    public void NoRepositoryMeansNoBranch(string? directory) => Assert.Null(GitBranch.Current(directory));

    [Fact]
    public void FoldersDifferingOnlyInCaseOrSeparatorShareACache()
    {
        Assert.Equal(StatuslinePaths.CacheKey(@"C:\Users\Someone\.claude"), StatuslinePaths.CacheKey(@"c:\users\someone\.CLAUDE\"));
        Assert.NotEqual(StatuslinePaths.CacheKey(@"C:\Users\Someone\.claude"), StatuslinePaths.CacheKey(@"C:\Users\Someone\.claude-work"));
        Assert.Matches("^[0-9a-f]{16}$", StatuslinePaths.CacheKey(@"C:\Users\Someone\.claude"));
    }

    [GeneratedRegex(@"\e\[[0-9;]*m")]
    private static partial Regex Plain();
}
