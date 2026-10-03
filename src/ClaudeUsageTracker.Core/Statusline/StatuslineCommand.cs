using System.Text.Json;

namespace ClaudeUsageTracker.Core.Statusline;

/// <summary>
/// One status line invocation: Claude Code's stdin plus the tracker's non-secret files give the line. It
/// reads no credentials, signs in to nothing and sends nothing (plan.md section 2), and it writes nothing,
/// so what one session reports never reaches another profile's data.
/// </summary>
public static class StatuslineCommand
{
    /// <summary>Claude Code's input is a few kilobytes; more than this is not read.</summary>
    public const int MaxInputBytes = 1024 * 1024;

    private const int MaxFileBytes = 256 * 1024;

    /// <param name="dataRoot">The tracker's data root, which holds the <c>statusline</c> folder.</param>
    /// <param name="configDirOverride">The session's <c>CLAUDE_CONFIG_DIR</c>, if any.</param>
    /// <returns>The line to print, without a trailing line break.</returns>
    /// <exception cref="InvalidDataException">The applied configuration exists but cannot be read as such.</exception>
    public static string Run(
        ReadOnlySpan<byte> stdin, string dataRoot, string? configDirOverride, string userProfile, DateTimeOffset now, TimeZoneInfo zone)
    {
        var config = LoadConfig(dataRoot);
        var input = StatuslineInput.Parse(stdin);
        var folder = StatuslinePaths.SessionConfigDirectory(configDirOverride, userProfile);
        var usage = StatuslineUsage.Resolve(LoadCache(dataRoot, folder), folder, input, now);
        var content = new StatuslineContent(
            DirectoryName(input.CurrentDirectory),
            config.ShowBranch ? GitBranch.Current(input.CurrentDirectory) : null,
            input.ModelName, input.ContextTokens, input.ContextWindowSize, usage);
        return StatuslineRenderer.Render(config, content, now, zone);
    }

    /// <summary>The applied configuration, or upstream's defaults when none has been applied.</summary>
    public static StatuslineConfig LoadConfig(string dataRoot)
    {
        var path = StatuslinePaths.ConfigFile(dataRoot);
        if (ReadShared(path) is not { } bytes)
        {
            return new StatuslineConfig();
        }

        try
        {
            return JsonSerializer.Deserialize(bytes, StatuslineJson.Default.StatuslineConfig)
                   ?? throw new InvalidDataException($"The status line configuration is empty: {path}");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The status line configuration is damaged: {path} ({e.Message})", e);
        }
    }

    /// <summary>The folder's cache, or null when there is none or it cannot be read: it is re-fetchable, and stdin still serves.</summary>
    public static StatuslineCache? LoadCache(string dataRoot, string configDirectory)
    {
        try
        {
            return ReadShared(StatuslinePaths.CacheFile(dataRoot, configDirectory)) is { } bytes
                ? JsonSerializer.Deserialize(bytes, StatuslineJson.Default.StatuslineCache)
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last component of a path written with either separator; null for a drive root or no path.</summary>
    private static string? DirectoryName(string? path)
    {
        var name = path?.TrimEnd('\\', '/');
        name = name?[(name.LastIndexOfAny(['\\', '/']) + 1)..];
        return string.IsNullOrEmpty(name) || name.EndsWith(':') ? null : name;
    }

    /// <summary>Reads a file the tracker may be replacing at this moment, without blocking that replacement.</summary>
    private static byte[]? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} is larger than {MaxFileBytes} bytes.");
            }

            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
