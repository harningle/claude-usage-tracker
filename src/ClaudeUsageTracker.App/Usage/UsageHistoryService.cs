using System.IO;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.History;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>
/// Records each profile's history after successful fetches (upstream <c>UsageHistoryService</c>). Each
/// profile's history is loaded once and saved whenever it changes. A history file that cannot be used is
/// reported and never overwritten: recording for that profile stops until the file is moved away.
/// </summary>
public sealed class UsageHistoryService(UsageHistoryStore store, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, UsageHistoryData> _loaded = [];

    /// <summary>Records what a new usage observation calls for. Returns the error when the history cannot be used or saved.</summary>
    public string? RecordUsage(Guid profileId, Provider provider, UsageData? previous, UsageData current) =>
        Record(profileId, history => UsageHistoryRecorder.RecordUsage(history, previous, current, provider, time.GetUtcNow()));

    /// <summary>Records a finished billing cycle. Returns the error when the history cannot be used or saved.</summary>
    public string? RecordBilling(Guid profileId, ApiUsage? previous, ApiUsage current) =>
        Record(profileId, history => UsageHistoryRecorder.RecordBilling(history, previous, current, time.GetUtcNow()));

    /// <summary>The profile's history for display and export.</summary>
    /// <exception cref="HistoryStoreException">The file is unreadable, damaged or from a newer format.</exception>
    public UsageHistoryData Load(Guid profileId)
    {
        lock (_gate)
        {
            if (!_loaded.TryGetValue(profileId, out var history))
            {
                _loaded[profileId] = history = store.Load(profileId);
            }

            // A copy, so the view never sees a list a polling thread is adding to.
            var copy = new UsageHistoryData
            {
                LastSessionRecordAt = history.LastSessionRecordAt,
                LastWeeklyRecordAt = history.LastWeeklyRecordAt,
            };
            copy.Snapshots.AddRange(history.Snapshots);
            return copy;
        }
    }

    /// <summary>Deletes a removed profile's history.</summary>
    public void Delete(Guid profileId)
    {
        lock (_gate)
        {
            _loaded.Remove(profileId);
            store.Delete(profileId);
        }
    }

    private string? Record(Guid profileId, Func<UsageHistoryData, bool> record)
    {
        lock (_gate)
        {
            try
            {
                if (!_loaded.TryGetValue(profileId, out var history))
                {
                    _loaded[profileId] = history = store.Load(profileId);
                }

                if (record(history))
                {
                    store.Save(profileId, history);
                }

                return null;
            }
            catch (HistoryStoreException e)
            {
                return e.Message;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Kept in memory; the next change saves again.
                return e.Message;
            }
        }
    }
}
