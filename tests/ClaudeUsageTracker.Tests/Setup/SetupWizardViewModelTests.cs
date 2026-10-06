using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.Setup;

/// <summary>
/// Windows acceptance tests for the setup wizard (upstream <c>SetupWizardView</c>): the Claude Code,
/// claude.ai, Console and Codex routes, each with a read-only test and a review, and that failed tests and
/// cancelled routes change nothing.
/// </summary>
public sealed class SetupWizardViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private const string UsageBody = """{"five_hour":{"utilization":39},"seven_day":{"utilization":60}}""";

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSignIn _signIn = new();

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string ClaudeDirectory => Path.Combine(UserProfile, ".claude");
    private string CodexHome => Path.Combine(UserProfile, ".codex");
    private string Data => Path.Combine(_temp.Path, "data");
    private string SettingsPath => Path.Combine(Data, "settings.json");

    public SetupWizardViewModelTests() => Directory.CreateDirectory(ClaudeDirectory);

    public void Dispose() => _temp.Dispose();

    private void WriteLogin(string directory, DateTimeOffset expires, string? accountJsonPath = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".credentials.json"),
            $$$"""{"claudeAiOauth":{"accessToken":"at-1","refreshToken":"rt-1","expiresAt":{{{expires.ToUnixTimeMilliseconds()}}},"subscriptionType":"team"}}""");
        File.WriteAllText(accountJsonPath ?? Path.Combine(UserProfile, ".claude.json"),
            """{"oauthAccount":{"accountUuid":"acc-1","emailAddress":"user@example.com","organizationName":"ExampleOrg"}}""");
    }

    private sealed record Wizard(SetupWizardViewModel Model, ScriptedHandler Handler, AppSettings Settings, ScriptedTransport Transport, LinkHarness Links);

    /// <param name="respond">Answers HTTP requests (usage, Console, Codex); the default answers usage and the Console.</param>
    private Wizard Create(
        HttpStatusCode status = HttpStatusCode.OK, string body = UsageBody, string? configDirOverride = null,
        Action<HttpResponseMessage>? configure = null, Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        Func<Guid, ClaudeAiOperation, string?, BrowserResponse>? claudeAi = null, AppSettings? settings = null)
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            if (respond is not null)
            {
                return Task.FromResult(respond(request));
            }

            if (request.RequestUri!.Host == "platform.claude.com" && ConsoleEndpoint.Answer(request.RequestUri) is { } console)
            {
                return Task.FromResult(LinkHarness.Json(console.Status, console.Body));
            }

            var response = LinkHarness.Json(status, body);
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
        settings ??= new AppSettings();
        var http = new HttpClient(handler);
        var log = new RequestLog(Path.Combine(Data, "diagnostics"));
        var links = new LinkHarness(Data, UserProfile, _time);
        var client = new UsageClient(http, _time, log);
        var setup = new SetupService(new SettingsStore(SettingsPath), settings, links.Service, links.Secrets, links.Lock,
            new UsageCacheStore(Path.Combine(Data, "cache")), _time);
        var transport = new ScriptedTransport(claudeAi ?? ((_, operation, _) => ScriptedTransport.SignedIn(operation)));
        var services = new WizardServices(
            new ClaudeAiClient(transport, _time, log), new ConsoleClient(http, _time, log), new CodexClient(http, _time, log), _signIn, CodexHome);
        return new Wizard(new SetupWizardViewModel(links.Service, client, setup, services, _time, UserProfile, configDirOverride),
            handler, settings, transport, links);
    }

    [Fact]
    public void UsableLoginIsDetected()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, handler, _, _, _) = Create();

        wizard.ChooseAnthropic();

        Assert.Equal(WizardStep.Detected, wizard.Step);
        Assert.Equal("user@example.com — ExampleOrg · default", wizard.Selected?.Label);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void NoLoginSaysSoAndOffersARetry()
    {
        var (wizard, _, _, _, _) = Create();

        wizard.ChooseAnthropic();

        Assert.Equal(WizardStep.NotFound, wizard.Step);
        Assert.Contains("No Claude Code login was found", wizard.Message, StringComparison.Ordinal);

        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        wizard.Detect();
        Assert.Equal(WizardStep.Detected, wizard.Step);
    }

    [Fact]
    public void ExpiredLoginIsNotReportedAsMissing()
    {
        WriteLogin(ClaudeDirectory, Now.AddMinutes(-10));
        var (wizard, handler, _, _, links) = Create();

        wizard.ChooseAnthropic();

        Assert.Equal(WizardStep.NotFound, wizard.Step);
        Assert.Contains("expired", wizard.Message, StringComparison.Ordinal);
        Assert.Contains("run any claude command", wizard.Message, StringComparison.Ordinal);
        // Setup never renews a login: only linked profiles may, under the guard.
        Assert.Empty(handler.Requests);
        Assert.Equal(0, links.TokenRequests);
    }

    [Fact]
    public void OverrideFolderIsDetectedWithItsOwnAccount()
    {
        var work = Path.Combine(_temp.Path, ".claude-work");
        WriteLogin(work, Now.AddHours(8), Path.Combine(work, ".claude.json"));
        var (wizard, _, _, _, _) = Create(configDirOverride: work);

        wizard.ChooseAnthropic();

        Assert.Equal(work, wizard.Selected?.Snapshot.Directory.Path);
        Assert.False(wizard.Selected?.Snapshot.Directory.IsDefault);
    }

    [Fact]
    public void ChosenFolderIsAddedToDetection()
    {
        var work = Path.Combine(_temp.Path, "elsewhere");
        WriteLogin(work, Now.AddHours(8), Path.Combine(work, ".claude.json"));
        var (wizard, _, _, _, _) = Create();
        wizard.ChooseAnthropic();
        Assert.Equal(WizardStep.NotFound, wizard.Step);

        wizard.AddFolder(work);

        Assert.Equal(WizardStep.Detected, wizard.Step);
        Assert.Equal(work, wizard.Selected?.Snapshot.Directory.Path);
    }

    [Fact]
    public async Task SuccessfulTestLeadsToReviewWithoutSaving()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, handler, settings, _, _) = Create();
        wizard.ChooseAnthropic();

        await wizard.TestAsync(CancellationToken.None);

        Assert.Equal(WizardStep.Review, wizard.Step);
        Assert.Equal(3, wizard.ProgressStep);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/oauth/usage", request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains(wizard.Review, r => r.Value == "user@example.com");
        Assert.Contains(wizard.Review, r => r.Value == "Session Usage 39%, All models 60%");
        Assert.Empty(settings.Profiles);
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected this login (HTTP 401)")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.BadGateway, "returned an error (HTTP 502)")]
    public async Task FailedTestIsReportedAndChangesNothing(HttpStatusCode status, string expected)
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, _, settings, _, _) = Create(status, "{}");
        wizard.ChooseAnthropic();

        await wizard.TestAsync(CancellationToken.None);

        Assert.Equal(WizardStep.TestFailed, wizard.Step);
        Assert.Contains(expected, wizard.Message, StringComparison.Ordinal);
        Assert.Empty(settings.Profiles);
        Assert.False(File.Exists(SettingsPath));

        wizard.Back();
        Assert.Equal(WizardStep.Detected, wizard.Step);
        Assert.Null(wizard.Message);
    }

    [Fact]
    public async Task RetryDuringTheServersWaitSendsNothing()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, handler, _, _, _) = Create(HttpStatusCode.TooManyRequests, "{}",
            configure: r => r.Headers.TryAddWithoutValidation("Retry-After", "120"));
        wizard.ChooseAnthropic();
        await wizard.TestAsync(CancellationToken.None);
        var firstMessage = wizard.Message;

        wizard.Back();
        _time.Now += TimeSpan.FromSeconds(60);
        await wizard.TestAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(WizardStep.TestFailed, wizard.Step);
        Assert.Contains("Try again after", wizard.Message, StringComparison.Ordinal);
        Assert.Equal(firstMessage, wizard.Message);
    }

    [Fact]
    public async Task TestUsesTheLoginAsItIsNow()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, handler, _, _, _) = Create();
        wizard.ChooseAnthropic();
        File.WriteAllText(Path.Combine(ClaudeDirectory, ".credentials.json"),
            $$$"""{"claudeAiOauth":{"accessToken":"at-renewed","refreshToken":"rt-2","expiresAt":{{{Now.AddHours(9).ToUnixTimeMilliseconds()}}}}}""");

        await wizard.TestAsync(CancellationToken.None);

        Assert.Equal("at-renewed", handler.Requests.Single().Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task LoginThatExpiredSinceDetectionIsReportedNotTested()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, handler, _, _, _) = Create();
        wizard.ChooseAnthropic();
        WriteLogin(ClaudeDirectory, Now.AddMinutes(-1));

        await wizard.TestAsync(CancellationToken.None);

        Assert.Equal(WizardStep.NotFound, wizard.Step);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SaveConfiguresTheProfile()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, _, settings, _, _) = Create();
        ProfileSettings? saved = null;
        wizard.Saved += profile => saved = profile;
        wizard.ChooseAnthropic();
        await wizard.TestAsync(CancellationToken.None);

        await wizard.SaveAsync(CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(saved.Id, settings.ActiveProfileId);
        Assert.Equal("acc-1", new SettingsStore(SettingsPath).Load().ActiveProfile?.CliLink?.Identity);
    }

    [Fact]
    public async Task SaveFailureKeepsTheReviewWithTheReason()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, _, settings, _, _) = Create();
        wizard.ChooseAnthropic();
        await wizard.TestAsync(CancellationToken.None);
        File.Delete(Path.Combine(ClaudeDirectory, ".credentials.json"));

        await wizard.SaveAsync(CancellationToken.None);

        Assert.Equal(WizardStep.Review, wizard.Step);
        Assert.Contains("changed after the test", wizard.Message, StringComparison.Ordinal);
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public void BackFromDetectionReturnsToProviderChoice()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, _, _, _, _) = Create();
        wizard.ChooseAnthropic();

        wizard.Back();

        Assert.Equal(WizardStep.ChooseProvider, wizard.Step);
    }

    [Fact]
    public async Task ClaudeAiSignInListsOrganizationsThenTestsAndSavesTheChosenOne()
    {
        var (wizard, handler, settings, transport, _) = Create();
        wizard.SetUpWithClaudeAi();
        Assert.Equal(WizardStep.EnterKey, wizard.Step);

        await wizard.SignInAsync(CancellationToken.None);

        Assert.Equal(WizardStep.SelectOrganization, wizard.Step);
        Assert.Equal(["Example Team", "Other"], wizard.Organizations.Select(o => o.Name));
        Assert.Equal("org-1", wizard.SelectedOrganization?.Id);

        wizard.SelectedOrganization = wizard.Organizations[1];
        await wizard.TestAsync(CancellationToken.None);
        Assert.Equal(WizardStep.Review, wizard.Step);
        Assert.Contains(transport.Requests, r => r is { Operation: ClaudeAiOperation.Usage, OrganizationId: "org-2" });
        Assert.Empty(settings.Profiles);

        await wizard.SaveAsync(CancellationToken.None);

        Assert.Equal("org-2", settings.ActiveProfile?.ClaudeAi?.OrganizationId);
        // claude.ai goes through the browser only; no HttpClient request was sent.
        Assert.Empty(handler.Requests);
        Assert.All(transport.Requests, r => Assert.Equal(wizard.TargetProfileId, r.ProfileId));
    }

    [Fact]
    public async Task FreshSignInIsRetriedWhileClaudeAiCatchesUp()
    {
        var attempts = 0;
        var (wizard, _, _, _, _) = Create(claudeAi: (_, operation, _) =>
            operation == ClaudeAiOperation.Organizations && Interlocked.Increment(ref attempts) < 3
                ? ScriptedTransport.Status(401)
                : ScriptedTransport.SignedIn(operation));
        wizard.SetUpWithClaudeAi();

        await wizard.SignInAsync(CancellationToken.None);

        Assert.Equal(WizardStep.SelectOrganization, wizard.Step);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ProfileSwitchedWhileTheWizardIsOpenKeepsTheSignInAndSaveOnTheProfileItOpenedFor()
    {
        var opened = new ProfileSettings { Name = "Opened" };
        var other = new ProfileSettings { Name = "Other" };
        var settings = new AppSettings { Profiles = [opened, other], ActiveProfileId = opened.Id };
        var (wizard, _, _, transport, _) = Create(settings: settings);
        wizard.SetUpWithClaudeAi();
        await wizard.SignInAsync(CancellationToken.None);

        // The Next Profile shortcut or the profile switcher, while the wizard stays open.
        settings.ActiveProfileId = other.Id;
        await wizard.TestAsync(CancellationToken.None);
        // Closing the wizard now would discard an unsaved sign-in from this profile's browser context.
        Assert.Equal(opened.Id, wizard.TargetProfileId);
        await wizard.SaveAsync(CancellationToken.None);

        Assert.Equal("org-1", opened.ClaudeAi?.OrganizationId);
        Assert.Null(other.ClaudeAi);
        Assert.All(transport.Requests, r => Assert.Equal(opened.Id, r.ProfileId));
    }

    [Fact]
    public async Task ConsoleKeyIsSavedForTheProfileTheWizardOpenedFor()
    {
        var opened = new ProfileSettings { Name = "Opened" };
        var other = new ProfileSettings { Name = "Other" };
        var settings = new AppSettings { Profiles = [opened, other], ActiveProfileId = opened.Id };
        var (wizard, _, _, _, links) = Create(settings: settings);
        wizard.Start(WizardRoute.Console);
        await wizard.SignInAsync(CancellationToken.None);
        await wizard.TestAsync(CancellationToken.None);

        settings.ActiveProfileId = other.Id;
        await wizard.SaveAsync(CancellationToken.None);

        Assert.Equal("org-api", opened.Console?.OrganizationId);
        Assert.Equal("sk-ant-sid01-synthetic-console", links.Secrets.Load(opened.Id).Console?.SessionKey);
        Assert.Null(other.Console);
        Assert.False(links.Secrets.Exists(other.Id));
    }

    [Fact]
    public async Task ProfileDeletedWhileTheWizardIsOpenIsNotRecreatedOrReplaced()
    {
        var opened = new ProfileSettings { Name = "Opened" };
        var other = new ProfileSettings { Name = "Other" };
        var settings = new AppSettings { Profiles = [opened, other], ActiveProfileId = opened.Id };
        var (wizard, _, _, _, _) = Create(settings: settings);
        wizard.SetUpWithClaudeAi();
        await wizard.SignInAsync(CancellationToken.None);
        await wizard.TestAsync(CancellationToken.None);

        settings.Profiles.Remove(opened);
        settings.ActiveProfileId = other.Id;
        await wizard.SaveAsync(CancellationToken.None);

        Assert.Equal(WizardStep.Review, wizard.Step);
        Assert.Contains("deleted", wizard.Message, StringComparison.Ordinal);
        Assert.Equal([other], settings.Profiles);
        Assert.Null(other.ClaudeAi);
    }

    [Fact]
    public async Task CancelledSignInChangesNothing()
    {
        _signIn.ClaudeAiResult = false;
        var (wizard, _, settings, transport, _) = Create();
        wizard.SetUpWithClaudeAi();

        await wizard.SignInAsync(CancellationToken.None);

        Assert.Equal(WizardStep.EnterKey, wizard.Step);
        Assert.Empty(transport.Requests);
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public async Task PastedKeyThatFailsIsTakenOutOfTheBrowserAgain()
    {
        var (wizard, _, settings, _, _) = Create(claudeAi: (_, _, _) => ScriptedTransport.Status(401));
        wizard.SetUpWithClaudeAi();

        await wizard.TestKeyAsync("sk-ant-sid01-" + new string('a', 40), CancellationToken.None);

        Assert.Equal(WizardStep.EnterKey, wizard.Step);
        Assert.Contains("rejected this session key", wizard.Message, StringComparison.Ordinal);
        Assert.Single(_signIn.PastedKeys);
        Assert.Equal(1, _signIn.Undone);
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public async Task PastedKeyThatWorksIsUndoneWhenTheWizardClosesUnsaved()
    {
        var (wizard, _, _, _, _) = Create();
        wizard.SetUpWithClaudeAi();
        await wizard.TestKeyAsync("sk-ant-sid01-" + new string('a', 40), CancellationToken.None);
        Assert.Equal(WizardStep.SelectOrganization, wizard.Step);
        Assert.Equal(0, _signIn.Undone);

        await wizard.DiscardAsync();

        Assert.Equal(1, _signIn.Undone);
    }

    [Fact]
    public async Task MalformedKeyIsRefusedBeforeAnythingIsSent()
    {
        var (wizard, _, _, transport, _) = Create();
        wizard.SetUpWithClaudeAi();

        await wizard.TestKeyAsync("not a key", CancellationToken.None);

        Assert.NotNull(wizard.Message);
        Assert.Empty(_signIn.PastedKeys);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ConsoleRouteTestsBillingAndSavesTheEncryptedKey()
    {
        var (wizard, handler, settings, _, links) = Create();
        wizard.Start(WizardRoute.Console);

        await wizard.SignInAsync(CancellationToken.None);
        Assert.Equal(WizardStep.SelectOrganization, wizard.Step);
        Assert.Equal("org-api", wizard.SelectedOrganization?.Id);

        await wizard.TestAsync(CancellationToken.None);
        Assert.Equal(WizardStep.Review, wizard.Step);
        Assert.Contains(wizard.Review, r => r.Value == "$12.34 spent, $87.66 remaining");
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));

        await wizard.SaveAsync(CancellationToken.None);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("org-api", profile.Console?.OrganizationId);
        Assert.Equal("sk-ant-sid01-synthetic-console", links.Secrets.Load(profile.Id).Console?.SessionKey);
        Assert.DoesNotContain("synthetic-console", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedConsoleSessionSaysSo()
    {
        var (wizard, _, _, _, _) = Create(respond: _ => LinkHarness.Json(HttpStatusCode.Unauthorized, "{}"));
        wizard.Start(WizardRoute.Console);

        await wizard.SignInAsync(CancellationToken.None);

        Assert.Equal(WizardStep.EnterKey, wizard.Step);
        Assert.Contains("rejected this session", wizard.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexWithoutALoginOffersPasting()
    {
        var (wizard, _, _, _, _) = Create();

        wizard.ChooseCodex();

        Assert.Equal(WizardStep.CodexNotDetected, wizard.Step);
    }

    [Fact]
    public async Task DetectedCodexLoginIsTestedAndSavedAsTheCliFile()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "auth.json"),
            """{"tokens":{"access_token":"codex-at","refresh_token":"codex-rt","account_id":"chatgpt-1"},"last_refresh":"2026-10-01T10:00:00Z"}""");
        var (wizard, handler, settings, _, links) = Create(
            respond: _ => LinkHarness.Json(HttpStatusCode.OK, """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000,"reset_at":1790000000}}}"""));
        wizard.ChooseCodex();
        Assert.Equal(WizardStep.CodexDetected, wizard.Step);

        await wizard.TestAsync(CancellationToken.None);
        Assert.Equal(WizardStep.Review, wizard.Step);
        Assert.Equal("Bearer", handler.Requests.Single().Headers.Authorization?.Scheme);

        await wizard.SaveAsync(CancellationToken.None);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(Provider.Codex, profile.ProviderKind);
        Assert.False(profile.Codex?.IsManual);
        Assert.False(links.Secrets.Exists(profile.Id));
        Assert.Equal(0, links.TokenRequests);
    }

    [Fact]
    public async Task InvalidPastedCodexCredentialsAreRefusedWithoutARequest()
    {
        var (wizard, handler, _, _, _) = Create();
        wizard.ChooseCodex();

        await wizard.TestCodexAsync("{\"nothing\":true}", CancellationToken.None);

        Assert.Equal(WizardStep.CodexNotDetected, wizard.Step);
        Assert.Contains("could not be read", wizard.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
