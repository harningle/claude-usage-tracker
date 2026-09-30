using System.Text;

namespace ClaudeUsageTracker.Core.Launchers;

/// <summary>Per-profile launcher naming. Port of the naming rules in upstream <c>TerminalLauncherService</c>.</summary>
public static class LauncherNaming
{
    /// <summary>
    /// Lower-case ASCII letters and digits with every other run collapsed to one dash, trimmed:
    /// "My Work Acct!" becomes "my-work-acct". Names with no usable characters fall back to the first
    /// eight characters of the profile ID.
    /// </summary>
    public static string Slug(string name, Guid profileId)
    {
        var slug = new StringBuilder();
        var lastWasDash = true; // suppresses a leading dash
        foreach (var rune in name.ToLowerInvariant().EnumerateRunes())
        {
            if (rune.IsAscii && char.IsAsciiLetterOrDigit((char)rune.Value))
            {
                slug.Append((char)rune.Value);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                slug.Append('-');
                lastWasDash = true;
            }
        }

        var result = slug.ToString().TrimEnd('-');
        return result.Length > 0 ? result : IdPrefix(profileId, 8);
    }

    /// <summary>
    /// The profile's slug, with a short ID suffix when another profile's launcher already owns it.
    /// </summary>
    /// <param name="installedOwners">Owning profile ID of each installed launcher slug.</param>
    public static string AvailableSlug(string name, Guid profileId, IReadOnlyDictionary<string, Guid> installedOwners)
    {
        var slug = Slug(name, profileId);
        return installedOwners.TryGetValue(slug, out var owner) && owner != profileId
            ? $"{slug}-{IdPrefix(profileId, 4)}"
            : slug;
    }

    private static string IdPrefix(Guid id, int length) => id.ToString("D")[..length];
}
