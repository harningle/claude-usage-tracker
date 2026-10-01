using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.Setup;

/// <summary>
/// Windows acceptance tests for the setup wizard's Claude Code route (upstream <c>SetupWizardView</c>):
/// detection, the read-only test, review and save, and that failed tests change nothing.
/// </summary>
public sealed class SetupWizardViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string ClaudeDirectory => Path.Combine(UserProfile, ".claude");
    private string SettingsPath => Path.Combine(_temp.Path, "data", "settings.json");

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

    private (SetupWizardViewModel Wizard, ScriptedHandler Handler, AppSettings Settings) Create(
        HttpStatusCode status = HttpStatusCode.OK, string body = """{"five_hour":{"utilization":39},"seven_day":{"utilization":60}}""",
        string? configDirOverride = null, Action<HttpResponseMessage>? configure = null)
    {
        var handler = ScriptedHandler.Returning(status, body, configure: configure);
        var settings = new AppSettings();
        var links = new CliLinkService(new SecretStore(Path.Combine(_temp.Path, "data", "secrets")), _time, UserProfile);
        var client = new UsageClient(new HttpClient(handler), _time, new RequestLog(Path.Combine(_temp.Path, "data", "diagnostics")));
        var setup = new SetupService(new SettingsStore(SettingsPath), settings, links,
            new UsageCacheStore(Path.Combine(_temp.Path, "data", "cache")), _time);
        return (new SetupWizardViewModel(links, client, setup, _time, UserProfile, configDirOverride), handler, settings);
    }

    [Fact]
    public void UsableLoginIsDetected()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, handler, _) = Create();

        wizard.ChooseAnthropic();

        Assert.Equal(WizardStep.Detected, wizard.Step);
        Assert.Equal("user@example.com — ExampleOrg · default", wizard.Selected?.Label);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void NoLoginSaysSoAndOffersARetry()
    {
        var (wizard, _, _) = Create();

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
        var (wizard, _, _) = Create();

        wizard.ChooseAnthropic();

        Assert.Equal(WizardStep.NotFound, wizard.Step);
        Assert.Contains("expired", wizard.Message, StringComparison.Ordinal);
        Assert.Contains("never renews", wizard.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverrideFolderIsDetectedWithItsOwnAccount()
    {
        var work = Path.Combine(_temp.Path, ".claude-work");
        WriteLogin(work, Now.AddHours(8), Path.Combine(work, ".claude.json"));
        var (wizard, _, _) = Create(configDirOverride: work);

        wizard.ChooseAnthropic();

        Assert.Equal(work, wizard.Selected?.Snapshot.Directory.Path);
        Assert.False(wizard.Selected?.Snapshot.Directory.IsDefault);
    }

    [Fact]
    public void ChosenFolderIsAddedToDetection()
    {
        var work = Path.Combine(_temp.Path, "elsewhere");
        WriteLogin(work, Now.AddHours(8), Path.Combine(work, ".claude.json"));
        var (wizard, _, _) = Create();
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
        var (wizard, handler, settings) = Create();
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
        var (wizard, _, settings) = Create(status, "{}");
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
        var (wizard, handler, _) = Create(HttpStatusCode.TooManyRequests, "{}",
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
        var (wizard, handler, _) = Create();
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
        var (wizard, handler, _) = Create();
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
        var (wizard, _, settings) = Create();
        ProfileSettings? saved = null;
        wizard.Saved += profile => saved = profile;
        wizard.ChooseAnthropic();
        await wizard.TestAsync(CancellationToken.None);

        wizard.Save();

        Assert.NotNull(saved);
        Assert.Equal(saved.Id, settings.ActiveProfileId);
        Assert.Equal("acc-1", new SettingsStore(SettingsPath).Load().ActiveProfile?.CliLink?.Identity);
    }

    [Fact]
    public async Task SaveFailureKeepsTheReviewWithTheReason()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, _, settings) = Create();
        wizard.ChooseAnthropic();
        await wizard.TestAsync(CancellationToken.None);
        File.Delete(Path.Combine(ClaudeDirectory, ".credentials.json"));

        wizard.Save();

        Assert.Equal(WizardStep.Review, wizard.Step);
        Assert.Contains("changed after the test", wizard.Message, StringComparison.Ordinal);
        Assert.Empty(settings.Profiles);
    }

    [Fact]
    public void BackFromDetectionReturnsToProviderChoice()
    {
        WriteLogin(ClaudeDirectory, Now.AddHours(8));
        var (wizard, _, _) = Create();
        wizard.ChooseAnthropic();

        wizard.Back();

        Assert.Equal(WizardStep.ChooseProvider, wizard.Step);
    }
}
