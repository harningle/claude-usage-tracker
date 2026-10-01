using System.Globalization;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Setup;

public enum WizardStep
{
    ChooseProvider,
    Detecting,
    Detected,
    NotFound,
    Testing,
    TestFailed,
    Review,
    Saving,
}

/// <summary>A Claude Code folder found during detection.</summary>
public sealed record CandidateView(CliSnapshot Snapshot, string Label, string Detail, bool Usable);

public sealed record ReviewRow(string Label, string Value);

/// <summary>
/// Port of upstream <c>SetupWizardView</c> for the Claude Code route: provider choice, CLI detection, a
/// read-only connection test (an addition: upstream saved without testing), review and save. Tests and
/// navigation never write anything; only Save does. Browser sign-in, session keys and Codex arrive in
/// Milestone 3 and are labelled as not yet available rather than offered as working routes.
/// </summary>
public sealed class SetupWizardViewModel : ObservableObject
{
    private readonly CliLinkService _links;
    private readonly UsageClient _client;
    private readonly SetupService _setup;
    private readonly TimeProvider _time;
    private readonly string _userProfile;
    private readonly string? _configDirOverride;
    private readonly List<string> _extraDirectories = [];
    private WizardStep _step = WizardStep.ChooseProvider;
    private IReadOnlyList<CandidateView> _candidates = [];
    private CandidateView? _selected;
    private string? _message;
    private UsageData? _testedUsage;
    private DateTimeOffset _testedAt;
    private IReadOnlyList<ReviewRow> _review = [];
    private string? _reviewWarning;

    public SetupWizardViewModel(
        CliLinkService links, UsageClient client, SetupService setup, TimeProvider time, string userProfile, string? configDirOverride)
    {
        _links = links;
        _client = client;
        _setup = setup;
        _time = time;
        _userProfile = userProfile;
        _configDirOverride = configDirOverride;
    }

    /// <summary>Raised after a successful save with the configured profile.</summary>
    public event Action<ProfileSettings>? Saved;

    public WizardStep Step { get => _step; private set { if (Set(ref _step, value)) { OnPropertyChanged(nameof(ProgressStep)); } } }

    /// <summary>The 1-3 step indicator: detect, test, review.</summary>
    public int ProgressStep => Step switch
    {
        WizardStep.Testing or WizardStep.TestFailed => 2,
        WizardStep.Review or WizardStep.Saving => 3,
        _ => 1,
    };

    public IReadOnlyList<CandidateView> Candidates { get => _candidates; private set => Set(ref _candidates, value); }

    public CandidateView? Selected
    {
        get => _selected;
        set => Set(ref _selected, value is { Usable: true } ? value : _selected);
    }

    /// <summary>Why detection, the test or the save did not succeed, in plain words.</summary>
    public string? Message { get => _message; private set => Set(ref _message, value); }

    public IReadOnlyList<ReviewRow> Review { get => _review; private set => Set(ref _review, value); }

    /// <summary>Set when saving would move the profile to a different account than it follows now.</summary>
    public string? ReviewWarning { get => _reviewWarning; private set => Set(ref _reviewWarning, value); }

    public void ChooseAnthropic() => Detect();

    /// <summary>Reads every known Claude Code folder, without writing to any of them.</summary>
    public void Detect()
    {
        Step = WizardStep.Detecting;
        Message = null;
        var linked = _setup.TargetProfile?.CliLink?.ConfigDirectory;
        var known = new List<string>(_extraDirectories);
        if (linked is not null)
        {
            known.Add(linked);
        }

        var inventory = CliStore.Inventory(_userProfile, _configDirOverride, known);
        var snapshots = inventory.Select(_links.Read).ToList();
        Candidates = snapshots
            .Where(s => s.State != CliLoginState.NotSignedIn || _extraDirectories.Contains(s.Directory.Path, StringComparer.OrdinalIgnoreCase))
            .Select(s => new CandidateView(s, s.Label, Detail(s), s.State == CliLoginState.Usable))
            .ToList();

        // Prefer the folder this profile already follows, then the default folder.
        var usable = Candidates.Where(c => c.Usable).ToList();
        var preferred = usable.FirstOrDefault(c => linked is not null && string.Equals(c.Snapshot.Directory.Path, CliStore.Normalise(linked), StringComparison.OrdinalIgnoreCase))
                        ?? usable.FirstOrDefault();
        _selected = preferred;
        OnPropertyChanged(nameof(Selected));
        if (preferred is not null)
        {
            Step = WizardStep.Detected;
            return;
        }

        Message = snapshots.Any(s => s.State == CliLoginState.Expired) ? Strings.Get("windows.setup.not_found.expired")
            : snapshots.Any(s => s.State is CliLoginState.Unreadable or CliLoginState.Invalid) ? Strings.Get("windows.setup.not_found.unreadable")
            : Strings.Get("windows.setup.not_found.none");
        Step = WizardStep.NotFound;
    }

    /// <summary>Adds a folder the user chose (for example a second <c>CLAUDE_CONFIG_DIR</c>) and detects again.</summary>
    public void AddFolder(string path)
    {
        var normalised = CliStore.Normalise(path);
        if (!_extraDirectories.Contains(normalised, StringComparer.OrdinalIgnoreCase))
        {
            _extraDirectories.Add(normalised);
        }

        Detect();
    }

    /// <summary>
    /// Re-reads the selected folder, then runs the read-only usage request with its current login, so a retry
    /// after Claude Code refreshed its login uses the new token. Nothing is saved.
    /// </summary>
    public async Task TestAsync(CancellationToken cancellation)
    {
        if (Selected is null)
        {
            return;
        }

        var fresh = _links.Read(Selected.Snapshot.Directory);
        if (fresh.State != CliLoginState.Usable || fresh.Credentials is not { } credentials)
        {
            Detect();
            return;
        }

        var refreshed = new CandidateView(fresh, fresh.Label, Detail(fresh), true);
        Candidates = Candidates.Select(c => ReferenceEquals(c, Selected) ? refreshed : c).ToList();
        _selected = refreshed;
        OnPropertyChanged(nameof(Selected));

        Step = WizardStep.Testing;
        Message = null;
        var result = await _client.FetchUsageAsync(credentials.AccessToken, cancellation);
        if (result is FetchSucceeded success)
        {
            _testedUsage = success.Usage;
            _testedAt = success.At;
            BuildReview(Selected, success.Usage);
            Step = WizardStep.Review;
            return;
        }

        Message = FailureMessage((FetchFailed)result);
        Step = WizardStep.TestFailed;
    }

    public void Back()
    {
        Message = null;
        Step = Step switch
        {
            WizardStep.Review or WizardStep.TestFailed => WizardStep.Detected,
            _ => WizardStep.ChooseProvider,
        };
    }

    public void Save()
    {
        if (Selected is null || _testedUsage is null)
        {
            return;
        }

        Step = WizardStep.Saving;
        Message = null;
        try
        {
            var profile = _setup.Save(Selected.Snapshot, _testedUsage, _testedAt);
            Saved?.Invoke(profile);
        }
        catch (SetupException e)
        {
            Message = e.Message;
            Step = WizardStep.Review;
        }
    }

    private void BuildReview(CandidateView candidate, UsageData usage)
    {
        var snapshot = candidate.Snapshot;
        var target = _setup.TargetProfile;
        var rows = new List<ReviewRow>
        {
            new(Strings.Get("windows.setup.review.profile"), target?.Name ?? Strings.Get("windows.setup.review.new_profile")),
            new(Strings.Get("windows.setup.review.account"), snapshot.Account?.EmailAddress ?? Strings.Get("windows.setup.review.account_unknown")),
        };
        if (snapshot.Account?.OrganizationName is { } organisation)
        {
            rows.Add(new(Strings.Get("wizard.organization"), organisation));
        }

        rows.Add(new(Strings.Get("cli.subscription"), snapshot.Credentials?.SubscriptionType ?? "unknown"));
        rows.Add(new(Strings.Get("windows.setup.review.folder"), snapshot.Directory.Path));
        rows.Add(new(Strings.Get("windows.setup.review.usage_now"), string.Join(", ",
            new[] { (Strings.Get("menubar.session_usage"), usage.SessionPercentage), (Strings.Get("menubar.all_models"), usage.WeeklyPercentage) }
                .Select(p => $"{p.Item1} {(p.Item2 is { } v ? UsageDisplay.PercentText(v) : "—")}"))));
        Review = rows;

        var linked = target?.CliLink;
        ReviewWarning = linked?.Identity is { } identity && snapshot.Account?.Identity is { } current
                        && !string.Equals(identity, current, StringComparison.OrdinalIgnoreCase)
            ? Strings.Format("windows.setup.review.switch_warning", linked.EmailAddress ?? identity)
            : snapshot.Account?.Identity is null ? Strings.Get("windows.setup.review.no_identity") : null;
    }

    private string Detail(CliSnapshot snapshot) => snapshot.State switch
    {
        CliLoginState.Usable when snapshot.Credentials?.ExpiresAt is { } expiry =>
            Strings.Format("windows.setup.detail.usable", snapshot.Directory.Path,
                TimeZoneInfo.ConvertTime(expiry, TimeZoneInfo.Local).ToString("g", Strings.Culture)),
        CliLoginState.Usable => snapshot.Directory.Path,
        CliLoginState.Expired => Strings.Format("windows.setup.detail.expired", snapshot.Directory.Path),
        CliLoginState.NotSignedIn => Strings.Format("windows.setup.detail.not_signed_in", snapshot.Directory.Path),
        _ => Strings.Format("windows.setup.detail.unreadable", snapshot.Directory.Path),
    };

    private string FailureMessage(FetchFailed failed) => failed.Failure switch
    {
        FetchFailure.CredentialsRejected => Strings.Format("windows.setup.test.rejected", failed.Status ?? 0),
        FetchFailure.Forbidden => Strings.Format("windows.setup.test.forbidden", failed.Status ?? 0),
        FetchFailure.RateLimited => failed.RetryAfter is { } wait
            ? Strings.Format("windows.setup.test.rate_limited_until",
                TimeZoneInfo.ConvertTime(_time.GetUtcNow() + wait, TimeZoneInfo.Local).ToString("t", Strings.Culture))
            : Strings.Get("windows.setup.test.rate_limited"),
        FetchFailure.Challenge => Strings.Get("windows.setup.test.challenge"),
        FetchFailure.Network or FetchFailure.Timeout => Strings.Get("windows.setup.test.network"),
        FetchFailure.ServerError => Strings.Format("windows.setup.test.server", failed.Status ?? 0),
        _ => Strings.Format("windows.setup.test.invalid", failed.Status ?? 0),
    };

    private void OnPropertyChanged(string name) => RaisePropertyChanged(name);
}
