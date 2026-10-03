using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTracker.Core.Statusline;

/// <summary>
/// What the tracker last knew about the account signed in to one Claude Code folder, for the status line
/// of sessions that use that folder. Non-secret: it names the profile and holds usage numbers only. The
/// tracker writes it only from usage fetched with that folder's own verified login (plan.md section 2,
/// "Fast, secret-free statusline").
/// </summary>
public sealed class StatuslineCache
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The Claude Code folder these values belong to; a file found under another folder's key is not used.</summary>
    public string ConfigDirectory { get; set; } = "";

    public Guid ProfileId { get; set; }

    public string ProfileName { get; set; } = "";

    /// <summary>The profile's polling interval when the values were written; the values count as fresh for twice as long.</summary>
    public double RefreshIntervalSeconds { get; set; }

    /// <summary>When the usage was fetched, or null when the folder's profile is known but no usage has been fetched yet.</summary>
    public DateTimeOffset? ObservedAt { get; set; }

    public double? SessionPercentage { get; set; }

    public DateTimeOffset? SessionResetsAt { get; set; }

    public double? WeeklyPercentage { get; set; }

    public DateTimeOffset? WeeklyResetsAt { get; set; }

    public decimal? CostUsed { get; set; }

    public decimal? CostLimit { get; set; }

    public string? CostCurrency { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Where the status line's non-secret files live under the tracker's data root.</summary>
public static class StatuslinePaths
{
    public static string Directory(string dataRoot) => Path.Combine(dataRoot, "statusline");

    public static string ConfigFile(string dataRoot) => Path.Combine(Directory(dataRoot), "config.json");

    public static string CacheFile(string dataRoot, string configDirectory) =>
        Path.Combine(Directory(dataRoot), $"{CacheKey(configDirectory)}.cache.json");

    /// <summary>A file-name-safe key of a Claude Code folder: paths differing only in case or a trailing separator share it.</summary>
    public static string CacheKey(string configDirectory) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(configDirectory).ToLowerInvariant())))[..16];

    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    public static bool SameDirectory(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder a Claude Code session uses: <c>CLAUDE_CONFIG_DIR</c>, else <c>.claude</c> in the user profile.</summary>
    public static string SessionConfigDirectory(string? configDirOverride, string userProfile) =>
        Normalise(string.IsNullOrWhiteSpace(configDirOverride) ? Path.Combine(userProfile, ".claude") : configDirOverride);
}

/// <summary>Source-generated JSON for the files the NativeAOT helper reads, so it needs no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StatuslineConfig))]
[JsonSerializable(typeof(StatuslineCache))]
public sealed partial class StatuslineJson : JsonSerializerContext;
