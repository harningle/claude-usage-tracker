using System.Globalization;
using System.Reflection;
using System.Windows.Media;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.ViewModels;

/// <summary>An icon style option with a preview drawn by the tray renderer itself.</summary>
public sealed record IconStyleOption(string RawValue, string Name, ImageSource Preview);

public sealed record DetailRow(string Label, string Value);

/// <summary>
/// The settings window's state. Like upstream, every control persists on change; unlike upstream, a
/// changed refresh interval applies at once instead of after a profile switch or relaunch.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _changed;
    private ProfileUsageState? _state;
    private CliSnapshot? _snapshot;

    public SettingsViewModel(AppSettings settings, Action changed, bool taskbarDark)
    {
        _settings = settings;
        _changed = changed;
        IconStyles =
        [
            Option("battery", "windows.icon_style.battery", TrayIconStyle.Battery, taskbarDark),
            Option("progressBar", "windows.icon_style.progress_bar", TrayIconStyle.ProgressBar, taskbarDark),
            Option("percentageOnly", "windows.icon_style.percentage", TrayIconStyle.PercentageOnly, taskbarDark),
            Option("icon", "windows.icon_style.icon", TrayIconStyle.Ring, taskbarDark),
            Option("compact", "windows.icon_style.compact", TrayIconStyle.Dot, taskbarDark),
        ];
    }

    public IReadOnlyList<IconStyleOption> IconStyles { get; }

    private ProfileSettings? Profile => _settings.ActiveProfile;
    private IconConfiguration Icon => Profile?.IconConfig ?? new IconConfiguration();

    public bool HasProfile => Profile is not null;
    public string ProfileName => Profile?.Name ?? Strings.Get("popover.no_profile");

    // Appearance (per profile), upstream AppearanceSettingsView.

    public bool Monochrome { get => Icon.Monochrome; set => Edit(() => Icon.ColorMode = value ? "monochrome" : "multiColor"); }
    public bool ShowIconNames { get => Icon.ShowIconNames; set => Edit(() => Icon.ShowIconNames = value); }
    public bool ShowRemaining { get => Icon.ShowRemainingPercentage; set => Edit(() => Icon.ShowRemainingPercentage = value); }
    public bool ShowTimeMarker { get => Icon.ShowTimeMarker; set => Edit(() => Icon.ShowTimeMarker = value); }
    public bool ShowPaceMarker { get => Icon.ShowPaceMarker; set => Edit(() => Icon.ShowPaceMarker = value); }
    public bool UsePaceColoring { get => Icon.UsePaceColoring; set => Edit(() => Icon.UsePaceColoring = value); }

    public bool SessionEnabled { get => Metric("session").IsEnabled; set => Edit(() => Metric("session").IsEnabled = value); }
    public bool WeekEnabled { get => Metric("week").IsEnabled; set => Edit(() => Metric("week").IsEnabled = value); }

    public IconStyleOption SessionStyle
    {
        get => StyleOf("session");
        set => Edit(() => Metric("session").IconStyle = value.RawValue);
    }

    public IconStyleOption WeekStyle
    {
        get => StyleOf("week");
        set => Edit(() => Metric("week").IconStyle = value.RawValue);
    }

    public bool AllMetricsOff => !SessionEnabled && !WeekEnabled;

    // General (per profile): upstream GeneralSettingsView's refresh interval.

    public double RefreshInterval
    {
        get => Profile?.RefreshInterval ?? 30;
        set => Edit(() =>
        {
            if (Profile is { } profile)
            {
                profile.RefreshInterval = Math.Clamp(Math.Round(value / 10) * 10, 10, 300);
            }
        });
    }

    public string RefreshText => Strings.Format("general.refresh_seconds", (int)RefreshInterval);

    // Popover (app-wide), upstream PopoverSettingsView.

    public bool TimeDisplayReset { get => _settings.TimeDisplay == PopoverTimeDisplay.ResetTime; set => SetTimeDisplay(value, PopoverTimeDisplay.ResetTime); }
    public bool TimeDisplayRemaining { get => _settings.TimeDisplay == PopoverTimeDisplay.RemainingTime; set => SetTimeDisplay(value, PopoverTimeDisplay.RemainingTime); }
    public bool TimeDisplayBoth { get => _settings.TimeDisplay == PopoverTimeDisplay.Both; set => SetTimeDisplay(value, PopoverTimeDisplay.Both); }
    public bool TimeFormatSystem { get => _settings.TimeFormat == TimeFormatPreference.System; set => SetTimeFormat(value, TimeFormatPreference.System); }
    public bool TimeFormat12 { get => _settings.TimeFormat == TimeFormatPreference.TwelveHour; set => SetTimeFormat(value, TimeFormatPreference.TwelveHour); }
    public bool TimeFormat24 { get => _settings.TimeFormat == TimeFormatPreference.TwentyFourHour; set => SetTimeFormat(value, TimeFormatPreference.TwentyFourHour); }

    // CLI Account, upstream CLIAccountView.

    public bool IsLinked => Profile?.CliLink is not null;

    public string LinkStatusTitle => Strings.Get(IsLinked ? "cli.synced" : "cli.not_synced");

    public string? LinkedSince => Profile?.CliLink is { } link
        ? Strings.Format("windows.cli.linked_at", TimeZoneInfo.ConvertTime(link.LinkedAt, TimeZoneInfo.Local).ToString("g", Strings.Culture))
        : null;

    public string DetailsSubtitle => Strings.Get(IsLinked ? "cli.credentials_synced" : "cli.no_credentials");

    public string SyncLabel => Strings.Get(IsLinked ? "cli.resync" : "cli.sync_from_code");

    public IReadOnlyList<DetailRow> Details
    {
        get
        {
            if (Profile?.CliLink is not { } link)
            {
                return [];
            }

            var rows = new List<DetailRow>
            {
                new(Strings.Get("windows.setup.review.account"),
                    link.EmailAddress is { } email ? (link.OrganizationName is { } org ? $"{email} — {org}" : email) : Strings.Get("windows.setup.review.account_unknown")),
                new(Strings.Get("windows.setup.review.folder"), link.ConfigDirectory),
                new(Strings.Get("windows.cli.current_state"), CurrentState()),
            };
            if (_state?.MirrorError is { } mirrorError)
            {
                rows.Add(new(Strings.Get("windows.cli.mirror"), mirrorError));
            }

            if (_snapshot?.Credentials is { } credentials)
            {
                // Only the token's fixed type prefix is shown; upstream also showed the last four characters.
                rows.Add(new(Strings.Get("cli.access_token"), MaskedToken(credentials.AccessToken)));
                rows.Add(new(Strings.Get("cli.subscription"), credentials.SubscriptionType ?? link.SubscriptionType ?? "unknown"));
                if (credentials.Scopes.Count > 0)
                {
                    rows.Add(new(Strings.Get("cli.scopes"), string.Join(", ", credentials.Scopes)));
                }
            }

            return rows;
        }
    }

    public string Version =>
        Strings.Format("about.version", Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    /// <summary>Refreshes the CLI account page from the coordinator's state and a fresh read of the linked folder.</summary>
    public void UpdateLink(ProfileUsageState? state, CliSnapshot? snapshot)
    {
        _state = state;
        _snapshot = snapshot;
        foreach (var name in new[] { nameof(IsLinked), nameof(LinkStatusTitle), nameof(LinkedSince), nameof(DetailsSubtitle), nameof(SyncLabel), nameof(Details), nameof(HasProfile), nameof(ProfileName) })
        {
            RaisePropertyChanged(name);
        }
    }

    /// <summary>Re-reads every value, e.g. after the wizard changed the active profile.</summary>
    public void Reload()
    {
        foreach (var property in GetType().GetProperties())
        {
            RaisePropertyChanged(property.Name);
        }
    }

    public static string MaskedToken(string token) =>
        token.Length > 20 ? token[..12] + "•••••" : "•••••••••";

    private string CurrentState() => _state?.Link switch
    {
        LinkStatus.Ready => Strings.Get("windows.cli.state.ready"),
        LinkStatus.WaitingForCliRefresh => Strings.Get("windows.banner.waiting_for_cli"),
        LinkStatus.NotSignedIn => Strings.Get("windows.banner.cli_signed_out"),
        LinkStatus.Unreadable or LinkStatus.Invalid => Strings.Get("windows.banner.cli_unreadable"),
        LinkStatus.AccountChanged => Strings.Get("windows.banner.account_changed"),
        LinkStatus.AccountUnverified => Strings.Get("windows.banner.account_unverified"),
        _ => Strings.Get("windows.cli.state.not_checked"),
    };

    private MetricIconConfig Metric(string type)
    {
        if (Icon.Metric(type) is { } metric)
        {
            return metric;
        }

        var created = new MetricIconConfig { MetricType = type, Order = Icon.Metrics.Count };
        Icon.Metrics.Add(created);
        return created;
    }

    private IconStyleOption StyleOf(string type) =>
        IconStyles.FirstOrDefault(o => o.RawValue == Metric(type).IconStyle) ?? IconStyles[0];

    private void Edit(Action change)
    {
        if (Profile is null)
        {
            return;
        }

        change();
        _changed();
        Reload();
    }

    private void SetTimeDisplay(bool selected, PopoverTimeDisplay value)
    {
        if (selected && _settings.TimeDisplay != value)
        {
            _settings.TimeDisplay = value;
            _changed();
            Reload();
        }
    }

    private void SetTimeFormat(bool selected, TimeFormatPreference value)
    {
        if (selected && _settings.TimeFormat != value)
        {
            _settings.TimeFormat = value;
            _changed();
            Reload();
        }
    }

    private static IconStyleOption Option(string raw, string nameKey, TrayIconStyle style, bool taskbarDark)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            // A dark tile behind the preview, like the taskbar it will sit on.
            context.DrawRoundedRectangle(new SolidColorBrush(taskbarDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
                null, new System.Windows.Rect(-6, -6, 44, 44), 6, 6);
            TrayIconRenderer.Draw(context, 32,
                new TrayIconSpec(style, 0.6, 60, UsageStatusLevel.Safe, 0.45, PaceStatus.OnTrack, false, "S", false), taskbarDark);
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return new IconStyleOption(raw, Strings.Get(nameKey), image);
    }
}
