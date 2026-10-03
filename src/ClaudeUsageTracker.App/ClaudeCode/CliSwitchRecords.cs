using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.App.ClaudeCode;

/// <summary>An account switch that has started writing a Claude Code folder and not yet been verified. Non-secret.</summary>
/// <param name="Folder">The config directory being switched.</param>
/// <param name="TargetProfileId">The profile whose login is being written there.</param>
/// <param name="BackupId">The encrypted backup of the folder's files taken just before.</param>
public sealed record CliSwitchJournal(string Folder, Guid TargetProfileId, Guid BackupId, DateTimeOffset StartedAt);

/// <summary>
/// The two Claude Code files of a folder as they were before a switch. They hold tokens, so a backup is encrypted
/// like a profile's secrets. A class rather than a record so no generated <c>ToString</c> can print them.
/// </summary>
/// <param name="credentials">The bytes of <c>.credentials.json</c>, or null when it did not exist.</param>
/// <param name="accountFile">The <c>.claude.json</c> the folder uses, or null when none existed.</param>
public sealed class CliSwitchBackup(
    string folder, DateTimeOffset createdAt, string credentialsFile, byte[]? credentials, string? accountFile, byte[]? account)
{
    public string Folder { get; } = folder;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public string CredentialsFile { get; } = credentialsFile;

    public byte[]? Credentials { get; } = credentials;

    public string? AccountFile { get; } = accountFile;

    public byte[]? Account { get; } = account;
}

/// <summary>
/// The journal of a switch in progress (<c>cli-switch.json</c>) and the encrypted backups taken before each switch
/// (<c>secrets\backups</c>). While a journal exists, nothing is mirrored from its folder and no login is renewed:
/// the folder may hold one account's tokens beside another's account record. A journal that cannot be read blocks
/// every folder, because which folder it names is unknown. The newest <see cref="KeptBackups"/> backups are kept so a
/// switch can be undone by hand; they are never restored automatically over another writer.
/// </summary>
public sealed class CliSwitchRecords(string journalFile, SecretStore backups)
{
    public const int KeptBackups = 5;

    /// <summary>The journal, or null when no switch is in progress.</summary>
    /// <exception cref="SettingsStoreException">The journal exists but cannot be read.</exception>
    public CliSwitchJournal? ReadJournal()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(journalFile);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SettingsStoreException($"The record of a Claude Code account switch could not be read: {journalFile} ({e.Message})", e);
        }

        try
        {
            return JsonSerializer.Deserialize<CliSwitchJournal>(bytes, SettingsStore.JsonOptions)
                   ?? throw new JsonException("The record is empty.");
        }
        catch (JsonException e)
        {
            throw new SettingsStoreException($"The record of a Claude Code account switch is damaged: {journalFile} ({e.Message})", e);
        }
    }

    /// <summary>Whether a switch in progress (or an unreadable record of one) concerns this folder; null asks about any folder.</summary>
    public bool Blocks(string? folder)
    {
        try
        {
            return ReadJournal() is { } journal
                   && (folder is null || string.Equals(CliStore.Normalise(journal.Folder), CliStore.Normalise(folder), StringComparison.OrdinalIgnoreCase));
        }
        catch (SettingsStoreException)
        {
            return true;
        }
    }

    public void Begin(CliSwitchJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalFile)!);
        AtomicFile.WriteAllBytes(journalFile, JsonSerializer.SerializeToUtf8Bytes(journal, SettingsStore.JsonOptions));
    }

    public void End() => File.Delete(journalFile);

    /// <summary>Saves an encrypted backup and drops all but the newest <see cref="KeptBackups"/>. Returns its ID.</summary>
    public Guid SaveBackup(CliSwitchBackup backup)
    {
        var id = Guid.CreateVersion7(backup.CreatedAt);
        var record = new JsonObject
        {
            ["folder"] = backup.Folder,
            ["createdAt"] = backup.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["credentialsFile"] = backup.CredentialsFile,
            ["credentials"] = backup.Credentials is null ? null : Convert.ToBase64String(backup.Credentials),
            ["accountFile"] = backup.AccountFile,
            ["account"] = backup.Account is null ? null : Convert.ToBase64String(backup.Account),
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record);
        try
        {
            backups.Write(id, plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }

        // Version 7 IDs sort by creation time in their text form.
        foreach (var old in backups.ProfileIds().OrderByDescending(b => b.ToString("D"), StringComparer.Ordinal).Skip(KeptBackups))
        {
            try
            {
                backups.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Removed at a later switch.
            }
        }

        return id;
    }

    /// <summary>A backup, or null when it no longer exists.</summary>
    /// <exception cref="SecretStoreException">It exists but cannot be decrypted.</exception>
    public CliSwitchBackup? LoadBackup(Guid id)
    {
        var plaintext = backups.Read(id);
        if (plaintext is null)
        {
            return null;
        }

        try
        {
            var record = JsonNode.Parse(plaintext) as JsonObject
                         ?? throw new SecretStoreException("A Claude Code backup is damaged.");
            return new CliSwitchBackup(
                (string?)record["folder"] ?? "",
                DateTimeOffset.Parse((string?)record["createdAt"] ?? "", CultureInfo.InvariantCulture),
                (string?)record["credentialsFile"] ?? "",
                record["credentials"] is JsonValue c ? Convert.FromBase64String((string)c!) : null,
                (string?)record["accountFile"],
                record["account"] is JsonValue a ? Convert.FromBase64String((string)a!) : null);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new SecretStoreException("A Claude Code backup is damaged.", e);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }
}
