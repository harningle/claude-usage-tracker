using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ClaudeUsageTracker.App.Storage;

/// <summary>A secret file exists but cannot be used; reported rather than silently replaced.</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One DPAPI <c>CurrentUser</c>-encrypted file per profile, in a directory only the current user can
/// open (plan.md section 2, "Secrets, refresh ownership and persistence"). Each write replaces the
/// whole file atomically and is read back and decrypted before it counts as saved.
/// </summary>
public sealed class SecretStore
{
    // File layout: magic, format version, then the DPAPI blob of the caller's plaintext.
    private static readonly byte[] Magic = "CUTS"u8.ToArray();
    private const byte FormatVersion = 1;

    private readonly string _directory;

    public SecretStore(string directory)
    {
        _directory = directory;
        CreateOwnerOnlyDirectory(directory);
    }

    public string PathFor(Guid profileId) => Path.Combine(_directory, $"{profileId:N}.secret");

    public bool Exists(Guid profileId) => File.Exists(PathFor(profileId));

    /// <summary>The profiles that have a secret file; none before the first one is saved.</summary>
    public IReadOnlyList<Guid> ProfileIds() =>
        Directory.Exists(_directory)
            ? [.. Directory.EnumerateFiles(_directory, "*.secret")
                .Select(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ? id : (Guid?)null)
                .OfType<Guid>()]
            : [];

    /// <summary>Encrypts and atomically replaces the profile's secret file, then verifies the read-back.</summary>
    public void Write(Guid profileId, byte[] plaintext)
    {
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy(profileId), DataProtectionScope.CurrentUser);
        var file = new byte[Magic.Length + 1 + protectedBytes.Length];
        Magic.CopyTo(file, 0);
        file[Magic.Length] = FormatVersion;
        protectedBytes.CopyTo(file, Magic.Length + 1);

        var path = PathFor(profileId);
        AtomicFile.WriteAllBytes(path, file);

        var readBack = Read(profileId)
            ?? throw new SecretStoreException("The saved credentials disappeared before they could be verified.");
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(readBack, plaintext))
            {
                throw new SecretStoreException("The saved credentials did not read back unchanged.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readBack);
        }
    }

    /// <summary>The decrypted plaintext, or null when the profile has no secret file.</summary>
    public byte[]? Read(Guid profileId)
    {
        var path = PathFor(profileId);
        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (file.Length <= Magic.Length + 1 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new SecretStoreException($"The credential file for this profile is not in a recognised format ({path}).");
        }

        if (file[Magic.Length] != FormatVersion)
        {
            throw new SecretStoreException(
                $"The credential file for this profile was written by a newer version (format {file[Magic.Length]}).");
        }

        try
        {
            return ProtectedData.Unprotect(file[(Magic.Length + 1)..], Entropy(profileId), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException e)
        {
            // DPAPI files only open for the Windows user, and on the machine, that wrote them.
            throw new SecretStoreException(
                "The credential file for this profile cannot be decrypted by this Windows user on this PC.", e);
        }
    }

    public void Delete(Guid profileId) => File.Delete(PathFor(profileId));

    /// <summary>Binds each blob to its profile, so a file copied to another profile's name does not decrypt.</summary>
    private static byte[] Entropy(Guid profileId) =>
        Encoding.UTF8.GetBytes($"ClaudeUsageTracker.Secret.v1|{profileId:N}");

    /// <summary>
    /// Creates the directory with a protected DACL granting only the current user, or tightens an
    /// existing one. Files created inside inherit it.
    /// </summary>
    private static void CreateOwnerOnlyDirectory(string directory)
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var info = new DirectoryInfo(directory);
        if (info.Exists)
        {
            info.SetAccessControl(security);
        }
        else
        {
            info.Parent?.Create();
            info.Create(security);
        }
    }
}
