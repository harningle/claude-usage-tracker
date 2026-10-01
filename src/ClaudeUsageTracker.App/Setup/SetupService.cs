using System.IO;
using System.Security.Cryptography;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.CodexCli;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.ClaudeCode;
using ClaudeUsageTracker.Core.Profiles;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Setup;

/// <summary>Saving a tested configuration failed; nothing the user can see changed.</summary>
public sealed class SetupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Saves tested configurations: a Claude Code link, a claude.ai organisation, a Console organisation or a
/// Codex login. Upstream's routes saved without a test or review and could lose a credential to a stale
/// in-memory save. Here every save writes the settings first and the secret second, and restores the
/// settings if the secret cannot be written, so a profile never ends up linked to something it holds no
/// credentials for. As upstream, the wizard configures the active profile, and the profile's provider
/// changes only when a route completes.
/// </summary>
public sealed class SetupService(
    SettingsStore store, AppSettings settings, CliLinkService links, ProfileSecretStore secrets, CredentialLock credentialLock,
    UsageCacheStore cache, TimeProvider time)
{
    private Guid _newProfileId = Guid.NewGuid();

    /// <summary>The profile setup will configure: the active one, or a new one when none exists.</summary>
    public ProfileSettings? TargetProfile => settings.ActiveProfile;

    /// <summary>
    /// The ID the target profile has or will get. A browser sign-in made before saving uses it, so the
    /// session lands in the browser context the saved profile will use.
    /// </summary>
    public Guid TargetProfileId => TargetProfile?.Id ?? _newProfileId;

    /// <summary>
    /// Links a Claude Code login. The folder is re-read first, and a login whose account changed since the
    /// test is refused.
    /// </summary>
    public async Task<ProfileSettings> SaveCliAsync(CliSnapshot tested, UsageData testedUsage, DateTimeOffset observedAt, CancellationToken cancellation)
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

        var link = links.CreateLink(current);
        var profile = await SaveAsync(
            Provider.Anthropic,
            p =>
            {
                var previous = p.CliLink;
                // Whether the login is also used elsewhere is the user's statement about the account, not the folder.
                link.UsedElsewhere = previous?.UsedElsewhere == true && previous.Identity == link.Identity;
                p.CliLink = link;
                return () => p.CliLink = previous;
            },
            p => links.WriteMirrorAsync(p.Id, current, cancellation)).ConfigureAwait(false);
        CacheUsage(profile.Id, "oauthUsage", link.Identity, testedUsage, observedAt);
        return profile;
    }

    /// <summary>
    /// Links a claude.ai organisation. The session itself is already in the profile's browser context; only
    /// the organisation is saved.
    /// </summary>
    public async Task<ProfileSettings> SaveClaudeAiAsync(ClaudeOrganization organization, UsageData testedUsage, DateTimeOffset observedAt)
    {
        var profile = await SaveAsync(
            Provider.Anthropic,
            p =>
            {
                var previous = p.ClaudeAi;
                p.ClaudeAi = new ClaudeAiLink
                {
                    OrganizationId = organization.Uuid,
                    OrganizationName = organization.Name.Length > 0 ? organization.Name : null,
                    LinkedAt = time.GetUtcNow(),
                };
                return () => p.ClaudeAi = previous;
            },
            null).ConfigureAwait(false);
        CacheUsage(profile.Id, "claudeAi", organization.Uuid, testedUsage, observedAt);
        return profile;
    }

    /// <summary>Links an Anthropic Console organisation and saves its session key in the profile's encrypted file.</summary>
    public Task<ProfileSettings> SaveConsoleAsync(ConsoleOrganization organization, ConsoleSecret key, CancellationToken cancellation) =>
        SaveAsync(
            Provider.Anthropic,
            p =>
            {
                var previous = p.Console;
                p.Console = new ConsoleLink
                {
                    OrganizationId = organization.Uuid,
                    OrganizationName = organization.Name.Length > 0 ? organization.Name : null,
                    LinkedAt = time.GetUtcNow(),
                    SessionKeyExpiry = key.Expires,
                };
                return () => p.Console = previous;
            },
            p => ChangeSecretsAsync(p.Id, bundle => bundle.Console = key, cancellation));

    /// <summary>
    /// Links a Codex login: the Codex CLI's own <c>auth.json</c> in <paramref name="codexHome"/>, read live,
    /// or credentials the user pasted, which are saved in the profile's encrypted file.
    /// </summary>
    public async Task<ProfileSettings> SaveCodexAsync(
        string codexHome, string? pastedJson, CodexCredentials tested, UsageData testedUsage, DateTimeOffset observedAt, CancellationToken cancellation)
    {
        var link = new CodexLink
        {
            Source = pastedJson is null ? CodexLink.AuthFileSource : CodexLink.ManualSource,
            CodexHome = codexHome,
            AccountId = tested.ResolvedAccountId,
            Email = tested.Email,
            LinkedAt = time.GetUtcNow(),
        };
        var profile = await SaveAsync(
            Provider.Codex,
            p =>
            {
                var previous = p.Codex;
                p.Codex = link;
                return () => p.Codex = previous;
            },
            // Pasted credentials are kept only for the manual source; following the CLI's file removes an earlier paste.
            p => pastedJson is null && !secrets.Exists(p.Id)
                ? Task.CompletedTask
                : ChangeSecretsAsync(p.Id, bundle => bundle.CodexAuthJson = pastedJson, cancellation)).ConfigureAwait(false);
        CacheUsage(profile.Id, "codex", link.Identity, testedUsage, observedAt);
        return profile;
    }

    /// <summary>
    /// Applies one route to the target profile. Settings are written first and the secret second; if the
    /// secret cannot be written, the settings are restored.
    /// </summary>
    /// <param name="apply">Sets the link and returns how to undo it.</param>
    private async Task<ProfileSettings> SaveAsync(Provider provider, Func<ProfileSettings, Action> apply, Func<ProfileSettings, Task>? writeSecrets)
    {
        var existing = settings.ActiveProfile;
        var profile = existing ?? new ProfileSettings
        {
            Id = _newProfileId,
            Name = FunnyNames.Random(settings.Profiles.Select(p => p.Name)),
            CreatedAt = time.GetUtcNow(),
        };

        var previous = (settings.ActiveProfileId, settings.HasCompletedSetup, profile.LastUsedAt, profile.Provider);
        var undo = apply(profile);
        void Restore()
        {
            undo();
            (settings.ActiveProfileId, settings.HasCompletedSetup, profile.LastUsedAt, profile.Provider) = previous;
            if (existing is null)
            {
                settings.Profiles.Remove(profile);
            }
        }

        if (profile.ProviderKind != provider)
        {
            profile.Provider = provider.RawValue();
        }

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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Restore();
            throw new SetupException(Strings.Format("windows.setup.error.save_failed", e.Message), e);
        }

        if (writeSecrets is not null)
        {
            try
            {
                await writeSecrets(profile).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException)
            {
                Restore();
                try
                {
                    store.Save(settings);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    throw new SetupException(Strings.Format("windows.setup.error.save_failed", $"{e.Message} {restore.Message}"), e);
                }

                throw new SetupException(Strings.Format("windows.setup.error.save_failed", e.Message), e);
            }
        }

        if (existing is null)
        {
            _newProfileId = Guid.NewGuid();
        }

        return profile;
    }

    private async Task ChangeSecretsAsync(Guid profileId, Action<ProfileSecrets> change, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            var bundle = secrets.Load(profileId);
            change(bundle);
            secrets.Save(profileId, bundle);
        }
    }

    private void CacheUsage(Guid profileId, string source, string? identity, UsageData usage, DateTimeOffset observedAt)
    {
        try
        {
            cache.Save(new CachedUsage
            {
                ProfileId = profileId,
                ObservedAt = observedAt,
                Source = source,
                AccountIdentity = identity,
                Usage = usage,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache only shortens the first popover load; the next poll fetches again.
        }
    }
}
