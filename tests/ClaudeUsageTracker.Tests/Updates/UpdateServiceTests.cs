using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Updates;
using ClaudeUsageTracker.Core.Updates;
using ClaudeUsageTracker.Tests.Notifications;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.Updates;

/// <summary>Release versions order as Semantic Versioning 2.0.0 says, which the release tags follow.</summary>
public sealed class ReleaseVersionTests
{
    // The precedence example of semver.org, with the port's own tags among them.
    private static readonly string[] Ascending =
    [
        "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.1", "1.0.0-beta.2", "1.0.0-beta.2.1",
        "1.0.0-beta.10", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0", "10.0.0",
    ];

    public static TheoryData<string, string> Neighbours()
    {
        var data = new TheoryData<string, string>();
        for (var i = 1; i < Ascending.Length; i++)
        {
            data.Add(Ascending[i - 1], Ascending[i]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Neighbours))]
    public void EachVersionComesAfterTheOneBefore(string lower, string higher)
    {
        var low = ReleaseVersion.Parse(lower)!;
        var high = ReleaseVersion.Parse(higher)!;

        Assert.True(low.CompareTo(high) < 0);
        Assert.True(high.CompareTo(low) > 0);
        Assert.Equal(0, low.CompareTo(ReleaseVersion.Parse(lower)));
    }

    [Fact]
    public void ATagParsesWithItsPreRelease()
    {
        Assert.Equal(new ReleaseVersion(1, 0, 0, "beta.2"), ReleaseVersion.Parse("v1.0.0-beta.2"));
        Assert.True(ReleaseVersion.Parse("v1.0.0-beta.2")!.IsPreRelease);
        Assert.Equal("1.0.0-beta.2", ReleaseVersion.Parse("v1.0.0-beta.2")!.ToString());
    }

    [Fact]
    public void BuildMetadataIsIgnored()
    {
        // The SDK appends the commit to the informational version.
        Assert.Equal(new ReleaseVersion(1, 0, 0, null), ReleaseVersion.Parse("1.0.0+0123abcd"));
        Assert.Equal(new ReleaseVersion(1, 0, 0, "beta.2"), ReleaseVersion.Parse("1.0.0-beta.2+0123abcd"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0.0-beta.02")]
    [InlineData("1.0.0-beta/1")]
    [InlineData("V1.0.0")]
    [InlineData("1.0.0\n")]
    [InlineData("1.0.0-é")]
    [InlineData("99999999999.0.0")]
    public void OtherTextIsNoVersion(string? text) => Assert.Null(ReleaseVersion.Parse(text));
}

/// <summary>Which release of GitHub's list is offered, and the page a tag opens.</summary>
public sealed class GitHubReleasesTests
{
    private static string Releases(params (string Tag, bool Draft)[] releases) =>
        JsonSerializer.Serialize(releases.Select(r => new Dictionary<string, object>
        {
            ["tag_name"] = r.Tag,
            ["draft"] = r.Draft,
            ["prerelease"] = r.Tag.Contains('-'),
            ["html_url"] = "https://example.invalid/never-used",
        }));

    private static readonly ReleaseVersion Beta2 = ReleaseVersion.Parse("1.0.0-beta.2")!;

    [Fact]
    public void TheNewestNewerReleaseIsOfferedWhateverItsPlaceInTheList()
    {
        var newest = GitHubReleases.Newest(Releases(("v1.0.0-beta.3", false), ("v1.0.0-beta.10", false), ("v1.0.0-beta.1", false)), Beta2);

        Assert.Equal(new AvailableRelease("v1.0.0-beta.10", ReleaseVersion.Parse("1.0.0-beta.10")!), newest);
    }

    [Fact]
    public void NothingIsOfferedWithoutANewerRelease()
    {
        Assert.Null(GitHubReleases.Newest(Releases(("v1.0.0-beta.2", false), ("v1.0.0-beta.1", false)), Beta2));
        Assert.Null(GitHubReleases.Newest("[]", Beta2));
    }

    [Fact]
    public void DraftsAndTagsThatAreNoVersionAreSkipped()
    {
        var newest = GitHubReleases.Newest(Releases(("v2.0.0", true), ("nightly", false), ("v1.0.0-beta.3", false)), Beta2);

        Assert.Equal("v1.0.0-beta.3", newest?.Tag);
    }

    [Fact]
    public void AStableBuildIsNeverOfferedAPreRelease()
    {
        var stable = ReleaseVersion.Parse("1.0.0")!;

        Assert.Null(GitHubReleases.Newest(Releases(("v1.1.0-beta.1", false)), stable));
        Assert.Equal("v1.0.1", GitHubReleases.Newest(Releases(("v1.1.0-beta.1", false), ("v1.0.1", false)), stable)?.Tag);
    }

    [Fact]
    public void APreReleaseBuildIsOfferedTheStableRelease()
    {
        Assert.Equal("v1.0.0", GitHubReleases.Newest(Releases(("v1.0.0", false), ("v1.0.0-beta.3", false)), Beta2)?.Tag);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("[{\"tag_name\":")]
    [InlineData("")]
    public void ABodyThatIsNoListOfReleasesThrows(string body) =>
        Assert.ThrowsAny<JsonException>(() => GitHubReleases.Newest(body, Beta2));

    [Fact]
    public void ATagOpensItsReleasePageAndNothingElseOpensAnything()
    {
        Assert.Equal("https://github.com/harningle/claude-usage-tracker/releases/tag/v1.0.0-beta.3", GitHubReleases.Page("v1.0.0-beta.3")?.AbsoluteUri);
        Assert.Null(GitHubReleases.Page("../../evil"));
        Assert.Null(GitHubReleases.Page("v1.0.0/../../x"));
        Assert.Null(GitHubReleases.Page("https://example.invalid/"));
    }
}

/// <summary>
/// The update check (plan.md Milestone 7): one anonymous request to GitHub, a daily automatic check only when switched
/// on, one notification per release from an automatic check, and every failure shown for what it is.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private const string NewerRelease = """[{"tag_name":"v1.0.0-beta.3","draft":false,"prerelease":true}]""";
    private const string SameRelease = """[{"tag_name":"v1.0.0-beta.2","draft":false,"prerelease":true}]""";

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly AppSettings _settings = new();
    private readonly RecordingNotifier _notifier = new();
    private int _saves;

    public void Dispose() => _temp.Dispose();

    private RequestLog Log => new(Path.Combine(_temp.Path, "diagnostics"));

    /// <summary>A service that finds no release, for the settings pages' tests; it checks only when asked to.</summary>
    internal static UpdateService Idle(AppSettings settings, TimeProvider time, string logDirectory) =>
        new(new HttpClient(ScriptedHandler.Returning(HttpStatusCode.OK, "[]")), time, new RequestLog(logDirectory), settings, () => true,
            new RecordingNotifier(), ReleaseVersion.Parse("1.0.0-beta.2")!);

    private UpdateService Service(ScriptedHandler handler, string version = "1.0.0-beta.2") =>
        new(new HttpClient(handler), _time, Log, _settings, () =>
        {
            _saves++;
            return true;
        }, _notifier, ReleaseVersion.Parse(version)!);

    private static ScriptedHandler Answering(string body) => ScriptedHandler.Returning(HttpStatusCode.OK, body);

    [Fact]
    public async Task TheCheckIsOneAnonymousRequestForTheReleasesList()
    {
        var handler = Answering(SameRelease);

        await Service(handler).CheckNowAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/harningle/claude-usage-tracker/releases", request.RequestUri?.AbsoluteUri);
        Assert.Equal("ClaudeUsageTracker-Windows/1.0.0-beta.2", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Null(request.Content);
        var entry = Assert.Single(Log.ReadRecent(10));
        Assert.Equal(("updateCheck", "GET", "/repos/harningle/claude-usage-tracker/releases", 200, (string?)null),
            (entry.Operation, entry.Method, entry.Route, entry.Status, entry.ErrorCategory));
    }

    [Fact]
    public async Task UpToDateIsRecordedAndSaved()
    {
        var service = Service(Answering(SameRelease));

        await service.CheckNowAsync();

        Assert.Equal(new UpdateCheck(UpdateCheckOutcome.UpToDate, Now), service.Last);
        Assert.Equal(Now, _settings.LastUpdateCheck);
        Assert.Equal(1, _saves);
        Assert.False(service.IsChecking);
    }

    [Fact]
    public async Task TheButtonShowsANewerReleaseWithoutANotification()
    {
        var service = Service(Answering(NewerRelease));

        await service.CheckNowAsync();

        Assert.Equal(UpdateCheckOutcome.Available, service.Last?.Outcome);
        Assert.Equal("v1.0.0-beta.3", service.Last?.Release?.Tag);
        Assert.Empty(_notifier.Shown);
        // It is on the page, so a later automatic check does not announce it again.
        Assert.Equal("v1.0.0-beta.3", _settings.AnnouncedUpdate);
    }

    [Fact]
    public async Task NothingIsCheckedAutomaticallyUnlessSwitchedOn()
    {
        var handler = Answering(NewerRelease);
        var service = Service(handler);

        await service.Tick();

        Assert.Empty(handler.Requests);
        Assert.Null(service.Last);
    }

    [Fact]
    public async Task AnAutomaticCheckAnnouncesAReleaseOnce()
    {
        _settings.AutomaticUpdateChecks = true;
        var service = Service(Answering(NewerRelease));

        await service.Tick();
        _time.Now += UpdateService.Interval;
        await service.Tick();

        var alert = Assert.Single(_notifier.Shown);
        Assert.Equal(("Claude Usage Tracker 1.0.0-beta.3 is available", "v1.0.0-beta.3", UpdateService.NotificationTag, (Guid?)null),
            (alert.Title, alert.Release, alert.Tag, alert.ProfileId));
        Assert.Equal("v1.0.0-beta.3", _settings.AnnouncedUpdate);
    }

    [Fact]
    public async Task AnEvenNewerReleaseIsAnnouncedToo()
    {
        _settings.AutomaticUpdateChecks = true;
        _settings.AnnouncedUpdate = "v1.0.0-beta.3";
        var service = Service(Answering("""[{"tag_name":"v1.0.0-beta.4"},{"tag_name":"v1.0.0-beta.3"}]"""));

        await service.Tick();

        Assert.Equal("v1.0.0-beta.4", Assert.Single(_notifier.Shown).Release);
    }

    [Fact]
    public async Task AutomaticChecksRunOnceADay()
    {
        _settings.AutomaticUpdateChecks = true;
        var handler = Answering(SameRelease);
        var service = Service(handler);

        await service.Tick();
        _time.Now += UpdateService.Interval - TimeSpan.FromMinutes(1);
        await service.Tick();
        Assert.Single(handler.Requests);

        _time.Now += TimeSpan.FromMinutes(1);
        await service.Tick();
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ACheckLastNightCountsAcrossARestart()
    {
        _settings.AutomaticUpdateChecks = true;
        _settings.LastUpdateCheck = Now - TimeSpan.FromHours(3);
        var handler = Answering(SameRelease);

        await Service(handler).Tick();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ALastCheckInTheFutureDoesNotHoldOffChecks()
    {
        _settings.AutomaticUpdateChecks = true;
        _settings.LastUpdateCheck = Now + TimeSpan.FromDays(300);
        var handler = Answering(SameRelease);

        await Service(handler).Tick();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnAutomaticCheckThatReachedNothingIsTriedAgainWithinTheHour()
    {
        _settings.AutomaticUpdateChecks = true;
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("No such host"));
        var service = Service(handler);

        await service.Tick();
        Assert.Equal(UpdateCheckOutcome.Unreachable, service.Last?.Outcome);
        Assert.Null(_settings.LastUpdateCheck);
        Assert.Equal(0, _saves);

        _time.Now += UpdateService.RetryAfterUnreachable - TimeSpan.FromMinutes(1);
        await service.Tick();
        Assert.Single(handler.Requests);

        _time.Now += TimeSpan.FromMinutes(1);
        await service.Tick();
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("network", Log.ReadRecent(10)[0].ErrorCategory);
    }

    [Fact]
    public async Task ATimeoutReachedNothing()
    {
        var service = Service(new ScriptedHandler((_, _) => throw new TaskCanceledException()));

        await service.CheckNowAsync();

        Assert.Equal(UpdateCheckOutcome.Unreachable, service.Last?.Outcome);
        Assert.Equal("timeout", Assert.Single(Log.ReadRecent(10)).ErrorCategory);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null, UpdateCheckOutcome.NoReleases, "notFound")]
    [InlineData(HttpStatusCode.TooManyRequests, null, UpdateCheckOutcome.RateLimited, "rateLimited")]
    [InlineData(HttpStatusCode.Forbidden, "0", UpdateCheckOutcome.RateLimited, "rateLimited")]
    [InlineData(HttpStatusCode.Forbidden, "12", UpdateCheckOutcome.Failed, "httpError")]
    [InlineData(HttpStatusCode.InternalServerError, null, UpdateCheckOutcome.Failed, "httpError")]
    public async Task AnHttpFailureIsShownForWhatItIsAndWaitsADay(HttpStatusCode status, string? remaining, UpdateCheckOutcome expected, string category)
    {
        _settings.AutomaticUpdateChecks = true;
        var handler = ScriptedHandler.Returning(status, """{"message":"x"}""",
            configure: r => _ = remaining is null || r.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining));
        var service = Service(handler);

        await service.Tick();
        _time.Now += UpdateService.RetryAfterUnreachable;
        await service.Tick();

        Assert.Equal(new UpdateCheck(expected, Now, Status: (int)status), service.Last);
        Assert.Single(handler.Requests);
        Assert.Equal(Now, _settings.LastUpdateCheck);
        Assert.Empty(_notifier.Shown);
        Assert.Equal(category, Assert.Single(Log.ReadRecent(10)).ErrorCategory);
    }

    [Fact]
    public async Task AnUnreadableListIsAnInvalidResponse()
    {
        var service = Service(Answering("""{"message":"not a list"}"""));

        await service.CheckNowAsync();

        Assert.Equal(new UpdateCheck(UpdateCheckOutcome.InvalidResponse, Now, Status: 200), service.Last);
        Assert.Equal("invalidResponse", Assert.Single(Log.ReadRecent(10)).ErrorCategory);
    }

    [Fact]
    public async Task ASecondPressWhileCheckingSendsNoSecondRequest()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new ScriptedHandler((_, _) => answer.Task);
        var service = Service(handler);
        var changes = 0;
        service.Changed += () => changes++;

        var first = service.CheckNowAsync();
        Assert.True(service.IsChecking);
        // The second press returns at once, without waiting for an answer of its own.
        Assert.True(service.CheckNowAsync().IsCompleted);
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SameRelease) });
        await first;

        Assert.Single(handler.Requests);
        Assert.False(service.IsChecking);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ThisBuildHasAComparableVersion() => Assert.NotNull(UpdateService.ThisBuild());
}
