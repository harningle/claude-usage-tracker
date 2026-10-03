// The integration helper (plan.md section 2): one small NativeAOT executable with two jobs.
//
// Installed as ClaudeUsageTracker.Helper.exe, "statusline" renders Claude Code's status line from stdin and the
// tracker's non-secret files. It reads no credentials and uses no network.
//
// Copied to %USERPROFILE%\.local\bin\claude-<slug>.exe, it is a profile's launcher: it starts the native
// claude.exe with that profile's own folder and every argument unchanged, and waits for it.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeUsageTracker.Core.Integration;
using ClaudeUsageTracker.Core.Launchers;
using ClaudeUsageTracker.Core.Statusline;

var self = Environment.ProcessPath ?? throw new InvalidOperationException("The helper's own path is unknown.");

// A launcher is recognised by its file name alone, so no argument meant for Claude Code is ever interpreted here.
if (LauncherPaths.SlugOf(Path.GetFileName(self)) is { } slug)
{
    return Launch(self, slug, args);
}

return args switch
{
    ["statusline"] => Statusline(self),
    ["version"] => Version(),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine($"usage: {HelperInfo.FileName} statusline | version");
    return 2;
}

static int Version()
{
    Console.Out.WriteLine($"{Path.GetFileNameWithoutExtension(HelperInfo.FileName)} {HelperInfo.Version}");
    return 0;
}

static int Statusline(string self)
{
    var input = ReadBounded(Console.OpenStandardInput(), StatuslineCommand.MaxInputBytes);
    string line;
    try
    {
        line = StatuslineCommand.Run(
            input,
            HelperInfo.DataRootOf(self) ?? throw new InvalidDataException("The helper is not installed in the tracker's bin folder."),
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Local);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
    {
        // Claude Code shows a blank status line for a failing command and logs this text under "claude --debug".
        Console.Error.WriteLine($"{HelperInfo.FileName}: {e.Message}");
        return 1;
    }

    using var output = Console.OpenStandardOutput();
    output.Write(Encoding.UTF8.GetBytes(line + "\n"));
    return 0;
}

static int Launch(string self, string slug, string[] args)
{
    var name = Path.GetFileName(self);
    if (LauncherPaths.UserProfileOf(self) is not { } userProfile)
    {
        Console.Error.WriteLine($"{name}: this launcher only works from the .local\\bin folder of your user profile. Install it again from Claude Usage Tracker.");
        return 127;
    }

    if (LauncherPaths.FindRealCli(self, Environment.GetEnvironmentVariable("PATH")) is not { } claude)
    {
        Console.Error.WriteLine($"{name}: {LauncherPaths.RealCliFileName} was not found beside this launcher or on PATH. Install native Claude Code first.");
        return 127;
    }

    // Ctrl+C and Ctrl+Break reach Claude Code through the shared console. The launcher must outlive them, or the
    // shell prompt would return over the running session; so they are cancelled here, before the child starts.
    Console.CancelKeyPress += (_, e) => e.Cancel = true;

    var start = new ProcessStartInfo(claude) { UseShellExecute = false };
    foreach (var argument in args)
    {
        start.ArgumentList.Add(argument);
    }

    start.Environment["CLAUDE_CONFIG_DIR"] = LauncherPaths.ConfigDirectory(userProfile, slug);
    Process child;
    try
    {
        child = Process.Start(start) ?? throw new InvalidOperationException($"{claude} did not start.");
    }
    catch (System.ComponentModel.Win32Exception e)
    {
        Console.Error.WriteLine($"{name}: {claude} could not be started: {e.Message}");
        return 127;
    }

    using (child)
    {
        child.WaitForExit();
        return child.ExitCode;
    }
}

static byte[] ReadBounded(Stream stream, int limit)
{
    using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int read;
    while (buffer.Length < limit && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
    {
        buffer.Write(chunk, 0, read);
    }

    return buffer.ToArray();
}
