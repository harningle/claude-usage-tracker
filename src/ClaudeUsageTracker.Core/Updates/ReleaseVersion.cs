using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeUsageTracker.Core.Updates;

/// <summary>
/// A release version, <c>major.minor.patch[-pre-release]</c>, ordered by Semantic Versioning 2.0.0 precedence: a
/// pre-release comes before its release, and pre-release identifiers compare as numbers when both are numeric, so
/// <c>beta.2</c> comes before <c>beta.10</c>. Build metadata (<c>+...</c>) is ignored, as SemVer says.
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<ReleaseVersion>
{
    public bool IsPreRelease => PreRelease is not null;

    /// <summary>Parses a version or a release tag (<c>v1.0.0-beta.2</c>); null when it is neither.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (text is null || Pattern().Match(text) is not { Success: true } match
            || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }

        return new ReleaseVersion(major, minor, patch, match.Groups[4].Success ? match.Groups[4].Value : null);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0 || PreRelease == other.PreRelease)
        {
            return core;
        }

        if (PreRelease is null || other.PreRelease is null)
        {
            // A release comes after its pre-releases.
            return PreRelease is null ? 1 : -1;
        }

        var mine = PreRelease.Split('.');
        var theirs = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var order = CompareIdentifiers(mine[i], theirs[i]);
            if (order != 0)
            {
                return order;
            }
        }

        // With equal leading identifiers, the longer pre-release comes later.
        return mine.Length.CompareTo(theirs.Length);
    }

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    /// <summary>Numeric identifiers compare as numbers and come before alphanumeric ones, which compare in ASCII order.</summary>
    private static int CompareIdentifiers(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // Numeric identifiers have no leading zeros (the pattern refuses them), so the longer one is larger.
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }

        return aNumeric ? -1 : bNumeric ? 1 : string.CompareOrdinal(a, b);
    }

    [GeneratedRegex(
        @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)" +
        @"(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
