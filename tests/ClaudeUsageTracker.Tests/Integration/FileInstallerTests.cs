using System.Diagnostics;
using System.IO;
using ClaudeUsageTracker.App.Integration;

namespace ClaudeUsageTracker.Tests.Integration;

/// <summary>
/// The rename-aside replacement of an executable that may be running, with real Windows file semantics
/// (plan.md section 4, "Delivery": concurrent execution, interruption, promotion and rollback failures,
/// retained-image cleanup).
/// </summary>
public sealed class FileInstallerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<PendingInstall?> _records = [];

    public void Dispose() => _temp.Dispose();

    private string Canonical => Path.Combine(_temp.Path, "bin", "Tool.exe");

    private string Source(string content)
    {
        var path = Path.Combine(_temp.Path, $"source-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, content);
        return path;
    }

    private InstallResult Install(string source) => FileInstaller.Install(source, Canonical, new PendingInstall(), _records.Add);

    private string[] Leftovers() =>
        [.. Directory.EnumerateFiles(Path.GetDirectoryName(Canonical)!).Where(f => f != Canonical).Select(f => Path.GetFileName(f))];

    [Fact]
    public void AFirstInstallPutsTheFileInPlace()
    {
        var result = Install(Source("v1"));

        Assert.Equal(new InstallResult(InstallOutcome.Installed, null, null), result);
        Assert.Equal("v1", File.ReadAllText(Canonical));
        Assert.Empty(Leftovers());
        // The attempt was recorded before anything was renamed, with the hash of what is being installed.
        var attempt = Assert.Single(_records)!;
        Assert.Equal(Canonical, attempt.Canonical);
        Assert.Null(attempt.Retained);
        Assert.Equal(FileInstaller.Sha256Of(Canonical), attempt.Sha256);
    }

    [Fact]
    public void AnUpgradeRenamesTheOldFileAside()
    {
        Install(Source("v1"));

        var result = Install(Source("v2"));

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.Equal("v2", File.ReadAllText(Canonical));
        Assert.Equal("v1", File.ReadAllText(result.Retained!));
        Assert.Equal([Path.GetFileName(result.Retained!)], Leftovers());
        // Not an .exe name, so it is never run by name from a PATH folder.
        Assert.Matches(@"^Tool\.exe\.old-[0-9a-f]{8}$", Path.GetFileName(result.Retained!));
    }

    [Fact]
    public void ARunningImageIsReplacedWithoutEndingItsProcess()
    {
        // A real executable that stays running: a copy of cmd.exe waiting on ping.
        Directory.CreateDirectory(Path.GetDirectoryName(Canonical)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Canonical);
        using var running = Process.Start(new ProcessStartInfo(Canonical, "/d /c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var result = Install(Source("v2"));

            Assert.Equal(InstallOutcome.Installed, result.Outcome);
            Assert.Equal("v2", File.ReadAllText(Canonical));
            Assert.False(running.HasExited);
            // Windows keeps the image of a running process: it can be renamed, not deleted.
            Assert.False(FileInstaller.TryDelete(result.Retained!));
            Assert.True(File.Exists(result.Retained));

            running.Kill(entireProcessTree: true);
            running.WaitForExit();
            Assert.True(SpinWait.SpinUntil(() => FileInstaller.TryDelete(result.Retained!), TimeSpan.FromSeconds(10)));
            Assert.False(File.Exists(result.Retained));
        }
        finally
        {
            if (!running.HasExited)
            {
                running.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void AFileThatCannotBeRenamedLeavesTheOldInstallationIntact()
    {
        Install(Source("v1"));
        _records.Clear();

        InstallResult result;
        using (new FileStream(Canonical, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Install(Source("v2"));
        }

        Assert.Equal(InstallOutcome.Deferred, result.Outcome);
        Assert.NotNull(result.Problem);
        Assert.Equal("v1", File.ReadAllText(Canonical));
        Assert.Empty(Leftovers());
        // Recorded, then forgotten: nothing is left to recover.
        Assert.Equal(2, _records.Count);
        Assert.Null(_records[^1]);
    }

    [Fact]
    public void ASourceThatCannotBeReadChangesNothing()
    {
        Install(Source("v1"));

        Assert.Throws<FileNotFoundException>(() => Install(Path.Combine(_temp.Path, "absent.exe")));

        Assert.Equal("v1", File.ReadAllText(Canonical));
        Assert.Empty(Leftovers());
    }

    // Recovery of an install that was interrupted after it was recorded.

    private PendingInstall Interrupted(string? canonical, string? staged, string? retained, string? stagedHashOf = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Canonical)!);
        var attempt = new PendingInstall
        {
            Canonical = Canonical,
            Staged = Canonical + ".staged-11111111",
            Retained = retained is null ? null : Canonical + ".old-11111111",
        };
        if (canonical is not null)
        {
            File.WriteAllText(attempt.Canonical, canonical);
        }

        if (staged is not null)
        {
            File.WriteAllText(attempt.Staged, staged);
        }

        if (retained is not null)
        {
            File.WriteAllText(attempt.Retained!, retained);
        }

        attempt.Sha256 = FileInstaller.Sha256Of(Source(stagedHashOf ?? staged ?? canonical ?? "v2"));
        return attempt;
    }

    [Fact]
    public void InterruptedBeforeTheFirstRenameTheNewCopyIsDiscarded()
    {
        var attempt = Interrupted(canonical: "v1", staged: "v2", retained: null);

        Assert.Equal(RecoveryOutcome.RolledBack, FileInstaller.Recover(attempt));

        Assert.Equal("v1", File.ReadAllText(Canonical));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void InterruptedBetweenTheRenamesTheInstallIsFinished()
    {
        var attempt = Interrupted(canonical: null, staged: "v2", retained: "v1");

        Assert.Equal(RecoveryOutcome.Completed, FileInstaller.Recover(attempt));

        Assert.Equal("v2", File.ReadAllText(Canonical));
        // The old image stays for the caller to record and delete once nothing runs from it.
        Assert.Equal("v1", File.ReadAllText(attempt.Retained!));
    }

    [Fact]
    public void ADamagedStagedCopyIsNeverPromoted()
    {
        var attempt = Interrupted(canonical: null, staged: "torn", retained: "v1", stagedHashOf: "v2");

        Assert.Equal(RecoveryOutcome.RolledBack, FileInstaller.Recover(attempt));

        Assert.Equal("v1", File.ReadAllText(Canonical));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void InterruptedAfterTheLastRenameTheInstallCounts()
    {
        var attempt = Interrupted(canonical: "v2", staged: null, retained: "v1");

        Assert.Equal(RecoveryOutcome.Completed, FileInstaller.Recover(attempt));

        Assert.Equal("v2", File.ReadAllText(Canonical));
    }

    [Fact]
    public void AFirstInstallInterruptedBeforeItsRenameIsFinished()
    {
        var attempt = Interrupted(canonical: null, staged: "v1", retained: null);

        Assert.Equal(RecoveryOutcome.Completed, FileInstaller.Recover(attempt));

        Assert.Equal("v1", File.ReadAllText(Canonical));
    }

    [Fact]
    public void WithNothingLeftRecoveryReportsTheFileMissing()
    {
        var attempt = Interrupted(canonical: null, staged: null, retained: null);

        Assert.Equal(RecoveryOutcome.Missing, FileInstaller.Recover(attempt));

        Assert.False(File.Exists(Canonical));
    }
}
