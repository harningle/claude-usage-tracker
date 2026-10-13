using System.IO;
using System.Security.Cryptography;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Profiles;
using ClaudeUsageTracker.Core.Providers;

namespace ClaudeUsageTracker.App.Profiles;

/// <summary>
/// The profile lifecycle: create, rename, activate, delete, which profiles are shown, and removing a
/// profile's links. Port of upstream <c>ProfileManager</c>, with two differences. Activating a profile
/// here only changes which profile is viewed; it never writes a login into Claude Code's files. When the
/// user picks a profile, the app asks <c>CliSwitcher</c> for the guarded switch (plan.md section 2). And a removal
/// is recoverable: the settings are saved first, and whatever data could not be deleted at once is listed
/// in the settings and deleted at the next start.
/// </summary>
public sealed class ProfileService(
    SettingsStore store, AppSettings settings, CliLinkService links, ProfileSecretStore secrets, CredentialLock credentialLock,
    UsageCacheStore cache, UsageHistoryService history, TimeProvider time)
{
    /// <summary>Adds a profile with upstream's defaults. It is not activated.</summary>
    /// <param name="name">Null or blank for a random name, as upstream.</param>
    public ProfileSettings Create(string? name, Provider provider)
    {
        var profile = new ProfileSettings
        {
            Name = string.IsNullOrWhiteSpace(name) ? FunnyNames.Random(settings.Profiles.Select(p => p.Name)) : name.Trim(),
            Provider = provider.RawValue(),
            CreatedAt = time.GetUtcNow(),
            LastUsedAt = time.GetUtcNow(),
        };
        Change(() => settings.Profiles.Add(profile), () => settings.Profiles.Remove(profile));
        return profile;
    }

    public void Rename(ProfileSettings profile, string name)
    {
        var previous = profile.Name;
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == previous)
        {
            return;
        }

        Change(() => profile.Name = name.Trim(), () => profile.Name = previous);
    }

    /// <summary>Makes a profile the one that is viewed. Claude Code's own login is not touched.</summary>
    public void Activate(ProfileSettings profile)
    {
        var previous = (settings.ActiveProfileId, profile.LastUsedAt);
        Change(
            () => (settings.ActiveProfileId, profile.LastUsedAt) = (profile.Id, time.GetUtcNow()),
            () => (settings.ActiveProfileId, profile.LastUsedAt) = previous);
    }

    public void SetMultiProfile(bool enabled)
    {
        var previous = settings.IsMultiProfile;
        Change(() => settings.IsMultiProfile = enabled, () => settings.IsMultiProfile = previous);
    }

    /// <summary>Shows or hides a profile in multi-profile mode. At least one profile stays selected; returns false when refused.</summary>
    public bool ToggleDisplayed(ProfileSettings profile)
    {
        if (profile.IsSelectedForDisplay && settings.Profiles.Count(p => p.IsSelectedForDisplay) <= 1)
        {
            return false;
        }

        var previous = profile.IsSelectedForDisplay;
        Change(() => profile.IsSelectedForDisplay = !previous, () => profile.IsSelectedForDisplay = previous);
        return true;
    }

    /// <summary>
    /// Deletes a profile and everything stored for it. The last profile cannot be deleted, as upstream.
    /// Returns a message when some of its data could not be removed yet; it is removed at the next start.
    /// </summary>
    /// <param name="stopWatching">Stops polling the profile before its data is deleted.</param>
    /// <param name="clearBrowser">Removes the profile's browser data; false when that must be retried later.</param>
    public async Task<string?> DeleteAsync(ProfileSettings profile, Func<Guid, Task> stopWatching, Func<Guid, Task<bool>> clearBrowser)
    {
        if (settings.Profiles.Count <= 1)
        {
            throw new SetupException(Strings.Get("windows.profiles.cannot_delete_last"));
        }

        var index = settings.Profiles.IndexOf(profile);
        var previousActive = settings.ActiveProfileId;
        var cleanup = new PendingCleanup { ProfileId = profile.Id, Secrets = true, Data = true, Browser = true };
        Change(
            () =>
            {
                settings.Profiles.Remove(profile);
                if (settings.ActiveProfileId == profile.Id || settings.Profiles.All(p => p.Id != settings.ActiveProfileId))
                {
                    settings.ActiveProfileId = settings.Profiles[0].Id;
                }

                if (settings.Profiles.All(p => !p.IsSelectedForDisplay))
                {
                    settings.Profiles[0].IsSelectedForDisplay = true;
                }

                settings.PendingCleanups.Add(cleanup);
            },
            () =>
            {
                settings.PendingCleanups.Remove(cleanup);
                settings.Profiles.Insert(index, profile);
                settings.ActiveProfileId = previousActive;
            });

        await stopWatching(profile.Id).ConfigureAwait(true);
        return await CleanUpAsync(cleanup, clearBrowser).ConfigureAwait(true);
    }

    /// <summary>
    /// Removes a profile's Claude Code link and its copy of the login. Claude Code's own files are
    /// untouched. <paramref name="linkRemoved"/> runs after the settings are saved and before the copy is
    /// deleted, so the caller can stop in-flight checks of the old link first.
    /// </summary>
    public async Task UnlinkCliAsync(ProfileSettings profile, Action linkRemoved)
    {
        var previous = profile.CliLink;
        Change(() => profile.CliLink = null, () => profile.CliLink = previous);
        linkRemoved();
        try
        {
            await links.DeleteMirrorAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            throw new SetupException(Strings.Format("windows.setup.error.copy_not_deleted", e.Message), e);
        }

        DeleteCache(profile);
    }

    /// <summary>
    /// Removes a profile's claude.ai link and signs its browser context out by deleting its browser data.
    /// Returns a message when the browser data could not be removed yet.
    /// </summary>
    public async Task<string?> UnlinkClaudeAiAsync(ProfileSettings profile, Action linkRemoved, Func<Guid, Task<bool>> clearBrowser)
    {
        var previous = profile.ClaudeAi;
        var cleanup = new PendingCleanup { ProfileId = profile.Id, Browser = true };
        Change(
            () =>
            {
                profile.ClaudeAi = null;
                settings.PendingCleanups.Add(cleanup);
            },
            () =>
            {
                profile.ClaudeAi = previous;
                settings.PendingCleanups.Remove(cleanup);
            });
        linkRemoved();
        DeleteCache(profile);
        return await CleanUpAsync(cleanup, clearBrowser).ConfigureAwait(true);
    }

    /// <summary>Removes a profile's Console link, its session key and its cached billing.</summary>
    public async Task UnlinkConsoleAsync(ProfileSettings profile, Action linkRemoved)
    {
        var previous = profile.Console;
        Change(() => profile.Console = null, () => profile.Console = previous);
        linkRemoved();
        await RemoveSecretAsync(profile.Id, bundle => bundle.Console = null).ConfigureAwait(true);
        try
        {
            cache.DeleteApi(profile.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache holds no secrets and is never shown without a matching link.
        }
    }

    /// <summary>
    /// Takes the expiry of a Console session cookie that billing renewed and saved in place of the key of the link
    /// linked at <paramref name="linkedAt"/>, for the settings page and the expiry warning. A link replaced or removed
    /// since keeps its own expiry.
    /// </summary>
    public void ConsoleKeyRenewed(Guid profileId, DateTimeOffset linkedAt, DateTimeOffset? expiry)
    {
        if (settings.Profiles.FirstOrDefault(p => p.Id == profileId)?.Console is not { } link
            || link.LinkedAt != linkedAt || link.SessionKeyExpiry == expiry)
        {
            return;
        }

        var previous = link.SessionKeyExpiry;
        Change(() => link.SessionKeyExpiry = expiry, () => link.SessionKeyExpiry = previous);
    }

    /// <summary>Removes a profile's Codex link and any pasted credentials. The Codex CLI's own files are untouched.</summary>
    public async Task UnlinkCodexAsync(ProfileSettings profile, Action linkRemoved)
    {
        var previous = profile.Codex;
        Change(() => profile.Codex = null, () => profile.Codex = previous);
        linkRemoved();
        await RemoveSecretAsync(profile.Id, bundle => bundle.CodexAuthJson = null).ConfigureAwait(true);
        DeleteCache(profile);
    }

    /// <summary>
    /// Removes browser data that a cancelled setup left for a profile with no claude.ai link, so a sign-in
    /// that was never saved does not stay signed in.
    /// </summary>
    public async Task DiscardUnlinkedBrowserAsync(Guid profileId, Func<Guid, Task<bool>> clearBrowser)
    {
        if (settings.Profiles.FirstOrDefault(p => p.Id == profileId)?.ClaudeAi is not null)
        {
            return;
        }

        var cleanup = new PendingCleanup { ProfileId = profileId, Browser = true };
        Change(() => settings.PendingCleanups.Add(cleanup), () => settings.PendingCleanups.Remove(cleanup));
        await CleanUpAsync(cleanup, clearBrowser).ConfigureAwait(true);
    }

    /// <summary>Finishes removals an earlier run could not complete. Returns a message when something still remains.</summary>
    public async Task<string?> RunPendingCleanupAsync(Func<Guid, Task<bool>> clearBrowser)
    {
        string? remaining = null;
        foreach (var cleanup in settings.PendingCleanups.ToList())
        {
            remaining = await CleanUpAsync(cleanup, clearBrowser).ConfigureAwait(true) ?? remaining;
        }

        return remaining;
    }

    /// <summary>Saves an edit already made to the settings; on failure undoes it and reports.</summary>
    public void Change(Action apply, Action undo)
    {
        apply();
        try
        {
            store.Save(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            undo();
            throw new SetupException(Strings.Format("windows.setup.error.save_failed", e.Message), e);
        }
    }

    /// <summary>
    /// Deletes what a cleanup entry lists and takes the entry off the list once nothing is left. A profile
    /// that exists again under the same ID (never the case for deleted profiles) keeps its live data.
    /// </summary>
    private async Task<string?> CleanUpAsync(PendingCleanup cleanup, Func<Guid, Task<bool>> clearBrowser)
    {
        var id = cleanup.ProfileId;
        if (cleanup.Secrets)
        {
            try
            {
                using (await credentialLock.AcquireAsync(CancellationToken.None).ConfigureAwait(true))
                {
                    secrets.Delete(id);
                }

                cleanup.Secrets = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (cleanup.Data)
        {
            try
            {
                cache.Delete(id);
                cache.DeleteApi(id);
                history.Delete(id);
                cleanup.Data = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (cleanup.Browser && await clearBrowser(id).ConfigureAwait(true))
        {
            cleanup.Browser = false;
        }

        var done = !cleanup.Secrets && !cleanup.Data && !cleanup.Browser;
        if (done)
        {
            settings.PendingCleanups.Remove(cleanup);
        }

        try
        {
            store.Save(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The list in memory is right; a later save writes it.
        }

        return done ? null : Strings.Get("windows.profiles.cleanup_pending");
    }

    private async Task RemoveSecretAsync(Guid profileId, Action<ProfileSecrets> remove)
    {
        try
        {
            using (await credentialLock.AcquireAsync(CancellationToken.None).ConfigureAwait(true))
            {
                if (secrets.Exists(profileId))
                {
                    var bundle = secrets.Load(profileId);
                    remove(bundle);
                    secrets.Save(profileId, bundle);
                }
            }
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            throw new SetupException(Strings.Format("windows.setup.error.copy_not_deleted", e.Message), e);
        }
    }

    private void DeleteCache(ProfileSettings profile)
    {
        // The usage cache belongs to the source that is used; it stays when another link still provides usage.
        if (profile.UsageSource is not null)
        {
            return;
        }

        try
        {
            cache.Delete(profile.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache holds no secrets and is never shown without a matching link.
        }
    }

    private static bool IsStorageFailure(Exception e) =>
        e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException;
}
