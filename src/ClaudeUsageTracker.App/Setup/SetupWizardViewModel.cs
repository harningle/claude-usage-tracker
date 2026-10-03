using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.CodexCli;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Authentication;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Setup;

public enum WizardStep
{
    ChooseProvider,

    // Claude Code route
    Detecting,
    Detected,
    NotFound,

    // claude.ai and Console routes (upstream's three steps: key, organisation, confirm)
    EnterKey,
    SelectOrganization,

    // Codex route
    CodexDetected,
    CodexNotDetected,

    // Every route
    Testing,
    TestFailed,
    Review,
    Saving,
}

/// <summary>What the wizard is setting up.</summary>
public enum WizardRoute
{
    ClaudeCode,
    ClaudeAi,
    Console,
    Codex,
}

/// <summary>A Claude Code folder found during detection.</summary>
public sealed record CandidateView(CliSnapshot Snapshot, string Label, string Detail, bool Usable) : ListItem;

public sealed record ReviewRow(string Label, string Value) : ListItem;

/// <summary>An organisation offered in step 2.</summary>
public sealed record OrganizationView(string Id, string Name) : ListItem;

/// <summary>
/// The browser side of setup, kept behind an interface so the wizard's logic can be tested without
/// WebView2. Sessions and cookies stay in the profile's browser context; only the Console's cookie is
/// handed over, because the Console is polled through <c>HttpClient</c>.
/// </summary>
public interface IBrowserSignIn
{
    /// <summary>Lets the user sign in to claude.ai in the profile's browser context. False when they cancelled.</summary>
    Task<bool> SignInToClaudeAiAsync(Guid profileId);

    /// <summary>Lets the user sign in to the Anthropic Console. Null when they cancelled.</summary>
    Task<ConsoleSecret?> SignInToConsoleAsync(Guid profileId);

    /// <summary>Puts a pasted claude.ai session key into the profile's browser context; the result undoes it.</summary>
    Task<Func<Task>> UseSessionKeyAsync(Guid profileId, string sessionKey);
}

/// <summary>What the wizard needs besides the Claude Code route's services.</summary>
/// <param name="CodexHome">The Codex home directory to look in.</param>
public sealed record WizardServices(
    ClaudeAiClient ClaudeAi, ConsoleClient Console, CodexClient Codex, IBrowserSignIn Browser, string CodexHome);

/// <summary>
/// Port of upstream <c>SetupWizardView</c>: provider choice, then one route. Claude Code: detection, a
/// read-only test, review and save. claude.ai and Console: sign in (or paste a key), choose the
/// organisation, review and save. Codex: detect the CLI's login or paste credentials, test, review and
/// save. Tests and navigation never write settings; only Save does. Unlike upstream, every route runs
/// one read-only request and shows a review before saving, and a failed test changes nothing.
/// </summary>
public sealed class SetupWizardViewModel : ObservableObject
{
    private readonly CliLinkService _links;
    private readonly UsageClient _client;
    private readonly SetupService _setup;
    private readonly WizardServices _services;
    private readonly TimeProvider _time;
    private readonly string _userProfile;
    private readonly string? _configDirOverride;
    private readonly Func<IReadOnlyList<string>>? _knownFolders;
    private readonly List<string> _extraDirectories = [];
    private WizardStep _step = WizardStep.ChooseProvider;
    private WizardRoute _route = WizardRoute.ClaudeCode;
    private IReadOnlyList<CandidateView> _candidates = [];
    private CandidateView? _selected;
    private string? _message;
    private UsageData? _testedUsage;
    private DateTimeOffset _testedAt;
    private IReadOnlyList<ReviewRow> _review = [];
    private string? _reviewWarning;
    private IReadOnlyList<OrganizationView> _organizations = [];
    private OrganizationView? _selectedOrganization;
    private ConsoleSecret? _consoleKey;
    private Func<Task>? _undoSessionKey;
    private CodexSnapshot? _codexSnapshot;
    private CodexCredentials? _codexCredentials;
    private string? _codexPasted;
    private bool _busy;

    public SetupWizardViewModel(
        CliLinkService links, UsageClient client, SetupService setup, WizardServices services, TimeProvider time, string userProfile,
        string? configDirOverride, Func<IReadOnlyList<string>>? knownFolders = null)
    {
        _knownFolders = knownFolders;
        _links = links;
        _client = client;
        _setup = setup;
        _services = services;
        _time = time;
        _userProfile = userProfile;
        _configDirOverride = configDirOverride;
    }

    /// <summary>Raised after a successful save with the configured profile.</summary>
    public event Action<ProfileSettings>? Saved;

    public WizardStep Step { get => _step; private set { if (Set(ref _step, value)) { RaisePropertyChanged(nameof(ProgressStep)); } } }

    public WizardRoute Route { get => _route; private set => Set(ref _route, value); }

    /// <summary>The profile whose browser context a sign-in uses; the profile the save will configure.</summary>
    public Guid TargetProfileId => _setup.TargetProfileId;

    /// <summary>The 1-3 step indicator: find or enter the credentials, test or choose, review.</summary>
    public int ProgressStep => Step switch
    {
        WizardStep.SelectOrganization or WizardStep.Testing or WizardStep.TestFailed => 2,
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

    public IReadOnlyList<OrganizationView> Organizations { get => _organizations; private set => Set(ref _organizations, value); }

    public OrganizationView? SelectedOrganization { get => _selectedOrganization; set => Set(ref _selectedOrganization, value ?? _selectedOrganization); }

    /// <summary>The signed-in email of the detected Codex login, when its tokens carry one.</summary>
    public string? CodexEmail => _codexSnapshot?.Credentials?.Email;

    /// <summary>True while a sign-in window is open or a request is in flight; the view disables its buttons.</summary>
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

    /// <summary>Opens the wizard at a route's first step, for the settings pages.</summary>
    public void Start(WizardRoute route)
    {
        switch (route)
        {
            case WizardRoute.ClaudeCode:
                Route = WizardRoute.ClaudeCode;
                Detect();
                break;
            case WizardRoute.Codex:
                ChooseCodex();
                break;
            default:
                EnterKey(route);
                break;
        }
    }

    public void ChooseAnthropic()
    {
        Route = WizardRoute.ClaudeCode;
        Detect();
    }

    /// <summary>Reads every known Claude Code folder, without writing to any of them.</summary>
    public void Detect()
    {
        Step = WizardStep.Detecting;
        Message = null;
        var linked = _setup.TargetProfile?.CliLink?.ConfigDirectory;
        // Folders of installed launchers are known places to look, and are listed once they hold a login.
        var known = new List<string>([.. _extraDirectories, .. _knownFolders?.Invoke() ?? []]);
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
        RaisePropertyChanged(nameof(Selected));
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

    /// <summary>Upstream's "Set Up Manually Instead": the claude.ai route's first step.</summary>
    public void SetUpWithClaudeAi() => EnterKey(WizardRoute.ClaudeAi);

    private void EnterKey(WizardRoute route)
    {
        Route = route;
        Message = null;
        Step = WizardStep.EnterKey;
    }

    /// <summary>
    /// The read-only test of the current route. Claude Code: re-reads the selected folder, so a retry after
    /// Claude Code refreshed its login uses the new token. claude.ai and Console: usage or billing of the
    /// chosen organisation. Codex: the detected login. Nothing is saved.
    /// </summary>
    public async Task TestAsync(CancellationToken cancellation)
    {
        switch (Route)
        {
            case WizardRoute.ClaudeCode:
                await TestCliAsync(cancellation);
                break;
            case WizardRoute.ClaudeAi:
                await TestClaudeAiAsync(cancellation);
                break;
            case WizardRoute.Console:
                await TestConsoleAsync(cancellation);
                break;
            default:
                await TestCodexAsync(_codexPasted, cancellation);
                break;
        }
    }

    private async Task TestCliAsync(CancellationToken cancellation)
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
        RaisePropertyChanged(nameof(Selected));

        Step = WizardStep.Testing;
        Message = null;
        var result = await _client.FetchUsageAsync(
            credentials.AccessToken, cancellation, fresh.Account?.Identity ?? _setup.TargetProfileId.ToString("N"));
        if (result is FetchSucceeded success)
        {
            Tested(success);
            var target = _setup.TargetProfile;
            var rows = new List<ReviewRow>
            {
                ProfileRow(),
                new(Strings.Get("windows.setup.review.account"), fresh.Account?.EmailAddress ?? Strings.Get("windows.setup.review.account_unknown")),
            };
            if (fresh.Account?.OrganizationName is { } organisation)
            {
                rows.Add(new(Strings.Get("wizard.organization"), organisation));
            }

            rows.Add(new(Strings.Get("cli.subscription"), fresh.Credentials?.SubscriptionType ?? "unknown"));
            rows.Add(new(Strings.Get("windows.setup.review.folder"), fresh.Directory.Path));
            rows.Add(UsageRow(success.Usage));
            Review = rows;

            var linked = target?.CliLink;
            ReviewWarning = linked?.Identity is { } identity && fresh.Account?.Identity is { } current
                            && !string.Equals(identity, current, StringComparison.OrdinalIgnoreCase)
                ? Strings.Format("windows.setup.review.switch_warning", linked.EmailAddress ?? identity)
                : fresh.Account?.Identity is null ? Strings.Get("windows.setup.review.no_identity") : null;
            Step = WizardStep.Review;
            return;
        }

        Message = FailureMessage((FetchFailed)result);
        Step = WizardStep.TestFailed;
    }

    /// <summary>
    /// Upstream's "Sign in" button: opens the browser sign-in for claude.ai or the Console, then lists the
    /// organisations the new session can see.
    /// </summary>
    public async Task SignInAsync(CancellationToken cancellation)
    {
        Message = null;
        IsBusy = true;
        try
        {
            if (Route == WizardRoute.Console)
            {
                if (await _services.Browser.SignInToConsoleAsync(_setup.TargetProfileId) is { } key)
                {
                    await LoadConsoleOrganizationsAsync(key, cancellation);
                }

                return;
            }

            if (await _services.Browser.SignInToClaudeAiAsync(_setup.TargetProfileId))
            {
                _undoSessionKey = null;
                await LoadClaudeAiOrganizationsAsync(freshSession: true, cancellation);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Upstream's "Advanced: Manual Session Key". A claude.ai key is tested through the profile's browser
    /// context, the same route a sign-in uses; a key that fails is taken out again and nothing is saved.
    /// </summary>
    public async Task TestKeyAsync(string sessionKey, CancellationToken cancellation)
    {
        Message = null;
        string key;
        try
        {
            key = SessionKeyValidator.Validate(sessionKey);
        }
        catch (SessionKeyValidationException e)
        {
            Message = $"{e.Message}. {e.RecoverySuggestion}.";
            return;
        }

        IsBusy = true;
        try
        {
            if (Route == WizardRoute.Console)
            {
                await LoadConsoleOrganizationsAsync(new ConsoleSecret(key, null), cancellation);
                return;
            }

            var undo = await _services.Browser.UseSessionKeyAsync(_setup.TargetProfileId, key);
            if (await LoadClaudeAiOrganizationsAsync(freshSession: false, cancellation))
            {
                _undoSessionKey = undo;
            }
            else
            {
                await undo();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Lists the session's organisations and moves to step 2. A freshly issued session can be refused for a
    /// moment before it works, so upstream retries an unauthorised answer up to three times (after 1.5 s,
    /// 3 s and 4.5 s); a pasted key gets one attempt.
    /// </summary>
    private async Task<bool> LoadClaudeAiOrganizationsAsync(bool freshSession, CancellationToken cancellation)
    {
        Step = WizardStep.Testing;
        var result = await _services.ClaudeAi.FetchOrganizationsAsync(_setup.TargetProfileId, cancellation);
        for (var attempt = 1; freshSession && attempt < 4 && result.Failure?.Failure == FetchFailure.CredentialsRejected; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), _time, cancellation);
            result = await _services.ClaudeAi.FetchOrganizationsAsync(_setup.TargetProfileId, cancellation);
        }

        if (result.Organizations is not { Count: > 0 } organizations)
        {
            Message = result.Failure is { } failure
                ? failure.Failure switch
                {
                    FetchFailure.CredentialsRejected => Strings.Get(freshSession ? "windows.setup.claude_ai.not_signed_in" : "windows.setup.claude_ai.key_rejected"),
                    FetchFailure.Challenge => Strings.Get(freshSession ? "windows.setup.test.challenge" : "windows.setup.claude_ai.key_challenged"),
                    _ => FailureMessage(failure),
                }
                : Strings.Get("windows.setup.no_organizations");
            Step = WizardStep.EnterKey;
            return false;
        }

        ShowOrganizations(organizations.Select(o => new OrganizationView(o.Uuid, o.DisplayName)), _setup.TargetProfile?.ClaudeAi?.OrganizationId);
        return true;
    }

    private async Task LoadConsoleOrganizationsAsync(ConsoleSecret key, CancellationToken cancellation)
    {
        Step = WizardStep.Testing;
        var result = await _services.Console.FetchOrganizationsAsync(key.SessionKey, cancellation);
        if (result.Value is not { Count: > 0 } organizations)
        {
            Message = result.Failure is { } failure
                ? failure == FetchFailure.CredentialsRejected
                    ? Strings.Get("windows.setup.console.key_rejected")
                    : FailureMessage(new FetchFailed(failure, result.Status, result.RetryAfter) { At = _time.GetUtcNow() })
                : Strings.Get("windows.setup.no_organizations");
            Step = WizardStep.EnterKey;
            return;
        }

        _consoleKey = result.RenewedKey ?? key;
        ShowOrganizations(organizations.Select(o => new OrganizationView(o.Uuid, o.DisplayName)), _setup.TargetProfile?.Console?.OrganizationId);
    }

    private void ShowOrganizations(IEnumerable<OrganizationView> organizations, string? linkedId)
    {
        Organizations = organizations.ToList();
        // Upstream selects the first organisation; an organisation the profile already tracks is kept.
        _selectedOrganization = Organizations.FirstOrDefault(o => o.Id == linkedId) ?? Organizations[0];
        RaisePropertyChanged(nameof(SelectedOrganization));
        Message = null;
        Step = WizardStep.SelectOrganization;
    }

    private async Task TestClaudeAiAsync(CancellationToken cancellation)
    {
        if (SelectedOrganization is not { } organization)
        {
            return;
        }

        Step = WizardStep.Testing;
        Message = null;
        var result = await _services.ClaudeAi.FetchUsageAsync(_setup.TargetProfileId, organization.Id, cancellation);
        if (result is not FetchSucceeded success)
        {
            Message = FailureMessage((FetchFailed)result);
            Step = WizardStep.TestFailed;
            return;
        }

        Tested(success);
        Review =
        [
            ProfileRow(),
            new(Strings.Get("wizard.organization"), organization.Name),
            new(Strings.Get("windows.setup.review.source"), Strings.Get("windows.setup.review.source_claude_ai")),
            UsageRow(success.Usage),
        ];
        var linked = _setup.TargetProfile?.ClaudeAi;
        ReviewWarning = linked is not null && linked.OrganizationId != organization.Id
            ? Strings.Format("windows.setup.review.switch_warning", linked.OrganizationName ?? linked.OrganizationId)
            : null;
        Step = WizardStep.Review;
    }

    private async Task TestConsoleAsync(CancellationToken cancellation)
    {
        if (SelectedOrganization is not { } organization || _consoleKey is not { } key)
        {
            return;
        }

        Step = WizardStep.Testing;
        Message = null;
        var result = await _services.Console.FetchApiUsageAsync(key.SessionKey, organization.Id, cancellation);
        if (result.Value is not { } billing)
        {
            Message = FailureMessage(new FetchFailed(result.Failure ?? FetchFailure.InvalidResponse, result.Status, result.RetryAfter) { At = _time.GetUtcNow() });
            Step = WizardStep.TestFailed;
            return;
        }

        _consoleKey = result.RenewedKey ?? key;
        Review =
        [
            ProfileRow(),
            new(Strings.Get("wizard.organization"), organization.Name),
            new(Strings.Get("windows.setup.review.billing_now"), Strings.Format(
                "windows.setup.review.billing_values",
                CurrencyText.Format(billing.UsedAmount, billing.Currency, Strings.Culture),
                CurrencyText.Format(billing.RemainingAmount, billing.Currency, Strings.Culture))),
        ];
        ReviewWarning = null;
        Step = WizardStep.Review;
    }

    /// <summary>Looks for the Codex CLI's login. Nothing is read beyond <c>auth.json</c>.</summary>
    public void ChooseCodex()
    {
        Route = WizardRoute.Codex;
        Message = null;
        _codexPasted = null;
        _codexSnapshot = CodexStore.Read(_services.CodexHome);
        RaisePropertyChanged(nameof(CodexEmail));
        Step = _codexSnapshot.State == CodexLoginState.Usable ? WizardStep.CodexDetected : WizardStep.CodexNotDetected;
    }

    /// <summary>
    /// Tests the detected Codex login, or credentials the user pasted, with one read-only usage request.
    /// Pasted credentials are not renewed before they are saved.
    /// </summary>
    public async Task TestCodexAsync(string? pastedJson, CancellationToken cancellation)
    {
        Message = null;
        var credentials = pastedJson is null ? _codexSnapshot?.Credentials : CodexAuthJson.Parse(pastedJson);
        if (credentials is null)
        {
            Message = $"{Strings.Get("error.codex_credentials_invalid")}. {Strings.Get("error.codex_credentials_invalid.suggestion")}.";
            Step = WizardStep.CodexNotDetected;
            return;
        }

        _codexPasted = pastedJson;
        _codexCredentials = credentials;
        Step = WizardStep.Testing;
        var result = await _services.Codex.FetchUsageAsync(credentials, CodexStore.ReadConfig(_services.CodexHome), null, cancellation);
        if (result is not FetchSucceeded success)
        {
            var failed = (FetchFailed)result;
            Message = failed.Failure == FetchFailure.CredentialsRejected
                ? $"{Strings.Get("error.codex_auth_expired")}. {Strings.Get("error.codex_auth_expired.suggestion")}."
                : FailureMessage(failed);
            Step = WizardStep.TestFailed;
            return;
        }

        Tested(success);
        var rows = new List<ReviewRow>
        {
            ProfileRow(),
            new(Strings.Get("windows.setup.review.account"), credentials.Email ?? Strings.Get("windows.setup.review.codex_account_unknown")),
            new(Strings.Get("windows.setup.review.source"), Strings.Get(pastedJson is null ? "codex.source_authfile" : "codex.source_manual")),
        };
        if (success.Usage.PlanType is { } plan)
        {
            rows.Add(new(Strings.Get("cli.subscription"), plan));
        }

        rows.Add(UsageRow(success.Usage));
        Review = rows;
        ReviewWarning = _setup.TargetProfile is { ProviderKind: Core.Providers.Provider.Anthropic, UsageSource: not null }
            ? Strings.Get("windows.setup.review.provider_change")
            : null;
        Step = WizardStep.Review;
    }

    public void Back()
    {
        Message = null;
        Step = (Route, Step) switch
        {
            (WizardRoute.ClaudeCode, WizardStep.Review or WizardStep.TestFailed) => WizardStep.Detected,
            (WizardRoute.ClaudeAi or WizardRoute.Console, WizardStep.Review or WizardStep.TestFailed) => WizardStep.SelectOrganization,
            (WizardRoute.ClaudeAi or WizardRoute.Console, WizardStep.SelectOrganization) => WizardStep.EnterKey,
            (WizardRoute.Codex, WizardStep.Review or WizardStep.TestFailed) =>
                _codexPasted is null && _codexSnapshot?.State == CodexLoginState.Usable ? WizardStep.CodexDetected : WizardStep.CodexNotDetected,
            _ => WizardStep.ChooseProvider,
        };
    }

    public async Task SaveAsync(CancellationToken cancellation)
    {
        Step = WizardStep.Saving;
        Message = null;
        try
        {
            var profile = Route switch
            {
                WizardRoute.ClaudeCode when Selected is not null && _testedUsage is not null =>
                    await _setup.SaveCliAsync(Selected.Snapshot, _testedUsage, _testedAt, cancellation),
                WizardRoute.ClaudeAi when SelectedOrganization is { } organization && _testedUsage is not null =>
                    await _setup.SaveClaudeAiAsync(new ClaudeOrganization(organization.Id, organization.Name), _testedUsage, _testedAt),
                WizardRoute.Console when SelectedOrganization is { } organization && _consoleKey is not null =>
                    await _setup.SaveConsoleAsync(new ConsoleOrganization(organization.Id, organization.Name), _consoleKey, cancellation),
                WizardRoute.Codex when _codexCredentials is not null && _testedUsage is not null =>
                    await _setup.SaveCodexAsync(_services.CodexHome, _codexPasted, _codexCredentials, _testedUsage, _testedAt, cancellation),
                _ => null,
            };
            if (profile is null)
            {
                Step = WizardStep.Review;
                return;
            }

            _undoSessionKey = null;
            Saved?.Invoke(profile);
        }
        catch (SetupException e)
        {
            Message = e.Message;
            Step = WizardStep.Review;
        }
    }

    /// <summary>Takes a pasted key that was tested but never saved out of the browser context again.</summary>
    public async Task DiscardAsync()
    {
        if (_undoSessionKey is { } undo)
        {
            _undoSessionKey = null;
            await undo();
        }
    }

    private void Tested(FetchSucceeded success)
    {
        _testedUsage = success.Usage;
        _testedAt = success.At;
    }

    private ReviewRow ProfileRow() =>
        new(Strings.Get("windows.setup.review.profile"), _setup.TargetProfile?.Name ?? Strings.Get("windows.setup.review.new_profile"));

    private static ReviewRow UsageRow(UsageData usage) =>
        new(Strings.Get("windows.setup.review.usage_now"), string.Join(", ",
            new[] { (Strings.Get("menubar.session_usage"), usage.SessionPercentage), (Strings.Get("menubar.all_models"), usage.WeeklyPercentage) }
                .Select(p => $"{p.Item1} {(p.Item2 is { } v ? UsageDisplay.PercentText(v) : "—")}")));

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
}
