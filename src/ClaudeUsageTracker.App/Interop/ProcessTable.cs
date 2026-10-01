using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ClaudeUsageTracker.App.Interop;

/// <summary>
/// One process from a snapshot. The details beyond the IDs and image name are null when Windows would
/// not let this process read them; callers treat that as unknown, never as absent.
/// </summary>
public sealed record ProcessEntry(
    int Pid, int ParentPid, string ImageName, bool? OwnedByCurrentUser, string? ExecutablePath, DateTime? CreatedUtc);

/// <summary>
/// A <c>CreateToolhelp32Snapshot</c> process snapshot (plan.md section 2, "CLI files, safe switching and
/// launchers"). It reads no process's environment or memory: only IDs, image names and, with
/// limited-query access, the owner, executable path and creation time.
/// </summary>
internal static class ProcessTable
{
    private const uint SnapProcess = 0x2;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x8;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// Every process whose image name is <paramref name="imageName"/>, with details, followed by their
    /// parents, with details. Other processes are left out.
    /// </summary>
    public static IReadOnlyList<ProcessEntry> Capture(string imageName)
    {
        var all = new List<(int Pid, int ParentPid, string Name)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The process snapshot could not be taken.");
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                all.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var matches = all.Where(p => string.Equals(p.Name, imageName, StringComparison.OrdinalIgnoreCase)).ToList();
        var parents = matches.Select(m => m.ParentPid).ToHashSet();
        var currentUser = WindowsIdentity.GetCurrent().User;
        return [.. matches.Concat(all.Where(p => parents.Contains(p.Pid) && !matches.Contains(p))).Select(p => Describe(p, currentUser))];
    }

    private static ProcessEntry Describe((int Pid, int ParentPid, string Name) process, SecurityIdentifier? currentUser)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)process.Pid);
        if (handle == IntPtr.Zero)
        {
            return new ProcessEntry(process.Pid, process.ParentPid, process.Name, null, null, null);
        }

        try
        {
            var capacity = 1024u;
            var path = new StringBuilder((int)capacity);
            var executable = QueryFullProcessImageNameW(handle, 0, path, ref capacity) ? path.ToString(0, (int)capacity) : null;
            DateTime? created = GetProcessTimes(handle, out var creation, out _, out _, out _) ? DateTime.FromFileTimeUtc(creation) : null;
            bool? owned = null;
            if (currentUser is not null && OpenProcessToken(handle, TokenQuery, out var token))
            {
                try
                {
                    using var identity = new WindowsIdentity(token);
                    owned = identity.User is { } user ? user == currentUser : null;
                }
                finally
                {
                    CloseHandle(token);
                }
            }

            return new ProcessEntry(process.Pid, process.ParentPid, process.Name, owned, executable, created);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
