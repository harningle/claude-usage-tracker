using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.ClaudeCode;

namespace ClaudeUsageTracker.App.ClaudeCode;

public enum LinkStatus
{
    /// <summary>The account's login is usable.</summary>
    Ready,

    /// <summary>
    /// The linked account's token has expired and the tracker may not renew it now: Claude Code is running
    /// or may be, the login is also used elsewhere, or a renewal just failed. Claude Code refreshes it on
    /// next use, and the tracker re-reads the file at each interval.
    /// </summary>
    WaitingForCliRefresh,

    NotSignedIn,
    Unreadable,
    Invalid,

    /// <summary>Claude Code is now signed in to a different account than the one linked.</summary>
    AccountChanged,

    /// <summary>Neither account identity nor token lineage can show that this is the linked account.</summary>
    AccountUnverified,

    /// <summary>The login service refused to renew this login; only signing in again helps.</summary>
    LoginRejected,

    /// <summary>
    /// A login the tracker renews itself has expired and could not be renewed: the renewal got no usable answer,
    /// or a pending hand-back or an unreadable copy of another profile stopped it. It is tried again.
    /// </summary>
    RefreshFailed,
}

/// <summary>Who may renew a profile's Claude Code login (plan.md section 2, "Secrets, refresh ownership and persistence").</summary>
public enum CredentialOwnership
{
    /// <summary>A known Claude Code folder holds this account's login, so Claude Code owns the token lineage.</summary>
    CliLinked,

    /// <summary>No known folder holds the account any more; the tracker's copy is the only one and the tracker renews it.</summary>
    AppOwned,

    /// <summary>It cannot be told who holds the login (an unreadable folder, a login without identity). Nothing is renewed.</summary>
    Unresolved,
}

/// <summary>Something about the login that the user should know although usage may still be shown.</summary>
public enum LinkNotice
{
    /// <summary>The tracker renewed Claude Code's login but could not yet write it back to every folder.</summary>
    HandBackPending,

    /// <summary>The tracker tried to renew the login and got no usable answer.</summary>
    RenewalFailed,

    /// <summary>Another known Claude Code folder could not be read, so the tracker will not renew anything.</summary>
    OtherFolderUnreadable,

    /// <summary>
    /// Another profile's saved copy could not be read. It may hold a pending hand-back, so the tracker will not
    /// renew anything.
    /// </summary>
    OtherCopyUnreadable,
}

/// <param name="Credentials">The login to poll with, from the linked folder or the profile's own copy; null when none is usable.</param>
/// <param name="MirrorError">A failure to read or update the app's encrypted copy; polling may continue.</param>
public sealed record LinkCheck(LinkStatus Status, CliSnapshot Snapshot, CliCredentials? Credentials, string? MirrorError)
{
    public CredentialOwnership Ownership { get; init; } = CredentialOwnership.CliLinked;

    public LinkNotice? Notice { get; init; }
}

/// <summary>
/// Links profiles to Claude Code logins and keeps each profile's encrypted copy. A profile follows an
/// account. While a known Claude Code folder holds that account, the login is Claude Code's: the tracker
/// reads it through and mirrors it, and renews it only when it has expired and no Claude Code process
/// could be using it (the guarded takeover of upstream issue #268), writing the result back to every
/// folder that held the same token lineage. When no known folder holds the account any more, the copy
/// becomes the tracker's own and is renewed by it. Every change to a copy happens under the app-wide
/// credential lock.
/// </summary>
public sealed class CliLinkService(
    ProfileSecretStore secrets,
    TimeProvider time,
    string userProfile,
    CredentialLock credentialLock,
    OAuthTokenClient tokens,
    CliProcessGuard processes,
    Func<IReadOnlyList<CliConfigDirectory>> inventory)
{
    /// <summary>A torn read during a CLI write is retried once after this delay.</summary>
    internal static readonly TimeSpan RereadDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>After a renewal that got no usable answer, the next attempt waits this long.</summary>
    internal static readonly TimeSpan RenewalRetryDelay = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();

    // Refresh tokens a successful grant used up: never sent again.
    private readonly HashSet<string> _consumed = [];

    // Refresh tokens the login service rejected: not sent again until the login changes.
    private readonly HashSet<string> _rejected = [];

    // Renewed logins that could not be saved yet. They are kept for a later save and nothing else is renewed
    // for that profile meanwhile.
    private readonly Dictionary<Guid, CliSecret> _unsaved = [];

    private readonly Dictionary<Guid, DateTimeOffset> _renewalNotBefore = [];

    public CliSnapshot Read(CliConfigDirectory directory) => CliStore.Read(directory, time.GetUtcNow());

    /// <summary>A link to the account in <paramref name="snapshot"/>, which must be usable.</summary>
    public CliLink CreateLink(CliSnapshot snapshot)
    {
        if (snapshot.State != CliLoginState.Usable || snapshot.Credentials is null)
        {
            throw new InvalidOperationException("Only a usable Claude Code login can be linked.");
        }

        return new CliLink
        {
            ConfigDirectory = snapshot.Directory.Path,
            IsDefaultDirectory = snapshot.Directory.IsDefault,
            AccountUuid = snapshot.Account?.AccountUuid,
            EmailAddress = snapshot.Account?.EmailAddress,
            OrganizationName = snapshot.Account?.OrganizationName,
            OrganizationUuid = snapshot.Account?.OrganizationUuid,
            SubscriptionType = snapshot.Credentials.SubscriptionType,
            LinkedAt = time.GetUtcNow(),
        };
    }

    /// <summary>Saves the snapshot's <c>claudeAiOauth</c> login as the profile's CLI-linked copy and verifies it.</summary>
    public async Task WriteMirrorAsync(Guid profileId, CliSnapshot snapshot, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            SaveCopy(profileId, MirrorOf(snapshot));
            Forget(profileId);
        }
    }

    /// <summary>
    /// Removes the profile's copy of the Claude Code login; its other secrets stay. Changes are serialised,
    /// so a check that started before an unlink cannot recreate the copy after it (callers cancel such
    /// checks first).
    /// </summary>
    public async Task DeleteMirrorAsync(Guid profileId, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            if (secrets.Exists(profileId))
            {
                SaveCopy(profileId, null);
            }

            Forget(profileId);
        }
    }

    /// <summary>
    /// Reads the linked folder and decides which login the profile may poll with. A usable or expired login
    /// of the linked account updates the copy when it changed, which captures Claude Code's own rotations.
    /// A copy that cannot be read is reported and left untouched.
    /// </summary>
    public async Task<LinkCheck> CheckAsync(Guid profileId, CliLink link, CancellationToken cancellation)
    {
        var directory = CliStore.ForLink(link, userProfile);
        var snapshot = Read(directory);
        if (snapshot.State is CliLoginState.Invalid or CliLoginState.Unreadable)
        {
            await Task.Delay(RereadDelay, time, cancellation).ConfigureAwait(false);
            snapshot = Read(directory);
        }

        var mirrorError = await SaveUnsavedAsync(profileId, cancellation).ConfigureAwait(false);
        var (copy, loadError) = LoadCopy(profileId);
        mirrorError ??= loadError;

        LinkNotice? notice = null;
        var handBackAfterMirror = false;
        if (copy?.Pending is not null)
        {
            if (RenewedInFolder(link, snapshot, copy))
            {
                // Claude Code renewed the login in the linked folder since the copy was saved. The hand-back waits
                // until the copy holds that newer login (below), so no folder is given a token Claude Code has used.
                handBackAfterMirror = true;
                notice = LinkNotice.HandBackPending;
            }
            else
            {
                (copy, notice) = await ReconcileAsync(profileId, cancellation).ConfigureAwait(false);
                // The hand-back may just have written the folder.
                snapshot = Read(directory);
            }
        }

        if (snapshot.Credentials is not { } stored)
        {
            return snapshot.State == CliLoginState.NotSignedIn
                ? await AccountLeftAsync(profileId, link, snapshot, copy, LinkStatus.NotSignedIn, mirrorError, notice, cancellation).ConfigureAwait(false)
                : FromCopy(
                    snapshot.State == CliLoginState.Unreadable ? LinkStatus.Unreadable : LinkStatus.Invalid,
                    snapshot, copy, CredentialOwnership.Unresolved, mirrorError, notice);
        }

        // The folder still holds a token that a renewal by this or another profile used up and has yet to hand
        // back. It is the same lineage, and a copy that differs from it is that renewal or newer.
        var awaitingHandBack = copy is not null && stored.AccessToken != AccessTokenOf(copy)
                               && SavedCopies().Any(saved => saved.Copy?.Pending?.ConsumedRefreshToken == stored.RefreshToken);
        var lineage = awaitingHandBack ? stored.RefreshToken : RefreshTokenOf(copy);
        if (lineage is not null && lineage != stored.RefreshToken)
        {
            // The token lineage changed: Claude Code refreshed, or a /login is writing .credentials.json and
            // .claude.json one after the other. Trust the files only once two reads agree.
            await Task.Delay(RereadDelay, time, cancellation).ConfigureAwait(false);
            var again = Read(directory);
            if (again.Credentials?.RawJson != stored.RawJson || again.Account?.Identity != snapshot.Account?.Identity)
            {
                return FromCopy(LinkStatus.Unreadable, again, copy, CredentialOwnership.Unresolved, mirrorError, notice);
            }
        }

        switch (CliLoginLabels.Compare(link.Identity, snapshot.Account?.Identity, lineage, stored.RefreshToken))
        {
            case CliAccountMatch.DifferentAccount:
                return await AccountLeftAsync(profileId, link, snapshot, copy, LinkStatus.AccountChanged, mirrorError, notice, cancellation)
                    .ConfigureAwait(false);
            case CliAccountMatch.Unresolved:
                return FromCopy(LinkStatus.AccountUnverified, snapshot, copy, CredentialOwnership.Unresolved, mirrorError, notice);
        }

        if (awaitingHandBack)
        {
            // The copy is newer than the folder. It is used for polling but never renewed again until the
            // hand-back is settled.
            return FromCopy(LinkStatus.WaitingForCliRefresh, snapshot, copy, CredentialOwnership.CliLinked, mirrorError, notice, LinkStatus.Ready);
        }

        if (mirrorError is null && OAuthObject(stored.RawJson) is { } oauth
            && (copy is null || copy.OAuthJson != oauth || copy.Ownership != CredentialOwnership.CliLinked))
        {
            mirrorError = await MirrorAsync(profileId, snapshot, cancellation).ConfigureAwait(false);
            if (handBackAfterMirror && mirrorError is null)
            {
                (_, notice) = await ReconcileAsync(profileId, cancellation).ConfigureAwait(false);
            }
        }

        return snapshot.State == CliLoginState.Usable
            ? new LinkCheck(LinkStatus.Ready, snapshot, stored, mirrorError) { Notice = notice }
            : await TakeOverAsync(profileId, link, snapshot, mirrorError, notice, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The linked folder no longer holds the linked account: it is signed out or holds another account. The
    /// profile keeps following its account through its copy. Once no known folder holds the account, no
    /// Claude Code process could still have it in memory and nothing is in doubt, the copy becomes the
    /// tracker's own. Ownership is settled again at every check, so a saved app-owned copy authorises nothing
    /// by itself: it goes back to Claude Code when a known folder holds the account again or the user marks
    /// the login as used elsewhere, and is not renewed while another folder is in doubt.
    /// </summary>
    private async Task<LinkCheck> AccountLeftAsync(
        Guid profileId, CliLink link, CliSnapshot snapshot, CliSecret? copy, LinkStatus status, string? mirrorError, LinkNotice? notice,
        CancellationToken cancellation)
    {
        if (copy is null)
        {
            return new LinkCheck(status, snapshot, null, mirrorError) { Ownership = CredentialOwnership.Unresolved, Notice = notice };
        }

        var lineage = RefreshTokenOf(copy);
        var unresolved = false;
        foreach (var other in inventory().Where(d => !SameDirectory(d.Path, snapshot.Directory.Path)).Select(Read))
        {
            if (other.State is CliLoginState.Unreadable or CliLoginState.Invalid)
            {
                unresolved = true;
            }
            else if (other.Credentials is { } elsewhere)
            {
                switch (CliLoginLabels.Compare(link.Identity, other.Account?.Identity, lineage, elsewhere.RefreshToken))
                {
                    case CliAccountMatch.SameAccount when other.State == CliLoginState.Usable:
                        // Another known folder holds the account, so it is Claude Code's to renew, even if the copy
                        // had become the tracker's own.
                        if (mirrorError is null && (OAuthObject(elsewhere.RawJson) != copy.OAuthJson || copy.Ownership != CredentialOwnership.CliLinked))
                        {
                            mirrorError = await MirrorAsync(profileId, other, cancellation).ConfigureAwait(false);
                        }

                        return new LinkCheck(LinkStatus.Ready, other, elsewhere, mirrorError) { Notice = notice };
                    case CliAccountMatch.SameAccount:
                        mirrorError ??= await RelinkAsync(profileId, copy, cancellation).ConfigureAwait(false);
                        return FromCopy(LinkStatus.WaitingForCliRefresh, other, copy, CredentialOwnership.CliLinked, mirrorError, notice);
                    case CliAccountMatch.Unresolved:
                        unresolved = true;
                        break;
                }
            }
        }

        if (unresolved)
        {
            return FromCopy(status, snapshot, copy, CredentialOwnership.Unresolved, mirrorError, notice ?? LinkNotice.OtherFolderUnreadable);
        }

        if (link.UsedElsewhere)
        {
            // The user's statement that the login is also used where the tracker cannot see it: it is treated as
            // linked and never renewed here.
            mirrorError ??= await RelinkAsync(profileId, copy, cancellation).ConfigureAwait(false);
            return FromCopy(status, snapshot, copy, CredentialOwnership.CliLinked, mirrorError, notice);
        }

        if (copy.Ownership == CredentialOwnership.AppOwned)
        {
            return await UseOwnedAsync(profileId, snapshot, copy, mirrorError, cancellation).ConfigureAwait(false);
        }

        if (mirrorError is not null || processes.ActivityFor(snapshot.Directory) != StoreActivity.Idle)
        {
            return FromCopy(status, snapshot, copy, CredentialOwnership.CliLinked, mirrorError, notice);
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            var current = LoadCopy(profileId).Copy;
            if (current is null || current.Pending is not null)
            {
                return FromCopy(status, snapshot, current, CredentialOwnership.CliLinked, mirrorError, notice);
            }

            copy = new CliSecret(current.OAuthJson, CredentialOwnership.AppOwned, current.SourceConfigDirectory, time.GetUtcNow());
            if (Persist(profileId, copy) is { } error)
            {
                // Ownership that is not on disk would be forgotten at the next start; stay linked until it saves.
                Forget(profileId);
                return FromCopy(status, snapshot, current, CredentialOwnership.CliLinked, error, notice);
            }
        }

        return await UseOwnedAsync(profileId, snapshot, copy, mirrorError, cancellation).ConfigureAwait(false);
    }

    /// <summary>Polls with the tracker's own copy, renewing it when it has expired.</summary>
    private async Task<LinkCheck> UseOwnedAsync(
        Guid profileId, CliSnapshot snapshot, CliSecret copy, string? mirrorError, CancellationToken cancellation)
    {
        LinkCheck Result(LinkStatus status, CliCredentials? credentials, LinkNotice? notice = null) =>
            new(status, snapshot, credentials, mirrorError) { Ownership = CredentialOwnership.AppOwned, Notice = notice };

        if (CliCredentials.Parse(copy.CredentialsJson) is not { } credentials)
        {
            return Result(LinkStatus.Invalid, null);
        }

        if (!credentials.IsExpiredOrExpiring(time.GetUtcNow()))
        {
            return Result(LinkStatus.Ready, credentials);
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            // Another check may have renewed it, or given it back to Claude Code, while this one waited for the lock.
            var current = LoadCopy(profileId).Copy;
            if (current is not { Ownership: CredentialOwnership.AppOwned })
            {
                return FromCopy(LinkStatus.WaitingForCliRefresh, snapshot, current, current?.Ownership ?? CredentialOwnership.Unresolved, mirrorError, null);
            }

            copy = current;
            credentials = CliCredentials.Parse(copy.CredentialsJson) ?? credentials;
            if (!credentials.IsExpiredOrExpiring(time.GetUtcNow()))
            {
                return Result(LinkStatus.Ready, credentials);
            }

            if (credentials.RefreshToken is not { } refreshToken || IsRejected(refreshToken))
            {
                return Result(LinkStatus.LoginRejected, null);
            }

            if ((MayNotRenew(profileId, refreshToken) ? LinkNotice.RenewalFailed : HandBackBlocks(refreshToken)) is { } blocked)
            {
                return Result(LinkStatus.RefreshFailed, null, blocked);
            }

            var outcome = await tokens.RefreshClaudeAsync(refreshToken, cancellation).ConfigureAwait(false);
            switch (outcome.Result)
            {
                case RefreshResult.Succeeded:
                    var renewed = Renewed(copy.CredentialsJson, refreshToken, outcome, CredentialOwnership.AppOwned, copy.SourceConfigDirectory, null);
                    mirrorError = Persist(profileId, renewed) ?? mirrorError;
                    ShareRenewal(profileId, refreshToken, renewed);
                    return Result(LinkStatus.Ready, CliCredentials.Parse(renewed.CredentialsJson));
                case RefreshResult.LoginRejected:
                    Reject(refreshToken);
                    return Result(LinkStatus.LoginRejected, null);
                default:
                    HoldRenewal(profileId);
                    return Result(LinkStatus.RefreshFailed, null, LinkNotice.RenewalFailed);
            }
        }
    }

    /// <summary>
    /// The guarded idle takeover: the linked folder holds the linked account with an expired token. The
    /// tracker renews it once, only when no Claude Code process could be using any folder that holds the
    /// same lineage, saves the result, then writes it back to those folders.
    /// </summary>
    private async Task<LinkCheck> TakeOverAsync(
        Guid profileId, CliLink link, CliSnapshot snapshot, string? mirrorError, LinkNotice? notice, CancellationToken cancellation)
    {
        LinkCheck Waiting(LinkNotice? why = null) =>
            new(LinkStatus.WaitingForCliRefresh, snapshot, null, mirrorError) { Notice = why ?? notice };

        var refreshToken = snapshot.Credentials!.RefreshToken;
        if (refreshToken is null || link.UsedElsewhere || mirrorError is not null)
        {
            return Waiting();
        }

        if (RenewalBlocked(profileId, refreshToken, snapshot, mirrorError, notice) is { } blocked)
        {
            return blocked;
        }

        // Expiry alone does not prove the CLI idle: sleep, a network failure or a stalled session can leave a
        // live Claude Code holding an expired login.
        if (processes.ActivityFor(snapshot.Directory) != StoreActivity.Idle)
        {
            return Waiting();
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            // Checked again under the lock: another profile's renewal may have used up or been refused this
            // token while this check waited, even when its hand-back failed and the file still holds it.
            if (RenewalBlocked(profileId, refreshToken, snapshot, mirrorError, notice) is { } blockedNow)
            {
                return blockedNow;
            }

            // Claude Code may also have started or changed the file meanwhile.
            var fresh = Read(snapshot.Directory);
            if (fresh.Credentials?.RawJson != snapshot.Credentials.RawJson || fresh.Account?.Identity != snapshot.Account?.Identity)
            {
                return Waiting();
            }

            var stores = inventory().Select(d => SameDirectory(d.Path, snapshot.Directory.Path) ? fresh : Read(d)).ToList();
            if (stores.Any(s => s.State is CliLoginState.Unreadable or CliLoginState.Invalid))
            {
                return Waiting(LinkNotice.OtherFolderUnreadable);
            }

            var holders = stores.Where(s => s.Credentials?.RefreshToken == refreshToken).ToList();
            if (holders.Any(h => processes.ActivityFor(h.Directory) != StoreActivity.Idle))
            {
                return Waiting();
            }

            var outcome = await tokens.RefreshClaudeAsync(refreshToken, cancellation).ConfigureAwait(false);
            switch (outcome.Result)
            {
                case RefreshResult.Succeeded:
                    var pending = new PendingWriteBack(refreshToken, time.GetUtcNow(), [.. holders.Select(h => h.Directory.Path)]);
                    var renewed = Renewed(fresh.Credentials!.RawJson, refreshToken, outcome, CredentialOwnership.CliLinked, snapshot.Directory.Path, pending);
                    // Saved before it is handed back, so a crash in between can be reconciled at the next start. If
                    // it cannot be saved it is still handed back: the folder is where Claude Code looks.
                    mirrorError = Persist(profileId, renewed);
                    ShareRenewal(profileId, refreshToken, renewed);
                    var (settled, handBack) = WriteBack(profileId, renewed);
                    return new LinkCheck(LinkStatus.Ready, fresh, CliCredentials.Parse(settled.CredentialsJson), mirrorError ?? HasUnsavedError(profileId))
                    {
                        Notice = handBack,
                    };
                case RefreshResult.LoginRejected:
                    Reject(refreshToken);
                    return new LinkCheck(LinkStatus.LoginRejected, snapshot, null, mirrorError) { Notice = notice };
                default:
                    HoldRenewal(profileId);
                    return Waiting(LinkNotice.RenewalFailed);
            }
        }
    }

    /// <summary>Tries again to hand a renewed login back to the folders that still hold the old one.</summary>
    private async Task<(CliSecret? Copy, LinkNotice? Notice)> ReconcileAsync(Guid profileId, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            var copy = LoadCopy(profileId).Copy;
            return copy?.Pending is null ? (copy, null) : WriteBack(profileId, copy);
        }
    }

    /// <summary>
    /// Writes the copy's login (the renewal, or a newer login of the account that Claude Code wrote since) into
    /// each pending folder that still holds the used-up token and that no Claude Code process could be using,
    /// then verifies the read-back. A folder that was signed out or now holds a newer login is left alone and
    /// dropped from the list: a newer CLI login is never replaced. The caller holds the credential lock. Claude
    /// Code does not honour that lock, so a write by it between the read and the replace here remains possible;
    /// the process check makes it unlikely.
    /// </summary>
    private (CliSecret Copy, LinkNotice? Notice) WriteBack(Guid profileId, CliSecret copy)
    {
        var pending = copy.Pending!;
        var renewed = CliCredentials.Parse(copy.CredentialsJson)
                      ?? throw new InvalidOperationException("The renewed login to hand back is not a Claude Code login.");
        var known = inventory();
        var remaining = new List<string>();
        foreach (var path in pending.Targets)
        {
            var directory = known.FirstOrDefault(d => SameDirectory(d.Path, path))
                            ?? new CliConfigDirectory(CliStore.Normalise(path), false, userProfile);
            if (processes.ActivityFor(directory) != StoreActivity.Idle)
            {
                remaining.Add(path);
                continue;
            }

            var current = Read(directory);
            if (current.State == CliLoginState.NotSignedIn)
            {
                continue;
            }

            if (current.Credentials is not { } stored)
            {
                remaining.Add(path);
                continue;
            }

            if (stored.AccessToken == renewed.AccessToken || stored.RefreshToken != pending.ConsumedRefreshToken)
            {
                // Already handed back, or a newer login is there.
                continue;
            }

            try
            {
                var merged = ClaudeCliCredentials.ApplyTokens(
                    stored.RawJson, renewed.AccessToken, renewed.RefreshToken, renewed.ExpiresAt?.ToUnixTimeMilliseconds() ?? 0);
                AtomicFile.WriteAllBytes(directory.CredentialsFile, Encoding.UTF8.GetBytes(merged!));
                if (Read(directory).Credentials?.AccessToken != renewed.AccessToken)
                {
                    remaining.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                remaining.Add(path);
            }
        }

        var settled = new CliSecret(
            copy.OAuthJson, copy.Ownership, copy.SourceConfigDirectory, copy.SavedAt,
            remaining.Count == 0 ? null : new PendingWriteBack(pending.ConsumedRefreshToken, pending.CreatedAt, remaining));
        if (remaining.Count != pending.Targets.Count || HasUnsaved(profileId))
        {
            Persist(profileId, settled);
        }

        return (settled, remaining.Count == 0 ? null : LinkNotice.HandBackPending);
    }

    /// <summary>Every profile's saved copy, with the error for one that cannot be read.</summary>
    private IEnumerable<(CliSecret? Copy, string? Error)> SavedCopies() => secrets.ProfileIds().Select(LoadCopy);

    /// <summary>
    /// Why renewing this token could break a hand-back still pending, or null when it cannot. Nothing is renewed
    /// in a lineage that some folder is still waiting to be handed back (the token the renewal used up, or the
    /// login being handed back): a further renewal would leave that folder a token used up twice over. These
    /// records survive a restart, unlike the list of used tokens in memory, so a copy of any profile that cannot
    /// be read blocks every renewal: its record cannot be ruled out.
    /// </summary>
    private LinkNotice? HandBackBlocks(string refreshToken)
    {
        var copies = SavedCopies().ToList();
        if (copies.Any(saved => saved.Error is not null))
        {
            return LinkNotice.OtherCopyUnreadable;
        }

        return copies.Any(saved => saved.Copy?.Pending is { } pending
                                   && (pending.ConsumedRefreshToken == refreshToken || RefreshTokenOf(saved.Copy) == refreshToken))
            ? LinkNotice.HandBackPending
            : null;
    }

    /// <summary>
    /// Gives a renewal to every other profile whose copy holds the token it used up, whether or not that
    /// profile has yet seen who owns the login: they are copies of the same account's login, and theirs is now
    /// spent. Each keeps its own ownership, which its next check settles. A copy that cannot be saved is kept
    /// for a later save, like the profile's own. The caller holds the credential lock.
    /// </summary>
    private void ShareRenewal(Guid profileId, string consumedToken, CliSecret renewed)
    {
        foreach (var other in secrets.ProfileIds().Where(id => id != profileId))
        {
            if (LoadCopy(other).Copy is { } copy && RefreshTokenOf(copy) == consumedToken)
            {
                Persist(other, new CliSecret(renewed.OAuthJson, copy.Ownership, copy.SourceConfigDirectory, time.GetUtcNow(), copy.Pending));
            }
        }
    }

    /// <summary>
    /// Whether Claude Code renewed the linked account's login in the linked folder after the copy with a
    /// pending hand-back was saved: the folder holds this account (both identities known and equal) with a
    /// token that is neither the used-up one nor the copy's.
    /// </summary>
    private static bool RenewedInFolder(CliLink link, CliSnapshot snapshot, CliSecret copy) =>
        snapshot.Credentials?.RefreshToken is { } token
        && token != copy.Pending!.ConsumedRefreshToken
        && token != RefreshTokenOf(copy)
        && CliLoginLabels.Compare(link.Identity, snapshot.Account?.Identity, null, null) == CliAccountMatch.SameAccount;

    /// <summary>
    /// Gives an app-owned copy back to Claude Code, keeping its login, which may be newer than the folder's.
    /// Returns the error, if any.
    /// </summary>
    private async Task<string?> RelinkAsync(Guid profileId, CliSecret copy, CancellationToken cancellation)
    {
        if (copy.Ownership == CredentialOwnership.CliLinked)
        {
            return null;
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            if (LoadCopy(profileId) is not ({ Ownership: CredentialOwnership.AppOwned } current, null))
            {
                return null;
            }

            try
            {
                SaveCopy(profileId, new CliSecret(current.OAuthJson, CredentialOwnership.CliLinked, current.SourceConfigDirectory, time.GetUtcNow(), current.Pending));
                return null;
            }
            catch (Exception e) when (IsStorageFailure(e))
            {
                return e.Message;
            }
        }
    }

    /// <summary>The copy after a successful grant: the old login with the new tokens and expiry.</summary>
    private CliSecret Renewed(
        string credentialsJson, string sentRefreshToken, ClaudeRefreshOutcome outcome, CredentialOwnership ownership, string source,
        PendingWriteBack? pending)
    {
        var response = outcome.Response!;
        if (response.RefreshToken is { } rotated && rotated != sentRefreshToken)
        {
            lock (_gate)
            {
                _consumed.Add(sentRefreshToken);
            }
        }

        var merged = ClaudeCliCredentials.MergeRefreshedCredentials(credentialsJson, response, outcome.At)
                     ?? throw new InvalidOperationException("The login that was renewed is not a Claude Code login.");
        return new CliSecret(OAuthObject(merged)!, ownership, source, time.GetUtcNow(), pending);
    }

    /// <summary>Copies the folder's current login into the profile's file, under the lock. Returns the error, if any.</summary>
    private async Task<string?> MirrorAsync(Guid profileId, CliSnapshot snapshot, CancellationToken cancellation)
    {
        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            // A superseded check never writes.
            cancellation.ThrowIfCancellationRequested();
            try
            {
                // Only the same account is mirrored, so a pending hand-back stays: the folders it lists still hold
                // a used-up token of this account, and now get this newer login.
                SaveCopy(profileId, MirrorOf(snapshot, LoadCopy(profileId).Copy?.Pending));
                return null;
            }
            catch (Exception e) when (IsStorageFailure(e))
            {
                return e.Message;
            }
        }
    }

    private CliSecret MirrorOf(CliSnapshot snapshot, PendingWriteBack? pending = null) =>
        new(OAuthObject(snapshot.Credentials?.RawJson) ?? throw new InvalidOperationException("The snapshot has no claudeAiOauth login to mirror."),
            CredentialOwnership.CliLinked, snapshot.Directory.Path, time.GetUtcNow(), pending);

    /// <summary>The profile's result when only its copy can be used: its token while unexpired, otherwise nothing.</summary>
    private LinkCheck FromCopy(
        LinkStatus status, CliSnapshot snapshot, CliSecret? copy, CredentialOwnership ownership, string? mirrorError, LinkNotice? notice,
        LinkStatus? statusWhenUsable = null)
    {
        var credentials = copy is not null && CliCredentials.Parse(copy.CredentialsJson) is { } parsed
                          && !parsed.IsExpiredOrExpiring(time.GetUtcNow())
            ? parsed
            : null;
        return new LinkCheck(credentials is null ? status : statusWhenUsable ?? status, snapshot, credentials, mirrorError)
        {
            Ownership = ownership,
            Notice = notice,
        };
    }

    /// <summary>Saves a renewed login that an earlier save failed for. Returns the error while it still cannot be saved.</summary>
    private async Task<string?> SaveUnsavedAsync(Guid profileId, CancellationToken cancellation)
    {
        if (!HasUnsaved(profileId))
        {
            return null;
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            CliSecret? unsaved;
            lock (_gate)
            {
                _unsaved.TryGetValue(profileId, out unsaved);
            }

            return unsaved is null ? null : Persist(profileId, unsaved);
        }
    }

    /// <summary>
    /// Saves a copy; on failure keeps it in memory for a later save and returns the error. The caller holds
    /// the credential lock.
    /// </summary>
    private string? Persist(Guid profileId, CliSecret copy)
    {
        try
        {
            SaveCopy(profileId, copy);
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
                _unsaved[profileId] = copy;
            }

            return e.Message;
        }
    }

    private void SaveCopy(Guid profileId, CliSecret? copy)
    {
        var bundle = secrets.Load(profileId);
        bundle.Cli = copy;
        secrets.Save(profileId, bundle);
    }

    /// <summary>The saved copy, or the renewed one still waiting to be saved; an error when the file cannot be used.</summary>
    private (CliSecret? Copy, string? Error) LoadCopy(Guid profileId)
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
            return (secrets.Load(profileId).Cli, null);
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            return (null, e.Message);
        }
    }

    private static bool IsStorageFailure(Exception e) =>
        e is SecretStoreException or IOException or UnauthorizedAccessException or CryptographicException;

    private bool HasUnsaved(Guid profileId)
    {
        lock (_gate)
        {
            return _unsaved.ContainsKey(profileId);
        }
    }

    private string? HasUnsavedError(Guid profileId) =>
        HasUnsaved(profileId) ? "The renewed login could not be saved yet; it is kept in memory and saving is tried again." : null;

    /// <summary>
    /// Whether a renewal must not be sent now: the token was already used up by a successful grant, a
    /// renewed login is still waiting to be saved, or an attempt failed a moment ago.
    /// </summary>
    private bool MayNotRenew(Guid profileId, string refreshToken)
    {
        lock (_gate)
        {
            return _consumed.Contains(refreshToken)
                   || _unsaved.ContainsKey(profileId)
                   || (_renewalNotBefore.TryGetValue(profileId, out var notBefore) && time.GetUtcNow() < notBefore);
        }
    }

    /// <summary>
    /// Why a takeover with this token must not be sent now, or null when it may: the token was refused; it
    /// belongs to a lineage with a hand-back still pending, saved by any profile in this or an earlier run, or
    /// a profile's copy cannot be read; it was used up by a renewal (Claude Code then picks up the renewed
    /// login); or this profile's last renewal failed a moment ago or is still unsaved.
    /// </summary>
    private LinkCheck? RenewalBlocked(Guid profileId, string refreshToken, CliSnapshot snapshot, string? mirrorError, LinkNotice? notice)
    {
        LinkCheck Waiting(LinkNotice? why) => new(LinkStatus.WaitingForCliRefresh, snapshot, null, mirrorError) { Notice = why };

        if (IsRejected(refreshToken))
        {
            return new LinkCheck(LinkStatus.LoginRejected, snapshot, null, mirrorError) { Notice = notice };
        }

        if (HandBackBlocks(refreshToken) is { } why)
        {
            return Waiting(why);
        }

        bool consumed;
        lock (_gate)
        {
            consumed = _consumed.Contains(refreshToken);
        }

        return consumed ? Waiting(notice)
            : MayNotRenew(profileId, refreshToken) ? Waiting(LinkNotice.RenewalFailed)
            : null;
    }

    private bool IsRejected(string refreshToken)
    {
        lock (_gate)
        {
            return _rejected.Contains(refreshToken);
        }
    }

    private void Reject(string refreshToken)
    {
        lock (_gate)
        {
            _rejected.Add(refreshToken);
        }
    }

    private void HoldRenewal(Guid profileId)
    {
        lock (_gate)
        {
            _renewalNotBefore[profileId] = time.GetUtcNow() + RenewalRetryDelay;
        }
    }

    private void Forget(Guid profileId)
    {
        lock (_gate)
        {
            _unsaved.Remove(profileId);
            _renewalNotBefore.Remove(profileId);
        }
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(CliStore.Normalise(a), CliStore.Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string? RefreshTokenOf(CliSecret? copy) => TokenOf(copy, "refreshToken");

    private static string? AccessTokenOf(CliSecret? copy) => TokenOf(copy, "accessToken");

    private static string? TokenOf(CliSecret? copy, string name) =>
        copy is not null
        && JsonNode.Parse(copy.OAuthJson) is JsonObject node
        && node[name] is JsonValue value
        && value.TryGetValue(out string? token)
        && token.Length > 0
            ? token
            : null;

    /// <summary>The <c>claudeAiOauth</c> object alone: other members (such as MCP server logins) are not copied.</summary>
    private static string? OAuthObject(string? rawJson)
    {
        if (rawJson is null)
        {
            return null;
        }

        try
        {
            return (JsonNode.Parse(rawJson) as JsonObject)?["claudeAiOauth"] is JsonObject oauth ? oauth.ToJsonString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
