using ClaudeUsageTracker.Core.Launchers;

namespace ClaudeUsageTracker.Tests.Launchers;

/// <summary>
/// Port of the naming cases of upstream <c>TerminalLauncherServiceTests</c>. Installation cases are
/// scheduled with the Windows executable launchers (docs/parity.md).
/// </summary>
public class LauncherNamingTests
{
    private static readonly Guid ProfileId = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    [Theory]
    [InlineData("My Work Acct!", "my-work-acct")]
    [InlineData("Xtnd", "xtnd")]
    [InlineData("  spaced   out  ", "spaced-out")]
    [InlineData("a__b--c", "a-b-c")]
    public void SlugLowercasesAndDashesNonAlphanumerics(string name, string expected) =>
        Assert.Equal(expected, LauncherNaming.Slug(name, ProfileId));

    [Fact]
    public void SlugFallsBackToIdForUnusableNames() =>
        Assert.Equal("0f1e2d3c", LauncherNaming.Slug("日本語のみ", ProfileId));

    [Fact]
    public void CollidingNamesGetIdSuffixedSlug()
    {
        var installed = new Dictionary<string, Guid> { ["team"] = Guid.NewGuid() };

        Assert.Equal("team-0f1e", LauncherNaming.AvailableSlug("Team", ProfileId, installed));
    }

    /// <summary>No upstream case; the owner of an installed slug keeps it.</summary>
    [Fact]
    public void OwnerKeepsItsInstalledSlug()
    {
        var installed = new Dictionary<string, Guid> { ["team"] = ProfileId };

        Assert.Equal("team", LauncherNaming.AvailableSlug("Team", ProfileId, installed));
    }
}
