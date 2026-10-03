using System.IO;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App.Automation;

public enum LaunchAtLoginState
{
    Off,

    /// <summary>This copy of the app starts when the user signs in.</summary>
    On,

    /// <summary>The entry starts another copy of the app, at <see cref="LaunchAtLogin.RegisteredPath"/>.</summary>
    OtherCopy,

    /// <summary>The entry is this copy's, but the user switched it off in Task Manager's Startup apps.</summary>
    DisabledInTaskManager,
}

/// <summary>
/// Launch at login (upstream <c>LaunchAtLoginManager</c>, which used macOS <c>SMAppService</c>): a value in the user's
/// <c>Run</c> key that starts this executable. A portable build has no fixed path, so the entry names the copy that
/// registered it; after a move it is shown as another copy's until it is switched on again here. Task Manager's own
/// on/off choice (<c>StartupApproved</c>) is the user's and is only reported, never changed.
/// </summary>
/// <param name="runKey">The user's <c>Software\Microsoft\Windows\CurrentVersion\Run</c> key, or a scratch key in tests and fixture mode.</param>
/// <param name="approvedKey">Task Manager's matching <c>StartupApproved\Run</c> key, or null.</param>
public sealed class LaunchAtLogin(Func<RegistryKey> runKey, Func<RegistryKey?> approvedKey, string executable)
{
    public const string ValueName = "ClaudeUsageTracker";

    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The real per-user startup entry.</summary>
    public static LaunchAtLogin ForCurrentUser(string executable) => new(
        () => Registry.CurrentUser.CreateSubKey(RunPath, writable: true),
        () => Registry.CurrentUser.OpenSubKey(ApprovedPath),
        executable);

    /// <summary>The executable the entry starts, or null when there is no entry.</summary>
    public string? RegisteredPath
    {
        get
        {
            using var key = runKey();
            return key.GetValue(ValueName) is string command ? ExecutableOf(command) ?? command : null;
        }
    }

    public LaunchAtLoginState State
    {
        get
        {
            if (RegisteredPath is not { } path)
            {
                return LaunchAtLoginState.Off;
            }

            if (!Path.IsPathFullyQualified(path)
                || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            {
                return LaunchAtLoginState.OtherCopy;
            }

            using var approved = approvedKey();
            // Task Manager writes 12 bytes; an odd first byte (03, 07) means switched off there.
            return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1
                ? LaunchAtLoginState.DisabledInTaskManager
                : LaunchAtLoginState.On;
        }
    }

    /// <summary>
    /// Switches launch at login on for this copy (replacing another copy's entry) or off. Switching off removes the
    /// entry only when it is this copy's, so another copy's registration is never removed from here by accident.
    /// </summary>
    public void Set(bool enabled)
    {
        using var key = runKey();
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
        }
        else if (State is LaunchAtLoginState.On or LaunchAtLoginState.DisabledInTaskManager)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string? ExecutableOf(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        return trimmed.Split(' ', 2)[0];
    }
}
