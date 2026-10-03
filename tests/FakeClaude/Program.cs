// A stand-in for the native claude.exe, used only by the launcher tests. It is told what to do through
// environment variables, so that every command-line argument is free to be checked as it arrived:
//   FAKE_CLAUDE_REPORT       a file to which it appends what it was started with and each interruption
//   FAKE_CLAUDE_EXIT         its exit code (default 0)
//   FAKE_CLAUDE_INTERRUPTS   how many Ctrl+C or Ctrl+Break it waits for before it exits (default 0)
//   FAKE_CLAUDE_RAISE_BREAK  raise Ctrl+Break on its own console once it is ready
using System.Runtime.InteropServices;
using System.Text.Json;

var report = Environment.GetEnvironmentVariable("FAKE_CLAUDE_REPORT");
var exitCode = int.TryParse(Environment.GetEnvironmentVariable("FAKE_CLAUDE_EXIT"), out var code) ? code : 0;
var wanted = int.TryParse(Environment.GetEnvironmentVariable("FAKE_CLAUDE_INTERRUPTS"), out var count) ? count : 0;

void Report(object entry)
{
    if (report is not null)
    {
        lock (args)
        {
            File.AppendAllText(report, JsonSerializer.Serialize(entry) + "\n");
        }
    }
}

using var done = new ManualResetEventSlim(wanted == 0);
var seen = 0;
Console.CancelKeyPress += (_, e) =>
{
    // Like Claude Code, it handles the interruption itself and keeps running.
    e.Cancel = true;
    Report(new { interrupt = e.SpecialKey.ToString() });
    if (Interlocked.Increment(ref seen) >= wanted)
    {
        done.Set();
    }
};

Report(new
{
    args,
    configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
    cwd = Environment.CurrentDirectory,
    pid = Environment.ProcessId,
});
Console.Out.WriteLine("fake claude ready");
Console.Out.Flush();

if (Environment.GetEnvironmentVariable("FAKE_CLAUDE_RAISE_BREAK") == "1")
{
    // Ctrl+Break for every process on this console, the waiting launcher included.
    Native.GenerateConsoleCtrlEvent(1, 0);
}

if (!done.Wait(TimeSpan.FromSeconds(60)))
{
    Report(new { timeout = true });
    return 99;
}

Console.Out.WriteLine("fake claude exiting");
return exitCode;

internal static class Native
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
