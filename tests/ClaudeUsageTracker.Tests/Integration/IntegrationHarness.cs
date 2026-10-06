using System.IO;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Integration;

namespace ClaudeUsageTracker.Tests.Integration;

/// <summary>
/// The integrations as the app wires them, over a temporary data root and user profile: a file standing in for
/// the bundled helper, synthetic Claude Code folders, a real loopback listener and no real Claude Code file.
/// </summary>
internal sealed class IntegrationHarness : IDisposable
{
    /// <param name="helper">The content of the file standing in for the helper this build carries.</param>
    public IntegrationHarness(string root, AppSettings settings, TimeProvider time, bool bundled = true, string helper = "helper build 1")
    {
        Settings = settings;
        Data = Path.Combine(root, "data");
        Home = Path.Combine(root, "home");
        Bundle = Path.Combine(root, "app", "helper", HelperInfo.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(Bundle)!);
        Directory.CreateDirectory(Home);
        File.WriteAllText(Bundle, helper);
        Folders.Add(Path.Combine(Home, ".claude"));

        Service = new IntegrationService(Data, Home, bundled ? Bundle : null, time);
        Hud = new HudService(
            new HookEndpointStore(new SecretStore(Path.Combine(Data, "secrets", "integration"))), Service.Store, Service.Manifest,
            () => Reservations, KnownFolders, time, action => action());
        Files = new StatuslineFiles(Data);
        Coordinator = new IntegrationCoordinator(
            settings, Save, Service, Hud, Files, new StatuslineSync(Files, Read, Home), KnownFolders, () => GitBash,
            id => Fetched.GetValueOrDefault(id));
        Pages = new IntegrationPages(settings, Coordinator, Save, () => ActiveState, Read, _ => OnPath, Home, time);
    }

    public AppSettings Settings { get; }

    public string Data { get; }

    public string Home { get; }

    /// <summary>The file standing in for the helper this build carries.</summary>
    public string Bundle { get; }

    /// <summary>Claude Code folders the app knows besides those of installed launchers; the default one to begin with.</summary>
    public List<string> Folders { get; } = [];

    public bool GitBash { get; set; } = true;

    public bool OnPath { get; set; } = true;

    public PortReservations Reservations { get; set; } = new(new PortRange(1024, 15000), []);

    public Dictionary<Guid, CachedUsage> Fetched { get; } = [];

    public ProfileUsageState? ActiveState { get; set; }

    /// <summary>How often the settings were saved.</summary>
    public int Saves { get; private set; }

    /// <summary>Makes every save fail, as a full disk or a locked settings file would.</summary>
    public bool SaveFails { get; set; }

    public IntegrationService Service { get; }

    public HudService Hud { get; }

    public StatuslineFiles Files { get; }

    public IntegrationCoordinator Coordinator { get; }

    public IntegrationPages Pages { get; }

    public string DefaultFolder => Folders[0];

    public IReadOnlyList<string> KnownFolders() => [.. Folders.Union(Service.LauncherDirectories, StringComparer.OrdinalIgnoreCase)];

    public CliSnapshot Read(CliConfigDirectory directory) => CliStore.Read(directory, DateTimeOffset.UtcNow);

    public void Dispose() => Hud.Dispose();

    /// <summary>Stands for the app's save of the settings: false when it failed, which the app has shown.</summary>
    private bool Save()
    {
        if (SaveFails)
        {
            return false;
        }

        Saves++;
        return true;
    }
}
