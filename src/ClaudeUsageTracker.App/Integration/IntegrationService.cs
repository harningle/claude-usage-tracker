using System.IO;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Integration;
using ClaudeUsageTracker.Core.Launchers;

namespace ClaudeUsageTracker.App.Integration;

public enum HelperState
{
    NotInstalled,

    /// <summary>Installed, and the same file this build carries (or this build carries none to compare with).</summary>
    Installed,

    /// <summary>Installed, but this build carries a different helper of the same or a later version.</summary>
    UpdateAvailable,

    /// <summary>A newer build of the app installed a later helper. It is kept: an older build never downgrades it.</summary>
    NewerInstalled,

    /// <summary>The record says installed, but the file is gone.</summary>
    Missing,

    /// <summary>The file is not the one that was installed.</summary>
    Modified,

    /// <summary>An install was left with nothing in place.</summary>
    RepairRequired,
}

public enum StatuslineInstallStatus
{
    NotInstalled,
    Installed,

    /// <summary>The tracker's entry is there, with another command than this PC needs now.</summary>
    Outdated,

    /// <summary>The folder has a status line of the user's or of another tool. It is replaced only when the user agrees.</summary>
    Foreign,
}

/// <param name="Command">The command found in the folder's settings, if any.</param>
public sealed record StatuslineFolder(string ConfigDirectory, StatuslineInstallStatus Status, string? Command, string? Problem);

/// <summary>A profile's launcher as its card shows it.</summary>
/// <param name="FileProblem">Why the launcher's file is not usable (missing, or replaced by another file), or null.</param>
public sealed record LauncherView(string Slug, string Command, string Path, string ConfigDirectory, bool Installed, string? FileProblem);

/// <summary>
/// What the tracker installs outside its own folder, and how it is kept intact (plan.md section 2): the helper
/// at a fixed per-user path, each profile's launcher in <c>.local\bin</c>, and the status line entry in Claude
/// Code's settings. Everything is recorded in the install manifest, replaced by renaming aside, and removed
/// only when the manifest and the file's hash say it is the tracker's own. Used on the UI thread; the
/// launcher images are published as a snapshot for the process guard's threads.
/// </summary>
public sealed class IntegrationService
{
    private readonly string _dataRoot;
    private readonly string _userProfile;
    private readonly string? _bundledHelper;
    private readonly InstallManifestStore _store;
    private readonly TimeProvider _time;
    private readonly Lazy<string?> _bundledHash;
    private volatile IReadOnlyDictionary<string, LauncherImage> _launcherImages = new Dictionary<string, LauncherImage>();

    /// <param name="bundledHelper">The helper this build carries, or null when it carries none.</param>
    public IntegrationService(string dataRoot, string userProfile, string? bundledHelper, TimeProvider time)
    {
        _dataRoot = dataRoot;
        _userProfile = userProfile;
        _bundledHelper = bundledHelper is not null && File.Exists(bundledHelper) ? bundledHelper : null;
        _time = time;
        _store = new InstallManifestStore(Path.Combine(dataRoot, "install.json"));
        _bundledHash = new Lazy<string?>(() => _bundledHelper is null ? null : FileInstaller.Sha256Of(_bundledHelper));
        try
        {
            Manifest = _store.Load();
        }
        catch (InstallManifestException e)
        {
            // Nothing can be told apart as the tracker's own without the record: every change is refused.
            Manifest = new InstallManifest();
            LoadProblem = e.Message;
        }

        Publish();
    }

    public InstallManifest Manifest { get; }

    public InstallManifestStore Store => _store;

    /// <summary>Why the manifest cannot be used, or null. While set, nothing is installed, replaced or removed.</summary>
    public string? LoadProblem { get; }

    public bool HasBundledHelper => _bundledHelper is not null;

    /// <summary>The fixed path hooks and status lines refer to; never a build or extraction folder.</summary>
    public string HelperPath => HelperInfo.InstalledPath(_dataRoot);

    public string BinDirectory => LauncherPaths.BinDirectory(_userProfile);

    /// <summary>
    /// Each launcher image, retained ones included, with the Claude Code folder its sessions use and the hash it was
    /// installed with. Safe to read from any thread.
    /// </summary>
    public IReadOnlyDictionary<string, LauncherImage> LauncherImages => _launcherImages;

    /// <summary>The folders of installed launchers: known Claude Code folders even before a profile is linked to them.</summary>
    public IReadOnlyList<string> LauncherDirectories => [.. Manifest.Launchers.Select(l => l.ConfigDirectory)];

    /// <summary>The helper this build carries, where the build puts it: <c>helper\</c> beside the app.</summary>
    public static string DefaultBundledHelper => Path.Combine(AppContext.BaseDirectory, "helper", HelperInfo.FileName);

    // Startup

    /// <summary>
    /// Settles an install that was interrupted between its renames, and deletes renamed-aside images that no
    /// process uses any more. Returns what could not be settled, or null.
    /// </summary>
    public string? Recover()
    {
        if (LoadProblem is not null)
        {
            return LoadProblem;
        }

        string? problem = null;
        if (Manifest.Pending is { } attempt)
        {
            try
            {
                var previous = RecordedHash(attempt);
                var outcome = FileInstaller.Recover(attempt);
                if (outcome == RecoveryOutcome.Completed)
                {
                    Record(attempt);
                }

                if (attempt.Retained is { } retained && File.Exists(retained))
                {
                    Manifest.Retained.Add(new RetainedFile { Path = retained, ConfigDirectory = attempt.ConfigDirectory, Sha256 = previous });
                }

                Manifest.Pending = null;
                Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problem = e.Message;
            }
        }

        CleanRetained();
        return problem;
    }

    /// <summary>Deletes renamed-aside images that can be deleted now; one still in use stays recorded for the next time.</summary>
    public void CleanRetained()
    {
        if (LoadProblem is not null || Manifest.Retained.RemoveAll(retained => FileInstaller.TryDelete(retained.Path)) == 0)
        {
            return;
        }

        Save();
    }

    // Helper

    public HelperState Helper()
    {
        if (Manifest.Pending is { } pending && SamePath(pending.Canonical, HelperPath))
        {
            return HelperState.RepairRequired;
        }

        if (Manifest.Helper is not { } installed)
        {
            return HelperState.NotInstalled;
        }

        if (!File.Exists(HelperPath))
        {
            return HelperState.Missing;
        }

        string hash;
        try
        {
            hash = FileInstaller.Sha256Of(HelperPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return HelperState.Modified;
        }

        if (hash != installed.Sha256)
        {
            return HelperState.Modified;
        }

        if (_bundledHash.Value is not { } bundled || bundled == hash)
        {
            return HelperState.Installed;
        }

        return installed.HelperVersion > HelperInfo.Version ? HelperState.NewerInstalled : HelperState.UpdateAvailable;
    }

    /// <summary>
    /// Installs this build's helper, or replaces the installed one with it, and then brings every launcher copy
    /// to the same file. A helper a newer build installed is left alone.
    /// </summary>
    public InstallResult InstallHelper()
    {
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        var state = Helper();
        if (state == HelperState.NewerInstalled)
        {
            return new InstallResult(InstallOutcome.Installed, null, null);
        }

        if (state is HelperState.Modified || (state is HelperState.NotInstalled && File.Exists(HelperPath)))
        {
            // As for launchers: an existing file is replaced only when it is the very file the tracker installed.
            return new InstallResult(InstallOutcome.Failed, null, $"{HelperPath} exists and is not the helper the tracker installed. It was not changed.");
        }

        if (state != HelperState.Installed)
        {
            var result = Install(_bundledHelper!, HelperPath, new PendingInstall { HelperVersion = HelperInfo.Version });
            if (result.Outcome != InstallOutcome.Installed)
            {
                return result;
            }
        }

        InstallResult? worst = null;
        foreach (var launcher in Manifest.Launchers.ToList())
        {
            if (LauncherFileProblem(launcher) is null && launcher.Sha256 == _bundledHash.Value)
            {
                continue;
            }

            var result = InstallLauncherFile(launcher.Slug, launcher.ProfileId);
            if (result.Outcome != InstallOutcome.Installed)
            {
                worst = result;
            }
        }

        CleanRetained();
        return worst ?? new InstallResult(InstallOutcome.Installed, null, null);
    }

    // Launchers

    /// <summary>
    /// The slug a new launcher of this profile would get: its name's slug, with a short ID suffix when another
    /// profile's launcher, or a file the tracker did not install, already has that name.
    /// </summary>
    public string ProposedSlug(ProfileSettings profile)
    {
        var owners = Manifest.Launchers.ToDictionary(l => l.Slug, l => l.ProfileId, StringComparer.OrdinalIgnoreCase);
        var slug = LauncherNaming.Slug(profile.Name, profile.Id);
        if (!owners.ContainsKey(slug) && File.Exists(LauncherPaths.LauncherPath(_userProfile, slug)))
        {
            // Not the tracker's file: the name is taken, by an owner that is no profile.
            owners[slug] = Guid.Empty;
        }

        return LauncherNaming.AvailableSlug(profile.Name, profile.Id, owners);
    }

    public LauncherView LauncherFor(ProfileSettings profile)
    {
        var record = profile.TerminalLauncherSlug is { } installed
            ? Manifest.Launchers.Find(l => string.Equals(l.Slug, installed, StringComparison.OrdinalIgnoreCase) && l.ProfileId == profile.Id)
            : null;
        var slug = record?.Slug ?? ProposedSlug(profile);
        return new LauncherView(
            slug, Path.GetFileNameWithoutExtension(LauncherPaths.FileName(slug)), LauncherPaths.LauncherPath(_userProfile, slug),
            LauncherPaths.ConfigDirectory(_userProfile, slug), record is not null, record is null ? null : LauncherFileProblem(record));
    }

    /// <summary>Installs the profile's launcher and creates its folder. On success the profile carries the slug; the caller saves the settings.</summary>
    public InstallResult InstallLauncher(ProfileSettings profile)
    {
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        var view = LauncherFor(profile);
        var result = InstallLauncherFile(view.Slug, profile.Id);
        if (result.Outcome == InstallOutcome.Installed)
        {
            profile.TerminalLauncherSlug = view.Slug;
        }

        return result;
    }

    /// <summary>
    /// Removes the profile's launcher; its folder, which holds a login, stays. A launcher that is running is
    /// renamed aside and deleted later. Returns what the user should know, or null.
    /// </summary>
    public string? RemoveLauncher(ProfileSettings profile)
    {
        if (LoadProblem is not null)
        {
            return LoadProblem;
        }

        var slug = profile.TerminalLauncherSlug;
        var record = Manifest.Launchers.Find(l => string.Equals(l.Slug, slug, StringComparison.OrdinalIgnoreCase) && l.ProfileId == profile.Id);
        profile.TerminalLauncherSlug = null;
        if (record is null)
        {
            return null;
        }

        string? note = null;
        if (File.Exists(record.Path))
        {
            if (LauncherFileProblem(record) is not null)
            {
                // Another file has taken the launcher's place; it is not the tracker's to delete.
                note = record.Path;
            }
            else if (!FileInstaller.TryDelete(record.Path))
            {
                var retained = $"{record.Path}.old-{Guid.NewGuid().ToString("N")[..8]}";
                try
                {
                    File.Move(record.Path, retained);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    profile.TerminalLauncherSlug = slug;
                    return e.Message;
                }

                Manifest.Retained.Add(new RetainedFile { Path = retained, ConfigDirectory = record.ConfigDirectory, Sha256 = record.Sha256 });
            }
        }

        Manifest.Launchers.Remove(record);
        Save();
        return note;
    }

    // Status line entries

    public IReadOnlyList<StatuslineFolder> StatuslineStatus(IEnumerable<string> folders, bool gitBash)
    {
        var expected = StatuslineSettings.Command(HelperPath, gitBash);
        var result = new List<StatuslineFolder>();
        foreach (var folder in folders)
        {
            try
            {
                var entry = ClaudeSettingsFile.Read(ClaudeSettingsFile.PathFor(folder))?["statusLine"];
                var command = StatuslineSettings.CommandOf(entry);
                var status = entry is null ? StatuslineInstallStatus.NotInstalled
                    : !StatuslineSettings.IsOwned(entry, HelperPath) ? StatuslineInstallStatus.Foreign
                    : command == expected ? StatuslineInstallStatus.Installed
                    : StatuslineInstallStatus.Outdated;
                result.Add(new StatuslineFolder(folder, status, command ?? entry?.ToJsonString(), null));
            }
            catch (ClaudeSettingsException e)
            {
                result.Add(new StatuslineFolder(folder, StatuslineInstallStatus.NotInstalled, null, e.Message));
            }
        }

        return result;
    }

    /// <summary>
    /// Points each folder's status line at the installed helper. A status line that is not the tracker's is
    /// replaced only in the folders of <paramref name="replaceForeignIn"/>, and remembered so removal puts it back.
    /// </summary>
    /// <returns>Each folder's state afterwards; a folder still <see cref="StatuslineInstallStatus.Foreign"/> was left alone.</returns>
    public IReadOnlyList<StatuslineFolder> ApplyStatusline(IReadOnlyList<string> folders, bool gitBash, IReadOnlyCollection<string> replaceForeignIn)
    {
        if (LoadProblem is not null)
        {
            return [.. folders.Select(f => new StatuslineFolder(f, StatuslineInstallStatus.NotInstalled, null, LoadProblem))];
        }

        var expected = StatuslineSettings.Command(HelperPath, gitBash);
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A folder that is no longer known (its launcher was removed, its profile unlinked) loses the entry first.
        RemoveStatuslineFrom([.. Manifest.Statuslines.Select(s => s.ConfigDirectory).Where(written => !folders.Any(folder => SamePath(folder, written)))]);
        foreach (var folder in folders)
        {
            string? replaced = null;
            var written = false;
            try
            {
                ClaudeSettingsFile.Update(ClaudeSettingsFile.PathFor(folder), settings =>
                {
                    replaced = null;
                    written = false;
                    var current = settings["statusLine"];
                    if (current is not null && !StatuslineSettings.IsOwned(current, HelperPath))
                    {
                        if (!replaceForeignIn.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        replaced = current.ToJsonString();
                        settings["statusLine"] = StatuslineSettings.Entry(expected);
                    }
                    else if (current is JsonObject own)
                    {
                        if (StatuslineSettings.CommandOf(own) == expected && own["type"]?.ToString() == "command")
                        {
                            written = true;
                            return false;
                        }

                        // Keeps what the user added to the entry, such as "padding".
                        own["type"] = "command";
                        own["command"] = expected;
                    }
                    else
                    {
                        settings["statusLine"] = StatuslineSettings.Entry(expected);
                    }

                    written = true;
                    return true;
                });
            }
            catch (Exception e) when (e is ClaudeSettingsException or IOException or UnauthorizedAccessException)
            {
                problems[folder] = e.Message;
                continue;
            }

            if (written)
            {
                var entry = Manifest.Statuslines.Find(s => SamePath(s.ConfigDirectory, folder));
                if (entry is null)
                {
                    Manifest.Statuslines.Add(entry = new StatuslineEntry { ConfigDirectory = folder });
                }

                entry.Command = expected;
                entry.Replaced = replaced ?? entry.Replaced;
            }
        }

        Save();
        return [.. StatuslineStatus(folders, gitBash).Select(s => problems.TryGetValue(s.ConfigDirectory, out var problem) ? s with { Problem = problem } : s)];
    }

    /// <summary>
    /// Takes the tracker's status line out of each folder, putting back the one it replaced there. A status line
    /// that is not the tracker's is not touched. Returns what could not be done, or null.
    /// </summary>
    public string? RemoveStatusline(IReadOnlyList<string> folders)
    {
        if (LoadProblem is not null)
        {
            return LoadProblem;
        }

        var problems = RemoveStatuslineFrom([.. folders.Union(Manifest.Statuslines.Select(s => s.ConfigDirectory), StringComparer.OrdinalIgnoreCase)]);
        Save();
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private List<string> RemoveStatuslineFrom(IReadOnlyList<string> folders)
    {
        var problems = new List<string>();
        foreach (var folder in folders)
        {
            var entry = Manifest.Statuslines.Find(s => SamePath(s.ConfigDirectory, folder));
            try
            {
                ClaudeSettingsFile.Update(ClaudeSettingsFile.PathFor(folder), settings =>
                {
                    if (!StatuslineSettings.IsOwned(settings["statusLine"], HelperPath))
                    {
                        return false;
                    }

                    if (entry?.Replaced is { } replaced)
                    {
                        settings["statusLine"] = JsonNode.Parse(replaced);
                    }
                    else
                    {
                        settings.Remove("statusLine");
                    }

                    return true;
                });
            }
            catch (Exception e) when (e is ClaudeSettingsException or IOException or UnauthorizedAccessException)
            {
                problems.Add(e.Message);
                continue;
            }

            if (entry is not null)
            {
                Manifest.Statuslines.Remove(entry);
            }
        }

        return problems;
    }

    // Internals

    private InstallResult? Refusal() =>
        LoadProblem is not null ? new InstallResult(InstallOutcome.Failed, null, LoadProblem)
        : _bundledHelper is null ? new InstallResult(InstallOutcome.Failed, null, "This build of the app does not include the helper.")
        : null;

    private InstallResult InstallLauncherFile(string slug, Guid profileId)
    {
        var path = LauncherPaths.LauncherPath(_userProfile, slug);
        var record = Manifest.Launchers.Find(l => string.Equals(l.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (record is not null && record.ProfileId != profileId)
        {
            return new InstallResult(InstallOutcome.Failed, null, $"{Path.GetFileName(path)} is another profile's launcher.");
        }

        // An existing file is replaced only when it is the very file the tracker installed there.
        if (File.Exists(path) && (record is null || LauncherFileProblem(record) is not null))
        {
            return new InstallResult(InstallOutcome.Failed, null, $"{path} exists and is not a launcher the tracker installed. It was not changed.");
        }

        var configDirectory = LauncherPaths.ConfigDirectory(_userProfile, slug);
        try
        {
            Directory.CreateDirectory(configDirectory);
            return Install(_bundledHelper!, path, new PendingInstall
            {
                HelperVersion = HelperInfo.Version, LauncherSlug = slug, ProfileId = profileId, ConfigDirectory = configDirectory,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new InstallResult(InstallOutcome.Failed, null, e.Message);
        }
    }

    private InstallResult Install(string source, string canonical, PendingInstall attempt)
    {
        var previous = RecordedHash(attempt);
        InstallResult result;
        try
        {
            result = FileInstaller.Install(source, canonical, attempt, pending =>
            {
                Manifest.Pending = pending;
                Save();
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new InstallResult(InstallOutcome.Failed, null, e.Message);
        }

        if (result.Outcome == InstallOutcome.Installed)
        {
            Record(attempt);
            if (result.Retained is { } retained)
            {
                Manifest.Retained.Add(new RetainedFile { Path = retained, ConfigDirectory = attempt.ConfigDirectory, Sha256 = previous });
            }

            Manifest.Pending = null;
            Save();
        }

        return result;
    }

    /// <summary>The hash recorded for the file an install replaces: the one that is renamed aside.</summary>
    private string? RecordedHash(PendingInstall attempt) => attempt.LauncherSlug is { } slug
        ? Manifest.Launchers.Find(l => string.Equals(l.Slug, slug, StringComparison.OrdinalIgnoreCase))?.Sha256
        : Manifest.Helper?.Sha256;

    /// <summary>Writes a completed install into the manifest's helper or launcher record.</summary>
    private void Record(PendingInstall attempt)
    {
        if (attempt.LauncherSlug is not { } slug)
        {
            Manifest.Helper = new InstalledFile
            {
                Path = attempt.Canonical, HelperVersion = attempt.HelperVersion, Sha256 = attempt.Sha256, InstalledAt = _time.GetUtcNow(),
            };
            return;
        }

        Manifest.Launchers.RemoveAll(l => string.Equals(l.Slug, slug, StringComparison.OrdinalIgnoreCase));
        Manifest.Launchers.Add(new InstalledLauncher
        {
            Slug = slug, ProfileId = attempt.ProfileId ?? Guid.Empty, ConfigDirectory = attempt.ConfigDirectory ?? "",
            Path = attempt.Canonical, HelperVersion = attempt.HelperVersion, Sha256 = attempt.Sha256, InstalledAt = _time.GetUtcNow(),
        });
    }

    /// <summary>Why a recorded launcher's file is not the installed one: gone, unreadable or replaced. Null when it is intact.</summary>
    public static string? LauncherFileProblem(InstalledLauncher launcher)
    {
        if (!File.Exists(launcher.Path))
        {
            return "missing";
        }

        try
        {
            return FileInstaller.Sha256Of(launcher.Path) == launcher.Sha256 ? null : "replaced";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private void Save()
    {
        _store.Save(Manifest);
        Publish();
    }

    private void Publish()
    {
        var images = new Dictionary<string, LauncherImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var launcher in Manifest.Launchers)
        {
            images[launcher.Path] = new LauncherImage(launcher.ConfigDirectory, launcher.Sha256);
        }

        foreach (var retained in Manifest.Retained)
        {
            if (retained.ConfigDirectory is { } directory)
            {
                images[retained.Path] = new LauncherImage(directory, retained.Sha256);
            }
        }

        _launcherImages = images;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
