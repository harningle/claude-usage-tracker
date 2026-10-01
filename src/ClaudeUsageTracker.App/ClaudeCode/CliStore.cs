using System.IO;
using System.Text;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.ClaudeCode;

namespace ClaudeUsageTracker.App.ClaudeCode;

/// <summary>
/// A Claude Code config directory: <c>.credentials.json</c> holds the login and <c>.claude.json</c> the
/// account metadata. Native Windows Claude Code 2.1.286 keeps the default directory's <c>.claude.json</c> in
/// the user profile and an override directory's inside that directory (docs/milestone-2.md).
/// </summary>
public sealed record CliConfigDirectory(string Path, bool IsDefault, string UserProfile)
{
    public string CredentialsFile => System.IO.Path.Combine(Path, ".credentials.json");

    /// <summary>Candidate <c>.claude.json</c> files, first existing wins. No fallback between directories.</summary>
    public IReadOnlyList<string> AccountFiles => IsDefault
        ? [System.IO.Path.Combine(UserProfile, ".claude.json"), System.IO.Path.Combine(Path, ".claude.json")]
        : [System.IO.Path.Combine(Path, ".claude.json")];

    /// <summary>"default", or the folder name, standing in for upstream's keychain service suffix.</summary>
    public string Shorthand => IsDefault ? "default" : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}

public enum CliLoginState
{
    /// <summary>No <c>.credentials.json</c>: Claude Code is not signed in here.</summary>
    NotSignedIn,

    /// <summary>The file exists but could not be read.</summary>
    Unreadable,

    /// <summary>The file has no usable <c>claudeAiOauth</c> login (or was read mid-write).</summary>
    Invalid,

    /// <summary>The access token has expired or expires within a minute.</summary>
    Expired,

    Usable,
}

/// <summary>One read-only observation of a config directory.</summary>
public sealed class CliSnapshot
{
    public required CliConfigDirectory Directory { get; init; }
    public required CliLoginState State { get; init; }
    public CliCredentials? Credentials { get; init; }
    public CliAccount? Account { get; init; }
    public required DateTimeOffset ReadAt { get; init; }

    public string Label => CliLoginLabels.DisplayLabel(
        Directory.Shorthand, Directory.Path, Account?.EmailAddress, Account?.OrganizationName, Credentials?.SubscriptionType);
}

/// <summary>
/// Read-only access to Claude Code's files. Never locks, writes or refreshes them: Milestone 2 only
/// mirrors CLI-owned credentials (plan.md section 3).
/// </summary>
public static class CliStore
{
    /// <summary>
    /// The known config directories: the default <c>%USERPROFILE%\.claude</c>, the app's own
    /// <c>CLAUDE_CONFIG_DIR</c> and directories already linked to profiles. This is an inventory of known
    /// places, not a claim that every login on disk was found.
    /// </summary>
    public static IReadOnlyList<CliConfigDirectory> Inventory(
        string userProfile, string? configDirOverride, IEnumerable<string> linkedDirectories)
    {
        var defaultPath = Normalise(Path.Combine(userProfile, ".claude"));
        var result = new List<CliConfigDirectory> { new(defaultPath, true, userProfile) };
        foreach (var candidate in new[] { configDirOverride }.Concat(linkedDirectories))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var path = Normalise(candidate);
            if (result.All(d => !string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new CliConfigDirectory(path, false, userProfile));
            }
        }

        return result;
    }

    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    /// <summary>Directory for a stored link, recognising the default directory by path.</summary>
    public static CliConfigDirectory ForLink(CliLink link, string userProfile) =>
        new(Normalise(link.ConfigDirectory), link.IsDefaultDirectory, userProfile);

    public static CliSnapshot Read(CliConfigDirectory directory, DateTimeOffset now)
    {
        string text;
        try
        {
            text = Decode(AtomicFile.ReadSharedBytes(directory.CredentialsFile));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CliSnapshot { Directory = directory, State = CliLoginState.NotSignedIn, ReadAt = now };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new CliSnapshot { Directory = directory, State = CliLoginState.Unreadable, ReadAt = now };
        }

        var credentials = CliCredentials.Parse(text);
        var account = ReadAccount(directory);
        var state = credentials is null ? CliLoginState.Invalid
            : credentials.IsExpiredOrExpiring(now) ? CliLoginState.Expired
            : CliLoginState.Usable;
        return new CliSnapshot
        {
            Directory = directory,
            State = state,
            Credentials = credentials,
            Account = account,
            ReadAt = now,
        };
    }

    /// <summary>UTF-8 without a leading byte-order mark, which editors such as Notepad may add.</summary>
    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

    /// <summary>
    /// <c>.claude.json</c> also holds Claude Code's project history and can grow to megabytes, so it is parsed
    /// again only when its size or write time changes.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, long Length, CliAccount? Account)> Accounts =
        new(StringComparer.OrdinalIgnoreCase);

    private const long MaxAccountFileBytes = 64 * 1024 * 1024;

    private static readonly TimeSpan StableAge = TimeSpan.FromSeconds(2);

    private static CliAccount? ReadAccount(CliConfigDirectory directory)
    {
        foreach (var file in directory.AccountFiles)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }

                if (Accounts.TryGetValue(file, out var cached) && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length)
                {
                    return cached.Account;
                }

                var account = CliAccount.FromClaudeJson(Decode(AtomicFile.ReadSharedBytes(file, MaxAccountFileBytes)));
                var after = new FileInfo(file);
                // Cache only a parsed account from a file that did not change during the read (so a torn read is
                // retried rather than remembered) and that was already a few seconds old, so that any later write
                // carries a newer timestamp even on a coarse file-system clock.
                if (account is not null && after.Exists && after.LastWriteTimeUtc == info.LastWriteTimeUtc && after.Length == info.Length
                    && DateTime.UtcNow - info.LastWriteTimeUtc > StableAge)
                {
                    Accounts[file] = (info.LastWriteTimeUtc, info.Length, account);
                }

                return account;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }
}
