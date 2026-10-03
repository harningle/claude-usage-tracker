using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTracker.Core.Statusline;

/// <summary>
/// Per-element colours for the <c>perElement</c> colour mode. Port of upstream
/// <c>StatuslineElementColors</c>: the defaults match the palette of the <c>colored</c> mode, and a null
/// base colour keeps that element's gradient or tiers.
/// </summary>
public sealed class StatuslineElementColors
{
    public string DirectoryHex { get; set; } = "#0000EE";

    public string BranchHex { get; set; } = "#00BB00";

    public string ModelHex { get; set; } = "#BBBB00";

    public string ProfileHex { get; set; } = "#BB00BB";

    public string ContextHex { get; set; } = "#00BBBB";

    public string SeparatorHex { get; set; } = "#808080";

    /// <summary>One colour instead of the ten-level usage gradient.</summary>
    public string? UsageBaseHex { get; set; }

    /// <summary>One colour instead of the six pace tiers.</summary>
    public string? PaceBaseHex { get; set; }

    public string? WeeklyBaseHex { get; set; }

    public string? ExtraUsageBaseHex { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// What the status line shows and in which colours. Port of upstream's statusline preferences
/// (<c>SharedDataStore</c>) with their defaults; key names follow upstream's. The app keeps it in its
/// settings and, once applied, in the non-secret file the helper reads.
/// </summary>
public sealed class StatuslineConfig
{
    public const int CurrentVersion = 1;

    public const string Colored = "colored";
    public const string Monochrome = "monochrome";
    public const string SingleColor = "singleColor";
    public const string PerElement = "perElement";

    public int Version { get; set; } = CurrentVersion;

    public bool ShowModel { get; set; } = true;

    public bool ShowDirectory { get; set; } = true;

    public bool ShowBranch { get; set; } = true;

    public bool ShowContext { get; set; } = true;

    public bool ContextAsTokens { get; set; }

    public bool ShowUsage { get; set; } = true;

    public bool ShowProgressBar { get; set; } = true;

    public bool ShowPaceMarker { get; set; } = true;

    public bool PaceMarkerStepColors { get; set; }

    public bool ShowResetTime { get; set; } = true;

    public bool Use24HourTime { get; set; }

    public bool ShowContextLabel { get; set; } = true;

    public bool ShowUsageLabel { get; set; } = true;

    public bool ShowResetLabel { get; set; } = true;

    public bool ShowProfile { get; set; }

    public bool ShowWeekly { get; set; }

    public bool ShowWeeklyBar { get; set; } = true;

    public bool ShowWeeklyPaceMarker { get; set; } = true;

    public bool ShowWeeklyResetTime { get; set; } = true;

    public bool ShowWeeklyLabel { get; set; } = true;

    public bool ShowExtraUsage { get; set; }

    /// <summary><see cref="Colored"/>, <see cref="Monochrome"/>, <see cref="SingleColor"/> or <see cref="PerElement"/>; anything else behaves as <see cref="Colored"/>.</summary>
    public string ColorMode { get; set; } = Colored;

    public string SingleColorHex { get; set; } = "#00BFFF";

    public StatuslineElementColors ElementColors { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Upstream refuses to apply a status line that would show nothing.</summary>
    [JsonIgnore]
    public bool ShowsAnything =>
        ShowModel || ShowDirectory || ShowBranch || ShowContext || ShowUsage || ShowProfile || ShowWeekly || ShowExtraUsage;
}
