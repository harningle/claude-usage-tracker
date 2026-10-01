using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>
/// The last usage observed for a profile, kept so a restart shows labelled stale data instead of
/// nothing. Identifies the profile, source, account and observation time (plan.md section 2).
/// </summary>
public sealed record CachedUsage
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public required Guid ProfileId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Where the values came from, e.g. <c>oauthUsage</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The account the values belong to, so another login's cache is never shown.</summary>
    public string? AccountIdentity { get; init; }

    public required UsageData Usage { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// One cache file per profile, separate from settings and secrets so polling never rewrites either.
/// The cache is re-fetchable: an unreadable file reads as no cache and never costs the profile.
/// </summary>
public sealed class UsageCacheStore(string directory)
{
    public string PathFor(Guid profileId) => Path.Combine(directory, $"{profileId:N}.usage.json");

    /// <summary>The cached usage, or null when absent, unreadable, from a newer format or for another profile.</summary>
    public CachedUsage? Load(Guid profileId)
    {
        try
        {
            var cached = JsonSerializer.Deserialize<CachedUsage>(File.ReadAllBytes(PathFor(profileId)), SettingsStore.JsonOptions);
            return cached is { Version: <= CachedUsage.CurrentVersion } && cached.ProfileId == profileId ? cached : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(CachedUsage cached)
    {
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllBytes(PathFor(cached.ProfileId), JsonSerializer.SerializeToUtf8Bytes(cached, SettingsStore.JsonOptions));
    }

    public void Delete(Guid profileId) => File.Delete(PathFor(profileId));
}
