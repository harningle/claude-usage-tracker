using System.IO;
using System.Text;
using ClaudeUsageTracker.Core.History;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>A history file exists but cannot be used. Reported; the file is never overwritten.</summary>
public sealed class HistoryStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One history file per profile, replaced atomically. Upstream read a damaged history as empty and
/// overwrote it with the next sample; here a damaged or newer-format file is reported and left alone
/// (plan.md section 2: "Report corrupted storage instead of resetting").
/// </summary>
public sealed class UsageHistoryStore(string directory)
{
    public string PathFor(Guid profileId) => Path.Combine(directory, $"{profileId:N}.history.json");

    /// <summary>The profile's history, or an empty one when it has no file yet.</summary>
    /// <exception cref="HistoryStoreException">The file is unreadable, damaged or from a newer format.</exception>
    public UsageHistoryData Load(Guid profileId)
    {
        var path = PathFor(profileId);
        string json;
        try
        {
            json = Encoding.UTF8.GetString(AtomicFile.ReadSharedBytes(path));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new UsageHistoryData();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new HistoryStoreException($"The history file could not be read and was not changed: {path} ({e.Message})", e);
        }

        try
        {
            return UsageHistoryJson.Parse(json);
        }
        catch (FormatException e)
        {
            throw new HistoryStoreException($"{e.Message} It was not changed: {path}", e);
        }
    }

    public void Save(Guid profileId, UsageHistoryData history)
    {
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllBytes(PathFor(profileId), Encoding.UTF8.GetBytes(UsageHistoryJson.Serialise(history)));
    }

    public void Delete(Guid profileId) => File.Delete(PathFor(profileId));
}
