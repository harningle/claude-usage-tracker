using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>What the automation remembered for one profile. Non-secret.</summary>
public sealed class ProfileAutomationState
{
    /// <summary>The reset time of the session window the notified levels belong to.</summary>
    public DateTimeOffset? SessionWindowResetAt { get; set; }

    /// <summary>Thresholds already notified in that window (upstream's persisted <c>sentNotifications</c>).</summary>
    public List<int> NotifiedLevels { get; set; } = [];

    /// <summary>No session auto-start is attempted for this profile before this time.</summary>
    public DateTimeOffset? AutoStartHoldUntil { get; set; }

    /// <summary>A failed auto-start was notified; the next failure is not notified again until one succeeds.</summary>
    public bool AutoStartFailureNotified { get; set; }

    /// <summary>The Console session expiry whose 24-hour warning was posted, so it is posted once.</summary>
    public DateTimeOffset? ConsoleExpiryNotifiedFor { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AutomationState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<Guid, ProfileAutomationState> Profiles { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public ProfileAutomationState For(Guid profileId)
    {
        if (!Profiles.TryGetValue(profileId, out var state))
        {
            Profiles[profileId] = state = new ProfileAutomationState();
        }

        return state;
    }
}

/// <summary>
/// The automation's memory (<c>automation.json</c>), so a restart neither repeats a notification nor retries an
/// auto-start too soon. Separate from the settings, so polling never rewrites them. A damaged or newer-format file
/// is reported and never overwritten.
/// </summary>
public sealed class AutomationStateStore(string path)
{
    public string Path { get; } = path;

    /// <summary>The saved state, or an empty one when no file exists yet.</summary>
    /// <exception cref="SettingsStoreException">The file exists but cannot be used; it is left untouched.</exception>
    public AutomationState Load()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AutomationState();
        }

        AutomationState? state;
        try
        {
            state = JsonSerializer.Deserialize<AutomationState>(bytes, SettingsStore.JsonOptions);
        }
        catch (JsonException e)
        {
            throw new SettingsStoreException($"The automation state file is damaged and was not changed: {Path} ({e.Message})", e);
        }

        if (state is null || state.Version > AutomationState.CurrentVersion)
        {
            throw new SettingsStoreException($"The automation state file is empty or from a newer version and was not changed: {Path}");
        }

        state.Profiles ??= [];
        foreach (var profile in state.Profiles.Values)
        {
            profile.NotifiedLevels ??= [];
        }

        return state;
    }

    public void Save(AutomationState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        AtomicFile.WriteAllBytes(Path, JsonSerializer.SerializeToUtf8Bytes(state, SettingsStore.JsonOptions));
    }
}
