using System.Globalization;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.ViewModels;

/// <summary>A usage card. Null values are shown as unavailable, never as 0 %.</summary>
/// <param name="BarFraction">Fill fraction 0-1; null when the value is unavailable.</param>
/// <param name="MarkerFraction">Elapsed-time marker position 0-1, or null for no marker.</param>
public sealed record UsageRowModel(
    string Title,
    string? Tag,
    string? Subtitle,
    string PercentText,
    double? BarFraction,
    UsageStatusLevel? Level,
    double? MarkerFraction,
    PaceStatus? Pace,
    string? ResetText);

public enum BannerTone
{
    Warning,
    Caution,
    Info,
}

public enum BannerAction
{
    None,
    Refresh,
    OpenSettings,
    OpenSetup,
}

/// <param name="Glyph">A Segoe Fluent Icons glyph standing in for upstream's SF Symbol.</param>
public sealed record BannerModel(string Message, BannerTone Tone, BannerAction Action, string Glyph = BannerGlyphs.Warning);

/// <summary>Segoe Fluent Icons code points for the banner symbols (docs/parity.md).</summary>
public static class BannerGlyphs
{
    /// <summary>For upstream's <c>exclamationmark.triangle.fill</c>.</summary>
    public const string Warning = "";

    /// <summary>For upstream's <c>arrow.clockwise.circle.fill</c>.</summary>
    public const string Sync = "";

    /// <summary>For upstream's <c>clock.fill</c>.</summary>
    public const string Clock = "";

    public const string Info = "";
}

/// <param name="Placeholder">Shown instead of rows when there is no usage to show.</param>
public sealed record PopoverModel(IReadOnlyList<UsageRowModel> Rows, BannerModel? Banner, string? Placeholder);

/// <summary>
/// Builds the popover's cards and banner. Port of upstream <c>SmartUsageDashboard</c>, <c>UsageRow</c> and the
/// banner selection in <c>PopoverContentView</c>, with Windows states for what upstream hid: waiting for
/// Claude Code, a changed account, a signed-out CLI, rate limits and data from a previous run.
/// </summary>
public static class PopoverModelBuilder
{
    /// <summary>Upstream's stale threshold: older data shows "Updated %dm ago".</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(300);

    public const int FailureBannerThreshold = 3;

    public static PopoverModel Build(
        ProfileUsageState? state,
        IconConfiguration display,
        PopoverTimeDisplay timeDisplay,
        bool use24Hour,
        DateTimeOffset now,
        TimeZoneInfo zone,
        CultureInfo culture)
    {
        var banner = Banner(state, now, use24Hour, zone, culture);
        if (state?.Usage is not { } cached)
        {
            var placeholder = state is null || !state.IsLinked ? null
                : state.LastAttemptAt is null || state.IsRefreshing ? Strings.Get("windows.popover.loading")
                : Strings.Get("windows.popover.no_data");
            return new PopoverModel([], banner, placeholder);
        }

        var usage = cached.Usage;
        var context = new RowContext(display, timeDisplay, use24Hour, now, zone, culture);
        var rows = new List<UsageRowModel>
        {
            context.Row(
                Strings.Get("menubar.session_usage"), null, Strings.Get("menubar.5_hour_window"),
                UsageDisplay.EffectiveSessionPercentage(usage, now), usage.SessionResetTime, UsageDisplay.SessionWindow),
        };
        if (usage.DesignWeeklyPercentage is { } design)
        {
            rows.Add(context.Row(Strings.Get("menubar.design_usage"), Strings.Get("menubar.weekly"), null,
                design, usage.DesignWeeklyResetTime, null));
        }

        rows.Add(context.Row(Strings.Get("menubar.all_models"), Strings.Get("menubar.weekly"), null,
            usage.WeeklyPercentage, usage.WeeklyResetTime, UsageDisplay.WeeklyWindow));
        if (usage.FableWeeklyPercentage is { } fable)
        {
            rows.Add(context.Row(Strings.Get("menubar.fable_usage"), Strings.Get("menubar.weekly"), null,
                fable, usage.FableWeeklyResetTime, null));
        }

        if (usage.OpusWeeklyPercentage is { } opus)
        {
            // Upstream never shows an Opus reset time.
            rows.Add(context.Row(Strings.Get("menubar.opus_usage"), Strings.Get("menubar.weekly"), null, opus, null, null));
        }

        if (usage.SonnetWeeklyPercentage is { } sonnet)
        {
            // Upstream's Sonnet card has no "Weekly" tag.
            rows.Add(context.Row(Strings.Get("menubar.sonnet_usage"), null, null, sonnet, usage.SonnetWeeklyResetTime, null));
        }

        return new PopoverModel(rows, banner, null);
    }

    /// <summary>The single banner, most important first.</summary>
    public static BannerModel? Banner(ProfileUsageState? state, DateTimeOffset now, bool use24Hour, TimeZoneInfo zone, CultureInfo culture)
    {
        if (state is null || !state.IsLinked)
        {
            return new BannerModel(Strings.Get("windows.banner.not_set_up"), BannerTone.Info, BannerAction.OpenSetup, BannerGlyphs.Info);
        }

        var link = state.Link switch
        {
            LinkStatus.AccountChanged => new BannerModel(Strings.Get("windows.banner.account_changed"), BannerTone.Warning, BannerAction.OpenSettings),
            LinkStatus.AccountUnverified => new BannerModel(Strings.Get("windows.banner.account_unverified"), BannerTone.Warning, BannerAction.OpenSettings),
            LinkStatus.NotSignedIn => new BannerModel(Strings.Get("windows.banner.cli_signed_out"), BannerTone.Warning, BannerAction.Refresh),
            LinkStatus.Unreadable or LinkStatus.Invalid => new BannerModel(Strings.Get("windows.banner.cli_unreadable"), BannerTone.Warning, BannerAction.Refresh),
            LinkStatus.WaitingForCliRefresh => new BannerModel(Strings.Get("windows.banner.waiting_for_cli"), BannerTone.Info, BannerAction.Refresh, BannerGlyphs.Clock),
            _ => null,
        };
        if (link is not null)
        {
            return link;
        }

        if (state.InternalError is { } internalError)
        {
            return new BannerModel(Strings.Format("windows.banner.internal_error", internalError), BannerTone.Warning, BannerAction.Refresh, BannerGlyphs.Warning);
        }

        switch (state.LastFailure)
        {
            case FetchFailure.Forbidden:
                return new BannerModel(Strings.Get("windows.banner.forbidden"), BannerTone.Warning, BannerAction.Refresh);
            case FetchFailure.CredentialsRejected:
                return new BannerModel(Strings.Get("windows.banner.credentials_rejected"), BannerTone.Warning, BannerAction.OpenSettings);
            case FetchFailure.RateLimited when state.RetryNotBefore is { } until && until > now:
                var time = TimeZoneInfo.ConvertTime(until, zone).ToString(use24Hour ? "HH:mm" : "h:mmtt", culture);
                return new BannerModel(Strings.Format("windows.banner.rate_limited", time), BannerTone.Caution, BannerAction.None, BannerGlyphs.Clock);
            case FetchFailure.Challenge:
                return new BannerModel(Strings.Get("windows.banner.challenge"), BannerTone.Caution, BannerAction.Refresh);
        }

        if (state.ConsecutiveFailures >= FailureBannerThreshold)
        {
            return new BannerModel(Strings.Format("popover.banner.refresh_failed", state.ConsecutiveFailures), BannerTone.Caution, BannerAction.Refresh, BannerGlyphs.Sync);
        }

        if (state.Usage is { } usage && now - usage.ObservedAt > StaleAfter)
        {
            var minutes = (int)((now - usage.ObservedAt).TotalSeconds / 60);
            return new BannerModel(Strings.Format("popover.banner.updated_ago", minutes), BannerTone.Warning, BannerAction.Refresh, BannerGlyphs.Clock);
        }

        return null;
    }

    private sealed record RowContext(
        IconConfiguration Display, PopoverTimeDisplay TimeDisplay, bool Use24Hour, DateTimeOffset Now, TimeZoneInfo Zone, CultureInfo Culture)
    {
        /// <summary>Port of upstream <c>UsageRow</c>'s computations.</summary>
        public UsageRowModel Row(string title, string? tag, string? subtitle, double? used, DateTimeOffset? reset, TimeSpan? period)
        {
            if (used is not { } value)
            {
                return new UsageRowModel(title, tag, subtitle, "—", null, null, null, null, ResetText(reset));
            }

            var showRemaining = Display.ShowRemainingPercentage;
            var display = UsageStatusCalculator.GetDisplayPercentage(value, showRemaining);
            var elapsed = period is { } p ? UsageStatusCalculator.ElapsedFraction(reset, p, false, Now) : null;
            var marker = Display.ShowTimeMarker && elapsed is { } e ? (showRemaining ? 1 - e : e) : (double?)null;
            var pace = Display.ShowPaceMarker && elapsed is { } t ? UsageDisplay.Pace(value, t) : null;
            var level = UsageStatusCalculator.CalculateStatus(value, showRemaining, Display.UsePaceColoring ? elapsed : null);
            return new UsageRowModel(
                title, tag, subtitle, UsageDisplay.PercentText(display), Math.Clamp(display / 100, 0, 1), level, marker, pace, ResetText(reset));
        }

        private string? ResetText(DateTimeOffset? reset)
        {
            if (reset is not { } time)
            {
                return null;
            }

            if (time <= Now)
            {
                // Upstream printed "Resets in Reset now"; say what is actually known instead.
                return Strings.Get("windows.popover.window_reset");
            }

            var absolute = UsageDisplay.AbsoluteResetTime(
                time, Now, Zone, Use24Hour, Culture, Strings.Get("windows.time.today"), Strings.Get("windows.time.tomorrow"));
            var remaining = time.TimeRemainingString(Now);
            return TimeDisplay switch
            {
                PopoverTimeDisplay.RemainingTime => Strings.Format("menubar.resets_in", remaining),
                PopoverTimeDisplay.Both => Strings.Format("menubar.resets_both", remaining, absolute),
                _ => Strings.Format("menubar.resets_time", absolute),
            };
        }
    }
}
