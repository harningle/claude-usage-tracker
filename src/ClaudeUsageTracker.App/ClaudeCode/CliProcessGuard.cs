using ClaudeUsageTracker.App.Interop;

namespace ClaudeUsageTracker.App.ClaudeCode;

/// <summary>Whether a Claude Code process could be using a config directory right now.</summary>
public enum StoreActivity
{
    /// <summary>No running Claude Code process could be using it.</summary>
    Idle,

    /// <summary>A running Claude Code process uses it, or may be using it.</summary>
    InUse,

    /// <summary>A Claude Code process is running whose details could not be read.</summary>
    Unknown,
}

/// <summary>A running <c>claude.exe</c> of the current user.</summary>
/// <param name="ConfigDirectory">
/// The config directory of the app-owned launcher that verifiably started it, or null when it was started
/// any other way and so counts as using the shared default.
/// </param>
/// <param name="Unknown">Its owner, creation time or parent could not be read.</param>
public sealed record CliProcess(int Pid, string? ConfigDirectory, bool Unknown);

/// <summary>
/// The conservative process guard for credential takeover and, later, account switching (plan.md
/// section 2). A <c>claude.exe</c> belongs to a profile only when its live parent is an app-owned launcher
/// image from the install manifest and that parent was created before it, which rules out a reused parent
/// PID. Every other <c>claude.exe</c> counts as using the default folder, and also any folder that no
/// launcher owns, because it may have been started with <c>CLAUDE_CONFIG_DIR</c> set. Nothing here reads a
/// process's environment. The launcher images come from the install manifest, retained ones included; with
/// no launcher installed, any running Claude Code blocks every takeover.
/// </summary>
/// <param name="sessionObserved">
/// Whether the activity HUD has a live session that uses, or may use, a folder. It can only add to what the
/// processes show: a folder the processes leave idle counts as in use while a session is observed there. An
/// empty HUD proves nothing, so it never makes a folder idle.
/// </param>
public sealed class CliProcessGuard(
    Func<IReadOnlyList<ProcessEntry>> capture, Func<IReadOnlyDictionary<string, string>> launcherImages, Func<string, bool>? sessionObserved = null)
{
    public const string ImageName = "claude.exe";

    /// <summary>The guard over real processes and an install manifest of launcher image path to config directory.</summary>
    public static CliProcessGuard ForThisPc(Func<IReadOnlyDictionary<string, string>> launcherImages, Func<string, bool>? sessionObserved = null) =>
        new(() => ProcessTable.Capture(ImageName), launcherImages, sessionObserved);

    /// <summary>Takes a fresh process snapshot and judges one config directory.</summary>
    public StoreActivity ActivityFor(CliConfigDirectory directory)
    {
        var launchers = launcherImages();
        IReadOnlyList<ProcessEntry> table;
        try
        {
            table = capture();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No snapshot means nothing is known about running sessions.
            return StoreActivity.Unknown;
        }

        var activity = ActivityFor(Classify(table, launchers), directory, launchers);
        return activity == StoreActivity.Idle && sessionObserved?.Invoke(directory.Path) == true ? StoreActivity.InUse : activity;
    }

    /// <summary>Attributes each current-user <c>claude.exe</c> in a snapshot.</summary>
    public static IReadOnlyList<CliProcess> Classify(IReadOnlyList<ProcessEntry> table, IReadOnlyDictionary<string, string> launcherImages)
    {
        var processes = new List<CliProcess>();
        foreach (var process in table.Where(p => string.Equals(p.ImageName, ImageName, StringComparison.OrdinalIgnoreCase)))
        {
            if (process.OwnedByCurrentUser == false)
            {
                // Another Windows user's session; shared PCs are out of scope (plan.md section 1).
                continue;
            }

            if (process.OwnedByCurrentUser is null || process.CreatedUtc is not { } created)
            {
                processes.Add(new CliProcess(process.Pid, null, Unknown: true));
                continue;
            }

            var parent = table.FirstOrDefault(p => p.Pid == process.ParentPid);
            if (parent is null)
            {
                // The parent has exited: nothing can vouch for a launcher.
                processes.Add(new CliProcess(process.Pid, null, Unknown: false));
            }
            else if (parent.ExecutablePath is not { } parentPath || parent.CreatedUtc is not { } parentCreated)
            {
                // A parent that cannot be inspected might be a launcher for any folder.
                processes.Add(new CliProcess(process.Pid, null, Unknown: true));
            }
            else
            {
                var launcher = launcherImages.FirstOrDefault(l => string.Equals(l.Key, parentPath, StringComparison.OrdinalIgnoreCase));
                // A parent created after its "child" is a different process that reused the PID.
                var verified = launcher.Key is not null && parentCreated <= created;
                processes.Add(new CliProcess(process.Pid, verified ? launcher.Value : null, Unknown: false));
            }
        }

        return processes;
    }

    /// <summary>Judges one config directory against classified processes.</summary>
    public static StoreActivity ActivityFor(
        IReadOnlyList<CliProcess> processes, CliConfigDirectory directory, IReadOnlyDictionary<string, string> launcherImages)
    {
        if (processes.Any(p => p.Unknown))
        {
            return StoreActivity.Unknown;
        }

        if (processes.Any(p => p.ConfigDirectory is { } attributed && SameDirectory(attributed, directory.Path)))
        {
            return StoreActivity.InUse;
        }

        var launcherOwned = launcherImages.Values.Any(d => SameDirectory(d, directory.Path));
        return processes.Any(p => p.ConfigDirectory is null) && (directory.IsDefault || !launcherOwned)
            ? StoreActivity.InUse
            : StoreActivity.Idle;
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(CliStore.Normalise(a), CliStore.Normalise(b), StringComparison.OrdinalIgnoreCase);
}
