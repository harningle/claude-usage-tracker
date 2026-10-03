namespace ClaudeUsageTracker.Core.Launchers;

/// <summary>
/// Where a profile's launcher and its Claude Code folder live. Upstream's layout (<c>TerminalLauncherService</c>):
/// <c>claude-&lt;slug&gt;</c> in <c>~/.local/bin</c> runs Claude Code with the folder <c>~/.claude-&lt;slug&gt;</c>. On
/// Windows the launcher is an executable, <c>claude-&lt;slug&gt;.exe</c>, and everything it needs follows from its
/// own path, so it reads no settings and no other process's environment says which folder it uses.
/// </summary>
public static class LauncherPaths
{
    public const string RealCliFileName = "claude.exe";

    private const string Prefix = "claude-";
    private const string Extension = ".exe";

    /// <summary><c>.local\bin</c> in the user profile: the per-user location native Claude Code itself installs to.</summary>
    public static string BinDirectory(string userProfile) => Path.Combine(userProfile, ".local", "bin");

    public static string FileName(string slug) => Prefix + slug + Extension;

    public static string LauncherPath(string userProfile, string slug) => Path.Combine(BinDirectory(userProfile), FileName(slug));

    /// <summary>The launcher's own Claude Code folder. Removing a launcher never removes it, because it holds a login.</summary>
    public static string ConfigDirectory(string userProfile, string slug) => Path.Combine(userProfile, ".claude-" + slug);

    /// <summary>The slug of a launcher file name, or null for any other name (including <c>claude.exe</c> itself).</summary>
    public static string? SlugOf(string fileName)
    {
        if (!fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var slug = fileName[Prefix.Length..^Extension.Length].ToLowerInvariant();
        return IsSlug(slug) ? slug : null;
    }

    /// <summary>Whether text has the shape <see cref="LauncherNaming.Slug"/> produces.</summary>
    public static bool IsSlug(string text) =>
        text.Length > 0 && text[0] != '-' && text[^1] != '-' && text.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>
    /// The user profile a launcher belongs to, from its own path <c>&lt;profile&gt;\.local\bin\claude-&lt;slug&gt;.exe</c>;
    /// null when the launcher is anywhere else, where it must not guess a folder.
    /// </summary>
    public static string? UserProfileOf(string launcherPath)
    {
        var bin = Path.GetDirectoryName(Path.GetFullPath(launcherPath));
        var local = Path.GetDirectoryName(bin);
        return string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(local), ".local", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(local)
            : null;
    }

    /// <summary>
    /// The native Claude Code executable a launcher starts: <c>claude.exe</c> beside the launcher, else the first
    /// one on <paramref name="pathVariable"/>. Only that exact file name is accepted, so the result is never a
    /// launcher (<c>claude-&lt;slug&gt;.exe</c>) or the helper, and a launcher cannot start itself.
    /// </summary>
    public static string? FindRealCli(string launcherPath, string? pathVariable)
    {
        var self = Path.GetFullPath(launcherPath);
        var directories = new[] { Path.GetDirectoryName(self) }
            .Concat((pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var directory in directories)
        {
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), RealCliFileName));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(candidate) && !string.Equals(candidate, self, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
