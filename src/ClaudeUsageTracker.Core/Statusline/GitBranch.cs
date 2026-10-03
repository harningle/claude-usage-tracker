namespace ClaudeUsageTracker.Core.Statusline;

/// <summary>
/// The checked-out branch of the repository a directory is in, read from <c>.git/HEAD</c>. Upstream's script
/// runs <c>git branch --show-current</c>; starting git twice per status line would cost more than the whole
/// helper, so the file git itself reads is read instead. Like that command, a detached HEAD gives no branch.
/// </summary>
public static class GitBranch
{
    private const string BranchPrefix = "ref: refs/heads/";
    private const string GitDirPrefix = "gitdir:";
    private const int MaxFileBytes = 4096;

    public static string? Current(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var marker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(marker))
                {
                    return BranchOf(marker);
                }

                // A linked worktree or submodule: a file naming the real git directory.
                if (File.Exists(marker))
                {
                    return FirstLine(marker) is { } line && line.StartsWith(GitDirPrefix, StringComparison.Ordinal)
                        ? BranchOf(Path.GetFullPath(line[GitDirPrefix.Length..].Trim(), current.FullName))
                        : null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return null;
    }

    private static string? BranchOf(string gitDirectory) =>
        FirstLine(Path.Combine(gitDirectory, "HEAD")) is { } head && head.StartsWith(BranchPrefix, StringComparison.Ordinal)
        && head[BranchPrefix.Length..].Trim() is { Length: > 0 } branch
            ? branch
            : null;

    private static string? FirstLine(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[MaxFileBytes];
        var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
