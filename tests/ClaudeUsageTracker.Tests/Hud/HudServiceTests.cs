using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.Tests.Hud;

/// <summary>
/// The HUD's lifecycle with a real loopback listener and synthetic Claude Code folders: listener first, then the
/// entries; on a clean stop the entries go first; and the path secret stays out of the tracker's plain files.
/// </summary>
public sealed class HudServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _folders = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly List<HudService> _services = [];
    private PortReservations _reservations = new(new PortRange(1024, 15000), []);

    public HudServiceTests()
    {
        _folders.Add(Folder(".claude"));
        _folders.Add(Folder(".claude-work"));
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        _http.Dispose();
        _temp.Dispose();
    }

    private string Folder(string name) => Path.Combine(_temp.Path, "home", name);

    private string Data => Path.Combine(_temp.Path, "data");

    private InstallManifestStore Manifests => new(Path.Combine(Data, "install.json"));

    private HookEndpointStore Endpoints => new(new SecretStore(Path.Combine(Data, "secrets", "integration")));

    /// <summary>A service as the app makes one at start; events are applied at once, as the UI thread would.</summary>
    private HudService Service(Random? random = null)
    {
        var service = new HudService(Endpoints, Manifests, Manifests.Load(), () => _reservations, () => _folders, _time, action => action(), random);
        _services.Add(service);
        return service;
    }

    private static JsonObject? Settings(string folder) => ClaudeSettingsFile.Read(ClaudeSettingsFile.PathFor(folder));

    private static List<string> HookUrls(string folder) =>
        [.. ((Settings(folder)?["hooks"] as JsonObject) ?? new JsonObject())
            .SelectMany(e => (JsonArray)e.Value!).SelectMany(g => (JsonArray)g!["hooks"]!).Select(h => (string?)h!["url"]).OfType<string>()];

    private async Task<HttpStatusCode> PostAsync(string url, string json) =>
        (await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken)).StatusCode;

    [Fact]
    public async Task StartingListensFirstAndThenInstallsTheHooksInEveryKnownFolder()
    {
        Directory.CreateDirectory(_folders[0]);
        File.WriteAllText(ClaudeSettingsFile.PathFor(_folders[0]),
            """{"model":"opus","hooks":{"Stop":[{"matcher":"","hooks":[{"type":"command","command":"notify.exe"}]}]}}""");
        var service = Service();

        service.Start();

        Assert.Equal(HudServerStatus.Running, service.Status);
        Assert.Null(service.Problem);
        var port = service.Port!.Value;
        Assert.True(_reservations.IsEligible(port));
        foreach (var folder in _folders)
        {
            var urls = HookUrls(folder);
            Assert.Equal(8, urls.Count);
            Assert.All(urls, url => Assert.StartsWith($"http://127.0.0.1:{port}/hook/", url));
        }

        Assert.All(service.FolderStatuses(), folder => Assert.Equal(HookInstallStatus.Installed, folder.Status));
        // The other tool's hook and setting are untouched.
        Assert.Equal("opus", (string?)Settings(_folders[0])!["model"]);
        Assert.Equal("notify.exe", (string?)Settings(_folders[0])!["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);

        // An installed URL is live: an event posted to it shows up as a session of its folder.
        var url = HookUrls(_folders[1]).Single(u => u.EndsWith("/pre-tool-use", StringComparison.Ordinal));
        var changes = 0;
        service.Changed += () => changes++;
        var transcript = Path.Combine(_folders[1], "projects", "C--work-app", "s1.jsonl").Replace(@"\", @"\\");
        Assert.Equal(HttpStatusCode.OK, await PostAsync(url,
            $$"""{"session_id":"s1","transcript_path":"{{transcript}}","cwd":"C:\\work\\app","tool_name":"Bash","tool_input":{"command":"dotnet test"},"tool_use_id":"t1"}"""));
        Assert.True(SpinWait.SpinUntil(() => service.Sessions.Sessions.Count == 1, TimeSpan.FromSeconds(5)));
        var session = service.Sessions.Sessions[0];
        Assert.Equal((SessionStatus.RunningCommand, "app", _folders[1]), (session.Status, session.DisplayName, session.ConfigDirectory));
        Assert.True(changes > 0);
    }

    [Fact]
    public void TheSecretIsKeptEncryptedAndOnlyClaudeCodesSettingsCarryIt()
    {
        var service = Service();
        service.Start();
        var token = HookUrls(_folders[0])[0].Split('/')[4];

        // 256 bits, as 64 hex characters.
        Assert.Matches("^[0-9a-f]{64}$", token);
        foreach (var file in Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(token, Encoding.Latin1.GetString(File.ReadAllBytes(file)));
        }

        // The manifest names the port, by which entries are known as the tracker's own, and nothing secret.
        Assert.Equal([service.Port!.Value], Manifests.Load().HookPorts);
        Assert.Equal(new HookEndpoint(service.Port, token), Endpoints.Load());
    }

    [Fact]
    public async Task ACleanStopRemovesTheHooksBeforeTheListenerStops()
    {
        Directory.CreateDirectory(_folders[0]);
        File.WriteAllText(ClaudeSettingsFile.PathFor(_folders[0]), """{"hooks":{"Stop":[{"matcher":"","hooks":[{"type":"command","command":"notify.exe"}]}]}}""");
        var service = Service();
        service.Start();
        var url = HookUrls(_folders[1])[0];
        await PostAsync(url.Replace("session-start", "stop"), """{"session_id":"s1"}""");
        Assert.True(SpinWait.SpinUntil(() => service.Sessions.Sessions.Count == 1, TimeSpan.FromSeconds(5)));

        service.Stop();

        Assert.Equal(HudServerStatus.Stopped, service.Status);
        Assert.Null(service.Problem);
        Assert.Null(service.Port);
        Assert.Empty(service.Sessions.Sessions);
        Assert.Equal("""{"hooks":{"Stop":[{"matcher":"","hooks":[{"type":"command","command":"notify.exe"}]}]}}""", Settings(_folders[0])!.ToJsonString());
        Assert.Equal("{}", Settings(_folders[1])!.ToJsonString());
        Assert.All(service.FolderStatuses(), folder => Assert.Equal(HookInstallStatus.NotInstalled, folder.Status));
        Assert.Empty(Manifests.Load().HookPorts);
        await Assert.ThrowsAsync<HttpRequestException>(() => PostAsync(url, """{"session_id":"s1"}"""));
    }

    [Fact]
    public void TheSamePortAndSecretAreUsedAgainAfterARestart()
    {
        var first = Service();
        first.Start();
        var port = first.Port;
        var urls = HookUrls(_folders[0]);
        var written = File.GetLastWriteTimeUtc(ClaudeSettingsFile.PathFor(_folders[0]));
        // A crash: the listener is gone, the entries stay.
        first.Dispose();

        var second = Service();
        second.Start();

        Assert.Equal(port, second.Port);
        Assert.Equal(urls, HookUrls(_folders[0]));
        // Entries that are already right are not written again.
        Assert.Equal(written, File.GetLastWriteTimeUtc(ClaudeSettingsFile.PathFor(_folders[0])));
    }

    [Fact]
    public void ASavedPortThatIsTakenIsReportedAndRepairMovesToAnotherPort()
    {
        var first = Service();
        first.Start();
        var port = first.Port!.Value;
        var old = HookUrls(_folders[0]);

        // Another process holds the port (here: the first listener, still running).
        var second = Service();
        second.Start();
        Assert.Equal(HudServerStatus.PortBusy, second.Status);
        Assert.Null(second.Port);
        Assert.Equal(old, HookUrls(_folders[0]));
        Assert.All(second.FolderStatuses(), folder => Assert.Equal(HookInstallStatus.Outdated, folder.Status));

        second.Start(newPort: true);

        Assert.Equal(HudServerStatus.Running, second.Status);
        Assert.NotEqual(port, second.Port);
        var urls = HookUrls(_folders[0]);
        Assert.Equal(8, urls.Count);
        Assert.All(urls, url => Assert.StartsWith($"http://127.0.0.1:{second.Port}/hook/", url));
        Assert.Equal(second.Port, Endpoints.Load()!.Port);
    }

    [Fact]
    public void ASavedPortThatWindowsHasSinceReservedIsReplaced()
    {
        var first = Service();
        first.Start();
        var port = first.Port!.Value;
        first.Dispose();
        // The machine's ranges changed: the saved port is now inside an excluded range.
        _reservations = new PortReservations(_reservations.Dynamic, [new PortRange(port, port)]);

        var second = Service();
        second.Start();

        Assert.Equal(HudServerStatus.Running, second.Status);
        Assert.NotEqual(port, second.Port);
        Assert.True(_reservations.IsEligible(second.Port!.Value));
        Assert.All(HookUrls(_folders[0]), url => Assert.StartsWith($"http://127.0.0.1:{second.Port}/hook/", url));
        Assert.All(second.FolderStatuses(), folder => Assert.Equal(HookInstallStatus.Installed, folder.Status));
    }

    [Fact]
    public void WithoutAnEligiblePortTheHudReportsWhyAndInstallsNothing()
    {
        _reservations = new PortReservations(new PortRange(1024, 65535), []);
        var service = Service();

        service.Start();

        Assert.Equal(HudServerStatus.Failed, service.Status);
        Assert.NotNull(service.Problem);
        Assert.All(_folders, folder => Assert.Null(Settings(folder)));
        Assert.Null(Endpoints.Load()?.Port);
    }

    [Fact]
    public void AFolderWhoseSettingsCannotBeEditedIsReportedAndTheOthersStillGetTheirHooks()
    {
        Directory.CreateDirectory(_folders[0]);
        File.WriteAllText(ClaudeSettingsFile.PathFor(_folders[0]), "{ not json");
        var service = Service();

        service.Start();

        Assert.Equal(HudServerStatus.Running, service.Status);
        Assert.NotNull(service.Problem);
        Assert.Equal("{ not json", File.ReadAllText(ClaudeSettingsFile.PathFor(_folders[0])));
        Assert.Equal(8, HookUrls(_folders[1]).Count);
        var statuses = service.FolderStatuses();
        Assert.NotNull(statuses[0].Problem);
        Assert.Equal(HookInstallStatus.Installed, statuses[1].Status);
    }

    [Fact]
    public void AFolderThatBecomesKnownLaterGetsItsHooks()
    {
        var service = Service();
        service.Start();
        _folders.Add(Folder(".claude-new"));

        service.InstallHooks();

        Assert.Equal(8, HookUrls(_folders[2]).Count);
    }

    [Fact]
    public async Task SilentSessionsAreSwept()
    {
        var service = Service();
        service.Start();
        var url = HookUrls(_folders[0]).Single(u => u.EndsWith("/user-prompt-submit", StringComparison.Ordinal));
        await PostAsync(url, """{"session_id":"s1","prompt":"hello"}""");
        Assert.True(SpinWait.SpinUntil(() => service.Sessions.Sessions.Count == 1, TimeSpan.FromSeconds(5)));
        var changes = 0;
        service.Changed += () => changes++;

        service.Sweep();
        Assert.Equal(0, changes);

        _time.Now += HudSessionStore.StaleSessionTimeout + TimeSpan.FromSeconds(1);
        service.Sweep();
        Assert.Empty(service.Sessions.Sessions);
        Assert.Equal(1, changes);
    }
}
