using System.IO;
using System.Text.Json;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.App.Integration;

/// <summary>
/// The non-secret files the helper's status line reads: the applied configuration and one usage cache per
/// Claude Code folder (plan.md section 2, "Fast, secret-free statusline"). Upstream wrote the session key and
/// Cloudflare cookies into a script the status line ran; nothing here holds a credential.
/// </summary>
public sealed class StatuslineFiles(string dataRoot)
{
    private const int WriteAttempts = 4;

    public void WriteConfig(StatuslineConfig config) =>
        Write(StatuslinePaths.ConfigFile(dataRoot), JsonSerializer.SerializeToUtf8Bytes(config, StatuslineJson.Default.StatuslineConfig));

    /// <summary>Writes a folder's cache, unless the file already holds exactly this.</summary>
    public void WriteCache(StatuslineCache cache)
    {
        var path = StatuslinePaths.CacheFile(dataRoot, cache.ConfigDirectory);
        var contents = JsonSerializer.SerializeToUtf8Bytes(cache, StatuslineJson.Default.StatuslineCache);
        if (!File.Exists(path) || !AtomicFile.ReadSharedBytes(path).AsSpan().SequenceEqual(contents))
        {
            Write(path, contents);
        }
    }

    public StatuslineCache? ReadCache(string configDirectory) => StatuslineCommand.LoadCache(dataRoot, configDirectory);

    /// <summary>Removes every cache except those of <paramref name="keepFolders"/>, so a folder whose login changed shows nothing old.</summary>
    public void DeleteCachesExcept(IEnumerable<string> keepFolders)
    {
        var keep = keepFolders.Select(folder => StatuslinePaths.CacheFile(dataRoot, folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(StatuslinePaths.Directory(dataRoot)))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(StatuslinePaths.Directory(dataRoot), "*.cache.json").Where(file => !keep.Contains(file)))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Replaces a file the helper may be reading at this very moment. Windows refuses to rename over a file that
    /// is open, so the replacement is tried again a few milliseconds later; the helper holds it for less.
    /// </summary>
    private static void Write(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                AtomicFile.WriteAllBytes(path, contents);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < WriteAttempts)
            {
                Thread.Sleep(5 * attempt);
            }
        }
    }
}
