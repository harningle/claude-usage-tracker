using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Integration;
using ClaudeUsageTracker.Core.Launchers;

namespace ClaudeUsageTracker.Tests.Integration;

/// <summary>
/// The installed helper, the launchers and the status line entry, in a temporary data root and user profile.
/// The launcher cases port upstream's <c>TerminalLauncherServiceTests</c>; the file standing in for the helper
/// is any file, because only its bytes are installed here.
/// </summary>
public sealed class IntegrationServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly string _data;
    private readonly string _home;
    private readonly string _bundle;

    public IntegrationServiceTests()
    {
        _data = Path.Combine(_temp.Path, "data");
        _home = Path.Combine(_temp.Path, "home");
        _bundle = Path.Combine(_temp.Path, "app", "helper", HelperInfo.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_bundle)!);
        Directory.CreateDirectory(_home);
        File.WriteAllText(_bundle, "helper build 1");
    }

    public void Dispose() => _temp.Dispose();

    private IntegrationService Service(bool bundled = true) => new(_data, _home, bundled ? _bundle : null, _time);

    private static ProfileSettings Profile(string name) => new() { Name = name };

    private string Bin => Path.Combine(_home, ".local", "bin");

    // The helper

    [Fact]
    public void TheHelperIsInstalledAtItsFixedPathUnderTheDataRoot()
    {
        var service = Service();
        Assert.Equal(HelperState.NotInstalled, service.Helper());

        Assert.Equal(InstallOutcome.Installed, service.InstallHelper().Outcome);

        Assert.Equal(Path.Combine(_data, "bin", "ClaudeUsageTracker.Helper.exe"), service.HelperPath);
        Assert.Equal("helper build 1", File.ReadAllText(service.HelperPath));
        Assert.Equal(HelperState.Installed, service.Helper());
        // A later start reads the same record.
        var reloaded = Service();
        Assert.Equal(HelperState.Installed, reloaded.Helper());
        Assert.Equal(HelperInfo.Version, reloaded.Manifest.Helper!.HelperVersion);
        Assert.Null(reloaded.Manifest.Pending);
    }

    [Fact]
    public void ANewBuildsHelperReplacesTheInstalledOneOnRequestOnly()
    {
        Service().InstallHelper();
        File.WriteAllText(_bundle, "helper build 2");

        var service = Service();
        Assert.Equal(HelperState.UpdateAvailable, service.Helper());
        Assert.Equal("helper build 1", File.ReadAllText(service.HelperPath));

        Assert.Equal(InstallOutcome.Installed, service.InstallHelper().Outcome);
        Assert.Equal("helper build 2", File.ReadAllText(service.HelperPath));
        Assert.Equal(HelperState.Installed, service.Helper());
        // Nothing ran from the old file, so it is gone and no longer recorded.
        Assert.Equal([HelperInfo.FileName], Directory.EnumerateFiles(Path.Combine(_data, "bin")).Select(Path.GetFileName));
        Assert.Empty(service.Manifest.Retained);
    }

    [Fact]
    public void AnOlderBuildNeverDowngradesANewerHelper()
    {
        var service = Service();
        service.InstallHelper();
        // As a newer build of the app would leave it.
        File.WriteAllText(service.HelperPath, "helper build 9");
        service.Manifest.Helper!.HelperVersion = HelperInfo.Version + 1;
        service.Manifest.Helper.Sha256 = FileInstaller.Sha256Of(service.HelperPath);
        service.Store.Save(service.Manifest);

        var older = Service();
        Assert.Equal(HelperState.NewerInstalled, older.Helper());
        Assert.Equal(InstallOutcome.Installed, older.InstallHelper().Outcome);

        Assert.Equal("helper build 9", File.ReadAllText(older.HelperPath));
        Assert.Equal(HelperInfo.Version + 1, older.Manifest.Helper!.HelperVersion);
    }

    [Fact]
    public void AMissingOrReplacedHelperIsNoticedAndRepaired()
    {
        var service = Service();
        service.InstallHelper();

        File.WriteAllText(service.HelperPath, "something else");
        Assert.Equal(HelperState.Modified, service.Helper());

        File.Delete(service.HelperPath);
        Assert.Equal(HelperState.Missing, service.Helper());

        Assert.Equal(InstallOutcome.Installed, service.InstallHelper().Outcome);
        Assert.Equal("helper build 1", File.ReadAllText(service.HelperPath));
        Assert.Equal(HelperState.Installed, service.Helper());
    }

    [Fact]
    public void ABuildWithoutAHelperInstallsNothing()
    {
        var service = Service(bundled: false);

        Assert.False(service.HasBundledHelper);
        Assert.Equal(InstallOutcome.Failed, service.InstallHelper().Outcome);
        Assert.Equal(InstallOutcome.Failed, service.InstallLauncher(Profile("Work")).Outcome);
        Assert.False(Directory.Exists(Path.Combine(_data, "bin")));
        Assert.False(Directory.Exists(Bin));
    }

    [Fact]
    public void ADamagedManifestIsReportedAndNothingIsChanged()
    {
        Service().InstallHelper();
        var manifest = Path.Combine(_data, "install.json");
        File.WriteAllText(manifest, "{ torn");

        var service = Service();
        var profile = Profile("Work");

        Assert.NotNull(service.LoadProblem);
        Assert.Equal(InstallOutcome.Failed, service.InstallHelper().Outcome);
        Assert.Equal(InstallOutcome.Failed, service.InstallLauncher(profile).Outcome);
        Assert.NotNull(service.RemoveLauncher(profile));
        Assert.NotNull(service.RemoveStatusline([Path.Combine(_home, ".claude")]));
        Assert.NotNull(service.Recover());
        Assert.Equal("{ torn", File.ReadAllText(manifest));
    }

    [Fact]
    public void AnInstallInterruptedBetweenItsRenamesIsFinishedAtTheNextStart()
    {
        var service = Service();
        service.InstallHelper();
        var old = FileInstaller.Sha256Of(service.HelperPath);
        File.WriteAllText(_bundle, "helper build 2");

        // The state a crash between the two renames leaves: recorded, old image renamed, new copy staged.
        var attempt = new PendingInstall
        {
            Canonical = service.HelperPath, Staged = service.HelperPath + ".staged-1", Retained = service.HelperPath + ".old-1",
            Sha256 = FileInstaller.Sha256Of(_bundle), HelperVersion = HelperInfo.Version,
        };
        File.Copy(_bundle, attempt.Staged);
        File.Move(service.HelperPath, attempt.Retained);
        service.Manifest.Pending = attempt;
        service.Store.Save(service.Manifest);

        var restarted = Service();
        Assert.Equal(HelperState.RepairRequired, restarted.Helper());
        Assert.Null(restarted.Recover());

        Assert.Equal("helper build 2", File.ReadAllText(restarted.HelperPath));
        Assert.Equal(HelperState.Installed, restarted.Helper());
        Assert.NotEqual(old, restarted.Manifest.Helper!.Sha256);
        Assert.Null(restarted.Manifest.Pending);
        Assert.False(File.Exists(attempt.Retained));
    }

    // Launchers: upstream TerminalLauncherServiceTests

    [Fact]
    public void InstallWritesLauncherWithConfigDirAndManifestEntry()
    {
        var service = Service();
        var profile = Profile("Work Account");

        Assert.Equal(InstallOutcome.Installed, service.InstallLauncher(profile).Outcome);

        Assert.Equal("work-account", profile.TerminalLauncherSlug);
        var launcher = Path.Combine(Bin, "claude-work-account.exe");
        Assert.Equal("helper build 1", File.ReadAllText(launcher));
        Assert.True(Directory.Exists(Path.Combine(_home, ".claude-work-account")));
        // The manifest, not a marker inside the file, says whose launcher it is.
        var record = Assert.Single(service.Manifest.Launchers);
        Assert.Equal((profile.Id, launcher, Path.Combine(_home, ".claude-work-account")), (record.ProfileId, record.Path, record.ConfigDirectory));
        // The launcher derives the same folder from its own path.
        Assert.Equal(record.ConfigDirectory, LauncherPaths.ConfigDirectory(LauncherPaths.UserProfileOf(launcher)!, LauncherPaths.SlugOf(Path.GetFileName(launcher))!));
        Assert.Equal("claude-work-account", service.LauncherFor(profile).Command);
    }

    [Fact]
    public void IsInstalledTracksManifestOwnership()
    {
        var service = Service();
        var profile = Profile("Alpha");
        service.InstallLauncher(profile);
        Assert.True(service.LauncherFor(profile).Installed);

        // Another profile claiming the same slug does not have that launcher.
        var impostor = Profile("Alpha");
        impostor.TerminalLauncherSlug = profile.TerminalLauncherSlug;
        Assert.False(service.LauncherFor(impostor).Installed);
        Assert.Null(service.RemoveLauncher(impostor));
        Assert.True(File.Exists(Path.Combine(Bin, "claude-alpha.exe")));
    }

    [Fact]
    public void CollidingNamesGetIdSuffixedSlug()
    {
        var service = Service();
        service.InstallLauncher(Profile("Team"));
        var second = Profile("Team");

        Assert.Equal(InstallOutcome.Installed, service.InstallLauncher(second).Outcome);

        Assert.Equal($"team-{second.Id.ToString("D")[..4]}", second.TerminalLauncherSlug);
        Assert.Equal(2, Directory.EnumerateFiles(Bin, "claude-team*.exe").Count());
    }

    [Fact]
    public void AFileTheTrackerDidNotInstallIsNeverOverwritten()
    {
        // As on a PC that already has a claude-swap.exe from another tool.
        Directory.CreateDirectory(Bin);
        var foreign = Path.Combine(Bin, "claude-swap.exe");
        File.WriteAllText(foreign, "someone else's tool");
        var service = Service();
        var profile = Profile("Swap");

        Assert.Equal(InstallOutcome.Installed, service.InstallLauncher(profile).Outcome);

        Assert.Equal($"swap-{profile.Id.ToString("D")[..4]}", profile.TerminalLauncherSlug);
        Assert.Equal("someone else's tool", File.ReadAllText(foreign));

        // Even when the suffixed name is taken too, nothing is replaced.
        var other = Profile("Swap");
        File.WriteAllText(Path.Combine(Bin, $"claude-swap-{other.Id.ToString("D")[..4]}.exe"), "also foreign");
        var result = service.InstallLauncher(other);
        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Null(other.TerminalLauncherSlug);
        Assert.Equal("also foreign", File.ReadAllText(Path.Combine(Bin, $"claude-swap-{other.Id.ToString("D")[..4]}.exe")));
    }

    [Fact]
    public void UninstallRemovesOnlyOwnLauncherAndKeepsConfigDir()
    {
        var service = Service();
        var profile = Profile("Keep My Login");
        service.InstallLauncher(profile);
        var folder = Path.Combine(_home, ".claude-keep-my-login");
        File.WriteAllText(Path.Combine(folder, ".credentials.json"), "{}");

        Assert.Null(service.RemoveLauncher(profile));

        Assert.False(File.Exists(Path.Combine(Bin, "claude-keep-my-login.exe")));
        Assert.Null(profile.TerminalLauncherSlug);
        Assert.Empty(service.Manifest.Launchers);
        // The folder holds the profile's Claude Code login and is never deleted.
        Assert.True(File.Exists(Path.Combine(folder, ".credentials.json")));
    }

    [Fact]
    public void ALauncherReplacedBySomethingElseIsLeftInPlace()
    {
        var service = Service();
        var profile = Profile("Work");
        service.InstallLauncher(profile);
        var launcher = Path.Combine(Bin, "claude-work.exe");
        File.WriteAllText(launcher, "replaced by the user");

        Assert.Equal("replaced", service.LauncherFor(profile).FileProblem);
        // Reinstalling would overwrite a file that is no longer the tracker's.
        Assert.Equal(InstallOutcome.Failed, service.InstallLauncher(profile).Outcome);
        Assert.Equal(launcher, service.RemoveLauncher(profile));

        Assert.Equal("replaced by the user", File.ReadAllText(launcher));
        Assert.Empty(service.Manifest.Launchers);
    }

    [Fact]
    public void TheSlugStaysWhenTheProfileIsRenamed()
    {
        var service = Service();
        var profile = Profile("Work");
        service.InstallLauncher(profile);

        profile.Name = "Something Else";

        Assert.Equal("claude-work", service.LauncherFor(profile).Command);
        Assert.Equal(Path.Combine(_home, ".claude-work"), service.LauncherFor(profile).ConfigDirectory);
    }

    [Fact]
    public void ARunningLauncherSurvivesRemovalAndUpgradeAndStaysAttributedToItsFolder()
    {
        // The bundled "helper" is a real program here, so a launcher process can be kept alive.
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), _bundle, overwrite: true);
        var service = Service();
        var profile = Profile("Work");
        service.InstallLauncher(profile);
        var launcher = Path.Combine(Bin, "claude-work.exe");
        var folder = Path.Combine(_home, ".claude-work");
        using var session = Process.Start(new ProcessStartInfo(launcher, "/d /c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.Equal(folder, service.LauncherImages[launcher]);

            // An upgrade: the helper and the launcher copy are replaced while the session runs.
            File.AppendAllText(_bundle, "new build");
            Assert.Equal(InstallOutcome.Installed, service.InstallHelper().Outcome);
            Assert.False(session.HasExited);
            Assert.Equal(FileInstaller.Sha256Of(_bundle), FileInstaller.Sha256Of(launcher));
            var retained = Assert.Single(service.Manifest.Retained);
            Assert.Equal(folder, service.LauncherImages[retained.Path]);
            Assert.Equal(folder, service.LauncherImages[launcher]);

            // Removal while sessions run from both images: each keeps its attribution until it ends.
            using var newer = Process.Start(new ProcessStartInfo(launcher, "/d /c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                Assert.Null(service.RemoveLauncher(profile));
                Assert.False(session.HasExited);
                Assert.False(newer.HasExited);
                Assert.False(File.Exists(launcher));
                Assert.Equal(2, service.Manifest.Retained.Count);
                Assert.All(service.Manifest.Retained, image => Assert.Equal(folder, service.LauncherImages[image.Path]));
                Assert.True(Directory.Exists(folder));

                // The record survives a restart of the tracker.
                Assert.Equal(folder, Service().LauncherImages[retained.Path]);
            }
            finally
            {
                newer.Kill(entireProcessTree: true);
                newer.WaitForExit();
            }
        }
        finally
        {
            session.Kill(entireProcessTree: true);
            session.WaitForExit();
        }

        Assert.True(SpinWait.SpinUntil(() =>
        {
            service.CleanRetained();
            return service.Manifest.Retained.Count == 0;
        }, TimeSpan.FromSeconds(10)));
        Assert.Empty(service.LauncherImages);
        Assert.Empty(Directory.EnumerateFiles(Bin));
    }

    // The status line entry

    private string Folder(string name = ".claude") => Path.Combine(_home, name);

    private static JsonObject Settings(string folder) => ClaudeSettingsFile.Read(ClaudeSettingsFile.PathFor(folder))!;

    private void WriteSettings(string folder, string json)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(ClaudeSettingsFile.PathFor(folder), json);
    }

    [Fact]
    public void ApplyPointsEachFoldersStatusLineAtTheInstalledHelper()
    {
        var service = Service();
        WriteSettings(Folder(), """{"model":"opus","hooks":{"Stop":[{"matcher":"","hooks":[{"type":"command","command":"x"}]}]},"theme":"dark"}""");

        var result = service.ApplyStatusline([Folder(), Folder(".claude-work")], gitBash: true, []);

        Assert.All(result, folder => Assert.Equal(StatuslineInstallStatus.Installed, folder.Status));
        var expected = StatuslineSettings.Command(service.HelperPath, gitBash: true);
        Assert.Contains(service.HelperPath.Replace('\\', '/'), expected);
        Assert.Equal(expected, (string?)Settings(Folder())["statusLine"]!["command"]);
        Assert.Equal("command", (string?)Settings(Folder())["statusLine"]!["type"]);
        Assert.Equal(expected, (string?)Settings(Folder(".claude-work"))["statusLine"]!["command"]);
        // Everything else keeps its value and its place; the new member comes last.
        Assert.Equal(["model", "hooks", "theme", "statusLine"], Settings(Folder()).Select(p => p.Key));
        Assert.Equal("x", (string?)Settings(Folder())["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal(2, service.Manifest.Statuslines.Count);
    }

    [Fact]
    public void AnotherToolsStatusLineIsReplacedOnlyWithConsentAndPutBackOnRemoval()
    {
        var service = Service();
        WriteSettings(Folder(), """{"statusLine":{"type":"command","command":"~/.claude/my-statusline.sh","padding":2}}""");

        var refused = Assert.Single(service.ApplyStatusline([Folder()], gitBash: true, []));
        Assert.Equal(StatuslineInstallStatus.Foreign, refused.Status);
        Assert.Equal("~/.claude/my-statusline.sh", refused.Command);
        Assert.Equal("~/.claude/my-statusline.sh", (string?)Settings(Folder())["statusLine"]!["command"]);
        Assert.Empty(service.Manifest.Statuslines);

        var replaced = Assert.Single(service.ApplyStatusline([Folder()], gitBash: true, [Folder()]));
        Assert.Equal(StatuslineInstallStatus.Installed, replaced.Status);

        Assert.Null(Service().RemoveStatusline([Folder()]));
        Assert.Equal("""{"type":"command","command":"~/.claude/my-statusline.sh","padding":2}""", Settings(Folder())["statusLine"]!.ToJsonString());
        Assert.Empty(Service().Manifest.Statuslines);
    }

    [Fact]
    public void RemovalTakesOutOnlyTheTrackersOwnEntry()
    {
        var service = Service();
        service.ApplyStatusline([Folder(), Folder(".claude-work")], gitBash: true, []);
        // The user switched one folder to another status line afterwards.
        WriteSettings(Folder(".claude-work"), """{"statusLine":{"type":"command","command":"other"},"model":"opus"}""");

        Assert.Null(service.RemoveStatusline([Folder(), Folder(".claude-work"), Folder(".claude-never-existed")]));

        Assert.Equal("{}", Settings(Folder()).ToJsonString());
        Assert.Equal("other", (string?)Settings(Folder(".claude-work"))["statusLine"]!["command"]);
        Assert.False(Directory.Exists(Folder(".claude-never-existed")));
    }

    [Fact]
    public void AnEntryForAnotherShellIsOutdatedAndRewrittenKeepingTheUsersAdditions()
    {
        // A user profile with a space needs each shell's own quoting.
        var data = Path.Combine(_temp.Path, "data with space");
        var service = new IntegrationService(data, _home, _bundle, _time);
        service.ApplyStatusline([Folder()], gitBash: false, []);
        var forward = service.HelperPath.Replace('\\', '/');
        Assert.Equal($"& '{forward}' statusline", (string?)Settings(Folder())["statusLine"]!["command"]);
        var entry = Settings(Folder());
        entry["statusLine"]!["padding"] = 2;
        File.WriteAllText(ClaudeSettingsFile.PathFor(Folder()), entry.ToJsonString());

        // Git Bash was installed since: Claude Code now runs the command through it.
        Assert.Equal(StatuslineInstallStatus.Outdated, Assert.Single(service.StatuslineStatus([Folder()], gitBash: true)).Status);
        service.ApplyStatusline([Folder()], gitBash: true, []);

        Assert.Equal($"'{forward}' statusline", (string?)Settings(Folder())["statusLine"]!["command"]);
        Assert.Equal(2, (int?)Settings(Folder())["statusLine"]!["padding"]);
    }

    [Fact]
    public void SettingsThatCannotBeReadAreReportedAndLeftAlone()
    {
        var service = Service();
        WriteSettings(Folder(), "{ not json");

        var result = Assert.Single(service.ApplyStatusline([Folder()], gitBash: true, []));

        Assert.NotNull(result.Problem);
        Assert.Equal("{ not json", File.ReadAllText(ClaudeSettingsFile.PathFor(Folder())));
        Assert.Empty(service.Manifest.Statuslines);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\ClaudeUsageTracker\bin\ClaudeUsageTracker.Helper.exe", true,
        "C:/Users/someone/AppData/Local/ClaudeUsageTracker/bin/ClaudeUsageTracker.Helper.exe statusline")]
    [InlineData(@"C:\Users\someone\AppData\Local\ClaudeUsageTracker\bin\ClaudeUsageTracker.Helper.exe", false,
        "C:/Users/someone/AppData/Local/ClaudeUsageTracker/bin/ClaudeUsageTracker.Helper.exe statusline")]
    [InlineData(@"C:\Users\用户\bin\Helper.exe", true, "C:/Users/用户/bin/Helper.exe statusline")]
    [InlineData(@"C:\Users\Jane Doe\bin\Helper.exe", true, "'C:/Users/Jane Doe/bin/Helper.exe' statusline")]
    [InlineData(@"C:\Users\Jane Doe\bin\Helper.exe", false, "& 'C:/Users/Jane Doe/bin/Helper.exe' statusline")]
    [InlineData(@"C:\Users\O'Brien\bin\Helper.exe", true, @"'C:/Users/O'\''Brien/bin/Helper.exe' statusline")]
    [InlineData(@"C:\Users\O'Brien\bin\Helper.exe", false, "& 'C:/Users/O''Brien/bin/Helper.exe' statusline")]
    [InlineData(@"C:\Users\a&b\bin\Helper.exe", true, "'C:/Users/a&b/bin/Helper.exe' statusline")]
    public void TheCommandUsesForwardSlashesAndQuotesOnlyWhenNeeded(string helper, bool gitBash, string expected)
    {
        Assert.Equal(expected, StatuslineSettings.Command(helper, gitBash));
        Assert.True(StatuslineSettings.IsOwned(StatuslineSettings.Entry(expected), helper));
        Assert.False(StatuslineSettings.IsOwned(StatuslineSettings.Entry("~/.claude/statusline.sh"), helper));
    }
}
