using System.Globalization;
using System.Text;

namespace ClaudeUsageTracker.Core.Statusline;

/// <summary>What one status line invocation knows, already resolved from stdin, the cache and the repository.</summary>
public sealed record StatuslineContent(
    string? DirectoryName,
    string? Branch,
    string? ModelName,
    long ContextTokens,
    long? ContextWindowSize,
    StatuslineUsage Usage);

/// <summary>
/// Renders the status line. Port of the bash script upstream installs (<c>StatuslineService.bashScript</c>):
/// the same components, order, separators, bars, pace markers and ANSI colours. Differences (docs/parity.md):
/// a per-element pace colour is used, as upstream's own preview shows it, where the script ignored it; a
/// stale value is labelled; and a value nobody reported is "~" rather than 0.
/// </summary>
public static class StatuslineRenderer
{
    public const string Reset = "\e[0m";
    public const string StaleLabel = " (stale)";

    private const int BarWidth = 10;
    private const char Filled = '▓', Empty = '░', Marker = '┃';
    private static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    // Upstream judges the pace only after this much of a window: 3 % of the session, 0.5 % of the week.
    private const long SessionPaceSeconds = 540, WeeklyPaceSeconds = 3024;

    // Upstream's ten usage levels (dark green to deep red) and six pace tiers, as 256-colour codes.
    private static readonly string[] StandardLevels =
        [Indexed(22), Indexed(28), Indexed(34), Indexed(100), Indexed(142), Indexed(178), Indexed(172), Indexed(166), Indexed(160), Indexed(124)];

    private static readonly string[] StandardPace = [Indexed(34), Indexed(37), Indexed(178), Indexed(208), Indexed(160), Indexed(135)];

    /// <summary>The status line text, without a trailing line break.</summary>
    public static string Render(StatuslineConfig config, StatuslineContent content, DateTimeOffset now, TimeZoneInfo zone)
    {
        var colours = Palette.For(config);
        var parts = new List<string>(8);

        if (config.ShowDirectory && !string.IsNullOrEmpty(content.DirectoryName))
        {
            parts.Add($"{colours.Directory}{content.DirectoryName}{Reset}");
        }

        if (config.ShowBranch && !string.IsNullOrEmpty(content.Branch))
        {
            parts.Add($"{colours.Branch}⎇ {content.Branch}{Reset}");
        }

        if (config.ShowModel && !string.IsNullOrEmpty(content.ModelName))
        {
            parts.Add($"{colours.Model}{content.ModelName}{Reset}");
        }

        if (config.ShowProfile && !string.IsNullOrEmpty(content.Usage.ProfileName))
        {
            parts.Add($"{colours.Profile}{content.Usage.ProfileName}{Reset}");
        }

        if (config.ShowContext && content.ContextWindowSize is { } size and > 0)
        {
            parts.Add(Context(config, colours, content.ContextTokens, size));
        }

        if (config.ShowUsage)
        {
            parts.Add(Session(config, colours, content.Usage.Session, now, zone));
            if (config.ShowWeekly && content.Usage.Weekly is { } weekly)
            {
                parts.Add(Weekly(config, colours, weekly, now, zone));
            }

            if (config.ShowExtraUsage && content.Usage.Cost is { } cost)
            {
                var percentage = (int)Math.Clamp(Math.Truncate(cost.Used / cost.Limit * 100), 0, 100);
                var colour = colours.ExtraOverride ?? colours.Level(percentage);
                parts.Add($"{colour}{cost.Used.ToString("0.00", CultureInfo.InvariantCulture)} {cost.Currency}{Reset}");
            }
        }

        return string.Join($"{colours.Separator} │ {Reset}", parts);
    }

    private static string Context(StatuslineConfig config, Palette colours, long tokens, long size)
    {
        var percentage = tokens * 100 / size;
        var colour = percentage <= 50 ? colours.Context : percentage <= 75 ? colours.Model : colours.Levels[8];
        var label = config.ShowContextLabel ? "Ctx: " : "";
        var value = !config.ContextAsTokens ? $"{percentage}%"
            : tokens >= 1000 ? $"{tokens / 1000}K"
            : tokens.ToString(CultureInfo.InvariantCulture);
        return $"{colour}{label}{value}{Reset}";
    }

    private static string Session(StatuslineConfig config, Palette colours, StatuslineValue? session, DateTimeOffset now, TimeZoneInfo zone)
    {
        var label = config.ShowUsageLabel ? "Usage: " : "";
        if (session is null)
        {
            // Upstream's "no data" text.
            return $"{colours.Model}{label}~{Reset}";
        }

        var percentage = Whole(session.Percentage);
        var colour = colours.Level(percentage);
        var bar = config.ShowProgressBar
            ? Bar(percentage, colour, config.ShowPaceMarker ? session.ResetsAt : null, SessionWindow, SessionPaceSeconds, now, colours, config.PaceMarkerStepColors)
            : "";
        var reset = "";
        if (config.ShowResetTime && session.ResetsAt is { } resetsAt)
        {
            var time = Local(resetsAt, zone).ToString(config.Use24HourTime ? "HH:mm" : "hh:mm tt", CultureInfo.InvariantCulture);
            reset = config.ShowResetLabel ? $" → Reset: {time}" : $" → {time}";
        }

        return $"{colour}{label}{percentage}%{bar}{reset}{Stale(session)}{Reset}";
    }

    private static string Weekly(StatuslineConfig config, Palette colours, StatuslineValue weekly, DateTimeOffset now, TimeZoneInfo zone)
    {
        var percentage = Whole(weekly.Percentage);
        var colour = colours.WeeklyOverride ?? colours.Level(percentage);
        var bar = config.ShowWeeklyBar
            ? Bar(percentage, colour, config.ShowWeeklyPaceMarker ? weekly.ResetsAt : null, WeeklyWindow, WeeklyPaceSeconds, now, colours, config.PaceMarkerStepColors)
            : "";
        var reset = config.ShowWeeklyResetTime && weekly.ResetsAt is { } resetsAt
            ? " → " + Local(resetsAt, zone).ToString(config.Use24HourTime ? "ddd HH:mm" : "ddd hh:mm tt", CultureInfo.InvariantCulture)
            : "";
        var label = config.ShowWeeklyLabel ? "Weekly: " : "";
        return $"{colour}{label}{percentage}%{bar}{reset}{Stale(weekly)}{Reset}";
    }

    /// <summary>A leading space, ten blocks and, inside a running window, the pace marker at the elapsed share of it.</summary>
    private static string Bar(
        int percentage, string colour, DateTimeOffset? resetsAt, TimeSpan window, long paceSeconds, DateTimeOffset now, Palette colours,
        bool stepColours)
    {
        var filled = percentage <= 0 ? 0 : percentage >= 100 ? BarWidth : Math.Clamp((percentage * 10 + 50) / 100, 0, BarWidth);
        var blocks = new string(Filled, filled) + new string(Empty, BarWidth - filled);
        var windowSeconds = (long)window.TotalSeconds;
        if (resetsAt is not { } reset || (long)(reset - now).TotalSeconds is not (> 0 and var remaining) || remaining >= windowSeconds)
        {
            return " " + blocks;
        }

        var elapsed = windowSeconds - remaining;
        var position = (int)Math.Clamp((elapsed * 10 + windowSeconds / 2) / windowSeconds, 0, BarWidth - 1);
        var markerColour = colours.PaceOverride
                           ?? (stepColours && !colours.IsMonochrome && elapsed >= paceSeconds
                               ? colours.Pace[Tier(percentage * windowSeconds / elapsed)]
                               : colour);
        return $" {blocks[..position]}{markerColour}{Marker}{Reset}{colour}{blocks[(position + 1)..]}";
    }

    /// <summary>The pace tier of a projected end-of-window percentage.</summary>
    private static int Tier(long projected) => projected switch
    {
        < 50 => 0,
        < 75 => 1,
        < 90 => 2,
        < 100 => 3,
        < 120 => 4,
        _ => 5,
    };

    private static string Stale(StatuslineValue value) => value.Source == StatuslineSource.StaleCache ? StaleLabel : "";

    /// <summary>Upstream writes <c>Int(percentage)</c>: truncation toward zero.</summary>
    private static int Whole(double percentage) => (int)Math.Clamp(Math.Truncate(percentage), int.MinValue, int.MaxValue);

    /// <summary>The local time rounded to the nearest minute, so a reset at 6:59:45 reads 7:00 as upstream.</summary>
    private static DateTimeOffset Local(DateTimeOffset time, TimeZoneInfo zone)
    {
        var seconds = time.ToUnixTimeSeconds();
        var part = seconds % 60;
        return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(part >= 30 ? seconds + 60 - part : seconds - part), zone);
    }

    private static string Indexed(int colour) => $"\e[38;5;{colour}m";

    /// <summary>A hex colour as a 24-bit ANSI foreground code; text that is not "#RRGGBB" gives no colour.</summary>
    public static string HexToAnsi(string? hex)
    {
        var digits = hex?.Trim().TrimStart('#');
        return digits is { Length: 6 } && int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? $"\e[38;2;{rgb >> 16};{(rgb >> 8) & 0xFF};{rgb & 0xFF}m"
            : "";
    }

    private sealed record Palette(
        string Directory, string Branch, string Separator, string Model, string Context, string Profile,
        string[] Levels, string[] Pace, bool IsMonochrome, string? PaceOverride, string? WeeklyOverride, string? ExtraOverride)
    {
        public string Level(int percentage) => Levels[Math.Clamp((percentage - 1) / 10, 0, 9)];

        public static Palette For(StatuslineConfig config)
        {
            switch (config.ColorMode)
            {
                case StatuslineConfig.Monochrome:
                    return Uniform("", monochrome: true);
                case StatuslineConfig.SingleColor:
                    // Upstream keeps the real pace tiers in this mode when they are switched on.
                    return Uniform(HexToAnsi(config.SingleColorHex), monochrome: false) with { Pace = StandardPace };
                case StatuslineConfig.PerElement:
                    var element = config.ElementColors;
                    return new Palette(
                        HexToAnsi(element.DirectoryHex), HexToAnsi(element.BranchHex), HexToAnsi(element.SeparatorHex),
                        HexToAnsi(element.ModelHex), HexToAnsi(element.ContextHex), HexToAnsi(element.ProfileHex),
                        Override(element.UsageBaseHex) is { } usage ? [.. Enumerable.Repeat(usage, 10)] : StandardLevels,
                        StandardPace, false, Override(element.PaceBaseHex), Override(element.WeeklyBaseHex), Override(element.ExtraUsageBaseHex));
                default:
                    return new Palette(
                        "\e[0;34m", "\e[0;32m", "\e[0;90m", "\e[0;33m", "\e[0;36m", "\e[0;35m",
                        StandardLevels, StandardPace, false, null, null, null);
            }
        }

        private static Palette Uniform(string colour, bool monochrome) => new(
            colour, colour, colour, colour, colour, colour,
            [.. Enumerable.Repeat(colour, 10)], [.. Enumerable.Repeat(colour, 6)], monochrome, null, null, null);

        private static string? Override(string? hex) => HexToAnsi(hex) is { Length: > 0 } ansi ? ansi : null;
    }
}
