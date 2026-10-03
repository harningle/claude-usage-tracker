using System.IO;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App.Notifications;

/// <summary>
/// Removes this app's stale notification registrations (Milestone 1, D6). An unpackaged app's
/// <c>AppNotificationManager.Register()</c> writes, per executable path, an <c>AppUserModelId\{GUID}</c> key whose
/// <c>CustomActivator</c> names a <c>CLSID\{GUID}\LocalServer32</c> command <c>"&lt;exe&gt;" ----AppNotificationActivated:</c>,
/// plus an empty key named after the path with dots for separators. A moved or deleted copy leaves its keys behind,
/// and a click on one of its notifications then does nothing. Only registrations of an executable with this app's
/// file name whose file no longer exists are removed: another copy that still exists keeps its registration, and
/// other apps' keys are never touched. Explorer's own tray entries (<c>NotifyIconSettings</c>) are left to Explorer.
/// </summary>
public static class NotificationRegistrations
{
    private const string Activation = "----AppNotificationActivated:";

    /// <param name="classes">The user's <c>Software\Classes</c> key (a test passes a scratch key).</param>
    /// <param name="currentExecutable">This executable's full path; its own registration is kept.</param>
    /// <param name="exists">Whether a file exists, replaceable in tests.</param>
    /// <returns>The executable paths whose registrations were removed.</returns>
    public static IReadOnlyList<string> RemoveStale(RegistryKey classes, string currentExecutable, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var name = Path.GetFileName(currentExecutable);
        var removed = new List<string>();
        using var aumids = classes.OpenSubKey("AppUserModelId", writable: true);
        using var clsids = classes.OpenSubKey("CLSID", writable: true);
        if (aumids is null)
        {
            return removed;
        }

        foreach (var aumid in aumids.GetSubKeyNames())
        {
            string? activator;
            string? command;
            using (var key = aumids.OpenSubKey(aumid))
            {
                activator = key?.GetValue("CustomActivator") as string;
                using var server = activator is null ? null : clsids?.OpenSubKey($@"{activator}\LocalServer32");
                command = server?.GetValue(null) as string;
            }

            if (ExecutableOf(command) is not { } executable
                || !Path.IsPathFullyQualified(executable)
                || !string.Equals(Path.GetFileName(executable), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(executable, Path.GetFullPath(currentExecutable), StringComparison.OrdinalIgnoreCase)
                || exists(executable))
            {
                continue;
            }

            aumids.DeleteSubKeyTree(aumid, throwOnMissingSubKey: false);
            clsids?.DeleteSubKeyTree(activator!, throwOnMissingSubKey: false);
            aumids.DeleteSubKeyTree(executable.Replace('\\', '.'), throwOnMissingSubKey: false);
            removed.Add(executable);
        }

        return removed;
    }

    /// <summary>The executable of a <c>"&lt;exe&gt;" ----AppNotificationActivated:</c> command, or null for any other command.</summary>
    internal static string? ExecutableOf(string? command)
    {
        if (command is null || !command.StartsWith('"'))
        {
            return null;
        }

        var end = command.IndexOf('"', 1);
        return end > 1 && command[(end + 1)..].Trim() == Activation ? command[1..end] : null;
    }
}
