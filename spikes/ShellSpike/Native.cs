using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ShellSpike;

internal static class Native
{
    public const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    public const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifGuid = 0x20, NifShowTip = 0x80;
    public const uint NotifyIconVersion4 = 4;
    public const int CallbackMessage = 0x0400 + 1; // WM_USER + 1
    public const int NinSelect = 0x0400, NinKeySelect = 0x0401, WmContextMenu = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Right - Left}x{Bottom - Top} px";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll")]
    public static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegisterWindowMessage(string name);

    /// <summary>
    /// The plan's unsigned-build tray identity: a name-based (SHA-1, version 5) UUID from a product
    /// namespace, the normalised absolute executable path and the logical icon slot.
    /// </summary>
    public static Guid TrayGuid(string executablePath, string slot)
    {
        var product = new Guid("6f1c3f7e-2b8a-4d5e-9c41-0a7d5e3b9f12");
        var normalised = Path.GetFullPath(executablePath).ToLowerInvariant();
        var name = Encoding.UTF8.GetBytes(normalised + "|" + slot);
        var hash = SHA1.HashData([.. product.ToByteArray(bigEndian: true), .. name]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
