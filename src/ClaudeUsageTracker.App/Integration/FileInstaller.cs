using System.IO;
using System.Security.Cryptography;

namespace ClaudeUsageTracker.App.Integration;

public enum InstallOutcome
{
    /// <summary>The new file is in place.</summary>
    Installed,

    /// <summary>The existing file could not be renamed aside (open handles, protection software); nothing changed.</summary>
    Deferred,

    /// <summary>The new file could not be put in place; the previous one was put back.</summary>
    Failed,

    /// <summary>Neither the new nor the previous file is in place. The record of the attempt is kept for repair.</summary>
    RepairRequired,
}

/// <param name="Retained">Where the previous file was renamed to, when there was one and the install went through.</param>
/// <param name="Problem">Why the outcome is not <see cref="InstallOutcome.Installed"/>.</param>
public sealed record InstallResult(InstallOutcome Outcome, string? Retained, string? Problem);

/// <summary>What recovery found for an interrupted install.</summary>
public enum RecoveryOutcome
{
    /// <summary>The new file is in place (the interruption came after the last rename, or recovery finished it).</summary>
    Completed,

    /// <summary>The previous file is in place, or there never was one; the new one was discarded.</summary>
    RolledBack,

    /// <summary>Nothing is in place and nothing is left to put there.</summary>
    Missing,
}

/// <summary>
/// Replaces an executable that may be running (plan.md section 2): a verified copy is staged beside it, the
/// existing file is renamed to a unique name, and the staged copy is renamed to the canonical name. A running
/// process keeps using the renamed image; new invocations get the new file. The two renames are not one
/// transaction, so the attempt is recorded first and <see cref="Recover"/> settles an interruption. For a
/// moment between the renames the canonical name does not exist, and an invocation at that moment fails.
/// </summary>
public static class FileInstaller
{
    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <param name="record">Saves the attempt before anything is renamed, and null once nothing is left to recover.</param>
    public static InstallResult Install(string source, string canonical, PendingInstall attempt, Action<PendingInstall?> record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        var tag = Guid.NewGuid().ToString("N")[..8];
        attempt.Canonical = canonical;
        attempt.Staged = $"{canonical}.staged-{tag}";
        attempt.Retained = File.Exists(canonical) ? $"{canonical}.old-{tag}" : null;
        attempt.Sha256 = Sha256Of(source);

        File.Copy(source, attempt.Staged, overwrite: false);
        if (Sha256Of(attempt.Staged) != attempt.Sha256)
        {
            File.Delete(attempt.Staged);
            throw new IOException($"The copy of {Path.GetFileName(source)} did not read back unchanged.");
        }

        record(attempt);
        if (attempt.Retained is not null)
        {
            try
            {
                File.Move(canonical, attempt.Retained);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                File.Delete(attempt.Staged);
                record(null);
                return new InstallResult(InstallOutcome.Deferred, null, e.Message);
            }
        }

        try
        {
            File.Move(attempt.Staged, canonical);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (attempt.Retained is null)
            {
                TryDelete(attempt.Staged);
                record(null);
                return new InstallResult(InstallOutcome.Failed, null, e.Message);
            }

            try
            {
                File.Move(attempt.Retained, canonical);
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                return new InstallResult(InstallOutcome.RepairRequired, null, $"{e.Message} {restore.Message}");
            }

            TryDelete(attempt.Staged);
            record(null);
            return new InstallResult(InstallOutcome.Failed, null, e.Message);
        }

        return new InstallResult(InstallOutcome.Installed, attempt.Retained, null);
    }

    /// <summary>
    /// Settles an install that was interrupted after it was recorded, from what is on disk. The caller records
    /// the result and forgets the attempt.
    /// </summary>
    public static RecoveryOutcome Recover(PendingInstall attempt)
    {
        var staged = File.Exists(attempt.Staged);
        if (File.Exists(attempt.Canonical))
        {
            if (staged)
            {
                // Interrupted before the existing file was renamed, or after it was put back.
                File.Delete(attempt.Staged);
                return RecoveryOutcome.RolledBack;
            }

            return Sha256Of(attempt.Canonical) == attempt.Sha256 ? RecoveryOutcome.Completed : RecoveryOutcome.RolledBack;
        }

        // Interrupted between the two renames: finish with the verified copy, else put the previous file back.
        if (staged && Sha256Of(attempt.Staged) == attempt.Sha256)
        {
            File.Move(attempt.Staged, attempt.Canonical);
            return RecoveryOutcome.Completed;
        }

        if (staged)
        {
            File.Delete(attempt.Staged);
        }

        if (attempt.Retained is not null && File.Exists(attempt.Retained))
        {
            File.Move(attempt.Retained, attempt.Canonical);
            return RecoveryOutcome.RolledBack;
        }

        return RecoveryOutcome.Missing;
    }

    /// <summary>Deletes a file if it can be deleted now; false while a process still runs from it or holds it open.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
