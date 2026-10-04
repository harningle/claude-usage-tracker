using System.IO;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Interop;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>
/// Parent-snapshot attribution of running Claude Code processes (plan.md section 2): only a verified
/// app-owned launcher parent ties a process to a profile's folder; everything else, including a reused
/// parent PID or details that could not be read, counts against takeover.
/// </summary>
public class CliProcessGuardTests
{
    private const string Launcher = @"C:\Users\user\AppData\Local\ClaudeUsageTracker\launchers\work\claude-work.exe";
    private const string WorkFolder = @"C:\Users\user\.claude-work";
    private const string OtherFolder = @"C:\Users\user\.claude-other";
    private static readonly DateTime Started = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly CliConfigDirectory DefaultFolder = new(@"C:\Users\user\.claude", true, @"C:\Users\user");
    private static readonly CliConfigDirectory Work = new(WorkFolder, false, @"C:\Users\user");
    private static readonly CliConfigDirectory Other = new(OtherFolder, false, @"C:\Users\user");

    private const string LauncherHash = "0f1e2d3c";
    private static readonly Dictionary<string, LauncherImage> Launchers = new() { [Launcher] = new LauncherImage(WorkFolder, LauncherHash) };

    private static ProcessEntry Claude(int pid, int parent, DateTime? created = null, bool? owned = true) =>
        new(pid, parent, "claude.exe", owned, @"C:\Users\user\.local\bin\claude.exe", created ?? Started.AddMinutes(1));

    private static StoreActivity Judge(CliConfigDirectory directory, params ProcessEntry[] table) =>
        JudgeWithImage(directory, _ => LauncherHash, table);

    /// <param name="imageHash">What the file at a launcher's path hashes to now.</param>
    private static StoreActivity JudgeWithImage(CliConfigDirectory directory, Func<string, string> imageHash, params ProcessEntry[] table) =>
        new CliProcessGuard(() => table, () => Launchers, imageHash: imageHash).ActivityFor(directory);

    [Fact]
    public void NoClaudeCodeMeansEveryFolderIsIdle()
    {
        Assert.Equal(StoreActivity.Idle, Judge(DefaultFolder));
        Assert.Equal(StoreActivity.Idle, Judge(Work, new ProcessEntry(10, 1, "explorer.exe", true, @"C:\Windows\explorer.exe", Started)));
    }

    [Fact]
    public void TerminalClaudeCodeBlocksTheDefaultAndUnownedFoldersButNotALaunchersFolder()
    {
        var shell = new ProcessEntry(10, 1, "pwsh.exe", true, @"C:\Program Files\PowerShell\7\pwsh.exe", Started);
        var claude = Claude(20, 10);

        Assert.Equal(StoreActivity.InUse, Judge(DefaultFolder, shell, claude));
        // CLAUDE_CONFIG_DIR may point anywhere a launcher does not own.
        Assert.Equal(StoreActivity.InUse, Judge(Other, shell, claude));
        Assert.Equal(StoreActivity.Idle, Judge(Work, shell, claude));
    }

    [Fact]
    public void VerifiedLauncherChildIsAttributedToItsFolderOnly()
    {
        var launcher = new ProcessEntry(10, 1, "claude-work.exe", true, Launcher, Started);
        var claude = Claude(20, 10);

        Assert.Equal(StoreActivity.InUse, Judge(Work, launcher, claude));
        Assert.Equal(StoreActivity.Idle, Judge(DefaultFolder, launcher, claude));
        Assert.Equal(StoreActivity.Idle, Judge(Other, launcher, claude));
    }

    [Fact]
    public void AnotherFileAtTheLaunchersPathVouchesForNothing()
    {
        var launcher = new ProcessEntry(10, 1, "claude-work.exe", true, Launcher, Started);
        var claude = Claude(20, 10);

        // Something else took the launcher's place and started Claude Code: it may use any folder, the default included.
        Assert.Equal(StoreActivity.Unknown, JudgeWithImage(DefaultFolder, _ => "another program", launcher, claude));
        Assert.Equal(StoreActivity.Unknown, JudgeWithImage(Work, _ => "another program", launcher, claude));
        // An image that cannot be read is not vouched for either.
        Assert.Equal(StoreActivity.Unknown, JudgeWithImage(DefaultFolder, _ => throw new IOException("in use"), launcher, claude));
    }

    [Fact]
    public void ALauncherImageWithoutARecordedHashVouchesForNothing()
    {
        // As a retained image recorded before hashes were kept.
        var unrecorded = new Dictionary<string, LauncherImage> { [Launcher] = new LauncherImage(WorkFolder, null) };
        var table = new[] { new ProcessEntry(10, 1, "claude-work.exe", true, Launcher, Started), Claude(20, 10) };

        Assert.Equal(StoreActivity.Unknown, new CliProcessGuard(() => table, () => unrecorded, imageHash: _ => LauncherHash).ActivityFor(DefaultFolder));
    }

    [Fact]
    public void ParentCreatedAfterTheChildIsAReusedPidAndVouchesForNothing()
    {
        var reused = new ProcessEntry(10, 1, "claude-work.exe", true, Launcher, Started.AddMinutes(5));
        var claude = Claude(20, 10, Started.AddMinutes(1));

        Assert.Equal(StoreActivity.InUse, Judge(DefaultFolder, reused, claude));
        Assert.Equal(StoreActivity.Idle, Judge(Work, reused, claude));
    }

    [Fact]
    public void ExitedParentCountsAsTheSharedDefault() =>
        Assert.Equal(StoreActivity.InUse, Judge(DefaultFolder, Claude(20, 999)));

    [Fact]
    public void UnreadableProcessOrParentMakesEveryFolderUnknown()
    {
        Assert.Equal(StoreActivity.Unknown, Judge(Work, Claude(20, 10, owned: null)));
        var opaqueParent = new ProcessEntry(10, 1, "svchost.exe", true, null, null);
        Assert.Equal(StoreActivity.Unknown, Judge(Work, opaqueParent, Claude(20, 10)));
    }

    [Fact]
    public void AnotherUsersClaudeCodeIsIgnored() =>
        Assert.Equal(StoreActivity.Idle, Judge(DefaultFolder, Claude(20, 10, owned: false)));

    [Fact]
    public void FailedSnapshotIsUnknownNotIdle()
    {
        var guard = new CliProcessGuard(() => throw new System.ComponentModel.Win32Exception(5), () => Launchers);

        Assert.Equal(StoreActivity.Unknown, guard.ActivityFor(DefaultFolder));
    }

    [Fact]
    public void RealSnapshotReadsWithoutFailing()
    {
        // The live process table: whatever runs, the call returns entries with readable IDs only.
        var table = ProcessTable.Capture(CliProcessGuard.ImageName);

        Assert.All(table, p => Assert.True(p.Pid > 0));
    }
}
