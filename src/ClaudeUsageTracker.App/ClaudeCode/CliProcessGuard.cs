using System.IO;
using ClaudeUsageTracker.App.Integration;
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
/// <param name="Unknown">Its owner, creation time or parent could not be read, or its parent could not be verified.</param>
public sealed record CliProcess(int Pid, string? ConfigDirectory, bool Unknown);

/// <summary>An app-owned launcher image from the install manifest.</summary>
/// <param name="ConfigDirectory">The folder its sessions use.</param>
/// <param name="Sha256">The hash recorded when the tracker put the file there, or null when none was recorded.</param>
public sealed record LauncherImage(string ConfigDirectory, string? Sha256);

/// <summary>
/// The conservative process guard for credential takeover and, later, account switching (plan.md
/// section 2). A <c>claude.exe</c> belongs to a profile only when its live parent is an app-owned launcher
/// image from the install manifest, the file at that path is still the one the tracker installed, and that
/// parent was created before it, which rules out a reused parent PID. Every other <c>claude.exe</c> counts as
/// using the default folder, and also any folder that no launcher owns, because it may have been started with
/// <c>CLAUDE_CONFIG_DIR</c> set. Nothing here reads a process's environment. The launcher images come from the
/// install manifest, retained ones included; with no launcher installed, any running Claude Code blocks every
/// takeover.
/// </summary>
/// <param name="sessionObserved">
/// Whether the activity HUD has a live session that uses, or may use, a folder. It can only add to what the
/// processes show: a folder the processes leave idle counts as in use while a session is observed there. An
/// empty HUD proves nothing, so it never makes a folder idle.
/// </param>
/// <param name="imageHash">The SHA-256 of a file; the real file's unless a test supplies another.</param>
public sealed class CliProcessGuard(
    Func<IReadOnlyList<ProcessEntry>> capture, Func<IReadOnlyDictionary<string, LauncherImage>> launcherImages,
    Func<string, bool>? sessionObserved = null, Func<string, string>? imageHash = null)
{
    public const string ImageName = "claude.exe";

    private readonly Func<string, string> _imageHash = imageHash ?? FileInstaller.Sha256Of;

    /// <summary>The guard over real processes and the install manifest's launcher images, keyed by path.</summary>
    public static CliProcessGuard ForThisPc(Func<IReadOnlyDictionary<string, LauncherImage>> launcherImages, Func<string, bool>? sessionObserved = null) =>
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

        var activity = ActivityFor(Classify(table, launchers, _imageHash), directory, launchers);
        return activity == StoreActivity.Idle && sessionObserved?.Invoke(directory.Path) == true ? StoreActivity.InUse : activity;
    }

    /// <summary>Attributes each current-user <c>claude.exe</c> in a snapshot.</summary>
    public static IReadOnlyList<CliProcess> Classify(
        IReadOnlyList<ProcessEntry> table, IReadOnlyDictionary<string, LauncherImage> launcherImages, Func<string, string> imageHash)
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
                if (launcher.Key is null || parentCreated > created)
                {
                    // Not a launcher; or a parent created after its "child", a different process that reused the PID.
                    processes.Add(new CliProcess(process.Pid, null, Unknown: false));
                }
                else if (!IsInstalledImage(parentPath, launcher.Value.Sha256, imageHash))
                {
                    // Another file has taken the launcher's place: which folder its session uses cannot be known.
                    processes.Add(new CliProcess(process.Pid, null, Unknown: true));
                }
                else
                {
                    processes.Add(new CliProcess(process.Pid, launcher.Value.ConfigDirectory, Unknown: false));
                }
            }
        }

        return processes;
    }

    /// <summary>
    /// Whether the file at a running launcher's path is still the one the tracker installed. A running image
    /// cannot be rewritten, only renamed, and a renamed one reports its new path, so the file there now is the
    /// one the process runs.
    /// </summary>
    private static bool IsInstalledImage(string path, string? recorded, Func<string, string> imageHash)
    {
        if (recorded is null)
        {
            return false;
        }

        try
        {
            return string.Equals(imageHash(path), recorded, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Judges one config directory against classified processes.</summary>
    public static StoreActivity ActivityFor(
        IReadOnlyList<CliProcess> processes, CliConfigDirectory directory, IReadOnlyDictionary<string, LauncherImage> launcherImages)
    {
        if (processes.Any(p => p.Unknown))
        {
            return StoreActivity.Unknown;
        }

        if (processes.Any(p => p.ConfigDirectory is { } attributed && SameDirectory(attributed, directory.Path)))
        {
            return StoreActivity.InUse;
        }

        var launcherOwned = launcherImages.Values.Any(image => SameDirectory(image.ConfigDirectory, directory.Path));
        return processes.Any(p => p.ConfigDirectory is null) && (directory.IsDefault || !launcherOwned)
            ? StoreActivity.InUse
            : StoreActivity.Idle;
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(CliStore.Normalise(a), CliStore.Normalise(b), StringComparison.OrdinalIgnoreCase);
}
