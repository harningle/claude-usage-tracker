using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AuthSpike;

/// <summary>One isolated browser profile: its own WebView2 user-data folder and browser process group.</summary>
internal sealed class ProfileSession(string name, Action<string> log)
{
    public string Name { get; } = name;
    public string UserDataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageTracker.Spikes", "auth", "profiles", name);
    public WebView2 View { get; } = new();
    private CoreWebView2Environment? environment;
    private bool reportedSignIn;

    public CoreWebView2 Core => View.CoreWebView2 ?? throw new InvalidOperationException($"Profile {Name} is not initialised.");

    public async Task InitialiseAsync(string startUrl)
    {
        var stopwatch = Stopwatch.StartNew();
        environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
        await View.EnsureCoreWebView2Async(environment);
        var ready = stopwatch.ElapsedMilliseconds;
        var firstNavigation = new TaskCompletionSource<bool>();
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) => firstNavigation.TrySetResult(e.IsSuccess);
        Core.NavigationCompleted += Completed;
        Core.Navigate(startUrl);
        var success = await firstNavigation.Task;
        Core.NavigationCompleted -= Completed;
        log($"[{Name}] startup: environment + control ready {ready} ms, first navigation {(success ? "ok" : "FAILED")} at {stopwatch.ElapsedMilliseconds} ms ({startUrl})");
        Core.NavigationCompleted += (_, _) => _ = CheckSignInAsync();
    }

    private async Task CheckSignInAsync()
    {
        var cookies = await Core.CookieManager.GetCookiesAsync("https://claude.ai");
        var signedIn = cookies.Any(c => c.Name == "sessionKey");
        if (signedIn && !reportedSignIn)
        {
            log($"[{Name}] claude.ai sessionKey cookie present");
        }

        reportedSignIn = signedIn;
    }

    /// <summary>Browser fetch, then HttpClient replay variants, against claude.ai or the Console origin.</summary>
    public async Task<List<ProbeResult>> CompareAsync(bool console)
    {
        var source = new Uri(Core.Source);
        var origin = console ? $"{source.Scheme}://{source.Host}" : "https://claude.ai";
        if (!console && source.Host != "claude.ai")
        {
            throw new InvalidOperationException($"Profile {Name} is on {source.Host}; open claude.ai first so the fetch is same-origin.");
        }

        var (results, orgIds) = await Probes.BrowserAsync(Core, "/api", usage: !console);
        var userAgent = (await Core.ExecuteScriptAsync("navigator.userAgent")).Trim('"');
        var firstOrg = console ? null : orgIds.FirstOrDefault();

        var hosts = console ? new[] { origin, "https://console.anthropic.com", "https://platform.claude.com" }.Distinct() : [origin];
        foreach (var host in hosts)
        {
            var all = (await Core.CookieManager.GetCookiesAsync(host)).Where(c => Probes.ReplayCookieNames.Contains(c.Name)).ToList();
            log($"[{Name}] cookies for {host}: {Probes.Describe(all)}");
            var suffix = console ? $" -> {new Uri(host).Host}" : "";
            results.AddRange(await Probes.ReplayAsync("replay, all four cookies, WebView2 UA" + suffix, all, userAgent, origin, $"{host}/api", firstOrg));
            results.AddRange(await Probes.ReplayAsync("replay, sessionKey only, WebView2 UA" + suffix, all.Where(c => c.Name == "sessionKey"), userAgent, origin, $"{host}/api", firstOrg));
            if (!console)
            {
                results.AddRange(await Probes.ReplayAsync("replay, all four cookies, upstream Safari UA", all, Probes.UpstreamSafariUserAgent, origin, $"{host}/api", firstOrg));
            }
        }

        return results;
    }

    /// <summary>
    /// Fresh claude.ai observations for the isolation check: the session cookie, the organisations
    /// and the account ID as this profile sees them now. Values stay in memory for comparison only.
    /// </summary>
    public async Task<(string? SessionKey, List<string> OrgIds, string? AccountId, int? AccountStatus)> ObserveClaudeAiAsync()
    {
        if (new Uri(Core.Source).Host != "claude.ai")
        {
            throw new InvalidOperationException($"Profile {Name} is on {new Uri(Core.Source).Host}; open claude.ai first.");
        }

        var sessionKey = (await Core.CookieManager.GetCookiesAsync("https://claude.ai")).FirstOrDefault(c => c.Name == "sessionKey")?.Value;
        var (_, orgIds) = await Probes.BrowserAsync(Core, "/api", usage: false);
        var (status, accountId) = await Probes.AccountIdAsync(Core);
        return (sessionKey, orgIds, accountId, status);
    }

    /// <summary>Aggregate memory of this profile's browser process group (indicative).</summary>
    public (int Processes, long WorkingSetMb, long PrivateMb, string Kinds) Memory()
    {
        var infos = environment?.GetProcessInfos() ?? [];
        long working = 0, privateBytes = 0;
        foreach (var info in infos)
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                working += process.WorkingSet64;
                privateBytes += process.PrivateMemorySize64;
            }
            catch (ArgumentException)
            {
            }
        }

        var kinds = string.Join(",", infos.GroupBy(i => i.Kind).Select(g => $"{g.Key}x{g.Count()}"));
        return (infos.Count, working / (1024 * 1024), privateBytes / (1024 * 1024), kinds);
    }

    /// <summary>Disposes the control, waits for the browser process group to exit, then deletes browser state.</summary>
    public async Task ClearAsync()
    {
        if (environment is null)
        {
            return;
        }

        var exited = new TaskCompletionSource();
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        View.Dispose();
        var finished = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        log($"[{Name}] browser processes {(finished == exited.Task ? "exited" : "did NOT exit within 15 s")}");
        try
        {
            Directory.Delete(UserDataFolder, recursive: true);
            log($"[{Name}] deleted user-data folder");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"[{Name}] could not delete user-data folder: {e.GetType().Name}");
        }
    }
}
