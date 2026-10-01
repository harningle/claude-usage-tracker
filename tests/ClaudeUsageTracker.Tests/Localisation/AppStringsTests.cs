using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ClaudeUsageTracker.App.Localisation;

namespace ClaudeUsageTracker.Tests.Localisation;

public partial class AppStringsTests
{
    [Fact]
    public void UpstreamEnglishTableLoads()
    {
        Assert.Equal("Claude Usage Tracker", Strings.Get("app.name"));
        Assert.Equal("Refresh", Strings.Get("common.refresh"));
    }

    [Fact]
    public void MissingKeyThrows() => Assert.Throws<KeyNotFoundException>(() => Strings.Get("no.such.key"));

    /// <summary>Every key the App source uses exists in the English table, so no lookup fails at run time.</summary>
    [Fact]
    public void EveryKeyUsedByTheAppExists()
    {
        var appDirectory = Path.Combine(RepositoryRoot(), "src", "ClaudeUsageTracker.App");
        var files = Directory.EnumerateFiles(appDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal));
        var keys = files
            .SelectMany(f => KeyUse().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        var missing = keys.Where(k =>
        {
            try
            {
                Strings.Get(k);
                return false;
            }
            catch (KeyNotFoundException)
            {
                return true;
            }
        }).ToList();
        Assert.Empty(missing);
    }

    [GeneratedRegex("""(?:\{l:Loc |Strings\.(?:Get|Format)\(")(?<key>[A-Za-z0-9_.]+)""")]
    private static partial Regex KeyUse();

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));
}
