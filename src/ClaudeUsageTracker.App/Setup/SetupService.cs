using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.ClaudeCode;
using ClaudeUsageTracker.Core.Profiles;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Setup;

/// <summary>Saving a tested configuration failed; nothing the user can see changed.</summary>
public sealed class SetupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Saves a tested Claude Code link. Upstream's CLI route saved without a test or review, re-read a
/// possibly different login at save time and could lose the copy to a stale in-memory save. Here the save
/// re-reads the folder, refuses a login whose account changed since the test, then writes the settings and
/// only after them the encrypted copy. If the copy cannot be written, the settings are restored, so a
/// profile never ends up linked to one account while holding another's copy.
/// </summary>
public sealed class SetupService(SettingsStore store, AppSettings settings, CliLinkService links, UsageCacheStore cache, TimeProvider time)
{
    /// <summary>The profile setup will configure: the active one, or a new one when none exists.</summary>
    public ProfileSettings? TargetProfile => settings.ActiveProfile;

    public ProfileSettings Save(CliSnapshot tested, UsageData testedUsage, DateTimeOffset observedAt)
    {
        var current = links.Read(tested.Directory);
        if (current.State != CliLoginState.Usable || current.Credentials is null)
        {
            throw new SetupException(Strings.Get("windows.setup.error.changed_since_test"));
        }

        var match = CliLoginLabels.Compare(
            tested.Account?.Identity, current.Account?.Identity, tested.Credentials?.RefreshToken, current.Credentials.RefreshToken);
        if (match != CliAccountMatch.SameAccount)
        {
            throw new SetupException(Strings.Get("windows.setup.error.changed_since_test"));
        }

        var existing = settings.ActiveProfile;
        var profile = existing ?? new ProfileSettings
        {
            Name = FunnyNames.Random(settings.Profiles.Select(p => p.Name)),
            CreatedAt = time.GetUtcNow(),
        };

        var previous = (profile.CliLink, settings.ActiveProfileId, settings.HasCompletedSetup, profile.LastUsedAt);
        void Restore()
        {
            (profile.CliLink, settings.ActiveProfileId, settings.HasCompletedSetup, profile.LastUsedAt) = previous;
            if (existing is null)
            {
                settings.Profiles.Remove(profile);
            }
        }

        profile.CliLink = links.CreateLink(current);
        profile.LastUsedAt = time.GetUtcNow();
        if (existing is null)
        {
            settings.Profiles.Add(profile);
        }

        settings.ActiveProfileId = profile.Id;
        settings.HasCompletedSetup = true;
        try
        {
            store.Save(settings);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Restore();
            throw new SetupException(Strings.Format("windows.setup.error.save_failed", e.Message), e);
        }

        try
        {
            links.WriteMirror(profile.Id, current);
        }
        catch (Exception e) when (e is SecretStoreException or System.IO.IOException or UnauthorizedAccessException
                                      or System.Security.Cryptography.CryptographicException)
        {
            Restore();
            try
            {
                store.Save(settings);
            }
            catch (Exception restore) when (restore is System.IO.IOException or UnauthorizedAccessException)
            {
                throw new SetupException(Strings.Format("windows.setup.error.save_failed", $"{e.Message} {restore.Message}"), e);
            }

            throw new SetupException(Strings.Format("windows.setup.error.save_failed", e.Message), e);
        }

        try
        {
            cache.Save(new CachedUsage
            {
                ProfileId = profile.Id,
                ObservedAt = observedAt,
                Source = "oauthUsage",
                AccountIdentity = profile.CliLink.Identity,
                Usage = testedUsage,
            });
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // The cache only shortens the first popover load; the next poll fetches again.
        }

        return profile;
    }

    /// <summary>
    /// Removes a profile's link and its encrypted copy. Claude Code's own files are untouched.
    /// <paramref name="linkRemoved"/> runs after the settings are saved and before the copy is deleted, so the
    /// caller can stop in-flight checks of the old link first.
    /// </summary>
    public void Unlink(ProfileSettings profile, Action linkRemoved)
    {
        var previous = profile.CliLink;
        profile.CliLink = null;
        try
        {
            store.Save(settings);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            profile.CliLink = previous;
            throw new SetupException(Strings.Format("windows.setup.error.save_failed", e.Message), e);
        }

        linkRemoved();
        try
        {
            links.DeleteMirror(profile.Id);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            throw new SetupException(Strings.Format("windows.setup.error.copy_not_deleted", e.Message), e);
        }

        try
        {
            cache.Delete(profile.Id);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // The cache holds no secrets and is never shown without a matching link.
        }
    }
}
