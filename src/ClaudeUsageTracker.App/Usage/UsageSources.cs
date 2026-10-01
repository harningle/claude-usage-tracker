using System.IO;
using System.Security.Cryptography;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.CodexCli;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Providers.Codex;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>Usage of a Claude Code login, through the read-only OAuth usage endpoint.</summary>
public sealed class CliUsageSource(CliLinkService links, UsageClient client) : IUsageSource
{
    public async Task<SourceCredentials> ResolveAsync(ProfileWatch watch, CancellationToken cancellation)
    {
        var check = await links.CheckAsync(watch.ProfileId, watch.Cli!, cancellation).ConfigureAwait(false);
        return new SourceCredentials
        {
            Status = check.Status,
            Secret = check.Credentials?.AccessToken,
            Stamp = check.Credentials?.AccessToken,
            StorageError = check.MirrorError,
            Ownership = check.Ownership,
            Notice = check.Notice,
        };
    }

    public async Task<SourceFetch> FetchAsync(ProfileWatch watch, SourceCredentials credentials, UsageData? previous, CancellationToken cancellation) =>
        new(await client.FetchUsageAsync((string)credentials.Secret!, cancellation, watch.AccountIdentity ?? watch.ProfileId.ToString("N"))
            .ConfigureAwait(false));
}

/// <summary>
/// Usage of a claude.ai organisation through the profile's own browser session. The session lives in
/// WebView2; nothing here holds a cookie or key.
/// </summary>
public sealed class ClaudeAiUsageSource(ClaudeAiClient client, IClaudeAiTransport transport) : IUsageSource
{
    public Task<SourceCredentials> ResolveAsync(ProfileWatch watch, CancellationToken cancellation) =>
        Task.FromResult(new SourceCredentials
        {
            Status = LinkStatus.Ready,
            Secret = watch.ClaudeAi!.OrganizationId,
            // A rejected session is asked again only after a sign-in or another change to the browser session.
            Stamp = "session:" + transport.SessionEpoch(watch.ProfileId),
        });

    public async Task<SourceFetch> FetchAsync(ProfileWatch watch, SourceCredentials credentials, UsageData? previous, CancellationToken cancellation)
    {
        var organizationId = (string)credentials.Secret!;
        var result = await client.FetchUsageAsync(watch.ProfileId, organizationId, cancellation).ConfigureAwait(false);
        if (result is not FetchFailed { Failure: FetchFailure.CredentialsRejected })
        {
            return new SourceFetch(result);
        }

        // Signed out, or signed in to an account that cannot see this organisation? The organisation list tells.
        var organizations = await client.FetchOrganizationsAsync(watch.ProfileId, cancellation).ConfigureAwait(false);
        LinkStatus? status = organizations.Organizations is { } list
            ? (list.Any(o => o.Uuid == organizationId) ? null : LinkStatus.AccountChanged)
            : organizations.Failure?.Failure == FetchFailure.CredentialsRejected ? LinkStatus.NotSignedIn
            : null;
        return new SourceFetch(result, status);
    }
}

/// <summary>
/// Usage of a Codex login. A login in the Codex CLI's <c>auth.json</c> is the CLI's: it is read through and
/// never renewed here, and a rejected one waits for the CLI (a Codex takeover is not verified, plan.md
/// section 2). Credentials the user pasted are the tracker's own and are renewed by it, as upstream does.
/// </summary>
public sealed class CodexUsageSource(
    ProfileSecretStore secrets, CredentialLock credentialLock, OAuthTokenClient tokens, CodexClient client, TimeProvider time,
    Func<string> defaultHome) : IUsageSource
{
    private readonly Lock _gate = new();

    // Refresh tokens a successful grant used up. OpenAI revokes the whole login when one is sent twice.
    private readonly HashSet<string> _consumed = [];

    // Renewed credentials that could not be saved yet, per profile.
    private readonly Dictionary<Guid, string> _unsaved = [];

    public async Task<SourceCredentials> ResolveAsync(ProfileWatch watch, CancellationToken cancellation)
    {
        var link = watch.Codex!;
        if (!link.IsManual)
        {
            var snapshot = CodexStore.Read(link.CodexHome ?? defaultHome());
            var status = snapshot.State switch
            {
                CodexLoginState.NotSignedIn => LinkStatus.NotSignedIn,
                CodexLoginState.Unreadable => LinkStatus.Unreadable,
                CodexLoginState.Invalid => LinkStatus.Invalid,
                // The shared auth.json holds one signed-in account; another account's login is never shown as this profile's.
                _ when link.Identity is { } linked && snapshot.Identity is { } current
                       && !string.Equals(linked, current, StringComparison.OrdinalIgnoreCase) => LinkStatus.AccountChanged,
                _ when link.Identity is not null && snapshot.Identity is null => LinkStatus.AccountUnverified,
                _ => LinkStatus.Ready,
            };
            return new SourceCredentials
            {
                Status = status,
                Secret = status == LinkStatus.Ready ? snapshot.Credentials : null,
                Stamp = snapshot.Credentials?.AccessToken,
                Ownership = CredentialOwnership.CliLinked,
            };
        }

        var (json, error) = LoadPasted(watch.ProfileId);
        if (json is null)
        {
            return new SourceCredentials { Status = error is null ? LinkStatus.NotSignedIn : LinkStatus.Unreadable, StorageError = error };
        }

        if (CodexAuthJson.Parse(json) is not { } credentials)
        {
            return new SourceCredentials { Status = LinkStatus.Invalid };
        }

        var result = LinkStatus.Ready;
        if (credentials.NeedsRefresh(time.GetUtcNow()))
        {
            // The Codex CLI renews after eight days; a failed renewal still leaves a token worth trying.
            var (renewed, outcome, saveError) = await RenewAsync(watch.ProfileId, credentials, cancellation).ConfigureAwait(false);
            credentials = renewed ?? credentials;
            error = saveError;
            result = outcome == RefreshResult.LoginRejected ? LinkStatus.LoginRejected : LinkStatus.Ready;
        }

        return new SourceCredentials
        {
            Status = result,
            Secret = result == LinkStatus.Ready ? credentials : null,
            Stamp = credentials.AccessToken,
            StorageError = error,
            Ownership = CredentialOwnership.AppOwned,
        };
    }

    public async Task<SourceFetch> FetchAsync(ProfileWatch watch, SourceCredentials credentials, UsageData? previous, CancellationToken cancellation)
    {
        var link = watch.Codex!;
        var codex = (CodexCredentials)credentials.Secret!;
        var config = CodexStore.ReadConfig(link.CodexHome ?? defaultHome());
        var result = await client.FetchUsageAsync(codex, config, previous, cancellation).ConfigureAwait(false);
        if (result is not FetchFailed { Failure: FetchFailure.CredentialsRejected })
        {
            return new SourceFetch(result);
        }

        if (!link.IsManual)
        {
            return new SourceFetch(result, LinkStatus.WaitingForCliRefresh);
        }

        if (codex.RefreshToken.Length == 0)
        {
            return new SourceFetch(result, LinkStatus.LoginRejected);
        }

        // The access token expired before the eight-day mark: renew once and retry once, as upstream.
        var (renewed, outcome, _) = await RenewAsync(watch.ProfileId, codex, cancellation).ConfigureAwait(false);
        return renewed is not null
            ? new SourceFetch(await client.FetchUsageAsync(renewed, config, previous, cancellation).ConfigureAwait(false))
            : new SourceFetch(result, outcome == RefreshResult.LoginRejected ? LinkStatus.LoginRejected : LinkStatus.RefreshFailed);
    }

    /// <summary>
    /// Renews pasted credentials under the credential lock and saves them before they are used. Returns the
    /// credentials to use: renewed ones, or ones another check renewed meanwhile; null when the renewal failed.
    /// </summary>
    private async Task<(CodexCredentials? Credentials, RefreshResult? Outcome, string? SaveError)> RenewAsync(
        Guid profileId, CodexCredentials stale, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            var (json, error) = LoadPasted(profileId);
            if (json is null || CodexAuthJson.Parse(json) is not { } current)
            {
                return (null, null, error);
            }

            if (current.AccessToken != stale.AccessToken)
            {
                return (current, RefreshResult.Succeeded, error);
            }

            lock (_gate)
            {
                // Never sent twice, and nothing more is renewed while an earlier renewal is still unsaved.
                if (current.RefreshToken.Length == 0 || _consumed.Contains(current.RefreshToken) || _unsaved.ContainsKey(profileId))
                {
                    return (null, RefreshResult.Failed, error);
                }
            }

            var outcome = await tokens.RefreshCodexAsync(current, cancellation).ConfigureAwait(false);
            if (outcome.Credentials is not { } renewed)
            {
                return (null, outcome.Result, error);
            }

            var serialised = CodexAuthJson.SerialiseAuthJson(renewed, json, time.GetUtcNow());
            lock (_gate)
            {
                if (renewed.RefreshToken != current.RefreshToken)
                {
                    _consumed.Add(current.RefreshToken);
                }
            }

            return (renewed, RefreshResult.Succeeded, SavePasted(profileId, serialised));
        }
    }

    /// <summary>Stores pasted credentials for a profile. The caller holds the credential lock.</summary>
    private string? SavePasted(Guid profileId, string json)
    {
        try
        {
            var bundle = secrets.Load(profileId);
            bundle.CodexAuthJson = json;
            secrets.Save(profileId, bundle);
            lock (_gate)
            {
                _unsaved.Remove(profileId);
            }

            return null;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            lock (_gate)
            {
                _unsaved[profileId] = json;
            }

            return e.Message;
        }
    }

    private (string? Json, string? Error) LoadPasted(Guid profileId)
    {
        lock (_gate)
        {
            if (_unsaved.TryGetValue(profileId, out var unsaved))
            {
                return (unsaved, null);
            }
        }

        try
        {
            return (secrets.Load(profileId).CodexAuthJson, null);
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            return (null, e.Message);
        }
    }

    private static bool IsStorageFailure(Exception e) =>
        e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException;
}

/// <param name="Usage">The billing; null when the request failed or no session key is saved.</param>
public sealed record BillingFetch(ApiUsage? Usage, FetchFailure? Failure, int? Status, TimeSpan? RetryAfter, string? StorageError);

/// <summary>Console billing of a profile, with the session key from its encrypted secret file.</summary>
public sealed class ConsoleBillingSource(ProfileSecretStore secrets, CredentialLock credentialLock, ConsoleClient client)
{
    public async Task<BillingFetch> FetchAsync(ProfileWatch watch, CancellationToken cancellation)
    {
        ConsoleSecret? key;
        try
        {
            key = secrets.Load(watch.ProfileId).Console;
        }
        catch (Exception e) when (e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new BillingFetch(null, FetchFailure.CredentialsRejected, null, null, e.Message);
        }

        if (key is null)
        {
            return new BillingFetch(null, FetchFailure.CredentialsRejected, null, null, null);
        }

        var result = await client.FetchApiUsageAsync(key.SessionKey, watch.Console!.OrganizationId, cancellation).ConfigureAwait(false);
        string? storageError = null;
        if (result.RenewedKey is { } renewed)
        {
            // The Console replaced the session cookie; the old one may stop working, so the new one is saved at once.
            using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
            {
                try
                {
                    var bundle = secrets.Load(watch.ProfileId);
                    if (bundle.Console is not null)
                    {
                        bundle.Console = renewed;
                        secrets.Save(watch.ProfileId, bundle);
                    }
                }
                catch (Exception e) when (e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException)
                {
                    storageError = e.Message;
                }
            }
        }

        return new BillingFetch(result.Value, result.Failure, result.Status, result.RetryAfter, storageError);
    }
}
