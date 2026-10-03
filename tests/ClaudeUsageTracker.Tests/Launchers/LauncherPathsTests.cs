using System.IO;
using ClaudeUsageTracker.Core.Launchers;

namespace ClaudeUsageTracker.Tests.Launchers;

/// <summary>What a launcher derives from its own path, and which executable it may start.</summary>
public sealed class LauncherPathsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_temp.Path, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void LayoutFollowsUpstream()
    {
        Assert.Equal(@"C:\Users\u\.local\bin\claude-work.exe", LauncherPaths.LauncherPath(@"C:\Users\u", "work"));
        Assert.Equal(@"C:\Users\u\.claude-work", LauncherPaths.ConfigDirectory(@"C:\Users\u", "work"));
    }

    [Theory]
    [InlineData("claude-work.exe", "work")]
    [InlineData("CLAUDE-My-Work.EXE", "my-work")]
    [InlineData("claude-a1b2c3d4.exe", "a1b2c3d4")]
    public void ALauncherIsRecognisedByItsFileName(string fileName, string slug) =>
        Assert.Equal(slug, LauncherPaths.SlugOf(fileName));

    [Theory]
    [InlineData("claude.exe")]
    [InlineData("claude-.exe")]
    [InlineData("claude--x.exe")]
    [InlineData("claude-x-.exe")]
    [InlineData("claude-work")]
    [InlineData("claude-wörk.exe")]
    [InlineData("claude-a b.exe")]
    [InlineData("ClaudeUsageTracker.Helper.exe")]
    [InlineData("notclaude-work.exe")]
    public void OtherFileNamesAreNotLaunchers(string fileName) =>
        Assert.Null(LauncherPaths.SlugOf(fileName));

    [Fact]
    public void EverySlugTheNamingProducesIsRecognised()
    {
        foreach (var name in new[] { "My Work Acct!", "Xtnd", "  spaced   out  ", "a__b--c", "日本語のみ" })
        {
            var slug = LauncherNaming.Slug(name, Guid.NewGuid());

            Assert.Equal(slug, LauncherPaths.SlugOf(LauncherPaths.FileName(slug)));
        }
    }

    [Fact]
    public void TheUserProfileComesFromTheLaunchersOwnPath()
    {
        Assert.Equal(@"C:\Users\u", LauncherPaths.UserProfileOf(@"C:\Users\u\.local\bin\claude-work.exe"));
        Assert.Equal(@"C:\Users\u", LauncherPaths.UserProfileOf(@"C:\Users\u\.LOCAL\BIN\claude-work.exe"));
        // Anywhere else it must not guess a folder.
        Assert.Null(LauncherPaths.UserProfileOf(@"C:\tools\claude-work.exe"));
        Assert.Null(LauncherPaths.UserProfileOf(@"C:\Users\u\bin\claude-work.exe"));
        Assert.Null(LauncherPaths.UserProfileOf(@"C:\Users\u\.local\bin\nested\claude-work.exe"));
    }

    [Fact]
    public void TheRealCliBesideTheLauncherIsPreferred()
    {
        var launcher = Touch("home", ".local", "bin", "claude-work.exe");
        var beside = Touch("home", ".local", "bin", "claude.exe");
        var elsewhere = Touch("other", "claude.exe");

        Assert.Equal(beside, LauncherPaths.FindRealCli(launcher, Path.GetDirectoryName(elsewhere)));
    }

    [Fact]
    public void OtherwiseTheFirstOneOnPathIsUsed()
    {
        var launcher = Touch("home", ".local", "bin", "claude-work.exe");
        var first = Touch("first dir", "claude.exe");
        var second = Touch("second", "claude.exe");
        var path = string.Join(';', Path.Combine(_temp.Path, "missing"), "", $"\"{Path.GetDirectoryName(first)}\"", Path.GetDirectoryName(second));

        Assert.Equal(first, LauncherPaths.FindRealCli(launcher, path));
    }

    [Fact]
    public void LaunchersAndOtherEntryPointsAreNeverTheRealCli()
    {
        var launcher = Touch("home", ".local", "bin", "claude-work.exe");
        Touch("home", ".local", "bin", "claude-other.exe");
        Touch("home", ".local", "bin", "ClaudeUsageTracker.Helper.exe");
        // npm's batch entry point is outside the native support target (plan.md section 2).
        Touch("npm", "claude.cmd");
        Touch("npm", "claude");

        Assert.Null(LauncherPaths.FindRealCli(launcher, Path.Combine(_temp.Path, "npm")));
        Assert.Null(LauncherPaths.FindRealCli(launcher, null));
    }

    [Fact]
    public void ALauncherNeverResolvesToItself()
    {
        // A copy of the helper that someone renamed to claude.exe: it is not started by itself.
        var self = Touch("home", ".local", "bin", "claude.exe");

        Assert.Null(LauncherPaths.FindRealCli(self, Path.GetDirectoryName(self)));
    }
}
