using System.IO;
using System.Security.Cryptography;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.CodexCli;
using ClaudeUsageTracker.App.Localisation;
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
/// section 2). Credentials the user pasted are the tracker's own and are renewed by it, as upstream does. When
/// several profiles hold the same pasted login, a renewal is saved in each with a record of the token it used up,
/// so a profile it could not reach at once is given it later, even after a restart, and never sends that token.
/// </summary>
public sealed class CodexUsageSource(
    ProfileSecretStore secrets, CredentialLock credentialLock, OAuthTokenClient tokens, CodexClient client, TimeProvider time,
    Func<string> defaultHome) : IUsageSource
{
    private readonly Lock _gate = new();

    // Refresh tokens a successful grant used up. OpenAI revokes the whole login when one is sent twice.
    private readonly HashSet<string> _consumed = [];

    // Refresh tokens the login service refused: not sent again until the credentials change.
    private readonly HashSet<string> _rejected = [];

    // Renewed credentials that could not be saved yet, per profile, with the saved credentials they renewed.
    // Saving is retried at each check while those are still the saved ones; credentials pasted or removed
    // meanwhile win, and the renewal is dropped.
    private readonly Dictionary<Guid, (string Replaced, string Renewed)> _unsaved = [];

    // After a failed renewal the next attempt waits, longer each time the login service refuses.
    private readonly RenewalBackoff _renewalHolds = new(time);

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

        // A renewed login that could not be saved is saved now, and one that has not reached every profile holding the
        // same login is given to them; until then the failure stays visible.
        var unsavedError = await SaveUnsavedAsync(watch.ProfileId, cancellation).ConfigureAwait(false);
        var (json, _, pending, error) = LoadPasted(watch.ProfileId);
        if (pending is not null)
        {
            error ??= await SettleAsync(watch.ProfileId, cancellation).ConfigureAwait(false);
        }

        error ??= unsavedError;
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
            error = saveError ?? error;
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
    /// credentials to use: renewed ones, or ones another check renewed or handed over meanwhile; null when the
    /// renewal failed. The same login pasted into other profiles is renewed for them too, since the renewal uses up
    /// their token as well; it is not renewed while another profile's credentials cannot be read, as they might
    /// hold it, nor while an earlier renewal of it has not reached every profile that held it.
    /// </summary>
    private async Task<(CodexCredentials? Credentials, RefreshResult? Outcome, string? SaveError)> RenewAsync(
        Guid profileId, CodexCredentials stale, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            var (json, saved, _, error) = LoadPasted(profileId);
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
                // A refused token is not sent again until the credentials change.
                if (_rejected.Contains(current.RefreshToken))
                {
                    return (null, RefreshResult.LoginRejected, error);
                }

                // Never sent twice, nothing more is renewed while an earlier renewal is still unsaved, and a
                // failed renewal waits before the next attempt.
                if (current.RefreshToken.Length == 0 || _consumed.Contains(current.RefreshToken) || _unsaved.ContainsKey(profileId)
                    || _renewalHolds.Holds(profileId))
                {
                    return (null, RefreshResult.Failed, error);
                }
            }

            // Renewals saved by any profile, in this run or an earlier one, first reach the profiles still holding the
            // token they used up. This profile, if it holds one, takes that renewal and sends nothing; while a profile
            // has not taken one, nothing in that login is renewed again.
            var waiting = new List<(PendingWriteBack Record, string? HolderToken, string? Error)>();
            foreach (var holder in secrets.ProfileIds())
            {
                var (_, holderSaved, pending, loadError) = LoadPasted(holder);
                if (loadError is not null)
                {
                    return (null, RefreshResult.Failed, Strings.Get("windows.codex.other_copy_unreadable"));
                }

                if (pending is not null && Settle(holder, holderSaved!, pending) is ({ } remaining, var settleError))
                {
                    waiting.Add((remaining, CodexAuthJson.Parse(holderSaved!)?.RefreshToken, settleError));
                }
            }

            var blocked = waiting.FirstOrDefault(w => w.Record.ConsumedRefreshToken == current.RefreshToken || w.HolderToken == current.RefreshToken);
            if (LoadPasted(profileId).Json is { } given && given != json && CodexAuthJson.Parse(given) is { } handedOver)
            {
                // Handed over; if only in memory, because this profile's file cannot be written, that is reported.
                return (handedOver, RefreshResult.Succeeded, error ?? blocked.Error);
            }

            if (blocked.Record is not null)
            {
                return (null, RefreshResult.Failed, blocked.Error);
            }

            var copies = new List<(Guid ProfileId, string Json, string Saved)>();
            foreach (var other in secrets.ProfileIds().Where(id => id != profileId))
            {
                var (otherJson, otherSaved, _, otherError) = LoadPasted(other);
                if (otherError is not null)
                {
                    return (null, RefreshResult.Failed, Strings.Get("windows.codex.other_copy_unreadable"));
                }

                if (otherJson is not null && CodexAuthJson.Parse(otherJson)?.RefreshToken == current.RefreshToken)
                {
                    copies.Add((other, otherJson, otherSaved!));
                }
            }

            var outcome = await tokens.RefreshCodexAsync(current, cancellation).ConfigureAwait(false);
            if (outcome.Credentials is not { } renewed)
            {
                lock (_gate)
                {
                    if (outcome.Result == RefreshResult.LoginRejected)
                    {
                        _rejected.Add(current.RefreshToken);
                    }
                    else
                    {
                        _renewalHolds.Failed(profileId, answered: outcome.Status is not null);
                    }
                }

                return (null, outcome.Result, error);
            }

            lock (_gate)
            {
                _renewalHolds.Clear(profileId);
                if (renewed.RefreshToken != current.RefreshToken)
                {
                    _consumed.Add(current.RefreshToken);
                }
            }

            // Each profile saves the renewal with a record of the others, so whichever saves fail, those profiles are given
            // it from one that succeeded, even after a restart. A save that fails is also retried at that profile's checks.
            List<(Guid ProfileId, string Json, string Saved)> holders = [(profileId, json, saved!), .. copies];
            string? saveError = null;
            foreach (var holder in holders)
            {
                var others = holders.Where(h => h.ProfileId != holder.ProfileId).Select(h => h.ProfileId.ToString("N")).ToList();
                var record = others.Count == 0 ? null : new PendingWriteBack(current.RefreshToken, time.GetUtcNow(), others);
                var failure = SavePasted(holder.ProfileId, holder.Saved, CodexAuthJson.SerialiseAuthJson(renewed, holder.Json, time.GetUtcNow()), record);
                if (failure is not null)
                {
                    saveError ??= holder.ProfileId == profileId ? failure : Strings.Format("windows.codex.copy_not_saved", failure);
                }
            }

            return (renewed, RefreshResult.Succeeded, saveError);
        }
    }

    /// <summary>Gives this profile's renewal to the other profiles that held the same login. Returns the failure while one remains.</summary>
    private async Task<string?> SettleAsync(Guid profileId, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            var (_, saved, pending, error) = LoadPasted(profileId);
            return error ?? (pending is null ? null : Settle(profileId, saved!, pending).Error);
        }
    }

    /// <summary>
    /// Gives a holder's renewal to the profiles its record lists that still hold the token it used up, and drops the
    /// others from the record: those that have it already, or were given other credentials or none. Returns what remains
    /// of the record and the failure. The caller holds the credential lock.
    /// </summary>
    private (PendingWriteBack? Remaining, string? Error) Settle(Guid holderId, string holderSaved, PendingWriteBack record)
    {
        var renewal = CodexAuthJson.Parse(holderSaved)!;
        var remaining = new List<string>();
        string? error = null;
        foreach (var target in record.Targets)
        {
            var targetId = Guid.ParseExact(target, "N");
            // What the profile's file holds counts, not a renewal of it still waiting in memory, which a restart loses.
            var (_, saved, _, failure) = LoadPasted(targetId);
            if (failure is null && saved is not null && CodexAuthJson.Parse(saved)?.RefreshToken == record.ConsumedRefreshToken)
            {
                failure = SavePasted(targetId, saved, CodexAuthJson.SerialiseAuthJson(renewal, saved, renewal.LastRefresh ?? time.GetUtcNow()), null);
            }

            if (failure is not null)
            {
                remaining.Add(target);
                error ??= Strings.Format("windows.codex.copy_not_saved", failure);
            }
        }

        var left = remaining.Count == 0 ? null : new PendingWriteBack(record.ConsumedRefreshToken, record.CreatedAt, remaining);
        if (remaining.Count < record.Targets.Count)
        {
            try
            {
                var bundle = secrets.Load(holderId);
                bundle.CodexPending = left;
                secrets.Save(holderId, bundle);
            }
            catch (Exception e) when (IsStorageFailure(e))
            {
                // The record still names profiles that have the renewal now; the next settling drops them.
            }
        }

        return (left, error);
    }

    /// <summary>Saves renewed credentials that an earlier save failed for. Returns the error while it still fails.</summary>
    private async Task<string?> SaveUnsavedAsync(Guid profileId, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (!_unsaved.ContainsKey(profileId))
            {
                return null;
            }
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            // Loading drops a renewal whose credentials were replaced or removed meanwhile.
            var (_, _, _, error) = LoadPasted(profileId);
            (string Replaced, string Renewed) unsaved;
            lock (_gate)
            {
                if (error is not null || !_unsaved.TryGetValue(profileId, out unsaved))
                {
                    return error;
                }
            }

            return SavePasted(profileId, unsaved.Replaced, unsaved.Renewed, null);
        }
    }

    /// <summary>
    /// Stores renewed credentials in place of the saved ones they renewed, with the record of other profiles that held
    /// them, if any. The caller holds the credential lock and has just read <paramref name="replaced"/> from the bundle.
    /// </summary>
    private string? SavePasted(Guid profileId, string replaced, string renewed, PendingWriteBack? pending)
    {
        try
        {
            var bundle = secrets.Load(profileId);
            bundle.CodexAuthJson = renewed;
            if (pending is not null)
            {
                bundle.CodexPending = pending;
            }

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
                _unsaved[profileId] = (replaced, renewed);
            }

            return e.Message;
        }
    }

    /// <summary>
    /// The pasted credentials: renewed ones still waiting to be saved while the saved ones are those they
    /// renewed, otherwise the saved ones. <c>Saved</c> and <c>Pending</c> are what the profile's file holds.
    /// </summary>
    private (string? Json, string? Saved, PendingWriteBack? Pending, string? Error) LoadPasted(Guid profileId)
    {
        string? saved;
        PendingWriteBack? pending;
        try
        {
            var bundle = secrets.Load(profileId);
            saved = bundle.CodexAuthJson;
            pending = bundle.CodexPending;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            return (null, null, null, e.Message);
        }

        lock (_gate)
        {
            if (_unsaved.TryGetValue(profileId, out var unsaved))
            {
                if (unsaved.Replaced == saved)
                {
                    return (unsaved.Renewed, saved, pending, null);
                }

                // The credentials were replaced or removed since; the renewal belonged to the old ones.
                _unsaved.Remove(profileId);
            }
        }

        return (saved, saved, pending, null);
    }

    private static bool IsStorageFailure(Exception e) =>
        e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException;
}

/// <param name="Usage">The billing; null when the request failed or no session key is saved.</param>
/// <param name="KeyRenewed">
/// The Console renewed the session cookie, and it was saved in place of the key sent. <paramref name="KeyExpiry"/> is its
/// expiry, null when the Console gave none.
/// </param>
public sealed record BillingFetch(
    ApiUsage? Usage, FetchFailure? Failure, int? Status, TimeSpan? RetryAfter, string? StorageError, bool KeyRenewed = false,
    DateTimeOffset? KeyExpiry = null);

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
        var keyRenewed = false;
        if (result.RenewedKey is { } renewed)
        {
            // The Console replaced the session cookie; the old one may stop working, so the new one is saved at once. It
            // replaces only the key this request sent: a key signed in or removed meanwhile wins.
            using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
            {
                try
                {
                    var bundle = secrets.Load(watch.ProfileId);
                    if (bundle.Console?.SessionKey == key.SessionKey)
                    {
                        bundle.Console = renewed;
                        secrets.Save(watch.ProfileId, bundle);
                        keyRenewed = true;
                    }
                }
                catch (Exception e) when (e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException)
                {
                    storageError = e.Message;
                }
            }
        }

        return new BillingFetch(
            result.Value, result.Failure, result.Status, result.RetryAfter, storageError, keyRenewed, keyRenewed ? result.RenewedKey!.Expires : null);
    }
}
