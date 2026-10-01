using ClaudeUsageTracker.Core.Profiles;

namespace ClaudeUsageTracker.Tests.Profiles;

/// <summary>Characterises upstream <c>FunnyNameGenerator</c>, which upstream did not test.</summary>
public class FunnyNamesTests
{
    [Fact]
    public void UpstreamListHasThirtyNames() => Assert.Equal(30, FunnyNames.Names.Distinct().Count());

    [Fact]
    public void UsedNamesAreExcluded()
    {
        var used = FunnyNames.Names.Skip(1).ToList();

        Assert.Equal(FunnyNames.Names[0], FunnyNames.Random(used, new Random(1)));
    }

    [Fact]
    public void ExclusionIsCaseSensitive() =>
        Assert.Contains(FunnyNames.Random(FunnyNames.Names.Select(n => n.ToUpperInvariant()), new Random(1)), FunnyNames.Names);

    [Fact]
    public void FallbackWhenEveryNameIsUsed()
    {
        var name = FunnyNames.Random(FunnyNames.Names, new Random(7));

        Assert.Matches("^Profile [1-9][0-9]{3}$", name);
    }
}
