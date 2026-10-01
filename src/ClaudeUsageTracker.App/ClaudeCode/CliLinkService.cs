using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.ClaudeCode;

namespace ClaudeUsageTracker.App.ClaudeCode;

public enum LinkStatus
{
    /// <summary>The linked account is signed in with a usable token.</summary>
    Ready,

    /// <summary>
    /// The linked account's token has expired. Claude Code refreshes it on next use; the app does not
    /// rotate a CLI-owned token in Milestone 2, so it waits and re-reads.
    /// </summary>
    WaitingForCliRefresh,

    NotSignedIn,
    Unreadable,
    Invalid,

    /// <summary>Claude Code is now signed in to a different account than the one linked.</summary>
    AccountChanged,

    /// <summary>Neither account identity nor token lineage can show that this is the linked account.</summary>
    AccountUnverified,
}

/// <param name="Credentials">Only set for <see cref="LinkStatus.Ready"/>.</param>
/// <param name="MirrorError">A failure to update the app's encrypted copy; polling may continue.</param>
public sealed record LinkCheck(LinkStatus Status, CliSnapshot Snapshot, CliCredentials? Credentials, string? MirrorError);

/// <summary>
/// Links profiles to Claude Code config directories and mirrors the CLI-owned login into the
/// profile's DPAPI file. The mirror is the CLI-linked app copy (plan.md section 2); it is never used to
/// refresh, and polling always uses the CLI's own current file.
/// </summary>
public sealed class CliLinkService(SecretStore secrets, TimeProvider time, string userProfile)
{
    private const int BundleVersion = 1;
    private const string BundleKind = "claudeCliOAuth";

    /// <summary>A torn read during a CLI write is retried once after this delay.</summary>
    internal static readonly TimeSpan RereadDelay = TimeSpan.FromMilliseconds(250);

    // The last mirrored login per profile, so an unchanged file is not re-encrypted every poll.
    private readonly Dictionary<Guid, string> _mirrored = [];
    private readonly Lock _gate = new();

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

    /// <summary>Encrypts the snapshot's <c>claudeAiOauth</c> login into the profile's file and verifies it.</summary>
    public void WriteMirror(Guid profileId, CliSnapshot snapshot)
    {
        lock (_gate)
        {
            WriteMirrorLocked(profileId, snapshot);
        }
    }

    /// <summary>
    /// Deletes the profile's encrypted copy. Writes and deletes are serialised, so a check that started
    /// before an unlink cannot recreate the file after it (callers cancel such checks first).
    /// </summary>
    public void DeleteMirror(Guid profileId)
    {
        lock (_gate)
        {
            secrets.Delete(profileId);
            _mirrored.Remove(profileId);
        }
    }

    /// <summary>
    /// Reads the linked directory and decides whether it still holds the linked account. A usable or
    /// expired login of the same account updates the mirror when it changed, which captures Claude
    /// Code's own token rotations. A copy that cannot be read is reported and left untouched.
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

        var status = snapshot.State switch
        {
            CliLoginState.NotSignedIn => LinkStatus.NotSignedIn,
            CliLoginState.Unreadable => LinkStatus.Unreadable,
            CliLoginState.Invalid => LinkStatus.Invalid,
            _ => (LinkStatus?)null,
        };
        if (status is { } failed)
        {
            return new LinkCheck(failed, snapshot, null, null);
        }

        var (mirrored, mirrorError) = ReadMirror(profileId);
        var mirroredRefresh = RefreshTokenOf(mirrored);
        if (mirroredRefresh is not null && mirroredRefresh != snapshot.Credentials!.RefreshToken)
        {
            // The token lineage changed: Claude Code refreshed, or a /login is writing .credentials.json and
            // .claude.json one after the other. Trust the files only once two reads agree.
            await Task.Delay(RereadDelay, time, cancellation).ConfigureAwait(false);
            var again = Read(directory);
            if (again.Credentials?.RawJson != snapshot.Credentials.RawJson || again.Account?.Identity != snapshot.Account?.Identity)
            {
                return new LinkCheck(LinkStatus.Unreadable, again, null, mirrorError);
            }
        }

        var credentials = snapshot.Credentials!;
        var match = CliLoginLabels.Compare(link.Identity, snapshot.Account?.Identity, mirroredRefresh, credentials.RefreshToken);
        if (match == CliAccountMatch.DifferentAccount)
        {
            return new LinkCheck(LinkStatus.AccountChanged, snapshot, null, mirrorError);
        }

        if (match == CliAccountMatch.Unresolved)
        {
            return new LinkCheck(LinkStatus.AccountUnverified, snapshot, null, mirrorError);
        }

        if (mirrorError is null && OAuthObject(credentials.RawJson) is { } oauth && oauth != mirrored)
        {
            lock (_gate)
            {
                // Checked under the same lock as DeleteMirror: a superseded check never writes.
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    WriteMirrorLocked(profileId, snapshot);
                }
                catch (Exception e) when (e is SecretStoreException or System.IO.IOException or UnauthorizedAccessException
                                              or System.Security.Cryptography.CryptographicException)
                {
                    mirrorError = e.Message;
                }
            }
        }

        return snapshot.State == CliLoginState.Expired
            ? new LinkCheck(LinkStatus.WaitingForCliRefresh, snapshot, null, mirrorError)
            : new LinkCheck(LinkStatus.Ready, snapshot, credentials, mirrorError);
    }

    private void WriteMirrorLocked(Guid profileId, CliSnapshot snapshot)
    {
        var oauth = OAuthObject(snapshot.Credentials?.RawJson)
                    ?? throw new InvalidOperationException("The snapshot has no claudeAiOauth login to mirror.");
        var bundle = new JsonObject
        {
            ["version"] = BundleVersion,
            ["kind"] = BundleKind,
            ["sourceConfigDirectory"] = snapshot.Directory.Path,
            ["mirroredAt"] = time.GetUtcNow().ToString("O"),
            ["claudeAiOauth"] = JsonNode.Parse(oauth),
        };
        secrets.Write(profileId, Encoding.UTF8.GetBytes(bundle.ToJsonString()));
        _mirrored[profileId] = oauth;
    }

    /// <summary>
    /// The mirrored login, or an error when the file exists but cannot be used (another Windows user's,
    /// a newer format, unreadable). No file is neither.
    /// </summary>
    private (string? Oauth, string? Error) ReadMirror(Guid profileId)
    {
        lock (_gate)
        {
            if (_mirrored.TryGetValue(profileId, out var cached))
            {
                return (cached, null);
            }
        }

        byte[]? plaintext;
        try
        {
            plaintext = secrets.Read(profileId);
        }
        catch (Exception e) when (e is SecretStoreException or System.IO.IOException or UnauthorizedAccessException)
        {
            return (null, e.Message);
        }

        if (plaintext is null)
        {
            return (null, null);
        }

        try
        {
            var root = JsonNode.Parse(plaintext) as JsonObject;
            if (root?["kind"]?.GetValue<string>() != BundleKind || root["claudeAiOauth"] is not JsonObject oauthNode)
            {
                return (null, "The encrypted copy of this profile's login is not a Claude Code login copy.");
            }

            var oauth = oauthNode.ToJsonString();
            lock (_gate)
            {
                _mirrored[profileId] = oauth;
            }

            return (oauth, null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return (null, "The encrypted copy of this profile's login is damaged.");
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static string? RefreshTokenOf(string? oauth) =>
        oauth is not null
        && JsonNode.Parse(oauth) is JsonObject node
        && node["refreshToken"] is JsonValue value
        && value.TryGetValue(out string? token)
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
