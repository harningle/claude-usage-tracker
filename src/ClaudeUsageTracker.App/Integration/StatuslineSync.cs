using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.ClaudeCode;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.App.Integration;

/// <summary>
/// Keeps each Claude Code folder's status line cache in step with the profile whose account is signed in there
/// (plan.md section 2). A folder gets a cache only while its login is verifiably a profile's account, and usage
/// only when that profile's usage was fetched with the Claude Code login itself, so a session never shows
/// another account's limits. Every other cache file is deleted. Used on the UI thread only.
/// </summary>
public sealed class StatuslineSync(StatuslineFiles files, Func<CliConfigDirectory, CliSnapshot> read, string userProfile)
{
    /// <summary>
    /// By folder, the profile and account whose usage this run wrote into its cache. The file names no account,
    /// so values this run did not write, or wrote for another account, are never carried over.
    /// </summary>
    private readonly Dictionary<string, (Guid Profile, string? Account)> _valuesFor = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="fetched">A profile's usage as fetched by this run, or null when there is none yet (a cached value from an earlier run does not count).</param>
    public void Sync(AppSettings settings, Func<Guid, CachedUsage?> fetched)
    {
        var kept = new List<string>();
        if (settings.StatuslineEnabled)
        {
            foreach (var profile in settings.Profiles.Where(p => p.ProviderKind == Provider.Anthropic && p.CliLink is not null))
            {
                var directory = CliStore.ForLink(profile.CliLink!, userProfile);
                if (kept.Contains(directory.Path, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var snapshot = read(directory);
                if (snapshot.Credentials is null
                    || CliLoginLabels.Compare(profile.CliLink!.Identity, snapshot.Account?.Identity, null, null) != CliAccountMatch.SameAccount)
                {
                    // The folder is signed out or holds another account now: its sessions are not this profile's.
                    continue;
                }

                kept.Add(directory.Path);
                // After a relink to another account, or at the start of a run, the values start empty.
                var owner = (profile.Id, profile.CliLink!.Identity);
                var cache = files.ReadCache(directory.Path) is { } existing && _valuesFor.TryGetValue(directory.Path, out var writtenFor) && writtenFor == owner
                    ? existing
                    : new StatuslineCache { ProfileId = profile.Id };
                cache.Version = StatuslineCache.CurrentVersion;
                cache.ConfigDirectory = directory.Path;
                cache.ProfileName = profile.Name;
                cache.RefreshIntervalSeconds = profile.RefreshInterval;
                // Only usage read with this folder's own login: the profile may have used another source or been
                // linked to another account when it was fetched.
                if (profile.UsageSource == UsageSourceKind.ClaudeCode && fetched(profile.Id) is { } usage
                    && usage.Source == RefreshCoordinator.SourceName(UsageSourceKind.ClaudeCode)
                    && usage.AccountIdentity is { } fetchedFor && fetchedFor == profile.CliLink!.Identity
                    && (cache.ObservedAt is not { } known || usage.ObservedAt >= known))
                {
                    cache.ObservedAt = usage.ObservedAt;
                    cache.SessionPercentage = usage.Usage.SessionPercentage;
                    cache.SessionResetsAt = usage.Usage.SessionResetTime;
                    cache.WeeklyPercentage = usage.Usage.WeeklyPercentage;
                    cache.WeeklyResetsAt = usage.Usage.WeeklyResetTime;
                    cache.CostUsed = usage.Usage.CostUsed;
                    cache.CostLimit = usage.Usage.CostLimit;
                    cache.CostCurrency = usage.Usage.CostCurrency;
                }

                files.WriteCache(cache);
                // Only once the file holds these values: a write that failed left the former ones, and their owner.
                _valuesFor[directory.Path] = owner;
            }
        }

        files.DeleteCachesExcept(kept);
        foreach (var folder in _valuesFor.Keys.Where(folder => !kept.Contains(folder, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _valuesFor.Remove(folder);
        }
    }
}
