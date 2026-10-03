namespace ClaudeUsageTracker.Core.Integration;

/// <summary>What the app and the helper agree on about the installed helper (plan.md section 2, "Stable helper installation").</summary>
public static class HelperInfo
{
    /// <summary>
    /// The helper's version, raised whenever its behaviour changes. An installed helper with a higher number
    /// was put there by a newer build of the app and is never replaced by an older one.
    /// </summary>
    public const int Version = 1;

    public const string FileName = "ClaudeUsageTracker.Helper.exe";

    /// <summary>The fixed per-user folder of the installed helper, under the tracker's data root.</summary>
    public static string BinDirectory(string dataRoot) => Path.Combine(dataRoot, "bin");

    public static string InstalledPath(string dataRoot) => Path.Combine(BinDirectory(dataRoot), FileName);

    /// <summary>The data root an installed helper belongs to: the folder above its own.</summary>
    public static string? DataRootOf(string helperPath) => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(helperPath)));
}
