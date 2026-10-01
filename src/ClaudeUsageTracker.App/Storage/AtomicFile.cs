using System.IO;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>Whole-file replacement that never leaves a partially written destination.</summary>
internal static class AtomicFile
{
    /// <summary>
    /// Writes and flushes a uniquely named temporary file in the destination directory, then renames
    /// it over the destination (a same-volume rename, so readers see the old or the new file).
    /// </summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Reads a file that other processes may be replacing or writing at the same time, without
    /// locking it against them. A concurrent in-place write can yield a torn read, which callers
    /// detect by validating the contents.
    /// </summary>
    public static byte[] ReadSharedBytes(string path) => ReadSharedBytes(path, MaxReadBytes);

    /// <inheritdoc cref="ReadSharedBytes(string)"/>
    public static byte[] ReadSharedBytes(string path, long maxBytes)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} is larger than {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>A bound on files this app reads; real settings, caches and credential files are far smaller.</summary>
    public const long MaxReadBytes = 16 * 1024 * 1024;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
