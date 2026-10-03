using System.IO;
using ClaudeUsageTracker.App.Hud;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.App.Integration;

public enum IntegrationProblemKind
{
    /// <summary>The record of installed integrations cannot be read; nothing is changed until it can.</summary>
    ManifestUnreadable,
    HelperMissing,
    HelperModified,
    HelperRepairRequired,
    LauncherMissing,
    LauncherReplaced,
    StatuslineNotInstalled,
    StatuslineOutdated,
    HooksIncomplete,
    HooksOutdated,
    HudPortBusy,
    HudFailed,

    /// <summary>A folder's <c>settings.json</c> cannot be read or edited.</summary>
    SettingsUnusable,
}

/// <param name="Detail">The file, folder or launcher the problem is about, or the system's message.</param>
public sealed record IntegrationProblem(IntegrationProblemKind Kind, string? Detail);

/// <summary>What Apply on the status line page came to.</summary>
/// <param name="Foreign">Folders that have a status line of their own, which was left alone; the user is asked before any is replaced.</param>
public sealed record StatuslineApplyResult(bool Applied, string? Problem, IReadOnlyList<StatuslineFolder> Foreign);

/// <summary>
/// The user's integration choices carried out: the status line, the HUD's hooks and the launchers, across every
/// known Claude Code folder, with one check of whether everything installed is still intact (plan.md section 2,
/// "validate all app-owned hook/statusline executable paths and launcher copies"). Runs on the UI thread.
/// </summary>
public sealed class IntegrationCoordinator
{
    private readonly AppSettings _settings;
    private readonly Action _persist;
    private readonly IntegrationService _service;
    private readonly HudService _hud;
    private readonly StatuslineFiles _files;
    private readonly StatuslineSync _sync;
    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly Func<bool> _gitBash;
    private readonly Func<Guid, CachedUsage?> _fetched;

    /// <param name="persist">Saves the settings after a choice was changed here.</param>
    /// <param name="folders">The known Claude Code folders: the default one, <c>CLAUDE_CONFIG_DIR</c>, linked and launcher folders.</param>
    /// <param name="gitBash">Whether Claude Code runs commands through Git Bash on this PC.</param>
    /// <param name="fetched">A profile's usage as fetched by this run, or null.</param>
    public IntegrationCoordinator(
        AppSettings settings, Action persist, IntegrationService service, HudService hud, StatuslineFiles files, StatuslineSync sync,
        Func<IReadOnlyList<string>> folders, Func<bool> gitBash, Func<Guid, CachedUsage?> fetched)
    {
        _settings = settings;
        _persist = persist;
        _service = service;
        _hud = hud;
        _files = files;
        _sync = sync;
        _folders = folders;
        _gitBash = gitBash;
        _fetched = fetched;
    }

    public IntegrationService Service => _service;

    public HudService Hud => _hud;

    /// <summary>Raised after anything here changed what is installed or how it is doing.</summary>
    public event Action? Changed;

    // Start and exit

    /// <summary>Settles what an earlier run left unfinished and brings up what the user has switched on.</summary>
    public void Start()
    {
        _service.Recover();
        if (HudWanted)
        {
            _hud.Start();
        }

        FoldersChanged();
    }

    /// <summary>The HUD is switched on and its entries can be recorded: without a usable manifest nothing is written anywhere.</summary>
    private bool HudWanted => _settings.HudEnabled && _service.LoadProblem is null;

    /// <summary>The clean exit: the hook entries are removed before the listener stops. The status line stays; it needs no running tracker.</summary>
    public void Shutdown()
    {
        if (_service.LoadProblem is null && (_hud.Status != HudServerStatus.Stopped || _settings.HudEnabled))
        {
            _hud.Stop();
        }

        _hud.Dispose();
    }

    /// <summary>
    /// After the set of known folders or the profiles may have changed: new folders get what is switched on, folders
    /// that are no longer known lose it, and the status line caches follow the profiles.
    /// </summary>
    public void FoldersChanged()
    {
        if (_settings.StatuslineEnabled && _service.LoadProblem is null)
        {
            _service.ApplyStatusline(_folders(), _gitBash(), []);
        }

        if (_hud.Status == HudServerStatus.Running)
        {
            _hud.InstallHooks();
        }

        SyncStatusline();
    }

    /// <summary>Brings the status line caches in step with the profiles and their latest usage. A cache that cannot be written now is written at the next change.</summary>
    public void SyncStatusline()
    {
        try
        {
            _sync.Sync(_settings, _fetched);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The status line

    /// <summary>
    /// Upstream's Apply: saves the configuration for the helper and points every known folder's status line at
    /// it, installing the helper first when it is not in place.
    /// </summary>
    /// <param name="replaceForeignIn">Folders whose own status line the user agreed to replace.</param>
    public StatuslineApplyResult ApplyStatusline(IReadOnlyCollection<string> replaceForeignIn)
    {
        if (_service.LoadProblem is { } unreadable)
        {
            return new StatuslineApplyResult(false, unreadable, []);
        }

        if (_service.Helper() is not (HelperState.Installed or HelperState.UpdateAvailable or HelperState.NewerInstalled))
        {
            var install = _service.InstallHelper();
            if (install.Outcome != InstallOutcome.Installed)
            {
                return new StatuslineApplyResult(false, install.Problem, []);
            }
        }

        IReadOnlyList<StatuslineFolder> folders;
        try
        {
            _files.WriteConfig(_settings.Statusline);
            folders = _service.ApplyStatusline(_folders(), _gitBash(), replaceForeignIn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new StatuslineApplyResult(false, e.Message, []);
        }

        // It counts as applied once any folder has it; a folder that keeps its own status line is the user's choice.
        var applied = folders.Any(f => f.Status == StatuslineInstallStatus.Installed);
        if (applied)
        {
            _settings.StatuslineEnabled = true;
            _persist();
        }

        SyncStatusline();
        Changed?.Invoke();
        var problems = folders.Where(f => f.Problem is not null).Select(f => f.Problem).ToList();
        return new StatuslineApplyResult(
            applied, problems.Count == 0 ? null : string.Join(" ", problems), [.. folders.Where(f => f.Status == StatuslineInstallStatus.Foreign)]);
    }

    /// <summary>Upstream's Reset: takes the status line out of every folder again. Returns what could not be done, or null.</summary>
    public string? ResetStatusline()
    {
        var problem = _service.RemoveStatusline(_folders());
        _settings.StatuslineEnabled = false;
        _persist();
        SyncStatusline();
        Changed?.Invoke();
        return problem;
    }

    /// <summary>The configuration changed on the page: an applied status line follows at once, as upstream regenerates its scripts.</summary>
    public void StatuslineConfigChanged()
    {
        if (!_settings.StatuslineEnabled)
        {
            return;
        }

        try
        {
            _files.WriteConfig(_settings.Statusline);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The HUD

    public void SetHudEnabled(bool enabled)
    {
        _settings.HudEnabled = enabled;
        _persist();
        if (HudWanted)
        {
            _hud.Start();
        }
        else if (_service.LoadProblem is null)
        {
            _hud.Stop();
        }

        Changed?.Invoke();
    }

    // Launchers

    public InstallResult InstallLauncher(ProfileSettings profile)
    {
        var result = _service.InstallLauncher(profile);
        if (result.Outcome == InstallOutcome.Installed)
        {
            _persist();
            FoldersChanged();
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>Removes the profile's launcher. Returns what the user should know, or null.</summary>
    public string? RemoveLauncher(ProfileSettings profile)
    {
        var note = _service.RemoveLauncher(profile);
        _persist();
        FoldersChanged();
        Changed?.Invoke();
        return note;
    }

    // Health and repair

    /// <summary>Everything installed that is not as it should be. Empty when all is intact, or nothing is installed.</summary>
    public IReadOnlyList<IntegrationProblem> Problems()
    {
        var problems = new List<IntegrationProblem>();
        if (_service.LoadProblem is { } unreadable)
        {
            problems.Add(new IntegrationProblem(IntegrationProblemKind.ManifestUnreadable, unreadable));
            return problems;
        }

        var helperNeeded = _settings.StatuslineEnabled || _service.Manifest.Helper is not null;
        switch (_service.Helper())
        {
            case HelperState.Missing or HelperState.NotInstalled when helperNeeded:
                problems.Add(new IntegrationProblem(IntegrationProblemKind.HelperMissing, _service.HelperPath));
                break;
            case HelperState.Modified:
                problems.Add(new IntegrationProblem(IntegrationProblemKind.HelperModified, _service.HelperPath));
                break;
            case HelperState.RepairRequired:
                problems.Add(new IntegrationProblem(IntegrationProblemKind.HelperRepairRequired, _service.HelperPath));
                break;
        }

        foreach (var launcher in _service.Manifest.Launchers)
        {
            switch (IntegrationService.LauncherFileProblem(launcher))
            {
                case "missing":
                    problems.Add(new IntegrationProblem(IntegrationProblemKind.LauncherMissing, launcher.Path));
                    break;
                case not null:
                    problems.Add(new IntegrationProblem(IntegrationProblemKind.LauncherReplaced, launcher.Path));
                    break;
            }
        }

        if (_settings.StatuslineEnabled)
        {
            foreach (var folder in _service.StatuslineStatus(_folders(), _gitBash()))
            {
                // A folder with a status line of its own is the user's choice, not a fault.
                if (folder.Problem is not null)
                {
                    problems.Add(new IntegrationProblem(IntegrationProblemKind.SettingsUnusable, folder.Problem));
                }
                else if (folder.Status == StatuslineInstallStatus.NotInstalled)
                {
                    problems.Add(new IntegrationProblem(IntegrationProblemKind.StatuslineNotInstalled, folder.ConfigDirectory));
                }
                else if (folder.Status == StatuslineInstallStatus.Outdated)
                {
                    problems.Add(new IntegrationProblem(IntegrationProblemKind.StatuslineOutdated, folder.ConfigDirectory));
                }
            }
        }

        if (_settings.HudEnabled)
        {
            switch (_hud.Status)
            {
                case HudServerStatus.PortBusy:
                    problems.Add(new IntegrationProblem(IntegrationProblemKind.HudPortBusy, null));
                    break;
                case HudServerStatus.Failed or HudServerStatus.Stopped:
                    problems.Add(new IntegrationProblem(IntegrationProblemKind.HudFailed, _hud.Problem));
                    break;
                default:
                    foreach (var folder in _hud.FolderStatuses())
                    {
                        if (folder.Problem is not null)
                        {
                            problems.Add(new IntegrationProblem(IntegrationProblemKind.SettingsUnusable, folder.Problem));
                        }
                        else if (folder.Status == HookInstallStatus.Outdated)
                        {
                            problems.Add(new IntegrationProblem(IntegrationProblemKind.HooksOutdated, folder.ConfigDirectory));
                        }
                        else if (folder.Status != HookInstallStatus.Installed)
                        {
                            problems.Add(new IntegrationProblem(IntegrationProblemKind.HooksIncomplete, folder.ConfigDirectory));
                        }
                    }

                    break;
            }
        }

        return problems;
    }

    /// <summary>
    /// Reinstalls from this build what is missing or broken and rewrites only the tracker's own entries. A file
    /// or entry that is no longer the tracker's is never overwritten. Returns what could still not be repaired.
    /// </summary>
    public IReadOnlyList<IntegrationProblem> Repair()
    {
        if (_service.LoadProblem is null)
        {
            _service.Recover();
            if (_settings.StatuslineEnabled || _service.Manifest.Helper is not null || _service.Manifest.Launchers.Count > 0)
            {
                _service.InstallHelper();
            }

            if (_settings.StatuslineEnabled)
            {
                StatuslineConfigChanged();
                _service.ApplyStatusline(_folders(), _gitBash(), []);
            }

            if (_settings.HudEnabled)
            {
                // A port that is taken is given up for a new one; the entries then follow it.
                _hud.Start(newPort: _hud.Status == HudServerStatus.PortBusy);
            }

            SyncStatusline();
        }

        Changed?.Invoke();
        return Problems();
    }
}
