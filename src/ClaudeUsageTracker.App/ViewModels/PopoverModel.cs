using System.Globalization;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Providers;
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

    /// <summary>Open the Claude CLI page, where an installed integration is repaired.</summary>
    OpenIntegration,
}

/// <param name="Glyph">A Segoe Fluent Icons glyph standing in for upstream's SF Symbol.</param>
public sealed record BannerModel(string Message, BannerTone Tone, BannerAction Action, string Glyph = BannerGlyphs.Warning);

/// <summary>Segoe Fluent Icons code points for the banner symbols (docs/parity.md).</summary>
public static class BannerGlyphs
{
    /// <summary>For upstream's <c>exclamationmark.triangle.fill</c>.</summary>
    public const string Warning = "\uE7BA";

    /// <summary>For upstream's <c>arrow.clockwise.circle.fill</c>.</summary>
    public const string Sync = "\uE895";

    /// <summary>For upstream's <c>clock.fill</c>.</summary>
    public const string Clock = "\uE917";

    public const string Info = "\uE946";
}

/// <summary>Upstream's API Credits card: Console spend against spend plus remaining prepaid credits.</summary>
/// <param name="Note">Why the values may be out of date, e.g. an expired Console session.</param>
public sealed record ApiCreditsModel(
    string PercentText, double BarFraction, UsageStatusLevel Level, string UsedText, string RemainingText, string? ResetText, string? Note);

public sealed record CostLine(string Name, string Cost) : ListItem;

/// <param name="Glyph">A Segoe Fluent Icons glyph for the kind of key (upstream's SF Symbols).</param>
public sealed record CostSourceModel(string Name, string Glyph, string Total, IReadOnlyList<CostLine> Models) : ListItem;

/// <summary>
/// Upstream's API Cost card: this month's total, the cost per day, and either the cost per API key (when
/// there are several) or the cost per model.
/// </summary>
public sealed record ApiCostModel(
    string TotalText, IReadOnlyList<(DateOnly Date, decimal Amount)> Daily, string Currency,
    IReadOnlyList<CostSourceModel> Sources, IReadOnlyList<CostLine> Models);

/// <param name="Placeholder">Shown instead of rows when there is no usage to show.</param>
/// <param name="OverageBalance">Upstream's balance line under the Extra Usage card.</param>
/// <param name="Plan">The provider's plan name, for providers that report one (Codex).</param>
/// <param name="Credits">The provider's credit balance text, for providers that report one (Codex).</param>
public sealed record PopoverModel(IReadOnlyList<UsageRowModel> Rows, BannerModel? Banner, string? Placeholder)
{
    public string? OverageBalance { get; init; }

    public string? Plan { get; init; }

    public string? Credits { get; init; }

    public ApiCreditsModel? ApiCredits { get; init; }

    public ApiCostModel? ApiCost { get; init; }
}

/// <summary>
/// Builds the popover's cards and banner. Port of upstream <c>SmartUsageDashboard</c>, <c>UsageRow</c>, the
/// API cards and the banner selection in <c>PopoverContentView</c>, with Windows states for what upstream
/// hid: waiting for Claude Code, a changed account, a signed-out CLI or browser session, rate limits and
/// data from a previous run.
/// </summary>
public static class PopoverModelBuilder
{
    /// <summary>Upstream's stale threshold: older data shows "Updated %dm ago".</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(300);

    public const int FailureBannerThreshold = 3;

    /// <param name="multi">The multi-profile display options, which replace the profile's own in multi-profile mode, as upstream.</param>
    public static PopoverModel Build(
        ProfileUsageState? state,
        IconConfiguration display,
        PopoverTimeDisplay timeDisplay,
        bool use24Hour,
        DateTimeOffset now,
        TimeZoneInfo zone,
        CultureInfo culture,
        Provider provider = Provider.Anthropic,
        MultiProfileDisplayConfig? multi = null)
    {
        var banner = Banner(state, now, use24Hour, zone, culture);
        var context = new RowContext(
            multi?.ShowRemainingPercentage ?? display.ShowRemainingPercentage,
            multi?.ShowTimeMarker ?? display.ShowTimeMarker,
            multi?.ShowPaceMarker ?? display.ShowPaceMarker,
            multi?.UsePaceColoring ?? display.UsePaceColoring,
            timeDisplay, use24Hour, now, zone, culture);
        var capabilities = provider.Descriptor().Capabilities;
        var api = capabilities.ConsoleBilling ? state?.Api : null;
        var apiCredits = api is null ? null : context.ApiCredits(api.Usage, state!.ApiFailure);
        var apiCost = api?.Usage is { ApiTokenCostCents: > 0 } billing ? ApiCost(billing, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).Date), culture) : null;

        if (state?.Usage is not { } cached)
        {
            var placeholder = state is null || !state.IsLinked || state.Source is null ? null
                : state.LastAttemptAt is null || state.IsRefreshing ? Strings.Get("windows.popover.loading")
                : Strings.Get("windows.popover.no_data");
            return new PopoverModel([], banner, placeholder) { ApiCredits = apiCredits, ApiCost = apiCost };
        }

        var usage = cached.Usage;
        var rows = new List<UsageRowModel>
        {
            context.Row(
                Strings.Get("menubar.session_usage"), null, Strings.Get("menubar.5_hour_window"),
                UsageDisplay.EffectiveSessionPercentage(usage, now), usage.SessionResetTime, UsageDisplay.SessionWindow),
        };
        if (capabilities.PerModelBreakdown && usage.DesignWeeklyPercentage is { } design)
        {
            rows.Add(context.Row(Strings.Get("menubar.design_usage"), Strings.Get("menubar.weekly"), null,
                design, usage.DesignWeeklyResetTime, null));
        }

        rows.Add(context.Row(Strings.Get("menubar.all_models"), Strings.Get("menubar.weekly"), null,
            usage.WeeklyPercentage, usage.WeeklyResetTime, UsageDisplay.WeeklyWindow));
        if (capabilities.PerModelBreakdown && usage.FableWeeklyPercentage is { } fable)
        {
            rows.Add(context.Row(Strings.Get("menubar.fable_usage"), Strings.Get("menubar.weekly"), null,
                fable, usage.FableWeeklyResetTime, null));
        }

        if (capabilities.PerModelBreakdown && usage.OpusWeeklyPercentage is { } opus)
        {
            // Upstream never shows an Opus reset time.
            rows.Add(context.Row(Strings.Get("menubar.opus_usage"), Strings.Get("menubar.weekly"), null, opus, null, null));
        }

        if (capabilities.PerModelBreakdown && usage.SonnetWeeklyPercentage is { } sonnet)
        {
            // Upstream's Sonnet card has no "Weekly" tag.
            rows.Add(context.Row(Strings.Get("menubar.sonnet_usage"), null, null, sonnet, usage.SonnetWeeklyResetTime, null));
        }

        // Upstream's Extra Usage card: shown when spent, limit and currency are known and the limit is positive.
        // Amounts keep the currency's own decimal places (upstream always printed two).
        string? balance = null;
        if (usage is { CostUsed: { } used, CostLimit: { } limit, CostCurrency: { } currency } && limit > 0)
        {
            var subtitle = $"{used.ToString(CultureInfo.InvariantCulture)} / {limit.ToString(CultureInfo.InvariantCulture)} {currency}";
            rows.Add(context.Row(Strings.Get("menubar.extra_usage"), null, subtitle, (double)(used / limit * 100), null, null));
            if (usage is { OverageBalance: { } remaining, OverageBalanceCurrency: { } balanceCurrency })
            {
                balance = $"{remaining.ToString(CultureInfo.InvariantCulture)} {balanceCurrency.ToUpperInvariant()}";
            }
        }

        return new PopoverModel(rows, banner, null)
        {
            OverageBalance = balance,
            // Upstream shows the plan with underscores as spaces and each word capitalised.
            Plan = usage.PlanType is { Length: > 0 } plan ? culture.TextInfo.ToTitleCase(plan.Replace('_', ' ')) : null,
            Credits = usage.CreditsUnlimited == true ? Strings.Get("popover.credits_unlimited")
                : usage.CreditsBalance is { } credits ? Strings.Format("popover.credits_balance", credits.ToString("0.00", CultureInfo.InvariantCulture))
                : null,
            ApiCredits = apiCredits,
            ApiCost = apiCost,
        };
    }

    /// <summary>The single banner, most important first.</summary>
    public static BannerModel? Banner(ProfileUsageState? state, DateTimeOffset now, bool use24Hour, TimeZoneInfo zone, CultureInfo culture)
    {
        if (state is null || !state.IsLinked)
        {
            return new BannerModel(Strings.Get("windows.banner.not_set_up"), BannerTone.Info, BannerAction.OpenSetup, BannerGlyphs.Info);
        }

        if (LinkBanner(state) is { } link)
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
                return new BannerModel(state.Source switch
                {
                    UsageSourceKind.ClaudeAi => Strings.Get("popover.banner.credentials_expired"),
                    UsageSourceKind.Codex => CodexExpired(),
                    _ => Strings.Get("windows.banner.credentials_rejected"),
                }, BannerTone.Warning, BannerAction.OpenSettings);
            case FetchFailure.RateLimited when state.RetryNotBefore is { } until && until > now:
                var time = TimeZoneInfo.ConvertTime(until, zone).ToString(use24Hour ? "HH:mm" : "h:mmtt", culture);
                return new BannerModel(Strings.Format("windows.banner.rate_limited", time), BannerTone.Caution, BannerAction.None, BannerGlyphs.Clock);
            case FetchFailure.Challenge:
                return new BannerModel(Strings.Get("windows.banner.challenge"), BannerTone.Caution,
                    state.Source == UsageSourceKind.ClaudeAi ? BannerAction.OpenSettings : BannerAction.Refresh);
        }

        if (state.Notice == LinkNotice.HandBackPending)
        {
            return new BannerModel(Strings.Get("windows.banner.hand_back_pending"), BannerTone.Caution, BannerAction.OpenSettings, BannerGlyphs.Info);
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

    /// <summary>What the state of the profile's login means, in the words of its source.</summary>
    private static BannerModel? LinkBanner(ProfileUsageState state) => (state.Source, state.Link) switch
    {
        (UsageSourceKind.ClaudeAi, LinkStatus.NotSignedIn) =>
            new BannerModel(Strings.Get("popover.banner.credentials_expired"), BannerTone.Warning, BannerAction.OpenSettings),
        (UsageSourceKind.ClaudeAi, LinkStatus.AccountChanged) =>
            new BannerModel(Strings.Get("windows.banner.claude_ai_other_account"), BannerTone.Warning, BannerAction.OpenSettings),

        (UsageSourceKind.Codex, LinkStatus.NotSignedIn) =>
            new BannerModel($"{Strings.Get("error.codex_credentials_not_found")}. {Strings.Get("error.codex_credentials_not_found.suggestion")}.",
                BannerTone.Warning, BannerAction.OpenSettings),
        (UsageSourceKind.Codex, LinkStatus.Unreadable or LinkStatus.Invalid) =>
            new BannerModel(Strings.Get("error.codex_credentials_invalid"), BannerTone.Warning, BannerAction.Refresh),
        (UsageSourceKind.Codex, LinkStatus.AccountChanged or LinkStatus.AccountUnverified) =>
            new BannerModel(Strings.Get("windows.banner.codex_account_changed"), BannerTone.Warning, BannerAction.OpenSettings),
        (UsageSourceKind.Codex, LinkStatus.WaitingForCliRefresh) =>
            new BannerModel(Strings.Get("windows.banner.waiting_for_codex"), BannerTone.Info, BannerAction.Refresh, BannerGlyphs.Clock),
        (UsageSourceKind.Codex, LinkStatus.LoginRejected) =>
            new BannerModel(CodexExpired(), BannerTone.Warning, BannerAction.OpenSettings),
        (UsageSourceKind.Codex, LinkStatus.RefreshFailed) =>
            new BannerModel(Strings.Get("error.codex_auth_refresh_failed"), BannerTone.Caution, BannerAction.Refresh, BannerGlyphs.Sync),

        (_, LinkStatus.AccountChanged) => new BannerModel(Strings.Get("windows.banner.account_changed"), BannerTone.Warning, BannerAction.OpenSettings),
        (_, LinkStatus.AccountUnverified) => new BannerModel(Strings.Get("windows.banner.account_unverified"), BannerTone.Warning, BannerAction.OpenSettings),
        (_, LinkStatus.NotSignedIn) => new BannerModel(Strings.Get("windows.banner.cli_signed_out"), BannerTone.Warning, BannerAction.Refresh),
        (_, LinkStatus.Unreadable or LinkStatus.Invalid) => new BannerModel(Strings.Get("windows.banner.cli_unreadable"), BannerTone.Warning, BannerAction.Refresh),
        (_, LinkStatus.WaitingForCliRefresh) =>
            new BannerModel(Strings.Get("windows.banner.waiting_for_cli"), BannerTone.Info, BannerAction.Refresh, BannerGlyphs.Clock),
        (_, LinkStatus.LoginRejected) => new BannerModel(Strings.Get("windows.banner.login_rejected"), BannerTone.Warning, BannerAction.OpenSettings),
        (_, LinkStatus.RefreshFailed) =>
            new BannerModel(Strings.Get("windows.banner.renewal_failed"), BannerTone.Caution, BannerAction.Refresh, BannerGlyphs.Sync),
        _ => null,
    };

    private static string CodexExpired() =>
        $"{Strings.Get("error.codex_auth_expired")}. {Strings.Get("error.codex_auth_expired.suggestion")}.";

    /// <summary>Upstream's API Cost card. The daily chart spans this month up to today, with days without cost at zero.</summary>
    private static ApiCostModel ApiCost(ApiUsage usage, DateOnly today, CultureInfo culture)
    {
        string Money(decimal cents) => CurrencyText.FormatCents(cents, usage.Currency, culture);
        List<CostLine> Lines(IEnumerable<KeyValuePair<string, decimal>> costs) => [.. costs.Select(c => new CostLine(c.Key, Money(c.Value)))];
        var daily = usage.SortedDailyCosts().ToDictionary(d => d.Date, d => d.Cents);
        var first = new DateOnly(today.Year, today.Month, 1);
        return new ApiCostModel(
            Money(usage.ApiTokenCostCents ?? 0),
            daily.Count == 0 ? [] : [.. Enumerable.Range(0, today.DayNumber - first.DayNumber + 1)
                .Select(offset => first.AddDays(offset))
                .Select(date => (date, daily.GetValueOrDefault(date) / 100m))],
            usage.Currency,
            usage.HasMultipleSources
                ? [.. usage.SortedCostSources().Select(s => new CostSourceModel(
                    s.KeyName,
                    s.SourceType switch { ApiCostSourceType.Cli => "\uE756", ApiCostSourceType.Api => "\uE943", _ => "\uE8D7" },
                    Money(s.TotalCents),
                    Lines(s.SortedModelCosts())))]
                : [],
            usage.HasMultipleSources ? [] : Lines(usage.SortedModelCosts()));
    }

    private sealed record RowContext(
        bool ShowRemaining, bool ShowTimeMarker, bool ShowPaceMarker, bool UsePaceColoring,
        PopoverTimeDisplay TimeDisplay, bool Use24Hour, DateTimeOffset Now, TimeZoneInfo Zone, CultureInfo Culture)
    {
        /// <summary>Port of upstream <c>UsageRow</c>'s computations.</summary>
        public UsageRowModel Row(string title, string? tag, string? subtitle, double? used, DateTimeOffset? reset, TimeSpan? period)
        {
            if (used is not { } value)
            {
                return new UsageRowModel(title, tag, subtitle, "—", null, null, null, null, ResetText(reset));
            }

            var display = UsageStatusCalculator.GetDisplayPercentage(value, ShowRemaining);
            var elapsed = period is { } p ? UsageStatusCalculator.ElapsedFraction(reset, p, false, Now) : null;
            var marker = ShowTimeMarker && elapsed is { } e ? (ShowRemaining ? 1 - e : e) : (double?)null;
            var pace = ShowPaceMarker && elapsed is { } t ? UsageDisplay.Pace(value, t) : null;
            var level = UsageStatusCalculator.CalculateStatus(value, ShowRemaining, UsePaceColoring ? elapsed : null);
            return new UsageRowModel(
                title, tag, subtitle, UsageDisplay.PercentText(display), Math.Clamp(display / 100, 0, 1), level, marker, pace, ResetText(reset));
        }

        /// <summary>Port of upstream <c>APIUsageCard</c>: no pace, and a reset line only while the reset lies ahead.</summary>
        public ApiCreditsModel ApiCredits(ApiUsage usage, FetchFailure? failure)
        {
            var display = UsageStatusCalculator.GetDisplayPercentage(usage.UsagePercentage, ShowRemaining);
            return new ApiCreditsModel(
                UsageDisplay.PercentText(display),
                Math.Clamp(display / 100, 0, 1),
                UsageStatusCalculator.CalculateStatus(usage.UsagePercentage, ShowRemaining),
                CurrencyText.Format(usage.UsedAmount, usage.Currency, Culture),
                CurrencyText.Format(usage.RemainingAmount, usage.Currency, Culture),
                usage.ResetsAt is { } reset && reset > Now ? ResetText(reset) : null,
                failure switch
                {
                    null => null,
                    FetchFailure.CredentialsRejected => Strings.Get("windows.api.note.session_expired"),
                    _ => Strings.Get("windows.api.note.not_updated"),
                });
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
