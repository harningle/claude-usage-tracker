using System.IO;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>
/// The per-user data root, <c>%LOCALAPPDATA%\ClaudeUsageTracker</c> (plan.md section 2). Build output
/// lives elsewhere, so removing user data never touches it. Tests pass a temporary root.
/// </summary>
public sealed class AppPaths(string root)
{
    public static AppPaths ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "ClaudeUsageTracker"));

    public string Root { get; } = root;

    /// <summary>Non-secret settings and profiles.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>One DPAPI file per profile.</summary>
    public string SecretsDirectory => Path.Combine(Root, "secrets");

    /// <summary>Last-known usage per profile, re-fetchable.</summary>
    public string CacheDirectory => Path.Combine(Root, "cache");

    /// <summary>Allowlisted request metadata only.</summary>
    public string DiagnosticsDirectory => Path.Combine(Root, "diagnostics");

    /// <summary>Usage history per profile.</summary>
    public string HistoryDirectory => Path.Combine(Root, "history");

    /// <summary>One WebView2 user-data folder per profile; browser sessions live there, managed by WebView2.</summary>
    public string BrowserDirectory => Path.Combine(Root, "browser");
}
