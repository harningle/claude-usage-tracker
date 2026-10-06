using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.ClaudeCode;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>
/// A profile's copy of a Claude Code login. A class rather than a record so no generated
/// <c>ToString</c> can print the tokens.
/// </summary>
/// <param name="oauthJson">The <c>claudeAiOauth</c> object as compact JSON.</param>
/// <param name="pending">
/// Folders that still hold the token a renewal used up and have to be given this copy's login. A newer login
/// of the same account that Claude Code writes meanwhile replaces the copy's login and keeps the record.
/// </param>
/// <param name="oauthAccountJson">
/// The account's <c>oauthAccount</c> object from <c>.claude.json</c>, as last seen with this login. Not secret, but
/// kept with the login it describes so an account switch writes both together.
/// </param>
public sealed class CliSecret(
    string oauthJson, CredentialOwnership ownership, string sourceConfigDirectory, DateTimeOffset savedAt, PendingWriteBack? pending = null,
    string? oauthAccountJson = null)
{
    public string OAuthJson { get; } = oauthJson;

    public CredentialOwnership Ownership { get; } = ownership;

    public string SourceConfigDirectory { get; } = sourceConfigDirectory;

    public DateTimeOffset SavedAt { get; } = savedAt;

    public PendingWriteBack? Pending { get; } = pending;

    public string? OAuthAccountJson { get; } = oauthAccountJson;

    /// <summary>The login as a credentials document, in the shape of <c>.credentials.json</c>.</summary>
    public string CredentialsJson => "{\"claudeAiOauth\":" + OAuthJson + "}";
}

/// <summary>
/// A renewal whose result is saved here but maybe not yet everywhere that held the old login (plan.md section 2,
/// "pending reconciliation"): Claude Code folders after a takeover, or other profiles holding the same pasted Codex
/// credentials.
/// </summary>
/// <param name="consumedRefreshToken">The refresh token the renewal used up; a place still holding it needs the new login.</param>
/// <param name="targets">Config directories, or for Codex the profile IDs, still to be written.</param>
public sealed class PendingWriteBack(string consumedRefreshToken, DateTimeOffset createdAt, IReadOnlyList<string> targets)
{
    public string ConsumedRefreshToken { get; } = consumedRefreshToken;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public IReadOnlyList<string> Targets { get; } = targets;
}

/// <summary>An Anthropic Console session cookie, with the expiry the browser reported.</summary>
public sealed class ConsoleSecret(string sessionKey, DateTimeOffset? expires)
{
    public string SessionKey { get; } = sessionKey;

    public DateTimeOffset? Expires { get; } = expires;
}

/// <summary>
/// Everything secret about one profile, as one versioned bundle that is encrypted and replaced as a whole
/// (plan.md section 2, "Secrets, refresh ownership and persistence"). claude.ai browser sessions are the
/// stated exception: they stay in the profile's WebView2 folder. Members this version does not know are
/// kept, so a file written by a newer build of the same format survives a save.
/// </summary>
public sealed class ProfileSecrets
{
    public const int CurrentVersion = 2;

    private readonly JsonObject _root;

    private ProfileSecrets(JsonObject root) => _root = root;

    public static ProfileSecrets Empty() => new(new JsonObject { ["version"] = CurrentVersion });

    /// <summary>Parses a decrypted bundle, including the Milestone 2 format (version 1: one Claude Code login).</summary>
    /// <exception cref="SecretStoreException">The bundle is damaged or from a newer format.</exception>
    public static ProfileSecrets Parse(ReadOnlySpan<byte> plaintext)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(plaintext) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new SecretStoreException("The encrypted credentials of this profile are damaged.", e);
        }

        var version = root?["version"] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
        if (root is null || version < 1)
        {
            throw new SecretStoreException("The encrypted credentials of this profile are damaged.");
        }

        if (version > CurrentVersion)
        {
            throw new SecretStoreException(
                $"The encrypted credentials of this profile were written by a newer version (bundle format {version}).");
        }

        if (version == 1)
        {
            // Milestone 2: {"version":1,"kind":"claudeCliOAuth","sourceConfigDirectory","mirroredAt","claudeAiOauth"}.
            if (String(root, "kind") != "claudeCliOAuth" || root["claudeAiOauth"] is not JsonObject oauth)
            {
                throw new SecretStoreException("The encrypted copy of this profile's login is not a Claude Code login copy.");
            }

            var upgraded = Empty();
            upgraded.Cli = new CliSecret(
                oauth.ToJsonString(), CredentialOwnership.CliLinked, String(root, "sourceConfigDirectory") ?? "",
                Time(root["mirroredAt"]) ?? DateTimeOffset.UnixEpoch);
            return upgraded;
        }

        return new ProfileSecrets(root);
    }

    public byte[] Serialise() => Encoding.UTF8.GetBytes(_root.ToJsonString());

    /// <summary>Whether no credential is left, in which case the file is removed rather than kept empty.</summary>
    public bool IsEmpty => _root.Count == 1 && _root.ContainsKey("version");

    public CliSecret? Cli
    {
        get
        {
            if (_root["claudeCli"] is not JsonObject cli || cli["claudeAiOauth"] is not JsonObject oauth)
            {
                return null;
            }

            var pending = cli["pendingWriteBack"] is JsonObject p ? Pending(p) : null;
            return new CliSecret(
                oauth.ToJsonString(),
                String(cli, "ownership") == "appOwned" ? CredentialOwnership.AppOwned : CredentialOwnership.CliLinked,
                String(cli, "sourceConfigDirectory") ?? "",
                Time(cli["savedAt"]) ?? DateTimeOffset.UnixEpoch,
                pending,
                cli["oauthAccount"] is JsonObject account ? account.ToJsonString() : null);
        }

        set
        {
            if (value is null)
            {
                _root.Remove("claudeCli");
                return;
            }

            var cli = new JsonObject
            {
                ["ownership"] = value.Ownership == CredentialOwnership.AppOwned ? "appOwned" : "cliLinked",
                ["sourceConfigDirectory"] = value.SourceConfigDirectory,
                ["savedAt"] = value.SavedAt.ToString("O", CultureInfo.InvariantCulture),
                ["claudeAiOauth"] = JsonNode.Parse(value.OAuthJson),
            };
            if (value.OAuthAccountJson is { } account)
            {
                cli["oauthAccount"] = JsonNode.Parse(account);
            }

            if (value.Pending is { } pending)
            {
                cli["pendingWriteBack"] = Json(pending);
            }

            _root["claudeCli"] = cli;
        }
    }

    public ConsoleSecret? Console
    {
        get => _root["console"] is JsonObject console && String(console, "sessionKey") is { Length: > 0 } key
            ? new ConsoleSecret(key, Time(console["expires"]))
            : null;

        set
        {
            if (value is null)
            {
                _root.Remove("console");
                return;
            }

            var console = new JsonObject { ["sessionKey"] = value.SessionKey };
            if (value.Expires is { } expires)
            {
                console["expires"] = expires.ToString("O", CultureInfo.InvariantCulture);
            }

            _root["console"] = console;
        }
    }

    /// <summary>
    /// Codex credentials the user pasted, in the shape of the Codex CLI's <c>auth.json</c>. Setting them drops
    /// <see cref="CodexPending"/>, which belonged to the credentials they replace.
    /// </summary>
    public string? CodexAuthJson
    {
        get => _root["codex"] is JsonObject codex ? String(codex, "authJson") : null;

        set
        {
            if (value is null)
            {
                _root.Remove("codex");
            }
            else
            {
                _root["codex"] = new JsonObject { ["authJson"] = value };
            }
        }
    }

    /// <summary>
    /// A renewal of the pasted credentials that other profiles held too: the token it used up and the profiles that may
    /// still hold it. Kept with <see cref="CodexAuthJson"/>, which is that renewal, so a restart cannot lose it.
    /// </summary>
    public PendingWriteBack? CodexPending
    {
        get => _root["codex"] is JsonObject codex && codex["pendingWriteBack"] is JsonObject p ? Pending(p) : null;

        set
        {
            if (_root["codex"] is not JsonObject codex)
            {
                throw new InvalidOperationException("A pending renewal is kept only with the credentials it renewed.");
            }

            if (value is null)
            {
                codex.Remove("pendingWriteBack");
            }
            else
            {
                codex["pendingWriteBack"] = Json(value);
            }
        }
    }

    private static PendingWriteBack? Pending(JsonObject pending) =>
        String(pending, "consumedRefreshToken") is { } consumed
            ? new PendingWriteBack(
                consumed,
                Time(pending["createdAt"]) ?? DateTimeOffset.UnixEpoch,
                (pending["targets"] as JsonArray)?.Select(t => t is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList() ?? [])
            : null;

    private static JsonObject Json(PendingWriteBack pending) => new()
    {
        ["consumedRefreshToken"] = pending.ConsumedRefreshToken,
        ["createdAt"] = pending.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        ["targets"] = new JsonArray([.. pending.Targets.Select(t => (JsonNode)JsonValue.Create(t))]),
    };

    private static string? String(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// Loads and saves <see cref="ProfileSecrets"/> through the DPAPI <see cref="SecretStore"/>. Every write
/// replaces the whole file and is verified by reading it back. Callers that change a bundle hold the
/// app-wide <see cref="CredentialLock"/>, so two changes to the same bundle never interleave.
/// </summary>
public sealed class ProfileSecretStore(SecretStore store)
{
    /// <summary>The profile's bundle, or an empty one when it has no secret file.</summary>
    /// <exception cref="SecretStoreException">The file exists but cannot be used; it is left untouched.</exception>
    public ProfileSecrets Load(Guid profileId)
    {
        var plaintext = store.Read(profileId);
        if (plaintext is null)
        {
            return ProfileSecrets.Empty();
        }

        try
        {
            return ProfileSecrets.Parse(plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    /// <summary>Saves the bundle, or removes the file when nothing secret is left.</summary>
    public void Save(Guid profileId, ProfileSecrets secrets)
    {
        if (secrets.IsEmpty)
        {
            store.Delete(profileId);
            return;
        }

        var plaintext = secrets.Serialise();
        try
        {
            store.Write(profileId, plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    public bool Exists(Guid profileId) => store.Exists(profileId);

    public IReadOnlyList<Guid> ProfileIds() => store.ProfileIds();

    public void Delete(Guid profileId) => store.Delete(profileId);
}
