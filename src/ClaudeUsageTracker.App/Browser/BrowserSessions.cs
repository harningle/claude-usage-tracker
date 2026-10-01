using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ClaudeUsageTracker.App.Usage;
using Microsoft.Web.WebView2.Core;

namespace ClaudeUsageTracker.App.Browser;

/// <summary>
/// One isolated WebView2 context per profile, each with its own user-data folder and browser process
/// group (plan.md section 2, "Browser authentication"). claude.ai requests are sent as same-origin
/// <c>fetch</c> calls from a page of that context, because every <c>HttpClient</c> replay was challenged
/// (Milestone 1). The background view has no window: it is parented to a message-only window and parked
/// on a small same-origin document, which costs far less memory than the claude.ai app page. Cookies stay
/// in WebView2's own storage and are never copied into settings, logs or secret files. Every member except
/// <see cref="SessionEpoch"/> and <see cref="SendAsync"/> must be called on the UI thread.
/// </summary>
public sealed partial class BrowserSessions(string root, Dispatcher dispatcher) : IClaudeAiTransport, IDisposable
{
    /// <summary>A small document on the claude.ai origin to send same-origin requests from.</summary>
    public const string ParkedUrl = "https://claude.ai/robots.txt";

    private static readonly IntPtr MessageOnlyParent = new(-3);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly ConcurrentDictionary<Guid, long> _epochs = new();

    public string FolderFor(Guid profileId) => Path.Combine(root, profileId.ToString("N"));

    public long SessionEpoch(Guid profileId) => _epochs.GetValueOrDefault(profileId);

    /// <summary>Marks the profile's browser session as changed, so a rejected session is asked again.</summary>
    public void SessionChanged(Guid profileId) => _epochs.AddOrUpdate(profileId, 1, (_, epoch) => epoch + 1);

    /// <summary>The installed WebView2 Runtime's version, or null when it is not installed.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The user agent WebView2 sends, built from the installed runtime's version rather than hard-coded.
    /// The Console's <c>HttpClient</c> requests use it, as the Milestone 1 replay did.
    /// </summary>
    public static string? UserAgent() =>
        RuntimeVersion()?.Split('.')[0] is { Length: > 0 } major
            ? $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0"
            : null;

    public Task<BrowserResponse> SendAsync(Guid profileId, ClaudeAiOperation operation, string? organizationId, CancellationToken cancellation)
    {
        var path = PathFor(operation, organizationId);
        return dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var core = await BackgroundAsync(profileId);
                return await ParkAsync(core) ? await FetchAsync(core, path) : new BrowserResponse(null, null, false, null, null, "navigation");
            }
            catch (Exception e) when (e is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // No runtime, or the browser process ended; the next check starts a new one.
                Drop(profileId);
                return new BrowserResponse(null, null, false, null, null, "network");
            }
        }).Task.Unwrap().WaitAsync(cancellation);
    }

    /// <summary>The profile's WebView2 environment, shared by its background view and its sign-in window.</summary>
    public Task<CoreWebView2Environment> EnvironmentAsync(Guid profileId) =>
        SessionFor(profileId).Environment ??= CoreWebView2Environment.CreateAsync(null, FolderFor(profileId));

    /// <summary>
    /// Puts a pasted session key into the profile's browser as its claude.ai <c>sessionKey</c> cookie and
    /// returns what was there before, so a key that fails its test can be undone. The key is validated
    /// through the same browser route as a sign-in (plan.md: never through a bare <c>HttpClient</c> request).
    /// </summary>
    public async Task<CoreWebView2Cookie?> SetSessionKeyAsync(Guid profileId, string sessionKey)
    {
        var core = await BackgroundAsync(profileId);
        var previous = (await core.CookieManager.GetCookiesAsync("https://claude.ai")).FirstOrDefault(c => c.Name == "sessionKey");
        var cookie = core.CookieManager.CreateCookie("sessionKey", sessionKey, ".claude.ai", "/");
        cookie.IsSecure = true;
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
        // claude.ai's own session cookie lasts 28 days (Milestone 1); the server decides when it really ends.
        cookie.Expires = DateTime.Now.AddDays(28);
        core.CookieManager.AddOrUpdateCookie(cookie);
        SessionChanged(profileId);
        return previous;
    }

    /// <summary>Undoes <see cref="SetSessionKeyAsync"/>: restores the earlier cookie, or removes the pasted one.</summary>
    public async Task RestoreSessionKeyAsync(Guid profileId, CoreWebView2Cookie? previous)
    {
        var core = await BackgroundAsync(profileId);
        if (previous is not null)
        {
            core.CookieManager.AddOrUpdateCookie(previous);
        }
        else
        {
            core.CookieManager.DeleteCookiesWithDomainAndPath("sessionKey", ".claude.ai", "/");
        }

        SessionChanged(profileId);
    }

    /// <summary>Closes the profile's background view; its browser processes exit once no window of it remains.</summary>
    public void Release(Guid profileId) => Drop(profileId);

    /// <summary>
    /// Signs the profile out by removing its browser state: closes the background view, waits for the
    /// browser processes to exit, then deletes the user-data folder. Returns false when the folder could not
    /// be deleted yet (a browser process still running, a sign-in window still open); the caller retries later.
    /// </summary>
    public async Task<bool> ClearAsync(Guid profileId)
    {
        var environment = _sessions.GetValueOrDefault(profileId)?.Environment is { IsCompletedSuccessfully: true } created ? created.Result : null;
        var exited = new TaskCompletionSource();
        if (environment is not null)
        {
            environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        }

        Drop(profileId);
        SessionChanged(profileId);
        if (environment is not null)
        {
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        }

        return DeleteFolder(FolderFor(profileId));
    }

    /// <summary>Deletes the browser folder of a profile that has no running browser, e.g. at startup.</summary>
    public bool DeleteFolderOf(Guid profileId) => !_sessions.ContainsKey(profileId) && DeleteFolder(FolderFor(profileId));

    public void Dispose()
    {
        foreach (var profileId in _sessions.Keys.ToList())
        {
            Drop(profileId);
        }
    }

    /// <summary>The fixed path of an allowlisted operation. Organisation IDs are checked before they reach a URL.</summary>
    public static string PathFor(ClaudeAiOperation operation, string? organizationId)
    {
        if (operation == ClaudeAiOperation.Organizations)
        {
            return "/api/organizations";
        }

        if (organizationId is null || !OrganizationId().IsMatch(organizationId))
        {
            throw new ArgumentException("The organisation ID is not in the expected form.", nameof(organizationId));
        }

        return operation switch
        {
            ClaudeAiOperation.Usage => $"/api/organizations/{organizationId}/usage",
            ClaudeAiOperation.OverageSpendLimit => $"/api/organizations/{organizationId}/overage_spend_limit",
            ClaudeAiOperation.OverageCreditGrant => $"/api/organizations/{organizationId}/overage_credit_grant",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an allowlisted operation."),
        };
    }

    /// <summary>
    /// Sends one credentialed GET from the page and returns what came back. The page must be on the
    /// claude.ai origin. Only the path, already built from an allowlisted operation, enters the script.
    /// </summary>
    public static async Task<BrowserResponse> FetchAsync(CoreWebView2 core, string path)
    {
        const string script = """
            (async () => {
              try {
                const r = await fetch(__PATH__, { credentials: 'include', headers: { 'Accept': 'application/json' }, signal: AbortSignal.timeout(30000) });
                const text = await r.text();
                return JSON.stringify({ status: r.status, contentType: r.headers.get('content-type'), cfMitigated: r.headers.get('cf-mitigated') !== null,
                                        retryAfter: r.headers.get('retry-after'), body: text.slice(0, 1048576) });
              } catch (e) { return JSON.stringify({ error: e && e.name === 'TimeoutError' ? 'timeout' : 'network' }); }
            })()
            """;
        var parameters = new JsonObject
        {
            ["expression"] = script.Replace("__PATH__", JsonSerializer.Serialize(path), StringComparison.Ordinal),
            ["awaitPromise"] = true,
            ["returnByValue"] = true,
        };
        var reply = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters.ToJsonString()));
        if (reply?["result"]?["value"] is not JsonValue value || !value.TryGetValue(out string? text)
            || JsonNode.Parse(text) is not JsonObject result)
        {
            return new BrowserResponse(null, null, false, null, null, "network");
        }

        return new BrowserResponse(
            (int?)result["status"], (string?)result["contentType"], (bool?)result["cfMitigated"] ?? false, (string?)result["body"],
            (string?)result["retryAfter"], (string?)result["error"]);
    }

    /// <summary>The profile's windowless background view, created on first use.</summary>
    private async Task<CoreWebView2> BackgroundAsync(Guid profileId)
    {
        // Two checks may arrive before the first controller exists; the second waits for the same task.
        var session = SessionFor(profileId);
        return (await (session.Controller ??= CreateAsync())).CoreWebView2;

        async Task<CoreWebView2Controller> CreateAsync()
        {
            var environment = await EnvironmentAsync(profileId);
            var controller = await environment.CreateCoreWebView2ControllerAsync(MessageOnlyParent);
            if (!ReferenceEquals(_sessions.GetValueOrDefault(profileId), session))
            {
                // The profile was released or cleared while its browser was starting.
                controller.Close();
                throw new InvalidOperationException("The browser session was closed while it was starting.");
            }

            controller.CoreWebView2.ProcessFailed += (_, _) => Drop(profileId);
            return controller;
        }
    }

    /// <summary>Makes sure the background view is on the claude.ai origin. Returns false when it cannot be reached.</summary>
    private static async Task<bool> ParkAsync(CoreWebView2 core)
    {
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var current) && current is { Scheme: "https", Host: "claude.ai" })
        {
            return true;
        }

        var completed = new TaskCompletionSource<bool>();
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => completed.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(ParkedUrl);
            return await Task.WhenAny(completed.Task, Task.Delay(NavigationTimeout)) == completed.Task && await completed.Task;
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }
    }

    private Session SessionFor(Guid profileId)
    {
        if (!_sessions.TryGetValue(profileId, out var session))
        {
            _sessions[profileId] = session = new Session();
        }

        return session;
    }

    private void Drop(Guid profileId)
    {
        if (_sessions.Remove(profileId, out var session) && session.Controller is { IsCompletedSuccessfully: true } controller)
        {
            controller.Result.Close();
        }
    }

    private static bool DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex OrganizationId();

    private sealed class Session
    {
        public Task<CoreWebView2Environment>? Environment { get; set; }

        public Task<CoreWebView2Controller>? Controller { get; set; }
    }
}
