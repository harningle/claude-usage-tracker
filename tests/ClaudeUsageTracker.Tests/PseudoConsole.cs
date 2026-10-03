using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ClaudeUsageTracker.Tests;

/// <summary>
/// A program run inside a Windows pseudo console, as a terminal runs a shell: what is written to it arrives
/// as typed keys, Ctrl+C included, and what it shows is collected as text. It lets a test press Ctrl+C in a
/// shell that waits for a launcher, without touching the console of the test run or of anything else.
/// </summary>
internal sealed partial class PseudoConsole : IDisposable
{
    private readonly IntPtr _console;
    private readonly IntPtr _process;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly StringBuilder _text = new();
    private readonly Thread _reader;

    private PseudoConsole(IntPtr console, IntPtr process, int processId, FileStream input, FileStream output)
    {
        _console = console;
        _process = process;
        ProcessId = processId;
        _input = input;
        _output = output;
        _reader = new Thread(Read) { IsBackground = true };
        _reader.Start();
    }

    public int ProcessId { get; }

    /// <summary>Everything shown so far, without the terminal's control sequences.</summary>
    public string Text
    {
        get
        {
            lock (_text)
            {
                return ControlSequence().Replace(_text.ToString(), "");
            }
        }
    }

    /// <param name="environment">The whole environment of the program.</param>
    public static PseudoConsole Start(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        if (!CreatePipe(out var inputRead, out var inputWrite, ref security, 0) || !CreatePipe(out var outputRead, out var outputWrite, ref security, 0))
        {
            throw new Win32Exception();
        }

        var result = CreatePseudoConsole(new Coord { X = 200, Y = 50 }, inputRead, outputWrite, 0, out var console);
        if (result != 0)
        {
            throw new Win32Exception(result);
        }

        // The pseudo console holds its own copies of these ends.
        inputRead.Dispose();
        outputWrite.Dispose();

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception();
            }

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            // With no standard handles of its own named here, the program would write to those of the test run when
            // they are redirected; naming none makes it use the pseudo console's.
            startup.StartupInfo.Flags = StartfUseStdHandles;
            var block = new StringBuilder();
            foreach (var (name, value) in environment.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
            {
                block.Append(name).Append('=').Append(value).Append('\0');
            }

            block.Append('\0');
            // A test run started in its own process group has Ctrl+C switched off, and every program it starts would
            // inherit that. A terminal starts its shell with Ctrl+C working, so that is what is handed on here.
            SetConsoleCtrlHandler(IntPtr.Zero, false);

            // The block has a separator after every variable, so it is passed as memory, not as one string.
            var environmentBlock = Marshal.StringToHGlobalUni(block.ToString());
            ProcessInformation information;
            try
            {
                if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environmentBlock, workingDirectory, ref startup, out information))
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(environmentBlock);
            }

            CloseHandle(information.Thread);
            return new PseudoConsole(
                console, information.Process, information.ProcessId,
                new FileStream(inputWrite, FileAccess.Write), new FileStream(outputRead, FileAccess.Read));
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>Types text into the console. "\r" is Enter and "\x03" is Ctrl+C.</summary>
    public void Type(string text)
    {
        _input.Write(Encoding.UTF8.GetBytes(text));
        _input.Flush();
    }

    public bool WaitForText(string text, TimeSpan timeout) => SpinWait.SpinUntil(() => Text.Contains(text, StringComparison.Ordinal), timeout);

    /// <summary>The program's exit code, or null when it has not ended within the time.</summary>
    public int? WaitForExit(TimeSpan timeout) =>
        WaitForSingleObject(_process, (uint)timeout.TotalMilliseconds) == 0 && GetExitCodeProcess(_process, out var code) ? (int)code : null;

    public void Dispose()
    {
        // Closing the pseudo console ends what still runs in it.
        ClosePseudoConsole(_console);
        _input.Dispose();
        _reader.Join(TimeSpan.FromSeconds(5));
        _output.Dispose();
        CloseHandle(_process);
    }

    private void Read()
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[8192];
        try
        {
            int read;
            while ((read = _output.Read(buffer)) > 0)
            {
                var count = decoder.GetChars(buffer, 0, read, characters, 0);
                lock (_text)
                {
                    _text.Append(characters, 0, count);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    [GeneratedRegex(@"\e\][^\a\e]*(?:\a|\e\\)|\e\[[0-9;?]*[ -/]*[@-~]|\e[=>]")]
    private static partial Regex ControlSequence();

    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x100;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
