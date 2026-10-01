using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageTracker.App.Tray;

/// <summary>
/// Tray icon GUIDs for unsigned builds (plan.md section 2, "Windows tray, notifications and visual
/// design"): a name-based (SHA-1, version 5) UUID of a product namespace, the normalised absolute
/// executable path and the logical icon slot. A moved build therefore gets a new identity and loses
/// Explorer's placement preference; two copies never share an entry.
/// </summary>
public static class TrayIdentity
{
    // Shared with the Milestone 1 spike, so the derivation measured there is the one shipped.
    private static readonly Guid ProductNamespace = new("6f1c3f7e-2b8a-4d5e-9c41-0a7d5e3b9f12");

    /// <param name="slot">Stable profile ID plus metric, never a display name.</param>
    public static Guid For(string executablePath, string slot)
    {
        var normalised = Path.GetFullPath(executablePath).ToLowerInvariant();
        var name = Encoding.UTF8.GetBytes(normalised + "|" + slot);
        var hash = SHA1.HashData([.. ProductNamespace.ToByteArray(bigEndian: true), .. name]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    public static string Slot(Guid profileId, string metric) => $"profile-{profileId:N}|{metric}";
}
