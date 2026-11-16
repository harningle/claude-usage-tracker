using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Updates;

namespace ClaudeUsageTracker.App.Updates;

public enum UpdateCheckOutcome
{
    UpToDate,
    Available,

    /// <summary>GitHub answered 404: the repository has no public releases (or is not public).</summary>
    NoReleases,
    RateLimited,

    /// <summary>Another HTTP status.</summary>
    Failed,

    /// <summary>GitHub was not reached: no network, a refused connection or a timeout.</summary>
    Unreachable,
    InvalidResponse,
}

/// <param name="Release">The newer release, when one is available.</param>
/// <param name="Status">GitHub's HTTP status when the check failed with one.</param>
public sealed record UpdateCheck(UpdateCheckOutcome Outcome, DateTimeOffset At, AvailableRelease? Release = null, int? Status = null);

/// <summary>
/// Asks GitHub's releases list whether the port has a newer release (plan.md Milestone 7, decided 2026-10-28). It never
/// downloads anything: a newer release is shown on the About page with a link to its page, and an automatic check also
/// posts one notification per release. Automatic checks are opt-in and run at most once a day; the About page's button
/// checks whenever it is pressed. The request carries no token or cookie, only the app's name and version as its user
/// agent. Upstream's Sparkle updater is not ported (docs/parity.md, difference 55).
/// </summary>
/// <remarks>
/// Called on the UI thread; its awaits resume there, so the settings are only changed on the UI thread.
/// </remarks>
public sealed class UpdateService(
    HttpClient http, TimeProvider time, RequestLog log, AppSettings settings, Func<bool> persist, INotifier notifier, ReleaseVersion current)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>
    /// An automatic check that did not reach GitHub is tried again after this long instead of a day, so a check made at
    /// sign-in before the network is up is not lost.
    /// </summary>
    public static readonly TimeSpan RetryAfterUnreachable = TimeSpan.FromHours(1);

    /// <summary>The tag of the update notification, so a newer one replaces it.</summary>
    public const string NotificationTag = "update";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private DateTimeOffset? _retryAt;

    /// <summary>The running build's version.</summary>
    public ReleaseVersion Current => current;

    /// <summary>The last check of this run, or null before one finished.</summary>
    public UpdateCheck? Last { get; private set; }

    public bool IsChecking { get; private set; }

    /// <summary>Raised when a check starts or ends.</summary>
    public event Action? Changed;

    /// <summary>This build's version from its assembly; a build whose version is not one is a build defect.</summary>
    public static ReleaseVersion ThisBuild()
    {
        var informational = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ReleaseVersion.Parse(informational)
               ?? throw new InvalidOperationException($"This build's version '{informational}' is not major.minor.patch[-pre-release].");
    }

    /// <summary>The About page's Check for Updates.</summary>
    public Task CheckNowAsync() => CheckAsync(automatic: false);

    /// <summary>
    /// Called every 30 seconds: starts an automatic check when they are switched on and one is due. Returns that check, or
    /// a completed task.
    /// </summary>
    public Task Tick()
    {
        if (!settings.AutomaticUpdateChecks || IsChecking)
        {
            return Task.CompletedTask;
        }

        var now = time.GetUtcNow();
        // A last check in the future (the clock was turned back) does not hold off checks.
        var due = settings.LastUpdateCheck is not { } last || now - last >= Interval || last > now;
        return due && (_retryAt is not { } retry || now >= retry) ? CheckAsync(automatic: true) : Task.CompletedTask;
    }

    private async Task CheckAsync(bool automatic)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        Changed?.Invoke();
        UpdateCheck check;
        try
        {
            check = await FetchAsync();
        }
        finally
        {
            IsChecking = false;
        }

        Last = check;
        if (check.Outcome == UpdateCheckOutcome.Unreachable)
        {
            _retryAt = automatic ? check.At + RetryAfterUnreachable : _retryAt;
        }
        else
        {
            _retryAt = null;
            settings.LastUpdateCheck = check.At;
            if (check.Release is { } release && settings.AnnouncedUpdate != release.Tag)
            {
                // A release found by the button is on the page already; only an automatic check announces it.
                if (automatic)
                {
                    notifier.Show(new AppAlert(
                        Strings.Format("windows.updates.notification.title", release.Version.ToString()),
                        Strings.Get("windows.updates.notification.body"), null, "default", NotificationTag, release.Tag));
                }

                settings.AnnouncedUpdate = release.Tag;
            }

            persist();
        }

        Changed?.Invoke();
    }

    private async Task<UpdateCheck> FetchAsync()
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleases.ApiUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeUsageTracker-Windows", current.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await HttpResponses.ReadBoundedAsync(response.Content, timeout.Token);
                try
                {
                    var newest = GitHubReleases.Newest(body, current);
                    Log(status, started, null);
                    return newest is null
                        ? new UpdateCheck(UpdateCheckOutcome.UpToDate, time.GetUtcNow())
                        : new UpdateCheck(UpdateCheckOutcome.Available, time.GetUtcNow(), newest);
                }
                catch (JsonException)
                {
                    Log(status, started, "invalidResponse");
                    return new UpdateCheck(UpdateCheckOutcome.InvalidResponse, time.GetUtcNow(), Status: status);
                }
            }

            var outcome = response.StatusCode switch
            {
                HttpStatusCode.NotFound => UpdateCheckOutcome.NoReleases,
                HttpStatusCode.TooManyRequests => UpdateCheckOutcome.RateLimited,
                // GitHub's primary rate limit answers 403 with no requests remaining.
                HttpStatusCode.Forbidden when response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
                                              && remaining.FirstOrDefault() == "0" => UpdateCheckOutcome.RateLimited,
                _ => UpdateCheckOutcome.Failed,
            };
            Log(status, started, outcome switch
            {
                UpdateCheckOutcome.NoReleases => "notFound",
                UpdateCheckOutcome.RateLimited => "rateLimited",
                _ => "httpError",
            });
            return new UpdateCheck(outcome, time.GetUtcNow(), Status: status);
        }
        catch (OperationCanceledException)
        {
            Log(null, started, "timeout");
            return new UpdateCheck(UpdateCheckOutcome.Unreachable, time.GetUtcNow());
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            Log(null, started, "network");
            return new UpdateCheck(UpdateCheckOutcome.Unreachable, time.GetUtcNow());
        }
    }

    private void Log(int? status, long started, string? error) =>
        log.Append(new RequestLogEntry(
            time.GetUtcNow(), "updateCheck", "GET", GitHubReleases.Route, status,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "HttpClient", error));
}
