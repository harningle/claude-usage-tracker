using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>
/// Non-secret settings, stored as versioned JSON. Key names follow upstream's property names. Every
/// class keeps unknown members, so a file written by a newer build survives a save by an older one.
/// Enum-like values stay raw strings for the same reason; the typed accessors interpret them.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<ProfileSettings> Profiles { get; set; } = [];

    public Guid? ActiveProfileId { get; set; }

    /// <summary>Upstream raw values <c>resetTime</c>, <c>remainingTime</c> or <c>both</c>.</summary>
    public string PopoverTimeDisplay { get; set; } = "resetTime";

    /// <summary>Upstream raw values <c>system</c>, <c>twelveHour</c> or <c>twentyFourHour</c>.</summary>
    public string TimeFormatPreference { get; set; } = "system";

    /// <summary>Set when a setup route saves a working configuration.</summary>
    public bool HasCompletedSetup { get; set; }

    /// <summary>Set when the wizard is first shown automatically, as upstream does.</summary>
    public bool HasShownWizardOnce { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public PopoverTimeDisplay TimeDisplay
    {
        get => PopoverTimeDisplay switch
        {
            "remainingTime" => Core.Usage.PopoverTimeDisplay.RemainingTime,
            "both" => Core.Usage.PopoverTimeDisplay.Both,
            _ => Core.Usage.PopoverTimeDisplay.ResetTime,
        };
        set => PopoverTimeDisplay = value switch
        {
            Core.Usage.PopoverTimeDisplay.RemainingTime => "remainingTime",
            Core.Usage.PopoverTimeDisplay.Both => "both",
            _ => "resetTime",
        };
    }

    [JsonIgnore]
    public TimeFormatPreference TimeFormat
    {
        get => TimeFormatPreference switch
        {
            "twelveHour" => Core.Usage.TimeFormatPreference.TwelveHour,
            "twentyFourHour" => Core.Usage.TimeFormatPreference.TwentyFourHour,
            _ => Core.Usage.TimeFormatPreference.System,
        };
        set => TimeFormatPreference = value switch
        {
            Core.Usage.TimeFormatPreference.TwelveHour => "twelveHour",
            Core.Usage.TimeFormatPreference.TwentyFourHour => "twentyFourHour",
            _ => "system",
        };
    }

    public ProfileSettings? ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.FirstOrDefault();
}

public sealed class ProfileSettings
{
    [JsonRequired]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonRequired]
    public string Name { get; set; } = "";

    /// <summary>
    /// The persisted provider value. An unknown value from a newer build is kept as written; a non-string
    /// value reads as <c>anthropic</c>, as upstream's tests require.
    /// </summary>
    [JsonConverter(typeof(LenientProviderConverter))]
    public string Provider { get; set; } = Core.Providers.Provider.Anthropic.RawValue();

    /// <summary>Seconds between polls; upstream's default is 30.</summary>
    public double RefreshInterval { get; set; } = 30;

    public IconConfiguration IconConfig { get; set; } = new();

    public bool IsSelectedForDisplay { get; set; } = true;

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>The Claude Code config directory this profile follows, or null when unlinked.</summary>
    public CliLink? CliLink { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Unknown providers behave as Anthropic, matching upstream; the raw value is not rewritten.</summary>
    [JsonIgnore]
    public Provider ProviderKind => Provider == Core.Providers.Provider.Codex.RawValue()
        ? Core.Providers.Provider.Codex
        : Core.Providers.Provider.Anthropic;
}

/// <summary>
/// A profile's link to a Claude Code config directory, with the account it was linked to. The email and
/// organisation are personal data shown in the UI; they never go into logs.
/// </summary>
public sealed class CliLink
{
    public string ConfigDirectory { get; set; } = "";

    public bool IsDefaultDirectory { get; set; }

    public string? AccountUuid { get; set; }

    public string? EmailAddress { get; set; }

    public string? OrganizationName { get; set; }

    public string? OrganizationUuid { get; set; }

    public string? SubscriptionType { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public string? Identity => AccountUuid ?? EmailAddress;
}

/// <summary>Port of upstream <c>MenuBarIconConfiguration</c> with its <c>init</c> defaults.</summary>
public sealed class IconConfiguration
{
    /// <summary><c>multiColor</c>, <c>monochrome</c> or <c>singleColor</c>.</summary>
    public string ColorMode { get; set; } = "multiColor";

    public string SingleColorHex { get; set; } = "#00BFFF";

    public bool ShowIconNames { get; set; } = true;

    public bool ShowRemainingPercentage { get; set; }

    public bool ShowTimeMarker { get; set; } = true;

    public bool ShowPaceMarker { get; set; } = true;

    public bool UsePaceColoring { get; set; } = true;

    public List<MetricIconConfig> Metrics { get; set; } =
    [
        new() { MetricType = "session", IsEnabled = true, Order = 0 },
        new() { MetricType = "week", Order = 1 },
        new() { MetricType = "api", Order = 2 },
    ];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool Monochrome => ColorMode == "monochrome";

    public MetricIconConfig? Metric(string metricType) => Metrics.FirstOrDefault(m => m.MetricType == metricType);
}

/// <summary>Port of upstream <c>MetricIconConfig</c>.</summary>
public sealed class MetricIconConfig
{
    /// <summary><c>session</c>, <c>week</c> or <c>api</c>.</summary>
    public string MetricType { get; set; } = "session";

    public bool IsEnabled { get; set; }

    /// <summary><c>battery</c>, <c>progressBar</c>, <c>percentageOnly</c>, <c>icon</c> or <c>compact</c>.</summary>
    public string IconStyle { get; set; } = "battery";

    public int Order { get; set; }

    public string WeekDisplayMode { get; set; } = "percentage";

    public string ApiDisplayMode { get; set; } = "remaining";

    public bool ShowNextSessionTime { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Reads a string as itself and any other JSON value as <c>anthropic</c>.</summary>
internal sealed class LenientProviderConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString()!;
        }

        reader.Skip();
        return Provider.Anthropic.RawValue();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
