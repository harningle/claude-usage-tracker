using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.Views;
using ClaudeUsageTracker.Core.Providers;
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

/// <summary>
/// An item shown in a list. Items are compared by reference: WPF's automation peers find an item's
/// container by item equality, so rows rebuilt with equal values would be matched to containers that no
/// longer exist, and screen readers could no longer reach them.
/// </summary>
public abstract record ListItem
{
    public virtual bool Equals(ListItem? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>A card ready for binding, with theme brushes resolved.</summary>
public sealed record UsageRowView(
    string Title, string? Tag, string? Subtitle, string PercentText, Brush PercentBrush,
    double? BarFraction, Brush FillBrush, double? Marker, Brush MarkerBrush, string? ResetText) : ListItem
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

/// <summary>An entry of the profile switcher menu.</summary>
public sealed record ProfileChoice(Guid Id, string Name, bool IsCurrent);

public sealed record ApiCreditsView(
    string PercentText, Brush Brush, double BarFraction, string UsedText, string RemainingText, string? ResetText, string? Note)
{
    public bool HasReset => ResetText is not null;
    public bool HasNote => Note is not null;
}

public sealed record ApiCostView(
    string TotalText, IReadOnlyList<ChartBar> Daily, Func<double, string> AxisLabel,
    IReadOnlyList<CostSourceModel> Sources, IReadOnlyList<CostLine> Models)
{
    public bool HasDaily => Daily.Count > 0;
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
    private IReadOnlyList<ProfileChoice> _profiles = [];
    private string? _overageBalance;
    private string? _plan;
    private string? _credits;
    private ApiCreditsView? _apiCredits;
    private ApiCostView? _apiCost;

    public string ProfileName { get => _profileName; private set => Set(ref _profileName, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public IReadOnlyList<UsageRowView> Rows { get => _rows; private set => Set(ref _rows, value); }
    public BannerView? Banner { get => _banner; private set => Set(ref _banner, value); }
    public string? Placeholder { get => _placeholder; private set => Set(ref _placeholder, value); }

    /// <summary>Every profile, for the switcher menu behind the profile name.</summary>
    public IReadOnlyList<ProfileChoice> Profiles { get => _profiles; private set => Set(ref _profiles, value); }

    public string? OverageBalance { get => _overageBalance; private set => Set(ref _overageBalance, value); }
    public string? Plan { get => _plan; private set { if (Set(ref _plan, value)) { RaisePropertyChanged(nameof(HasPlanRow)); } } }
    public string? Credits { get => _credits; private set { if (Set(ref _credits, value)) { RaisePropertyChanged(nameof(HasPlanRow)); } } }
    public bool HasPlanRow => Plan is not null || Credits is not null;
    public ApiCreditsView? ApiCredits { get => _apiCredits; private set => Set(ref _apiCredits, value); }
    public ApiCostView? ApiCost { get => _apiCost; private set => Set(ref _apiCost, value); }

    /// <summary>The status page of the viewed profile's provider.</summary>
    public string StatusPage { get; private set; } = ServiceStatus.ClaudeStatusPage;

    /// <summary>Set in fixture mode, so the popover never passes for real account data.</summary>
    public string? FixtureLabel { get; init; }

    public string RefreshLabel => Strings.Get("common.refresh");
    public string SettingsLabel => Strings.Get("common.settings");
    public string StatusTooltip => Strings.Get("menubar.click_status");

    /// <param name="profile">The profile shown: the active one, or in multi-profile mode the one whose icon was clicked.</param>
    /// <param name="integrationNeedsRepair">Something installed for Claude Code is missing or broken; said when the profile itself has nothing more urgent.</param>
    public void Update(
        AppSettings settings, ProfileSettings? profile, ProfileUsageState? state, ServiceStatus? status, DateTimeOffset now,
        bool integrationNeedsRepair = false)
    {
        var use24Hour = UsageDisplay.Uses24HourTime(settings.TimeFormat, CultureInfo.CurrentCulture);
        var provider = profile?.ProviderKind ?? Provider.Anthropic;
        var model = PopoverModelBuilder.Build(
            state, profile?.IconConfig ?? new IconConfiguration(), settings.TimeDisplay, use24Hour, now, TimeZoneInfo.Local, Strings.Culture,
            provider, settings.IsMultiProfile ? settings.MultiProfileDisplayConfig : null);

        ProfileName = profile?.Name ?? Strings.Get("popover.no_profile");
        Profiles = settings.Profiles.Select(p => new ProfileChoice(p.Id, p.Name, p.Id == profile?.Id)).ToList();
        StatusPage = provider == Provider.Codex ? ServiceStatus.OpenAiStatusPage : ServiceStatus.ClaudeStatusPage;
        OverageBalance = model.OverageBalance;
        Plan = model.Plan;
        Credits = model.Credits;
        ApiCredits = model.ApiCredits is { } credits
            ? new ApiCreditsView(credits.PercentText, Resource(LevelKey(credits.Level)), credits.BarFraction, credits.UsedText,
                credits.RemainingText, credits.ResetText, credits.Note)
            : null;
        ApiCost = model.ApiCost is { } cost
            ? new ApiCostView(
                cost.TotalText,
                cost.Daily.Select(d => new ChartBar(
                    d.Date.Day.ToString(CultureInfo.InvariantCulture), (double)d.Amount,
                    $"{d.Date.ToString("MMM d", Strings.Culture)}: {Core.Billing.CurrencyText.Format(d.Amount, cost.Currency, Strings.Culture)}")).ToList(),
                value => Core.Billing.CurrencyText.Format((decimal)value, cost.Currency, Strings.Culture),
                cost.Sources, cost.Models)
            : null;
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
        var banner = model.Banner
                     ?? (integrationNeedsRepair
                         ? new BannerModel(Strings.Get("windows.banner.integration"), BannerTone.Caution, BannerAction.OpenIntegration)
                         : null);
        Banner = banner is { } b
            ? new BannerView(b.Message, b.Glyph,
                Resource(b.Tone switch { BannerTone.Warning => "Banner.Warning", BannerTone.Caution => "Banner.Caution", _ => "Banner.Info" }),
                Resource(b.Tone switch { BannerTone.Warning => "Status.Moderate", BannerTone.Caution => "Status.Caution", _ => "Status.Info" }),
                b.Action)
            : null;
        Rows = model.Rows.Select(r =>
        {
            var colour = Resource(LevelKey(r.Level));
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

    private static string LevelKey(UsageStatusLevel? level) => level switch
    {
        UsageStatusLevel.Safe => "Status.Safe",
        UsageStatusLevel.Moderate => "Status.Moderate",
        UsageStatusLevel.Critical => "Status.Critical",
        _ => "Foreground.Secondary",
    };

    private static Brush Resource(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
