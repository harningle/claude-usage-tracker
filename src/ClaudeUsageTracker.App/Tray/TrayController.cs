using System.Globalization;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Tray;

/// <summary>One tray slot: a metric of the profile, or the logo shown before tracking is set up.</summary>
public sealed record TraySlot(string Key, TrayIconSpec? Spec, string Tooltip);

/// <summary>
/// What the tray shows: one icon per enabled metric of the active profile, in upstream's metric order, or
/// one logo icon when tracking is not set up or every metric is off (upstream's "App Logo Mode"). The API
/// credits metric arrives with Console billing in Milestone 3.
/// </summary>
public static class TrayModel
{
    public const string LogoKey = "logo";

    public static IReadOnlyList<TraySlot> Build(
        ProfileSettings? profile, ProfileUsageState? state, bool use24Hour, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var appName = Strings.Get("app.name");
        if (profile is null || state is null || !state.IsLinked)
        {
            return [new TraySlot(LogoKey, null, $"{appName}\n{Strings.Get("windows.tray.not_set_up")}")];
        }

        var config = profile.IconConfig;
        var metrics = config.Metrics
            .Where(m => m.IsEnabled && m.MetricType is "session" or "week")
            .OrderBy(m => m.Order)
            .ToList();
        var banner = PopoverModelBuilder.Banner(state, now, use24Hour, zone, culture);
        if (metrics.Count == 0)
        {
            return [new TraySlot(LogoKey, null, Join(appName, profile.Name, banner?.Message))];
        }

        var usage = state.Usage?.Usage;
        var dimmed = banner is not null || state.Usage is null;
        return metrics.Select(metric =>
        {
            var session = metric.MetricType == "session";
            var used = usage is null ? null : session ? UsageDisplay.EffectiveSessionPercentage(usage, now) : usage.WeeklyPercentage;
            var reset = session ? usage?.SessionResetTime : usage?.WeeklyResetTime;
            var period = session ? UsageDisplay.SessionWindow : UsageDisplay.WeeklyWindow;
            var spec = Spec(metric, config, used, reset, period, now, session ? "S" : "W", dimmed);
            var title = Strings.Get(session ? "menubar.session_usage" : "menubar.all_models");
            var value = used is { } u ? UsageDisplay.PercentText(UsageStatusCalculator.GetDisplayPercentage(u, config.ShowRemainingPercentage)) : "—";
            var resetText = reset is { } r && r > now
                ? Strings.Format("menubar.resets_time", UsageDisplay.AbsoluteResetTime(
                    r, now, zone, use24Hour, culture, Strings.Get("windows.time.today"), Strings.Get("windows.time.tomorrow")))
                : null;
            var tooltip = Join($"{profile.Name} · {title} {value}", resetText, banner?.Message);
            return new TraySlot(metric.MetricType, spec, tooltip);
        }).ToList();
    }

    private static TrayIconSpec Spec(
        MetricIconConfig metric, IconConfiguration config, double? used, DateTimeOffset? reset, TimeSpan period,
        DateTimeOffset now, string label, bool dimmed)
    {
        var style = metric.IconStyle switch
        {
            "progressBar" => TrayIconStyle.ProgressBar,
            "percentageOnly" => TrayIconStyle.PercentageOnly,
            "icon" => TrayIconStyle.Ring,
            "compact" => TrayIconStyle.Dot,
            _ => TrayIconStyle.Battery,
        };
        if (used is not { } value)
        {
            return new TrayIconSpec(style, null, null, null, null, null, config.Monochrome, config.ShowIconNames ? label : null, dimmed);
        }

        var showRemaining = config.ShowRemainingPercentage;
        var display = UsageStatusCalculator.GetDisplayPercentage(value, showRemaining);
        var elapsed = UsageStatusCalculator.ElapsedFraction(reset, period, false, now);
        var marker = config.ShowTimeMarker && elapsed is { } e ? (showRemaining ? 1 - e : e) : (double?)null;
        var pace = config.ShowPaceMarker && elapsed is { } t ? UsageDisplay.Pace(value, t) : null;
        var level = UsageStatusCalculator.CalculateStatus(value, showRemaining, config.UsePaceColoring ? elapsed : null);
        return new TrayIconSpec(style, Math.Clamp(display / 100, 0, 1), display, level, marker, pace,
            config.Monochrome, config.ShowIconNames ? label : null, dimmed);
    }

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
    private Guid? _profileId;

    public TrayController(TrayHost host, Func<bool> taskbarDark, string executablePath)
    {
        _host = host;
        _taskbarDark = taskbarDark;
        _executablePath = executablePath;
        _host.AppearanceChanged += (_, _) => Redraw();
    }

    /// <summary>A first tooltip line, used to label fixture data.</summary>
    public string? TooltipPrefix { get; init; }

    /// <summary>A left click, keyboard selection or context-menu request on one of the icons.</summary>
    public event Action<TrayIcon, TrayActivation>? Activated;

    /// <summary>The icon a keyboard shortcut or second launch should anchor the popover to.</summary>
    public TrayIcon? PrimaryIcon => _slots.Count > 0 && _icons.TryGetValue(_slots[0].Key, out var icon) ? icon : null;

    public void Show(Guid? profileId, IReadOnlyList<TraySlot> slots)
    {
        if (profileId != _profileId)
        {
            // Icon identities belong to a profile (plan.md: stable profile ID plus metric).
            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }

            _icons.Clear();
            _profileId = profileId;
        }

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
                var identity = slot.Key == TrayModel.LogoKey || _profileId is null
                    ? "app|logo"
                    : TrayIdentity.Slot(_profileId.Value, slot.Key);
                icon = _host.CreateIcon(TrayIdentity.For(_executablePath, identity));
                icon.Activated += (sender, activation) => Activated?.Invoke((TrayIcon)sender!, activation);
                _icons.Add(slot.Key, icon);
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
