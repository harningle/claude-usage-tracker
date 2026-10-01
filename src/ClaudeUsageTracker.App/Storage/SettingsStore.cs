using System.IO;
using System.Text.Json;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>Settings exist but cannot be used. Reported to the user; the file is never overwritten.</summary>
public sealed class SettingsStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Loads and atomically saves <see cref="AppSettings"/>. Upstream silently replaced an undecodable
/// profile list with a fresh default profile; here a corrupt or newer-format file is reported instead
/// and left untouched (docs/parity.md).
/// </summary>
public sealed class SettingsStore(string path)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string Path { get; } = path;

    /// <summary>The saved settings, or defaults when no file exists yet.</summary>
    public AppSettings Load()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings();
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(bytes, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new SettingsStoreException($"The settings file is damaged and was not changed: {Path} ({e.Message})", e);
        }

        if (settings is null)
        {
            throw new SettingsStoreException($"The settings file is empty or null and was not changed: {Path}");
        }

        if (settings.Version > AppSettings.CurrentVersion)
        {
            throw new SettingsStoreException(
                $"The settings file was written by a newer version (format {settings.Version}); this version reads " +
                $"format {AppSettings.CurrentVersion}. It was not changed: {Path}");
        }

        var ids = new HashSet<Guid>();
        foreach (var profile in settings.Profiles)
        {
            if (profile is null || profile.Id == Guid.Empty || !ids.Add(profile.Id))
            {
                throw new SettingsStoreException($"The settings file has a missing or repeated profile ID and was not changed: {Path}");
            }

            profile.IconConfig ??= new IconConfiguration();
            profile.IconConfig.Metrics ??= new IconConfiguration().Metrics;
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        AtomicFile.WriteAllBytes(Path, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
    }
}
