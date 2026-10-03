using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.ClaudeCode;

namespace ClaudeUsageTracker.App.ClaudeCode;

public enum CliSwitchOutcome
{
    /// <summary>The folder now holds the profile's login and account record, verified by reading them back.</summary>
    Switched,

    /// <summary>The folder already holds the profile's account; nothing was changed.</summary>
    AlreadyInUse,

    /// <summary>Claude Code is or may be running, or a renewal must wait; nothing was changed. Try again later.</summary>
    Deferred,

    /// <summary>The folder holds an account no profile follows. A manual switch must confirm that it signs it out.</summary>
    NeedsConfirmation,

    /// <summary>The switch is not safe or not possible (see <see cref="CliSwitchResult.Refusal"/>); nothing was changed.</summary>
    Refused,

    /// <summary>Another writer changed the folder during the switch. The switch stopped and undid its own write.</summary>
    Conflict,

    /// <summary>The switch wrote the folder but could neither finish nor undo it. Its record stays until it is settled.</summary>
    RepairNeeded,
}

public enum CliSwitchRefusal
{
    NoCopy,
    CopyUnreadable,
    HandBackPending,
    UsedElsewhere,
    HeldByOtherFolder,
    FolderUnreadable,
    LoginRejected,

    /// <summary>The target's expired login could not be renewed just now (no usable answer, or a renewal failed a moment ago).</summary>
    RenewalFailed,

    /// <summary>An earlier switch has not been settled, or was settled without having written anything.</summary>
    SwitchUnsettled,

    StorageFailed,

    /// <summary>An unfinished switch was left to a newer Claude Code sign-in.</summary>
    Superseded,

    /// <summary>An unfinished switch left the folder unclear; the user has to sign in again in Claude Code and say so.</summary>
    SignInAgain,
}

/// <summary>A profile's Claude Code link, copied from the settings for the switch.</summary>
public sealed record LinkedProfile(Guid Id, CliLink Link);

/// <param name="Detail">The folder concerned, the account to be signed out, or an error message.</param>
public sealed record CliSwitchResult(CliSwitchOutcome Outcome, CliSwitchRefusal? Refusal = null, string? Detail = null)
{
    /// <summary>The profile that was switched, for <see cref="CliSwitchOutcome.Switched"/>.</summary>
    public Guid? ProfileId { get; init; }

    /// <summary>
    /// For <see cref="CliSwitchOutcome.NeedsConfirmation"/>: identifies the login the user is asked about (its account and
    /// when its file was written; no token). A confirmed switch goes ahead only while the folder still holds that login.
    /// </summary>
    public string? OccupantStamp { get; init; }
}

/// <summary>
/// Switching the account of a shared Claude Code folder (upstream <c>ProfileManager.activateProfile</c>, which wrote the
/// profile's login into the CLI's storage and its <c>oauthAccount</c> into <c>.claude.json</c> on every activation).
/// Here it is a separate action, guarded as plan.md section 2 requires: nothing is changed while a Claude Code process
/// could be using the folder or anything is in doubt; the outgoing account's latest login is saved to the profiles that
/// follow it first; both files are backed up encrypted; each file is replaced only if it is unchanged since it was read,
/// with every other member kept; the result is verified; and a write that cannot be completed is undone only while the
/// file still holds exactly what this switch wrote. Two files are not one transaction: a journal marks the switch until it
/// is settled, here or at the next start. Claude Code does not honour the credential lock, so a write by it between the
/// last check and the replace remains possible; the process check makes it unlikely.
/// </summary>
public sealed partial class CliLinkService
{
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Whether a switch is recorded as unfinished (or its record cannot be read).</summary>
    public bool SwitchUnsettled => switches.Blocks(null);

    /// <summary>Test seam: runs just before a switch compares a Claude Code file with what it read and replaces it.</summary>
    internal Action<string>? BeforeReplacing { get; set; }

    // Claude Code writes .claude.json with two-space indentation.
    private static readonly JsonSerializerOptions IndentedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>Writes the target profile's login and account record into <paramref name="folder"/>.</summary>
    /// <param name="profiles">Every profile with a Claude Code link, to find who follows the folder's current account.</param>
    /// <param name="confirmedOccupant">The <see cref="CliSwitchResult.OccupantStamp"/> the user agreed to sign out, if any.</param>
    public async Task<CliSwitchResult> SwitchAsync(
        Guid targetId, IReadOnlyList<LinkedProfile> profiles, CliConfigDirectory folder, string? confirmedOccupant, CancellationToken cancellation)
    {
        if (processes.ActivityFor(folder) != StoreActivity.Idle)
        {
            return new CliSwitchResult(CliSwitchOutcome.Deferred);
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            return await SwitchLockedAsync(targetId, profiles, folder, confirmedOccupant, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Settles a switch that an earlier run (or this one) left unfinished. Returns null when none is recorded. The folder's
    /// own state decides:
    /// <list type="bullet">
    /// <item>The target account's record there means the switch landed; any rotation Claude Code made since is kept.</item>
    /// <item>The target's login beside the previous account's record is completed, keeping whatever else Claude Code wrote
    /// to <c>.claude.json</c> meanwhile.</item>
    /// <item>A login unchanged since the backup means nothing was written.</item>
    /// <item>A changed account record means a newer sign-in, which is left alone.</item>
    /// </list>
    /// What remains is unclear: an unknown login beside the previous account's record may be the target's login renewed
    /// by Claude Code, or a new sign-in to the previous account. It is left blocked until the user signs in again in
    /// Claude Code and says so (<paramref name="signedInAgain"/>), because guessing would put one account's login under
    /// another's name.
    /// </summary>
    public async Task<CliSwitchResult?> SettleSwitchAsync(IReadOnlyList<LinkedProfile> profiles, bool signedInAgain, CancellationToken cancellation)
    {
        CliSwitchJournal? journal;
        try
        {
            journal = switches.ReadJournal();
        }
        catch (SettingsStoreException e)
        {
            if (signedInAgain)
            {
                // Which folder it names cannot be read; the user has signed in again, so the folders are Claude Code's own.
                switches.End();
                return new CliSwitchResult(CliSwitchOutcome.Refused, CliSwitchRefusal.Superseded);
            }

            return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, CliSwitchRefusal.SignInAgain, e.Message);
        }

        if (journal is null)
        {
            return null;
        }

        var folder = DirectoryFor(journal.Folder);
        if (processes.ActivityFor(folder) != StoreActivity.Idle)
        {
            return new CliSwitchResult(CliSwitchOutcome.Deferred);
        }

        using (await credentialLock.AcquireAsync(cancellation).ConfigureAwait(false))
        {
            if (processes.ActivityFor(folder) != StoreActivity.Idle)
            {
                return new CliSwitchResult(CliSwitchOutcome.Deferred);
            }

            var target = profiles.FirstOrDefault(p => p.Id == journal.TargetProfileId);
            var (copy, copyError) = LoadCopy(journal.TargetProfileId);
            CliSwitchBackup? backup;
            try
            {
                backup = switches.LoadBackup(journal.BackupId);
            }
            catch (SecretStoreException e)
            {
                return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, Detail: e.Message);
            }

            if (copyError is not null)
            {
                return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, Detail: copyError);
            }

            var occupant = Read(folder);
            if (occupant.State is CliLoginState.Unreadable or CliLoginState.Invalid)
            {
                return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, CliSwitchRefusal.FolderUnreadable, folder.Path);
            }

            var now = time.GetUtcNow();
            if (target is not null && copy is not null && occupant.Credentials is not null
                && CliLoginLabels.Compare(target.Link.Identity, occupant.Account?.Identity, null, null) == CliAccountMatch.SameAccount)
            {
                // Both writes landed. Claude Code may have renewed the login since; the folder's login is the current one.
                return Finish(journal.TargetProfileId, MirrorOf(occupant, null, copy.OAuthAccountJson), folder);
            }

            byte[]? credentials;
            byte[]? currentAccount;
            try
            {
                credentials = CurrentBytes(folder.CredentialsFile);
                currentAccount = backup?.AccountFile is { } file ? CurrentBytes(file) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, CliSwitchRefusal.FolderUnreadable, e.Message);
            }

            // Claude Code rewrites .claude.json at every start, so only the account record itself tells whether it changed.
            var recordUnchanged = backup is not null
                                  && (backup.AccountFile is null
                                      || JsonNode.DeepEquals(Member(backup.Account, "oauthAccount"), Member(currentAccount, "oauthAccount")));
            var heldIsTarget = copy is not null && occupant.Credentials is { } held
                               && (held.AccessToken == AccessTokenOf(copy) || held.RefreshToken == RefreshTokenOf(copy));
            if (target is not null && copy is not null && heldIsTarget && recordUnchanged)
            {
                if (backup!.AccountFile is not { } accountFile)
                {
                    // There was no account record to write: the login alone is the whole switch.
                    return Finish(journal.TargetProfileId, MirrorOf(occupant, null, copy.OAuthAccountJson), folder);
                }

                // The login was written but not the account record: complete it, over the file as it is now.
                if (Merge(currentAccount, "oauthAccount", copy.OAuthAccountJson ?? MinimalAccount(target.Link), IndentedJson) is not { } account)
                {
                    return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, CliSwitchRefusal.FolderUnreadable, accountFile);
                }

                try
                {
                    if (!SameBytes(CurrentBytes(accountFile), currentAccount))
                    {
                        // Written again just now: settled at the next attempt.
                        return new CliSwitchResult(CliSwitchOutcome.Deferred);
                    }

                    WriteFile(accountFile, account);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, Detail: e.Message);
                }

                var settled = Read(folder);
                return settled.Credentials?.AccessToken == occupant.Credentials!.AccessToken
                    ? Finish(journal.TargetProfileId, MirrorOf(settled, null, copy.OAuthAccountJson), folder)
                    : new CliSwitchResult(CliSwitchOutcome.RepairNeeded, Detail: folder.Path);
            }

            if (backup is not null && SameBytes(credentials, backup.Credentials))
            {
                // Nothing of the switch reached the folder.
                switches.End();
                return new CliSwitchResult(CliSwitchOutcome.Refused, CliSwitchRefusal.SwitchUnsettled, folder.Path);
            }

            if (backup is not null && !recordUnchanged)
            {
                // A new sign-in wrote both files after the switch stopped; that login stands.
                switches.End();
                return new CliSwitchResult(CliSwitchOutcome.Refused, CliSwitchRefusal.Superseded, folder.Path);
            }

            if (signedInAgain)
            {
                switches.End();
                return new CliSwitchResult(CliSwitchOutcome.Refused, CliSwitchRefusal.Superseded, folder.Path);
            }

            return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, CliSwitchRefusal.SignInAgain, folder.Path);
        }
    }

    private async Task<CliSwitchResult> SwitchLockedAsync(
        Guid targetId, IReadOnlyList<LinkedProfile> profiles, CliConfigDirectory folder, string? confirmedOccupant, CancellationToken cancellation)
    {
        static CliSwitchResult Refused(CliSwitchRefusal why, string? detail = null) => new(CliSwitchOutcome.Refused, why, detail);

        if (profiles.FirstOrDefault(p => p.Id == targetId) is not { } target)
        {
            return Refused(CliSwitchRefusal.NoCopy);
        }

        if (switches.Blocks(null))
        {
            return Refused(CliSwitchRefusal.SwitchUnsettled);
        }

        if (target.Link.UsedElsewhere)
        {
            // Writing it here would give a second holder a login that is also used where the tracker cannot see it.
            return Refused(CliSwitchRefusal.UsedElsewhere);
        }

        var (copy, copyError) = LoadCopy(targetId);
        if (copyError is not null)
        {
            return Refused(CliSwitchRefusal.CopyUnreadable, copyError);
        }

        if (copy is null || CliCredentials.Parse(copy.CredentialsJson) is not { } credentials)
        {
            return Refused(CliSwitchRefusal.NoCopy);
        }

        var saved = SavedCopies().ToList();
        if (saved.FirstOrDefault(s => s.Error is not null).Error is { } unreadable)
        {
            return Refused(CliSwitchRefusal.CopyUnreadable, unreadable);
        }

        if (HasUnsaved(targetId) || saved.Any(s => s.Copy?.Pending is not null))
        {
            return Refused(CliSwitchRefusal.HandBackPending);
        }

        var occupant = Read(folder);
        if (occupant.State is CliLoginState.Unreadable or CliLoginState.Invalid)
        {
            return Refused(CliSwitchRefusal.FolderUnreadable, folder.Path);
        }

        var targetToken = RefreshTokenOf(copy);
        if (occupant.Credentials is { } present
            && CliLoginLabels.Compare(target.Link.Identity, occupant.Account?.Identity, targetToken, present.RefreshToken) == CliAccountMatch.SameAccount)
        {
            return new CliSwitchResult(CliSwitchOutcome.AlreadyInUse, Detail: folder.Path);
        }

        foreach (var other in inventory().Where(d => !SameDirectory(d.Path, folder.Path)).Select(Read))
        {
            if (other.State is CliLoginState.Unreadable or CliLoginState.Invalid)
            {
                return Refused(CliSwitchRefusal.FolderUnreadable, other.Directory.Path);
            }

            // A second folder with the same lineage would break the other's renewals; one that cannot be told apart might be one.
            if (other.Credentials is { } elsewhere
                && CliLoginLabels.Compare(target.Link.Identity, other.Account?.Identity, targetToken, elsewhere.RefreshToken) != CliAccountMatch.DifferentAccount)
            {
                return Refused(CliSwitchRefusal.HeldByOtherFolder, other.Directory.Path);
            }
        }

        var followers = new List<Guid>();
        if (occupant.Credentials is { } current)
        {
            foreach (var profile in profiles.Where(p => p.Id != targetId))
            {
                var match = CliLoginLabels.Compare(profile.Link.Identity, occupant.Account?.Identity, RefreshTokenOf(LoadCopy(profile.Id).Copy), current.RefreshToken);
                if (match == CliAccountMatch.SameAccount)
                {
                    followers.Add(profile.Id);
                }
            }

            if (followers.Count == 0 && OccupantStamp(occupant) is var stamp && confirmedOccupant != stamp)
            {
                return new CliSwitchResult(CliSwitchOutcome.NeedsConfirmation, Detail: occupant.Label) { OccupantStamp = stamp };
            }
        }

        // Checked again under the lock, before the renewal and again before the first write.
        if (processes.ActivityFor(folder) != StoreActivity.Idle)
        {
            return new CliSwitchResult(CliSwitchOutcome.Deferred);
        }

        if (credentials.IsExpiredOrExpiring(time.GetUtcNow()))
        {
            // No known folder holds the account and no Claude Code runs, so the copy is the tracker's to renew before it
            // is handed over, as upstream renewed before applying a profile.
            if (credentials.RefreshToken is not { } refreshToken || IsRejected(refreshToken))
            {
                return Refused(CliSwitchRefusal.LoginRejected);
            }

            if (MayNotRenew(targetId, refreshToken) || HandBackBlocks(refreshToken) is not null)
            {
                return Refused(CliSwitchRefusal.RenewalFailed);
            }

            var outcome = await tokens.RefreshClaudeAsync(refreshToken, cancellation).ConfigureAwait(false);
            switch (outcome.Result)
            {
                case RefreshResult.Succeeded:
                    copy = Renewed(copy.CredentialsJson, refreshToken, outcome, CredentialOwnership.AppOwned, copy.SourceConfigDirectory, null, copy.OAuthAccountJson);
                    if (Persist(targetId, copy) is { } persistError)
                    {
                        return Refused(CliSwitchRefusal.StorageFailed, persistError);
                    }

                    ShareRenewal(targetId, refreshToken, copy);
                    break;
                case RefreshResult.LoginRejected:
                    Reject(refreshToken);
                    return Refused(CliSwitchRefusal.LoginRejected);
                default:
                    HoldRenewal(targetId);
                    return Refused(CliSwitchRefusal.RenewalFailed);
            }
        }

        // Both new files are prepared before anything is written, so a file that cannot be merged changes nothing.
        var accountFile = folder.AccountFiles.FirstOrDefault(File.Exists);
        byte[]? oldCredentials, oldAccount;
        try
        {
            oldCredentials = CurrentBytes(folder.CredentialsFile);
            oldAccount = accountFile is null ? null : CurrentBytes(accountFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(CliSwitchRefusal.FolderUnreadable, folder.Path);
        }

        // Who follows the outgoing login and what is saved for them was decided on the earlier read, before a renewal's
        // request. The files must still be that read, or the newest login would be lost: if Claude Code wrote meanwhile,
        // nothing is changed now and the switch is tried again.
        var oldAccountIdentity = oldAccount is null ? null : CliAccount.FromClaudeJson(Decode(oldAccount))?.Identity;
        if ((oldCredentials is null ? null : Decode(oldCredentials)) != occupant.Credentials?.RawJson
            || (accountFile is not null && oldAccountIdentity != occupant.Account?.Identity))
        {
            return new CliSwitchResult(CliSwitchOutcome.Deferred);
        }

        var accountRecord = copy.OAuthAccountJson ?? MinimalAccount(target.Link);
        var newCredentials = Merge(oldCredentials, "claudeAiOauth", copy.OAuthJson, CompactJson);
        var newAccount = accountFile is null ? null : Merge(oldAccount, "oauthAccount", accountRecord, IndentedJson);
        if (newCredentials is null || (accountFile is not null && newAccount is null))
        {
            return Refused(CliSwitchRefusal.FolderUnreadable, folder.Path);
        }

        var now = time.GetUtcNow();
        try
        {
            var backupId = switches.SaveBackup(new CliSwitchBackup(folder.Path, now, folder.CredentialsFile, oldCredentials, accountFile, oldAccount));
            foreach (var follower in followers)
            {
                // The outgoing account's latest login, which Claude Code may have renewed since it was last mirrored.
                var existing = LoadCopy(follower).Copy;
                SaveCopy(follower, MirrorOf(occupant, existing?.Pending, existing?.OAuthAccountJson));
                Forget(follower);
            }

            switches.Begin(new CliSwitchJournal(folder.Path, targetId, backupId, now));
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            return Refused(CliSwitchRefusal.StorageFailed, e.Message);
        }

        // A renewal's request may have taken a while: a Claude Code started meanwhile would be using the old login.
        if (processes.ActivityFor(folder) != StoreActivity.Idle)
        {
            switches.End();
            return new CliSwitchResult(CliSwitchOutcome.Deferred);
        }

        try
        {
            BeforeReplacing?.Invoke(folder.CredentialsFile);
            if (!SameBytes(CurrentBytes(folder.CredentialsFile), oldCredentials))
            {
                switches.End();
                return new CliSwitchResult(CliSwitchOutcome.Conflict, Detail: folder.Path);
            }

            Directory.CreateDirectory(folder.Path);
            WriteFile(folder.CredentialsFile, newCredentials);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Undo(folder.CredentialsFile, newCredentials, oldCredentials, e.Message);
        }

        if (accountFile is not null)
        {
            try
            {
                BeforeReplacing?.Invoke(accountFile);
                if (!SameBytes(CurrentBytes(accountFile), oldAccount))
                {
                    return Undo(folder.CredentialsFile, newCredentials, oldCredentials, null);
                }

                WriteFile(accountFile, newAccount!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Undo(folder.CredentialsFile, newCredentials, oldCredentials, e.Message);
            }
        }

        bool verified;
        try
        {
            var written = Read(folder);
            verified = written.Credentials?.AccessToken == AccessTokenOf(copy)
                       && (accountFile is null || target.Link.Identity is null
                           || CliLoginLabels.Compare(target.Link.Identity, written.Account?.Identity, null, null) == CliAccountMatch.SameAccount)
                       && OthersUnchanged(oldCredentials, CurrentBytes(folder.CredentialsFile), "claudeAiOauth")
                       && (accountFile is null || OthersUnchanged(oldAccount, CurrentBytes(accountFile), "oauthAccount"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            verified = false;
        }

        if (!verified)
        {
            return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, Detail: folder.Path);
        }

        // Handed to a Claude Code folder, the login is Claude Code's again.
        return Finish(targetId, new CliSecret(copy.OAuthJson, CredentialOwnership.CliLinked, folder.Path, now, null, accountRecord), folder);
    }

    /// <summary>Saves the switched profile's copy as linked to the folder and closes the journal.</summary>
    private CliSwitchResult Finish(Guid targetId, CliSecret copy, CliConfigDirectory folder)
    {
        if (Persist(targetId, copy) is { } error)
        {
            // The journal stays, so nothing mirrors from the folder or renews until the copy can be saved.
            return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, CliSwitchRefusal.StorageFailed, error);
        }

        Forget(targetId);
        switches.End();
        return new CliSwitchResult(CliSwitchOutcome.Switched, Detail: folder.Path) { ProfileId = targetId };
    }

    /// <summary>
    /// Stops a switch whose next step failed (<paramref name="error"/>) or met another writer (no error). The credentials
    /// file is restored only while it still holds exactly what this switch wrote: another writer's newer content is never
    /// replaced. When the file never took the switch's write, there is nothing to undo.
    /// </summary>
    private CliSwitchResult Undo(string credentialsFile, byte[] written, byte[]? original, string? error)
    {
        CliSwitchResult Stopped() => error is null
            ? new CliSwitchResult(CliSwitchOutcome.Conflict, Detail: credentialsFile)
            : new CliSwitchResult(CliSwitchOutcome.Refused, CliSwitchRefusal.StorageFailed, error);

        try
        {
            var current = CurrentBytes(credentialsFile);
            if (SameBytes(current, original))
            {
                switches.End();
                return Stopped();
            }

            if (current is null || !SameBytes(current, written))
            {
                return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, Detail: error ?? credentialsFile);
            }

            if (original is null)
            {
                File.Delete(credentialsFile);
            }
            else
            {
                WriteFile(credentialsFile, original);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new CliSwitchResult(CliSwitchOutcome.RepairNeeded, Detail: e.Message);
        }

        switches.End();
        return Stopped();
    }

    private CliConfigDirectory DirectoryFor(string path) =>
        inventory().FirstOrDefault(d => SameDirectory(d.Path, path)) ?? new CliConfigDirectory(CliStore.Normalise(path), false, userProfile);

    /// <summary>The login the user is asked to sign out, by account and file write time; no token.</summary>
    private static string OccupantStamp(CliSnapshot occupant)
    {
        var written = File.Exists(occupant.Directory.CredentialsFile) ? File.GetLastWriteTimeUtc(occupant.Directory.CredentialsFile).Ticks : 0;
        return $"{occupant.Account?.Identity ?? "?"}|{written}";
    }

    /// <summary>
    /// The account record written for a login whose <c>oauthAccount</c> was never seen (copies saved before Milestone 4):
    /// only the identity fields the link knows. Claude Code may fill in the rest when it next fetches the profile.
    /// </summary>
    private static string MinimalAccount(CliLink link)
    {
        var account = new JsonObject();
        if (link.AccountUuid is { } uuid)
        {
            account["accountUuid"] = uuid;
        }

        if (link.EmailAddress is { } email)
        {
            account["emailAddress"] = email;
        }

        if (link.OrganizationUuid is { } organization)
        {
            account["organizationUuid"] = organization;
        }

        if (link.OrganizationName is { } name)
        {
            account["organizationName"] = name;
        }

        return account.ToJsonString();
    }

    /// <summary>
    /// The file with one member replaced (or added), every other member kept in place; a missing file becomes an object
    /// with that member. Null when the existing content is not a JSON object.
    /// </summary>
    private static byte[]? Merge(byte[]? existing, string member, string valueJson, JsonSerializerOptions options)
    {
        JsonObject root;
        try
        {
            if (existing is null)
            {
                root = [];
            }
            else if (JsonNode.Parse(Encoding.UTF8.GetString(existing).TrimStart('﻿')) is JsonObject parsed)
            {
                root = parsed;
            }
            else
            {
                return null;
            }

            root[member] = JsonNode.Parse(valueJson);
        }
        catch (JsonException)
        {
            return null;
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(options));
    }

    /// <summary>Whether every member except <paramref name="member"/> is as it was.</summary>
    private static bool OthersUnchanged(byte[]? before, byte[]? after, string member)
    {
        static JsonObject? Parse(byte[]? bytes)
        {
            try
            {
                return bytes is null ? [] : JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿')) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (Parse(before) is not { } old || Parse(after) is not { } now)
        {
            return false;
        }

        var keys = old.Select(p => p.Key).Concat(now.Select(p => p.Key)).Where(k => k != member).Distinct();
        return keys.All(k => JsonNode.DeepEquals(old[k], now[k]));
    }

    /// <summary>The file's bytes, or null when it does not exist. A file too large to be Claude Code's is an I/O failure.</summary>
    private static byte[]? CurrentBytes(string path)
    {
        try
        {
            return AtomicFile.ReadSharedBytes(path, 64 * 1024 * 1024);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>UTF-8 text without a leading byte-order mark, as <see cref="CliStore"/> reads it.</summary>
    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    /// <summary>One top-level member of a JSON file, or null when the file or member is absent or not an object.</summary>
    private static JsonNode? Member(byte[]? file, string member)
    {
        try
        {
            return file is null ? null : (JsonNode.Parse(Decode(file)) as JsonObject)?[member];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static void WriteFile(string path, byte[] contents) => AtomicFile.WriteAllBytes(path, contents);
}
