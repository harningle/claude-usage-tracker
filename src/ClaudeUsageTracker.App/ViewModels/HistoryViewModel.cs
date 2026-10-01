using System.Globalization;
using System.IO;
using System.Text;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.Views;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.History;
using ClaudeUsageTracker.Core.Providers;

namespace ClaudeUsageTracker.App.ViewModels;

/// <summary>Upstream <c>ChartTimeScale</c>: the hours the usage chart spans.</summary>
public enum ChartTimeScale
{
    Hours5 = 5,
    Hours24 = 24,
    Days7 = 168,
    Days30 = 720,
}

/// <summary>
/// The History page: upstream <c>UsageHistoryView</c> with its combined session and weekly chart, time-scale
/// choice, half-window navigation, billing-cycle chart and JSON/CSV export.
/// </summary>
public sealed class HistoryViewModel(UsageHistoryService history, TimeProvider time, Func<bool> use24Hour) : ObservableObject
{
    /// <summary>Upstream's billing chart shows the twelve most recent cycles on a scale of at least 10 units.</summary>
    public const int MaxBillingBars = 12;

    private UsageHistoryData _data = new();
    private ProfileSettings? _profile;
    private ChartTimeScale _scale = ChartTimeScale.Hours24;
    private double _offsetHours;
    private string? _recordingError;

    public bool HasProfile => _profile is not null;

    /// <summary>Why the history cannot be shown (a damaged or unreadable file); it is never replaced.</summary>
    public string? Error { get; private set; }

    /// <summary>Why the latest usage could not be recorded, from the polling loop; null when recording works.</summary>
    public string? RecordingError
    {
        get => _recordingError;
        set
        {
            if (Set(ref _recordingError, value))
            {
                RaisePropertyChanged(nameof(Problem));
            }
        }
    }

    /// <summary>What the page warns about: the file cannot be read, or new usage cannot be saved.</summary>
    public string? Problem => Error ?? RecordingError;

    public bool Scale5Hours { get => _scale == ChartTimeScale.Hours5; set => SetScale(value, ChartTimeScale.Hours5); }
    public bool Scale24Hours { get => _scale == ChartTimeScale.Hours24; set => SetScale(value, ChartTimeScale.Hours24); }
    public bool Scale7Days { get => _scale == ChartTimeScale.Days7; set => SetScale(value, ChartTimeScale.Days7); }
    public bool Scale30Days { get => _scale == ChartTimeScale.Days30; set => SetScale(value, ChartTimeScale.Days30); }

    public DateTimeOffset End => time.GetUtcNow().AddHours(_offsetHours);
    public DateTimeOffset Start => End.AddHours(-(double)_scale);

    public IReadOnlyList<ChartPoint> Session => Points(_data.SessionSnapshots, s => s.SessionPercentage);
    public IReadOnlyList<ChartPoint> Weekly => Points(_data.WeeklySnapshots, s => s.WeeklyPercentage);

    public bool HasData => Session.Count > 0 || Weekly.Count > 0;

    /// <summary>Upstream's axis formats per scale, as .NET formats.</summary>
    public string TimeFormat => _scale switch
    {
        ChartTimeScale.Days7 => use24Hour() ? "ddd HH" : "ddd htt",
        ChartTimeScale.Days30 => "MMM d",
        _ => use24Hour() ? "HH:mm" : "h:mmtt",
    };

    public string ReadoutFormat => use24Hour() ? "MMM d, HH:mm" : "MMM d, h:mmtt";

    public string RangeLabel
    {
        get
        {
            var format = _scale is ChartTimeScale.Hours5 or ChartTimeScale.Hours24 ? ReadoutFormat : "MMM d";
            string Text(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString(format, Strings.Culture);
            return $"{Text(Start)} – {Text(End)}";
        }
    }

    public bool CanGoForward => _offsetHours < 0;

    public bool CanGoBack =>
        _data.SessionSnapshots.Concat(_data.WeeklySnapshots).Select(s => (DateTimeOffset?)s.Timestamp).Min() is { } oldest && oldest < Start;

    /// <summary>Console billing exists only for providers that have it.</summary>
    public bool ShowBilling => _profile?.ProviderKind.Descriptor().Capabilities.ConsoleBilling == true;

    public IReadOnlyList<ChartBar> Billing =>
        [.. _data.BillingCycleSnapshots.Take(MaxBillingBars).Reverse().Select(s =>
        {
            var local = TimeZoneInfo.ConvertTime(s.Timestamp, TimeZoneInfo.Local);
            var amount = (s.ApiSpendCents ?? 0) / 100m;
            return new ChartBar(
                local.ToString("MM/dd", CultureInfo.InvariantCulture), (double)amount,
                $"{local.ToString("d", Strings.Culture)}: {CurrencyText.Format(amount, s.ApiCurrency ?? "USD", Strings.Culture)}");
        })];

    public bool HasBilling => Billing.Count > 0;

    public Func<double, string> BillingAxis =>
        value => CurrencyText.Format((decimal)Math.Round(value), _data.BillingCycleSnapshots.FirstOrDefault()?.ApiCurrency ?? "USD", Strings.Culture);

    public string RecordsText => Strings.Format("history.list.count", _data.Snapshots.Count);

    /// <summary>Reads the profile's history again, e.g. when the page is opened or the active profile changes.</summary>
    public void Load(ProfileSettings? profile)
    {
        _profile = profile;
        _data = new UsageHistoryData();
        Error = null;
        if (profile is not null)
        {
            try
            {
                _data = history.Load(profile.Id);
            }
            catch (HistoryStoreException e)
            {
                Error = e.Message;
            }
        }

        RaiseAll();
    }

    /// <summary>Upstream moves by half a window.</summary>
    public void Back() => Move(_offsetHours - (double)_scale / 2);

    public void Forward() => Move(Math.Min(0, _offsetHours + (double)_scale / 2));

    public void Now() => Move(0);

    /// <summary>
    /// Writes the whole history to a file the user chose, as upstream's JSON or CSV export. The profile's
    /// stored history is read again, so the file holds exactly what is on disk.
    /// </summary>
    public int Export(string path, bool csv)
    {
        if (_profile is not { } profile)
        {
            return 0;
        }

        var data = history.Load(profile.Id);
        var text = csv ? UsageHistoryCsv.Export(data, TimeZoneInfo.Local) : UsageHistoryJson.Export(data);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return data.Snapshots.Count;
    }

    private IReadOnlyList<ChartPoint> Points(IReadOnlyList<UsageSnapshot> snapshots, Func<UsageSnapshot, double?> value)
    {
        var (start, end) = (Start, End);
        return [.. snapshots
            .Where(s => s.Timestamp >= start && s.Timestamp <= end && value(s) is not null)
            .OrderBy(s => s.Timestamp)
            .Select(s => new ChartPoint(s.Timestamp, Math.Clamp(value(s)!.Value, 0, 100)))];
    }

    private void SetScale(bool selected, ChartTimeScale scale)
    {
        if (selected && _scale != scale)
        {
            _scale = scale;
            // Upstream returns to now when the scale changes.
            _offsetHours = 0;
            RaiseAll();
        }
    }

    private void Move(double offsetHours)
    {
        _offsetHours = offsetHours;
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var property in GetType().GetProperties())
        {
            RaisePropertyChanged(property.Name);
        }
    }
}
