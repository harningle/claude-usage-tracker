using System.Globalization;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Tray;

/// <summary>One tray slot: a metric of a profile, a profile in multi-profile mode, or the logo shown before tracking is set up.</summary>
/// <param name="Key">The slot's logical identity (a stable profile ID plus metric, never a display name); the tray GUID is derived from it.</param>
public sealed record TraySlot(string Key, TrayIconSpec? Spec, string Tooltip)
{
    /// <summary>The profile the icon belongs to, shown when it is clicked; null for the logo.</summary>
    public Guid? ProfileId { get; init; }
}

/// <summary>
/// What the tray shows. Single-profile mode: one icon per enabled metric of the active profile, in
/// upstream's metric order, or one logo icon when tracking is not set up or every metric is off
/// (upstream's "App Logo Mode"). Multi-profile mode: one icon per profile selected for display, drawn with
/// the multi-profile options (upstream <c>MultiProfileDisplayConfig</c>).
/// </summary>
public static class TrayModel
{
    public const string LogoKey = "app|logo";

    /// <param name="stateOf">The live state of a profile, or null before its first check.</param>
    public static IReadOnlyList<TraySlot> Build(
        AppSettings settings, Func<Guid, ProfileUsageState?> stateOf, bool use24Hour, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var context = new Context(use24Hour, now, zone, culture);
        if (settings.IsMultiProfile && settings.DisplayedProfiles is { Count: > 0 } displayed)
        {
            return [.. displayed.Select(p => context.Multi(p, stateOf(p.Id), settings.MultiProfileDisplayConfig, p.Id == settings.ActiveProfile?.Id))];
        }

        return context.Single(settings.ActiveProfile, settings.ActiveProfile is { } active ? stateOf(active.Id) : null);
    }

    private sealed record Context(bool Use24Hour, DateTimeOffset Now, TimeZoneInfo Zone, CultureInfo Culture)
    {
        public IReadOnlyList<TraySlot> Single(ProfileSettings? profile, ProfileUsageState? state)
        {
            var appName = Strings.Get("app.name");
            if (profile is null || state is null || !state.IsLinked)
            {
                return [new TraySlot(LogoKey, null, $"{appName}\n{Strings.Get("windows.tray.not_set_up")}") { ProfileId = profile?.Id }];
            }

            var config = profile.IconConfig;
            var tracksBilling = profile.Console is not null && profile.ProviderKind.Descriptor().Capabilities.ConsoleBilling;
            var metrics = config.Metrics
                .Where(m => m.IsEnabled && (m.MetricType is "session" or "week" ? state.Source is not null : m.MetricType == "api" && tracksBilling))
                .OrderBy(m => m.Order)
                .ToList();
            var banner = PopoverModelBuilder.Banner(state, Now, Use24Hour, Zone, Culture);
            if (metrics.Count == 0)
            {
                return [new TraySlot(LogoKey, null, Join(appName, profile.Name, banner?.Message)) { ProfileId = profile.Id }];
            }

            var usage = state.Usage?.Usage;
            var dimmed = banner is not null || state.Usage is null;
            return metrics.Select(metric =>
            {
                var style = metric.IconStyle switch
                {
                    "progressBar" => TrayIconStyle.ProgressBar,
                    "percentageOnly" => TrayIconStyle.PercentageOnly,
                    "icon" => TrayIconStyle.Ring,
                    "compact" => TrayIconStyle.Dot,
                    _ => TrayIconStyle.Battery,
                };
                var display = new Display(config.ShowRemainingPercentage, config.ShowTimeMarker, config.ShowPaceMarker, config.UsePaceColoring, config.Monochrome);
                if (metric.MetricType == "api")
                {
                    return Api(profile, state, style, display, config.ShowIconNames, banner);
                }

                var session = metric.MetricType == "session";
                var used = usage is null ? null : session ? UsageDisplay.EffectiveSessionPercentage(usage, Now) : usage.WeeklyPercentage;
                var reset = session ? usage?.SessionResetTime : usage?.WeeklyResetTime;
                var spec = Spec(style, display, used, reset, session ? UsageDisplay.SessionWindow : UsageDisplay.WeeklyWindow,
                    config.ShowIconNames ? (session ? "S" : "W") : null, dimmed);
                var title = Strings.Get(session ? "menubar.session_usage" : "menubar.all_models");
                var tooltip = Join($"{profile.Name} · {title} {Percent(used, display.ShowRemaining)}", ResetText(reset), banner?.Message);
                return new TraySlot(TrayIdentity.Slot(profile.Id, metric.MetricType), spec, tooltip) { ProfileId = profile.Id };
            }).ToList();
        }

        /// <summary>
        /// Upstream's API credits icon. Console billing has no time window, so there is no marker or pace;
        /// the amounts upstream wrote beside the icon are in the tooltip.
        /// </summary>
        private TraySlot Api(ProfileSettings profile, ProfileUsageState state, TrayIconStyle style, Display display, bool label, BannerModel? banner)
        {
            var api = state.Api?.Usage;
            var spec = Spec(style, display with { ShowTimeMarker = false, ShowPaceMarker = false, UsePaceColoring = false },
                api?.UsagePercentage, null, TimeSpan.Zero, label ? "A" : null, api is null || state.ApiFailure is not null);
            var amounts = api is null
                ? "—"
                : $"{CurrencyText.Format(api.UsedAmount, api.Currency, Culture)} / {CurrencyText.Format(api.TotalCredits, api.Currency, Culture)}";
            var tooltip = Join($"{profile.Name} · {Strings.Get("menubar.api_credits")} {amounts}", banner?.Message);
            return new TraySlot(TrayIdentity.Slot(profile.Id, "api"), spec, tooltip) { ProfileId = profile.Id };
        }

        /// <summary>
        /// One profile's icon in multi-profile mode. A square tray icon has no room for upstream's profile
        /// label or two percentages, so the tooltip carries the name and both values (docs/parity.md).
        /// </summary>
        public TraySlot Multi(ProfileSettings profile, ProfileUsageState? state, MultiProfileDisplayConfig config, bool active)
        {
            var key = TrayIdentity.Slot(profile.Id, "multi");
            if (state is null || !state.IsLinked || state.Source is null)
            {
                return new TraySlot(key, null, Join(profile.Name, Strings.Get("windows.tray.not_set_up"))) { ProfileId = profile.Id };
            }

            var usage = state.Usage?.Usage;
            var banner = PopoverModelBuilder.Banner(state, Now, Use24Hour, Zone, Culture);
            var display = new Display(config.ShowRemainingPercentage, config.ShowTimeMarker, config.ShowPaceMarker, config.UsePaceColoring, config.UseSystemColor);
            var style = (config.IconStyle, config.ShowWeek) switch
            {
                ("progressBar", true) => TrayIconStyle.DualBar,
                ("progressBar", false) => TrayIconStyle.ProgressBar,
                ("compact", _) => TrayIconStyle.Dot,
                ("percentage", _) => TrayIconStyle.PercentageOnly,
                (_, true) => TrayIconStyle.Concentric,
                _ => TrayIconStyle.Ring,
            };
            var session = usage is null ? null : UsageDisplay.EffectiveSessionPercentage(usage, Now);
            var spec = Spec(style, display, session, usage?.SessionResetTime, UsageDisplay.SessionWindow, null, banner is not null || usage is null) with
            {
                ActiveIndicator = active && config.ShowActiveProfileIndicator,
            };
            if (config.ShowWeek)
            {
                var week = Spec(style, display, usage?.WeeklyPercentage, usage?.WeeklyResetTime, UsageDisplay.WeeklyWindow, null, false);
                spec = spec with { HasSecondary = true, SecondaryFraction = week.Fraction, SecondaryLevel = week.Level };
            }

            var tooltip = Join(
                profile.Name,
                $"{Strings.Get("menubar.session_usage")} {Percent(session, display.ShowRemaining)}",
                config.ShowWeek ? $"{Strings.Get("menubar.all_models")} {Percent(usage?.WeeklyPercentage, display.ShowRemaining)}" : null,
                banner?.Message);
            return new TraySlot(key, spec, tooltip) { ProfileId = profile.Id };
        }

        private TrayIconSpec Spec(
            TrayIconStyle style, Display display, double? used, DateTimeOffset? reset, TimeSpan period, string? label, bool dimmed)
        {
            if (used is not { } value)
            {
                return new TrayIconSpec(style, null, null, null, null, null, display.Monochrome, label, dimmed);
            }

            var shown = UsageStatusCalculator.GetDisplayPercentage(value, display.ShowRemaining);
            var elapsed = period > TimeSpan.Zero ? UsageStatusCalculator.ElapsedFraction(reset, period, false, Now) : null;
            var marker = display.ShowTimeMarker && elapsed is { } e ? (display.ShowRemaining ? 1 - e : e) : (double?)null;
            var pace = display.ShowPaceMarker && elapsed is { } t ? UsageDisplay.Pace(value, t) : null;
            var level = UsageStatusCalculator.CalculateStatus(value, display.ShowRemaining, display.UsePaceColoring ? elapsed : null);
            return new TrayIconSpec(style, Math.Clamp(shown / 100, 0, 1), shown, level, marker, pace, display.Monochrome, label, dimmed);
        }

        private static string Percent(double? used, bool showRemaining) =>
            used is { } u ? UsageDisplay.PercentText(UsageStatusCalculator.GetDisplayPercentage(u, showRemaining)) : "—";

        private string? ResetText(DateTimeOffset? reset) => reset is { } r && r > Now
            ? Strings.Format("menubar.resets_time", UsageDisplay.AbsoluteResetTime(
                r, Now, Zone, Use24Hour, Culture, Strings.Get("windows.time.today"), Strings.Get("windows.time.tomorrow")))
            : null;
    }

    private sealed record Display(bool ShowRemaining, bool ShowTimeMarker, bool ShowPaceMarker, bool UsePaceColoring, bool Monochrome);

    private static string Join(params string?[] lines) =>
        TrayIcon.Truncate(string.Join('\n', lines.Where(l => !string.IsNullOrEmpty(l))));
}

/// <summary>Keeps the shell's icons in step with <see cref="TrayModel"/>.</summary>
public sealed class TrayController : IDisposable
{
    private readonly TrayHost _host;
    private readonly Func<bool> _taskbarDark;
    private readonly string _executablePath;
    private readonly Dictionary<string, TrayIcon> _icons = [];
    private IReadOnlyList<TraySlot> _slots = [];

    public TrayController(TrayHost host, Func<bool> taskbarDark, string executablePath)
    {
        _host = host;
        _taskbarDark = taskbarDark;
        _executablePath = executablePath;
        _host.AppearanceChanged += (_, _) => Redraw();
    }

    /// <summary>A first tooltip line, used to label fixture data.</summary>
    public string? TooltipPrefix { get; init; }

    /// <summary>A left click, keyboard selection or context-menu request on one of the icons, with the profile the icon belongs to.</summary>
    public event Action<TrayIcon, TrayActivation, Guid?>? Activated;

    /// <summary>The icon a keyboard shortcut or second launch should anchor the popover to.</summary>
    public TrayIcon? PrimaryIcon => _slots.Count > 0 && _icons.TryGetValue(_slots[0].Key, out var icon) ? icon : null;

    public void Show(IReadOnlyList<TraySlot> slots)
    {
        // Icon identities belong to a profile and metric (plan.md), so a slot that is gone takes its icon with it.
        foreach (var key in _icons.Keys.Except(slots.Select(s => s.Key)).ToList())
        {
            _icons[key].Dispose();
            _icons.Remove(key);
        }

        _slots = slots;
        Redraw();
    }

    public void Redraw()
    {
        var size = _host.IconPixelSize;
        var dark = _taskbarDark();
        foreach (var slot in _slots)
        {
            if (!_icons.TryGetValue(slot.Key, out var icon))
            {
                var key = slot.Key;
                icon = _host.CreateIcon(TrayIdentity.For(_executablePath, key));
                icon.Activated += (sender, activation) =>
                    Activated?.Invoke((TrayIcon)sender!, activation, _slots.FirstOrDefault(s => s.Key == key)?.ProfileId);
                _icons.Add(key, icon);
            }

            var handle = IconFactory.Create(size, context =>
            {
                if (slot.Spec is { } spec)
                {
                    TrayIconRenderer.Draw(context, size, spec, dark);
                }
                else
                {
                    TrayIconRenderer.DrawLogo(context, size, dark);
                }
            });
            icon.Update(handle, TooltipPrefix is null ? slot.Tooltip : TrayIcon.Truncate($"[{TooltipPrefix}]\n{slot.Tooltip}"));
        }
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }
}
