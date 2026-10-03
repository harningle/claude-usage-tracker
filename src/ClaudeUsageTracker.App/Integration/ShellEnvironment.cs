using System.IO;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App.Integration;

/// <summary>What the tracker needs to know about the user's shells: whether Git Bash is installed, and what PATH new terminals get.</summary>
public static class ShellEnvironment
{
    /// <summary>
    /// Whether Claude Code would run the status line through Git Bash: it does when Git Bash is installed, and
    /// uses PowerShell otherwise (https://code.claude.com/docs/en/statusline). Looked for where Claude Code is
    /// documented to look and where Git for Windows installs: <c>CLAUDE_CODE_GIT_BASH_PATH</c>, <c>bash.exe</c>
    /// next to a <c>git.exe</c> on PATH, and the standard install folders.
    /// </summary>
    public static bool GitBashInstalled(Func<string, string?> environment, Func<string, bool> fileExists)
    {
        if (environment("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured && fileExists(configured))
        {
            return true;
        }

        var candidates = new List<string>();
        foreach (var directory in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (fileExists(Path.Combine(directory, "git.exe")))
            {
                // Git\cmd\git.exe and Git\mingw64\bin\git.exe both belong to Git\bin\bash.exe.
                candidates.Add(Path.Combine(directory, "..", "bin", "bash.exe"));
                candidates.Add(Path.Combine(directory, "..", "..", "bin", "bash.exe"));
            }
        }

        foreach (var root in new[] { environment("ProgramFiles"), environment("ProgramFiles(x86)"), environment("LOCALAPPDATA") is { } local ? Path.Combine(local, "Programs") : null })
        {
            if (!string.IsNullOrEmpty(root))
            {
                candidates.Add(Path.Combine(root, "Git", "bin", "bash.exe"));
            }
        }

        return candidates.Any(fileExists);
    }

    public static bool GitBashInstalled() => GitBashInstalled(Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>Whether a PATH value lists the directory, compared as Windows compares paths.</summary>
    public static bool PathContains(string? pathValue, string directory)
    {
        var wanted = Normalise(directory);
        return (pathValue ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => string.Equals(Normalise(Environment.ExpandEnvironmentVariables(entry.Trim('"'))), wanted, StringComparison.OrdinalIgnoreCase));

        static string Normalise(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }
    }

    /// <summary>
    /// Whether a terminal opened now would find the directory on PATH: the user's and the machine's saved PATH,
    /// which may be newer than the one this process started with, or this process's own.
    /// </summary>
    public static bool OnPath(string directory)
    {
        using var user = Registry.CurrentUser.OpenSubKey("Environment");
        using var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        return PathContains(user?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, directory)
               || PathContains(machine?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, directory)
               || PathContains(Environment.GetEnvironmentVariable("PATH"), directory);
    }
}
