using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.App.Integration;

/// <summary>
/// What the tracker has installed outside its own folder: the helper, the launchers and its entries in Claude
/// Code's settings (plan.md section 2, "Stable helper installation, upgrades and repair"). It is how the
/// tracker knows a file or entry as its own, and how an install interrupted between its two renames is
/// finished at the next start. Non-secret; it lives in the data root, not in the movable app folder.
/// </summary>
public sealed class InstallManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The installed helper, or null when it has never been installed.</summary>
    public InstalledFile? Helper { get; set; }

    public List<InstalledLauncher> Launchers { get; set; } = [];

    /// <summary>Images renamed aside by an upgrade or removal. A running process may still use one; each is deleted once it can be.</summary>
    public List<RetainedFile> Retained { get; set; } = [];

    /// <summary>An install whose renames have not both been recorded yet.</summary>
    public PendingInstall? Pending { get; set; }

    /// <summary>The <c>statusLine</c> entries the tracker wrote, per Claude Code folder.</summary>
    public List<StatuslineEntry> Statuslines { get; set; } = [];

    /// <summary>Every loopback port the tracker has written hook entries for; an entry for one of them is the tracker's own.</summary>
    public List<int> HookPorts { get; set; } = [];

    /// <summary>The Claude Code folders whose settings hold the tracker's hook entries, so they are found again when a folder stops being known.</summary>
    public List<string> HookFolders { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class InstalledFile
{
    public string Path { get; set; } = "";

    /// <summary><see cref="Core.Integration.HelperInfo.Version"/> of the build that was installed.</summary>
    public int HelperVersion { get; set; }

    /// <summary>The installed file's SHA-256, so a file that was replaced by something else is noticed.</summary>
    public string Sha256 { get; set; } = "";

    public DateTimeOffset InstalledAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A profile's launcher: the alias-to-profile mapping upstream kept as a marker line in its script.</summary>
public sealed class InstalledLauncher : InstalledFile
{
    public string Slug { get; set; } = "";

    public Guid ProfileId { get; set; }

    /// <summary>The Claude Code folder the launcher runs with.</summary>
    public string ConfigDirectory { get; set; } = "";
}

public sealed class RetainedFile
{
    public string Path { get; set; } = "";

    /// <summary>For a launcher image: the folder its running sessions use, so they stay attributed to it.</summary>
    public string? ConfigDirectory { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PendingInstall
{
    /// <summary>The final path of the file being installed.</summary>
    public string Canonical { get; set; } = "";

    /// <summary>The verified new copy beside it.</summary>
    public string Staged { get; set; } = "";

    /// <summary>Where the existing file is renamed to, or null when there was none.</summary>
    public string? Retained { get; set; }

    public string Sha256 { get; set; } = "";

    public int HelperVersion { get; set; }

    /// <summary>Set when the file is a launcher.</summary>
    public string? LauncherSlug { get; set; }

    public Guid? ProfileId { get; set; }

    public string? ConfigDirectory { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StatuslineEntry
{
    public string ConfigDirectory { get; set; } = "";

    /// <summary>The command the tracker wrote.</summary>
    public string Command { get; set; } = "";

    /// <summary>The <c>statusLine</c> value that was there before and that the user agreed to replace, as JSON; put back on removal.</summary>
    public string? Replaced { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The manifest exists but cannot be used. Reported; the file is never overwritten.</summary>
public sealed class InstallManifestException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class InstallManifestStore(string path)
{
    public string Path { get; } = path;

    /// <summary>The saved manifest, or an empty one when nothing was ever installed.</summary>
    public InstallManifest Load()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new InstallManifest();
        }

        InstallManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<InstallManifest>(bytes, SettingsStore.JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InstallManifestException($"The record of installed integrations is damaged and was not changed: {Path} ({e.Message})", e);
        }

        if (manifest is null || manifest.Version > InstallManifest.CurrentVersion)
        {
            throw new InstallManifestException(
                $"The record of installed integrations is empty or from a newer version and was not changed: {Path}");
        }

        manifest.Launchers ??= [];
        manifest.Retained ??= [];
        manifest.Statuslines ??= [];
        manifest.HookPorts ??= [];
        manifest.HookFolders ??= [];
        return manifest;
    }

    public void Save(InstallManifest manifest)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        AtomicFile.WriteAllBytes(Path, JsonSerializer.SerializeToUtf8Bytes(manifest, SettingsStore.JsonOptions));
    }
}
