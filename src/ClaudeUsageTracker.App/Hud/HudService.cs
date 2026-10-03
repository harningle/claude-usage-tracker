using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.App.Hud;

/// <summary>The hook listener's address: its loopback port and the secret in its path.</summary>
public sealed record HookEndpoint(int? Port, string Token);

/// <summary>
/// Keeps the endpoint in a DPAPI file only the current user can open (plan.md section 2). The secret also
/// stands in the hook URLs in Claude Code's own settings, as upstream's does; it is never written to the
/// tracker's settings, logs or diagnostics.
/// </summary>
public sealed class HookEndpointStore(SecretStore store)
{
    // The one entry of this store; it is not a profile.
    private static readonly Guid Entry = new("6d1f9c1e-5f0b-4b0e-9a57-3d6f0f6a7c21");

    public HookEndpoint? Load()
    {
        if (store.Read(Entry) is not { } bytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return root.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } secret
                ? new HookEndpoint(root.TryGetProperty("port", out var port) && port.TryGetInt32(out var number) ? number : null, secret)
                : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new SecretStoreException("The saved hook listener address is not in a recognised format.", e);
        }
    }

    public void Save(HookEndpoint endpoint) =>
        store.Write(Entry, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["port"] = endpoint.Port, ["token"] = endpoint.Token }));

    /// <summary>A new 256-bit path secret.</summary>
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

public enum HudServerStatus
{
    Stopped,
    Running,

    /// <summary>The saved port is in use by something else.</summary>
    PortBusy,

    /// <summary>The listener could not be set up at all; <see cref="HudService.Problem"/> says why.</summary>
    Failed,
}

/// <summary>How one Claude Code folder's hook entries are doing.</summary>
public sealed record HookFolderStatus(string ConfigDirectory, HookInstallStatus Status, string? Problem);

/// <summary>
/// The HUD's lifecycle (plan.md section 2, "If HTTP is retained"): the listener is started before the hook
/// entries are written, and on a clean stop the entries are removed before the listener stops. A crash leaves
/// the entries behind; Claude Code then finds the port closed, which the Milestone 1 gate measured as a few
/// milliseconds per tool call. Runs on the UI thread, apart from the listener's own threads.
/// </summary>
public sealed class HudService : IDisposable
{
    private readonly HookEndpointStore _endpoints;
    private readonly InstallManifestStore _manifests;
    private readonly InstallManifest _manifest;
    private readonly Func<PortReservations> _reservations;
    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly Action<Action> _post;
    private readonly Random _random;
    private HookServer? _server;
    private HookEndpoint? _endpoint;

    /// <param name="folders">The known Claude Code folders, whose <c>settings.json</c> get the hook entries.</param>
    /// <param name="post">Runs an action on the UI thread, where the session store lives.</param>
    public HudService(
        HookEndpointStore endpoints, InstallManifestStore manifests, InstallManifest manifest, Func<PortReservations> reservations,
        Func<IReadOnlyList<string>> folders, TimeProvider time, Action<Action> post, Random? random = null)
    {
        _endpoints = endpoints;
        _manifests = manifests;
        _manifest = manifest;
        _reservations = reservations;
        _folders = folders;
        _post = post;
        _random = random ?? Random.Shared;
        Sessions = new HudSessionStore(time);
    }

    /// <summary>The observed sessions. Changed only on the UI thread.</summary>
    public HudSessionStore Sessions { get; }

    public HudServerStatus Status { get; private set; }

    /// <summary>Why the listener or a folder's entries could not be set up, or null.</summary>
    public string? Problem { get; private set; }

    public int? Port => _server?.Port;

    /// <summary>Raised on the UI thread after an event changed <see cref="Sessions"/>, a sweep, or a change of status.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts the listener and then writes the hook entries into every known folder. With <paramref name="newPort"/>
    /// the saved port is given up for a new one, which is what Repair does when the port is taken.
    /// </summary>
    public void Start(bool newPort = false)
    {
        StopListener();
        Problem = null;
        try
        {
            _endpoint = _endpoints.Load() ?? new HookEndpoint(null, HookEndpointStore.NewToken());
            var reservations = _reservations();
            var server = new HookServer(_endpoint.Token, hookEvent => _post(() => Apply(hookEvent)));
            if (_endpoint.Port is { } saved && !newPort && reservations.IsEligible(saved))
            {
                if (!server.TryStart(saved))
                {
                    server.Dispose();
                    Status = HudServerStatus.PortBusy;
                    Changed?.Invoke();
                    return;
                }
            }
            else if (HookPorts.Choose(reservations, _random, server.TryStart) is { } chosen)
            {
                // Only a port the listener has actually bound is kept.
                _endpoint = _endpoint with { Port = chosen };
                _endpoints.Save(_endpoint);
            }
            else
            {
                server.Dispose();
                throw new PortQueryException("No eligible loopback port could be bound.");
            }

            _server = server;
            Status = HudServerStatus.Running;
        }
        catch (Exception e) when (e is PortQueryException or SecretStoreException or IOException or UnauthorizedAccessException)
        {
            Status = HudServerStatus.Failed;
            Problem = e.Message;
            Changed?.Invoke();
            return;
        }

        InstallHooks();
        Changed?.Invoke();
    }

    /// <summary>Writes the eight entries into every known folder that lacks them; folders that fail are reported, the others still get theirs.</summary>
    public void InstallHooks()
    {
        if (_server?.Port is not { } port || _endpoint is null)
        {
            return;
        }

        var folders = _folders();
        // A folder that is no longer known (its launcher was removed, its profile unlinked) loses the entries first.
        var problems = RemoveFrom([.. _manifest.HookFolders.Where(written => !folders.Contains(written, StringComparer.OrdinalIgnoreCase))]);

        // The port and the folders are recorded before the first entry is written, so the entries are known as the
        // tracker's even after a crash.
        var missingFolders = folders.Where(folder => !_manifest.HookFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!_manifest.HookPorts.Contains(port) || missingFolders.Count > 0)
        {
            _manifest.HookFolders.AddRange(missingFolders);
            if (!_manifest.HookPorts.Contains(port))
            {
                _manifest.HookPorts.Add(port);
            }

            _manifests.Save(_manifest);
        }

        foreach (var folder in folders)
        {
            try
            {
                ClaudeSettingsFile.Update(
                    ClaudeSettingsFile.PathFor(folder), settings => HookSettings.Install(settings, port, _endpoint.Token, _manifest.HookPorts));
            }
            catch (Exception e) when (e is ClaudeSettingsException or IOException or UnauthorizedAccessException)
            {
                problems.Add(e.Message);
            }
        }

        Problem = problems.Count == 0 ? null : string.Join(" ", problems);
    }

    /// <summary>Removes the tracker's entries from every known folder while the listener keeps running. Returns what could not be removed, or null.</summary>
    public string? RemoveHooks() => RemoveHooks(keepListenerPort: true);

    private string? RemoveHooks(bool keepListenerPort)
    {
        var problems = RemoveFrom([.. _folders().Union(_manifest.HookFolders, StringComparer.OrdinalIgnoreCase)]);

        // Nothing of the tracker's is left in any folder it wrote to: its earlier ports need not be remembered.
        if (problems.Count == 0 && _manifest.HookPorts.RemoveAll(port => !keepListenerPort || port != _server?.Port) > 0)
        {
            _manifests.Save(_manifest);
        }

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    /// <summary>Takes the tracker's entries out of the given folders' settings and forgets each folder that is clean.</summary>
    private List<string> RemoveFrom(IReadOnlyList<string> folders)
    {
        var problems = new List<string>();
        var forgotten = 0;
        foreach (var folder in folders)
        {
            try
            {
                ClaudeSettingsFile.Update(ClaudeSettingsFile.PathFor(folder), settings => HookSettings.Remove(settings, _manifest.HookPorts));
                forgotten += _manifest.HookFolders.RemoveAll(written => string.Equals(written, folder, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is ClaudeSettingsException or IOException or UnauthorizedAccessException)
            {
                problems.Add(e.Message);
            }
        }

        if (forgotten > 0)
        {
            _manifests.Save(_manifest);
        }

        return problems;
    }

    /// <summary>The clean stop: entries first, then the listener, then the observed sessions.</summary>
    public void Stop()
    {
        Problem = RemoveHooks(keepListenerPort: false);
        StopListener();
        Sessions.Reset();
        Changed?.Invoke();
    }

    public IReadOnlyList<HookFolderStatus> FolderStatuses()
    {
        var result = new List<HookFolderStatus>();
        foreach (var folder in _folders())
        {
            try
            {
                var settings = ClaudeSettingsFile.Read(ClaudeSettingsFile.PathFor(folder));
                var status = _server?.Port is { } port && _endpoint is not null
                    ? HookSettings.Status(settings, port, _endpoint.Token, _manifest.HookPorts)
                    // Without a listener any entry of the tracker's is a leftover.
                    : settings is not null && HookSettings.Remove((System.Text.Json.Nodes.JsonObject)settings.DeepClone(), _manifest.HookPorts)
                        ? HookInstallStatus.Outdated
                        : HookInstallStatus.NotInstalled;
                result.Add(new HookFolderStatus(folder, status, null));
            }
            catch (ClaudeSettingsException e)
            {
                result.Add(new HookFolderStatus(folder, HookInstallStatus.NotInstalled, e.Message));
            }
        }

        return result;
    }

    /// <summary>Removes sessions that have been silent too long; the owner calls it every 30 seconds, as upstream's timer does.</summary>
    public void Sweep()
    {
        var before = Sessions.Sessions.Count;
        Sessions.SweepStaleSessions();
        if (Sessions.Sessions.Count != before)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose() => StopListener();

    private void Apply(HookEvent hookEvent)
    {
        if (_server is null)
        {
            // An event that was still on its way when the HUD was switched off.
            return;
        }

        Sessions.Apply(hookEvent);
        Changed?.Invoke();
    }

    private void StopListener()
    {
        _server?.Dispose();
        _server = null;
        Status = HudServerStatus.Stopped;
    }
}
