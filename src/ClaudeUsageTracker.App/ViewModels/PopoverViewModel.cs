using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Status;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void RaisePropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A card ready for binding, with theme brushes resolved.</summary>
public sealed record UsageRowView(
    string Title, string? Tag, string? Subtitle, string PercentText, Brush PercentBrush,
    double? BarFraction, Brush FillBrush, double? Marker, Brush MarkerBrush, string? ResetText)
{
    public bool HasTag => Tag is not null;
    public bool HasSubtitle => Subtitle is not null;
    public bool HasReset => ResetText is not null;

    /// <summary>A screen-reader summary, e.g. "Session Usage, 39%, Resets Today 14:08".</summary>
    public string AccessibleName => string.Join(", ", new[] { Title, PercentText, ResetText }.Where(t => t is not null));
}

public sealed record BannerView(string Message, string Glyph, Brush Background, Brush GlyphBrush, BannerAction Action)
{
    public bool HasAction => Action != BannerAction.None;
}

/// <summary>Binds the popover to the viewed profile's live state; rebuilt on every state change and each minute.</summary>
public sealed class PopoverViewModel : ObservableObject
{
    private string _profileName = "";
    private string _statusText = "";
    private Brush _statusBrush = Brushes.Gray;
    private bool _isRefreshing;
    private IReadOnlyList<UsageRowView> _rows = [];
    private BannerView? _banner;
    private string? _placeholder;

    public string ProfileName { get => _profileName; private set => Set(ref _profileName, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public IReadOnlyList<UsageRowView> Rows { get => _rows; private set => Set(ref _rows, value); }
    public BannerView? Banner { get => _banner; private set => Set(ref _banner, value); }
    public string? Placeholder { get => _placeholder; private set => Set(ref _placeholder, value); }

    /// <summary>Set in fixture mode, so the popover never passes for real account data.</summary>
    public string? FixtureLabel { get; init; }

    public string RefreshLabel => Strings.Get("common.refresh");
    public string SettingsLabel => Strings.Get("common.settings");
    public string StatusTooltip => Strings.Get("menubar.click_status");

    public void Update(AppSettings settings, ProfileSettings? profile, ProfileUsageState? state, ServiceStatus? status, DateTimeOffset now)
    {
        var use24Hour = UsageDisplay.Uses24HourTime(settings.TimeFormat, CultureInfo.CurrentCulture);
        var model = PopoverModelBuilder.Build(
            state, profile?.IconConfig ?? new IconConfiguration(), settings.TimeDisplay, use24Hour, now, TimeZoneInfo.Local, Strings.Culture);

        ProfileName = profile?.Name ?? Strings.Get("popover.no_profile");
        // Upstream's default before any status fetch; the status page's own text afterwards.
        StatusText = status?.Description ?? Strings.Get("windows.status.unknown");
        StatusBrush = Resource(status?.Indicator switch
        {
            ServiceStatusIndicator.None => "Status.Safe",
            ServiceStatusIndicator.Minor => "Status.Caution",
            ServiceStatusIndicator.Major => "Status.Moderate",
            ServiceStatusIndicator.Critical => "Status.Critical",
            _ => "Status.Unknown",
        });
        IsRefreshing = state?.IsRefreshing == true;
        Placeholder = model.Placeholder;
        Banner = model.Banner is { } b
            ? new BannerView(b.Message, b.Glyph,
                Resource(b.Tone switch { BannerTone.Warning => "Banner.Warning", BannerTone.Caution => "Banner.Caution", _ => "Banner.Info" }),
                Resource(b.Tone switch { BannerTone.Warning => "Status.Moderate", BannerTone.Caution => "Status.Caution", _ => "Status.Info" }),
                b.Action)
            : null;
        Rows = model.Rows.Select(r =>
        {
            var colour = Resource(r.Level switch
            {
                UsageStatusLevel.Safe => "Status.Safe",
                UsageStatusLevel.Moderate => "Status.Moderate",
                UsageStatusLevel.Critical => "Status.Critical",
                _ => "Foreground.Secondary",
            });
            var marker = Resource(r.Pace switch
            {
                PaceStatus.Comfortable => "Pace.Comfortable",
                PaceStatus.OnTrack => "Pace.OnTrack",
                PaceStatus.Warming => "Pace.Warming",
                PaceStatus.Pressing => "Pace.Pressing",
                PaceStatus.Critical => "Pace.Critical",
                PaceStatus.Runaway => "Pace.Runaway",
                _ => "Marker.Neutral",
            });
            return new UsageRowView(r.Title, r.Tag, r.Subtitle, r.PercentText, colour, r.BarFraction, colour, r.MarkerFraction, marker, r.ResetText);
        }).ToList();
    }

    private static Brush Resource(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
