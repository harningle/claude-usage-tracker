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

    /// <summary>Upstream raw values <c>single</c> or <c>multi</c>: one profile in the tray, or every selected one.</summary>
    public string ProfileDisplayMode { get; set; } = "single";

    public MultiProfileDisplayConfig MultiProfileDisplayConfig { get; set; } = new();

    /// <summary>
    /// Data of removed profiles or links that could not be deleted at the time (a file in use, a browser
    /// still running). Startup tries again, so a removal is never silently left half done.
    /// </summary>
    public List<PendingCleanup> PendingCleanups { get; set; } = [];

    /// <summary>
    /// Upstream <c>autoSwitchProfileEnabled</c>: when the active profile's session reaches 100 %, activate the next
    /// profile with capacity. Off by default, as upstream.
    /// </summary>
    public bool AutoSwitchProfileEnabled { get; set; }

    /// <summary>Global keyboard shortcuts (upstream <c>ShortcutManager</c>); none by default, as upstream.</summary>
    public ShortcutSettings Shortcuts { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsMultiProfile
    {
        get => ProfileDisplayMode == "multi";
        set => ProfileDisplayMode = value ? "multi" : "single";
    }

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

    /// <summary>
    /// The profiles shown in the tray and therefore polled: the active one, or in multi-profile mode
    /// every profile selected for display (upstream <c>getSelectedProfiles</c>).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ProfileSettings> DisplayedProfiles =>
        IsMultiProfile ? [.. Profiles.Where(p => p.IsSelectedForDisplay)]
        : ActiveProfile is { } active ? [active]
        : [];
}

/// <summary>What a failed removal left behind for one profile ID.</summary>
public sealed class PendingCleanup
{
    public Guid ProfileId { get; set; }

    /// <summary>Remove the encrypted secret file (the whole profile was deleted).</summary>
    public bool Secrets { get; set; }

    /// <summary>Remove the usage and billing caches and the history.</summary>
    public bool Data { get; set; }

    /// <summary>Remove the WebView2 user-data folder.</summary>
    public bool Browser { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Which credential a profile's usage comes from.</summary>
public enum UsageSourceKind
{
    /// <summary>A Claude Code login, through the read-only OAuth usage endpoint.</summary>
    ClaudeCode,

    /// <summary>A claude.ai browser session, through same-origin WebView2 requests.</summary>
    ClaudeAi,

    /// <summary>A Codex CLI login.</summary>
    Codex,
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

    /// <summary>The Claude Code login this profile follows, or null when unlinked.</summary>
    public CliLink? CliLink { get; set; }

    /// <summary>The claude.ai organisation tracked through this profile's browser session, or null.</summary>
    public ClaudeAiLink? ClaudeAi { get; set; }

    /// <summary>The Anthropic Console organisation whose billing is tracked, or null.</summary>
    public ConsoleLink? Console { get; set; }

    /// <summary>The Codex login this profile follows, or null.</summary>
    public CodexLink? Codex { get; set; }

    /// <summary>Upstream <c>notificationSettings</c>, with its defaults for a profile that has none.</summary>
    public NotificationSettings NotificationSettings { get; set; } = new();

    /// <summary>
    /// Upstream <c>autoStartSessionEnabled</c>: send one short message through the profile's claude.ai session when
    /// its session window has not started. It uses the account's usage, so it is off by default and only ever sent
    /// by this opt-in, never by usage polling.
    /// </summary>
    public bool AutoStartSessionEnabled { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Unknown providers behave as Anthropic, matching upstream; the raw value is not rewritten.</summary>
    [JsonIgnore]
    public Provider ProviderKind => Provider == Core.Providers.Provider.Codex.RawValue()
        ? Core.Providers.Provider.Codex
        : Core.Providers.Provider.Anthropic;

    /// <summary>
    /// Where usage comes from, or null when nothing is set up. As upstream, a claude.ai session is used
    /// before a Claude Code login, and a profile only ever uses its own provider's credentials.
    /// </summary>
    [JsonIgnore]
    public UsageSourceKind? UsageSource =>
        ProviderKind == Core.Providers.Provider.Codex ? (Codex is null ? null : UsageSourceKind.Codex)
        : ClaudeAi is not null ? UsageSourceKind.ClaudeAi
        : CliLink is not null ? UsageSourceKind.ClaudeCode
        : null;
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

    /// <summary>
    /// Set by the user when this login is also used somewhere the tracker cannot see (another PC, a copied
    /// folder). The tracker then never renews it itself, because that would strand the other copy.
    /// </summary>
    public bool UsedElsewhere { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public string? Identity => AccountUuid ?? EmailAddress;
}

/// <summary>
/// A profile's claude.ai link. The session itself lives in the profile's WebView2 user-data folder
/// (plan.md section 2); only the chosen organisation is kept here.
/// </summary>
public sealed class ClaudeAiLink
{
    public string OrganizationId { get; set; } = "";

    public string? OrganizationName { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A profile's Anthropic Console link. The session key is in the profile's encrypted secret file.</summary>
public sealed class ConsoleLink
{
    public string OrganizationId { get; set; } = "";

    public string? OrganizationName { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>When the Console session cookie expires, if the browser reported it. Not a secret.</summary>
    public DateTimeOffset? SessionKeyExpiry { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// A profile's Codex link: the Codex CLI's own <c>auth.json</c>, read live, or credentials pasted by the
/// user, which are in the profile's encrypted secret file.
/// </summary>
public sealed class CodexLink
{
    public const string AuthFileSource = "authFile";
    public const string ManualSource = "manual";

    /// <summary><see cref="AuthFileSource"/> or <see cref="ManualSource"/>.</summary>
    public string Source { get; set; } = AuthFileSource;

    /// <summary>The Codex home directory holding <c>auth.json</c>, for the auth-file source.</summary>
    public string? CodexHome { get; set; }

    /// <summary>The ChatGPT account the link was made for, so another account's login is never shown as this one.</summary>
    public string? AccountId { get; set; }

    public string? Email { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsManual => Source == ManualSource;

    [JsonIgnore]
    public string? Identity => AccountId ?? Email;
}

/// <summary>Port of upstream <c>NotificationSettings</c> with its <c>init</c> defaults: on, all three thresholds, the default sound.</summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;

    public bool Threshold75Enabled { get; set; } = true;

    public bool Threshold90Enabled { get; set; } = true;

    public bool Threshold95Enabled { get; set; } = true;

    /// <summary>
    /// <c>default</c>, <c>none</c> or a Windows notification sound (<c>im</c>, <c>mail</c>, <c>reminder</c>, <c>sms</c>).
    /// Upstream's other values name macOS sounds; one of those is kept as written and plays the default sound.
    /// </summary>
    public string SoundName { get; set; } = "default";

    public List<int> CustomThresholds { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public Core.Notifications.AlertThresholds Thresholds =>
        new(Enabled, Threshold75Enabled, Threshold90Enabled, Threshold95Enabled, CustomThresholds);
}

/// <summary>One global shortcut: a Windows virtual-key code and <c>RegisterHotKey</c> modifier flags.</summary>
public sealed class KeyCombo
{
    public const int Alt = 0x1, Control = 0x2, Shift = 0x4, Windows = 0x8;

    public int VirtualKey { get; set; }

    /// <summary>A combination of <see cref="Alt"/>, <see cref="Control"/>, <see cref="Shift"/> and <see cref="Windows"/>.</summary>
    public int Modifiers { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public bool SameKeys(KeyCombo? other) => other is not null && other.VirtualKey == VirtualKey && other.Modifiers == Modifiers;
}

/// <summary>Upstream <c>ShortcutAction</c>'s four actions, each with an optional shortcut.</summary>
public sealed class ShortcutSettings
{
    public KeyCombo? TogglePopover { get; set; }

    public KeyCombo? Refresh { get; set; }

    public KeyCombo? OpenSettings { get; set; }

    public KeyCombo? NextProfile { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Port of upstream <c>MultiProfileDisplayConfig</c> with its <c>init</c> defaults.</summary>
public sealed class MultiProfileDisplayConfig
{
    /// <summary><c>concentric</c>, <c>progressBar</c>, <c>compact</c> or <c>percentage</c>.</summary>
    public string IconStyle { get; set; } = "concentric";

    public bool ShowWeek { get; set; } = true;

    public bool ShowProfileLabel { get; set; } = true;

    public bool UseSystemColor { get; set; }

    public bool ShowTimeMarker { get; set; } = true;

    public bool ShowPaceMarker { get; set; } = true;

    public bool UsePaceColoring { get; set; } = true;

    public bool ShowRemainingPercentage { get; set; }

    public bool ShowActiveProfileIndicator { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
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
