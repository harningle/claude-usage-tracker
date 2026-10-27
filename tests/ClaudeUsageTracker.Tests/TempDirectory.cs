using System.IO;

namespace ClaudeUsageTracker.Tests;

/// <summary>A fresh directory under the system temporary folder, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ClaudeUsageTracker.Tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        // A process started from the folder keeps its files locked (IOException) or its image mapped
        // (UnauthorizedAccessException) for a moment after it is reported exited, so the delete is retried briefly;
        // a folder that cannot be deleted even then is left behind rather than failing the test.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                {
                    return;
                }

                Thread.Sleep(200);
            }
        }
    }
}
