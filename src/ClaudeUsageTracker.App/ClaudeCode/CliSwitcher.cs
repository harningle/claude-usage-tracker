using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Notifications;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.ClaudeCode;
using ClaudeUsageTracker.Core.Providers;

namespace ClaudeUsageTracker.App.ClaudeCode;

/// <summary>A switch waiting for Claude Code to close.</summary>
/// <param name="AutomaticFrom">For a switch requested by auto-switch rather than by the user: the profile that reached its limit.</param>
/// <param name="ConfirmedOccupant">The login the user agreed to sign out, if the switch needed that.</param>
/// <param name="FromActivation">Requested by picking the profile rather than on the CLI Account page.</param>
public sealed record PendingCliSwitch(
    Guid TargetProfileId, Guid? AutomaticFrom, string? ConfirmedOccupant, DateTimeOffset RequestedAt, bool FromActivation = false)
{
    public bool Automatic => AutomaticFrom is not null;

    /// <summary>
    /// Whether its wait for Claude Code was reported. One queued while another switch ran is reported at its first attempt
    /// that finds Claude Code running, since only then is that why it waits.
    /// </summary>
    public bool WaitReported { get; init; }
}

/// <summary>What the CLI Account page shows about switching Claude Code to the active profile's account.</summary>
/// <param name="InUse">Claude Code's shared folder holds this account now.</param>
/// <param name="CanSwitch">The profile has a login copy that could be written there.</param>
/// <param name="Pending">A switch to this profile is waiting for Claude Code to close.</param>
/// <param name="Message">The outcome of the last switch to this profile, or why it waits.</param>
/// <param name="NeedsSignIn">An unfinished switch left the folder unclear: the user must sign in again in Claude Code and say so.</param>
public sealed record CliSwitchView(bool InUse, bool CanSwitch, bool Pending, string? Message, string Folder, bool NeedsSignIn = false);

/// <summary>
/// The "Use in Claude Code" action, the switch that picking a profile asks for, and auto-switch's switches, on top of
/// <see cref="CliLinkService.SwitchAsync"/> (plan.md section 2). Viewing a profile never switches Claude Code. A switch
/// that has to wait because Claude Code is or may be running is kept, one at a time, shown in the popover and the
/// settings and tried again every few seconds; it can be cancelled. The queue is in memory only, so a restart drops it.
/// Each change of the waiting switch, and the outcome of each attempt that ends a request, is written to the network log
/// (see the private Log method). An unfinished switch from an earlier run is settled first. Use it from the UI thread only.
/// </summary>
public sealed class CliSwitcher
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly CliLinkService _links;
    private readonly ProfileService _profiles;
    private readonly AutomationService _automation;
    private readonly Func<CliConfigDirectory> _sharedFolder;
    private readonly Action _applied;
    private readonly TimeProvider _time;
    private readonly RequestLog _log;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, string> _messages = [];
    private long _requests;
    private bool _busy;
    private bool _needsSignIn;

    /// <param name="sharedFolder">The folder the plain <c>claude</c> command uses: <c>CLAUDE_CONFIG_DIR</c> or the default.</param>
    /// <param name="applied">Polls and redraws after a switch changed a profile's link.</param>
    public CliSwitcher(
        AppSettings settings, CliLinkService links, ProfileService profiles, AutomationService automation,
        Func<CliConfigDirectory> sharedFolder, Action applied, TimeProvider time, Dispatcher dispatcher, RequestLog log)
    {
        _settings = settings;
        _links = links;
        _profiles = profiles;
        _automation = automation;
        _sharedFolder = sharedFolder;
        _applied = applied;
        _time = time;
        _log = log;
        _timer = new DispatcherTimer(RetryInterval, DispatcherPriority.Background, async (_, _) => await TickAsync(), dispatcher);
    }

    public PendingCliSwitch? Pending { get; private set; }

    /// <summary>Raised when the pending switch or a message changed, so the settings page can be refreshed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Switches Claude Code to the profile's account now, or queues the switch when Claude Code is running. Returns the
    /// outcome; <see cref="CliSwitchOutcome.NeedsConfirmation"/> asks the caller to confirm and call again.
    /// </summary>
    /// <param name="automaticFrom">Set by auto-switch: the profile that reached its limit, whose account Claude Code is on.</param>
    /// <param name="fromActivation">
    /// Asked for by picking the profile. Its outcome is announced by a notification even when it switches at once, and it
    /// never asks to sign out a login no profile follows: such a login is left as it is.
    /// </param>
    public async Task<CliSwitchResult> RequestAsync(ProfileSettings target, Guid? automaticFrom, string? confirmedOccupant, bool fromActivation = false)
    {
        var automatic = automaticFrom is not null;
        // The newest request is the one that counts: a switch still waiting for another profile is dropped.
        var request = ++_requests;
        if (Pending is { } replaced)
        {
            Log("DROP", replaced, "superseded");
        }

        Pending = null;
        if (_busy)
        {
            // Another switch or a settlement is running: this one waits its turn rather than being lost, and its outcome
            // is reported when it runs.
            Pending = new PendingCliSwitch(target.Id, automaticFrom, confirmedOccupant, _time.GetUtcNow(), fromActivation);
            Log("QUEUE", Pending, "busy");
            Changed?.Invoke();
            KeepTrying();
            return new CliSwitchResult(CliSwitchOutcome.Deferred);
        }

        var started = Stopwatch.GetTimestamp();
        var result = await RunAsync(target.Id, confirmedOccupant, automaticFrom);
        var duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (result.Refusal == CliSwitchRefusal.NoLongerApplies)
        {
            // Dropped as a queued one would be: nothing was changed, so there is nothing to report. A switch that was
            // waiting was dropped by this request, so the views are drawn again.
            Log("RUN", target.Id, automatic, fromActivation, Category(result), duration);
            Changed?.Invoke();
            KeepTrying();
            return result;
        }

        if (result.Outcome == CliSwitchOutcome.Deferred)
        {
            if (request != _requests)
            {
                // A newer request made meanwhile is the one that waits; this one is dropped, as it would have been queued.
                Log("DROP", target.Id, automatic, fromActivation, "superseded", duration);
                KeepTrying();
                return result;
            }

            Pending = new PendingCliSwitch(target.Id, automaticFrom, confirmedOccupant, _time.GetUtcNow(), fromActivation) { WaitReported = true };
            Log("QUEUE", Pending, "claudeCodeRunning", duration);
        }
        else
        {
            Log("RUN", target.Id, automatic, fromActivation, Category(result), duration);
        }

        Report(target.Id, result, automatic, queued: false, fromActivation);
        KeepTrying();
        return result;
    }

    /// <summary>
    /// The user has signed in again in Claude Code after an unfinished switch left its folder unclear: the folder's login
    /// is now Claude Code's own, so the switch's record is closed (or the switch is settled, if it can be after all).
    /// </summary>
    public async Task SignedInAgainAsync() => await SettleAsync(signedInAgain: true);

    /// <summary>The user's Cancel Switch, in the popover or on the CLI Account page.</summary>
    public void Cancel() => Drop("CANCEL", null);

    /// <summary>The app is closing: a waiting switch ends with it, since the queue is in memory only, and is logged as dropped.</summary>
    public void DropOnExit() => Drop("DROP", "appClosed");

    /// <summary>Drops the waiting switch, if there is one, and logs why: the user's cancel, or a reason.</summary>
    private void Drop(string method, string? reason)
    {
        if (Pending is { } pending)
        {
            Pending = null;
            _messages.Remove(pending.TargetProfileId);
            Log(method, pending, reason);
            Changed?.Invoke();
        }
    }

    /// <summary>Settles a switch an earlier run (or this one) left unfinished; called at startup and before each queued attempt.</summary>
    public async Task SettleAsync(bool signedInAgain = false)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        CliSwitchResult? settled;
        try
        {
            settled = await _links.SettleSwitchAsync(LinkedProfiles(), signedInAgain, CancellationToken.None);
        }
        finally
        {
            _busy = false;
        }

        if (settled is null)
        {
            return;
        }

        _needsSignIn = settled is { Outcome: CliSwitchOutcome.RepairNeeded, Refusal: CliSwitchRefusal.SignInAgain };
        if (settled is { Outcome: CliSwitchOutcome.Switched, ProfileId: { } target })
        {
            Relink(target);
        }

        // A deferred settlement is not news; anything else is shown until the next switch.
        if (settled.Outcome != CliSwitchOutcome.Deferred || !_messages.ContainsKey(Guid.Empty))
        {
            _messages[Guid.Empty] = Describe(settled, _settings.Profiles.FirstOrDefault(p => p.Id == settled.ProfileId));
        }

        KeepTrying();
        Changed?.Invoke();
    }

    /// <summary>The page's view of the active profile.</summary>
    public CliSwitchView ViewFor(ProfileSettings profile, CliSnapshot? shared)
    {
        var folder = _sharedFolder();
        var link = profile.CliLink;
        var inUse = link is not null && shared?.Credentials is not null
                    && CliLoginLabels.Compare(link.Identity, shared.Account?.Identity, null, null) == CliAccountMatch.SameAccount;
        var message = _messages.GetValueOrDefault(profile.Id) ?? _messages.GetValueOrDefault(Guid.Empty);
        return new CliSwitchView(
            inUse, link is not null && profile.ProviderKind.Descriptor().Capabilities.CliAccountSync && !inUse,
            Pending?.TargetProfileId == profile.Id, message, folder.Path, _needsSignIn && _links.SwitchUnsettled);
    }

    /// <summary>A short text for an outcome, for the settings page and message boxes.</summary>
    public string Describe(CliSwitchResult result, ProfileSettings? target)
    {
        var name = target?.Name ?? "";
        return result.Outcome switch
        {
            CliSwitchOutcome.Switched => Strings.Format("windows.cli.switch.done", name),
            CliSwitchOutcome.AlreadyInUse => Strings.Get("windows.cli.switch.already"),
            CliSwitchOutcome.Deferred => Strings.Get("windows.cli.switch.waiting"),
            CliSwitchOutcome.NeedsConfirmation => Strings.Format("windows.cli.switch.confirm.message", result.Detail ?? ""),
            CliSwitchOutcome.Conflict => Strings.Get("windows.cli.switch.conflict"),
            CliSwitchOutcome.RepairNeeded when result.Refusal == CliSwitchRefusal.SignInAgain => Strings.Get("windows.cli.switch.sign_in_again"),
            CliSwitchOutcome.RepairNeeded => Strings.Format("windows.cli.switch.repair", result.Detail ?? ""),
            _ => result.Refusal switch
            {
                CliSwitchRefusal.RenewalFailed => Strings.Get("windows.cli.switch.refused.renewal_failed"),
                CliSwitchRefusal.Superseded => Strings.Get("windows.cli.switch.superseded"),
                CliSwitchRefusal.UsedElsewhere => Strings.Get("windows.cli.switch.refused.used_elsewhere"),
                CliSwitchRefusal.HeldByOtherFolder => Strings.Format("windows.cli.switch.refused.other_folder", result.Detail ?? ""),
                CliSwitchRefusal.FolderUnreadable => Strings.Format("windows.cli.switch.refused.unreadable", result.Detail ?? ""),
                CliSwitchRefusal.HandBackPending => Strings.Get("windows.cli.switch.refused.hand_back"),
                CliSwitchRefusal.LoginRejected => Strings.Get("windows.cli.switch.refused.rejected"),
                CliSwitchRefusal.SwitchUnsettled => Strings.Get("windows.cli.switch.refused.unsettled"),
                CliSwitchRefusal.CopyUnreadable or CliSwitchRefusal.StorageFailed => Strings.Format("windows.cli.switch.refused.storage", result.Detail ?? ""),
                _ => Strings.Get("windows.cli.switch.refused.no_copy"),
            },
        };
    }

    internal async Task TickAsync()
    {
        // A tick never overlaps a running switch or settlement (one can hold the credential lock through a renewal).
        if (_busy)
        {
            return;
        }

        await SettleAsync();
        if (_busy || Pending is not { } pending)
        {
            KeepTrying();
            return;
        }

        if (_settings.Profiles.FirstOrDefault(p => p.Id == pending.TargetProfileId) is not { CliLink: not null } target)
        {
            Pending = null;
            Log("DROP", pending, "profileUnlinked");
            Changed?.Invoke();
            KeepTrying();
            return;
        }

        if (pending.AutomaticFrom is { } origin)
        {
            var applies = AutomaticStillApplies(origin);
            if (applies != true)
            {
                if (applies == false)
                {
                    // What the automatic switch was queued for no longer holds, so it is dropped rather than applied late.
                    Drop("DROP", "noLongerApplies");
                }

                KeepTrying();
                return;
            }
        }

        var started = Stopwatch.GetTimestamp();
        var result = await RunAsync(target.Id, pending.ConfirmedOccupant, pending.AutomaticFrom);
        var duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // A newer request made meanwhile replaced this one and stays queued.
        if (result.Outcome == CliSwitchOutcome.Deferred && ReferenceEquals(Pending, pending) && !pending.WaitReported)
        {
            Pending = pending with { WaitReported = true };
            Report(target.Id, result, pending.Automatic, queued: true, pending.FromActivation);
        }
        else if (result.Outcome != CliSwitchOutcome.Deferred && ReferenceEquals(Pending, pending))
        {
            if (result.Refusal == CliSwitchRefusal.NoLongerApplies)
            {
                Drop("DROP", "noLongerApplies");
            }
            else
            {
                Pending = null;
                Log("RUN", pending, Category(result), duration);
                Report(target.Id, result, pending.Automatic, queued: true, pending.FromActivation);
            }
        }
        else if (result.Outcome != CliSwitchOutcome.Deferred)
        {
            // Cancelled or replaced while it ran (as while it waited for the credential lock): it may still have switched
            // Claude Code, so its outcome is logged after the cancel or the drop.
            Log("RUN", pending, Category(result), duration);
        }

        KeepTrying();
    }

    /// <summary>
    /// Whether a queued automatic switch should still happen (see <see cref="AutomaticConditions"/>). Null while the
    /// folder cannot be read, which decides nothing.
    /// </summary>
    private bool? AutomaticStillApplies(Guid fromProfileId)
    {
        var shared = _links.Read(_sharedFolder());
        return shared.State is CliLoginState.Unreadable or CliLoginState.Invalid ? null : AutomaticConditions(fromProfileId)(shared);
    }

    /// <summary>
    /// What an automatic switch depends on, for a read of Claude Code's folder: auto-switch is still on and Claude Code is
    /// still signed in to the account that reached its limit. The switch asks it again from another thread just before it
    /// writes, so the profile's identity is taken here and only the setting is read later.
    /// </summary>
    private Func<CliSnapshot, bool> AutomaticConditions(Guid fromProfileId)
    {
        var identity = _settings.Profiles.FirstOrDefault(p => p.Id == fromProfileId)?.CliLink?.Identity;
        return shared => _settings.AutoSwitchProfileEnabled && shared.Credentials is not null
                         && CliLoginLabels.Compare(identity, shared.Account?.Identity, null, null) == CliAccountMatch.SameAccount;
    }

    /// <summary>Runs the retry timer while a switch waits or an unfinished one has to be settled.</summary>
    private void KeepTrying()
    {
        if (Pending is not null || _links.SwitchUnsettled)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private async Task<CliSwitchResult> RunAsync(Guid targetId, string? confirmedOccupant, Guid? automaticFrom)
    {
        _busy = true;
        try
        {
            var folder = _sharedFolder();
            // A folder that already holds the account needs no switch, so none is left waiting for Claude Code to close.
            if (_settings.Profiles.FirstOrDefault(p => p.Id == targetId)?.CliLink is { } link
                && _links.Read(folder) is { Credentials: not null } shared
                && CliLoginLabels.Compare(link.Identity, shared.Account?.Identity, null, null) == CliAccountMatch.SameAccount)
            {
                return new CliSwitchResult(CliSwitchOutcome.AlreadyInUse, Detail: folder.Path);
            }

            var stillWanted = automaticFrom is { } origin ? AutomaticConditions(origin) : null;
            var result = await _links.SwitchAsync(targetId, LinkedProfiles(), folder, confirmedOccupant, stillWanted, CancellationToken.None);
            if (result.Outcome == CliSwitchOutcome.Switched)
            {
                Relink(targetId);
            }

            return result;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The switched profile now follows its account in the shared folder.</summary>
    private void Relink(Guid targetId)
    {
        var shared = _sharedFolder();
        if (_settings.Profiles.FirstOrDefault(p => p.Id == targetId)?.CliLink is { } link
            && !string.Equals(CliStore.Normalise(link.ConfigDirectory), shared.Path, StringComparison.OrdinalIgnoreCase))
        {
            var (path, isDefault) = (link.ConfigDirectory, link.IsDefaultDirectory);
            try
            {
                _profiles.Change(
                    () => (link.ConfigDirectory, link.IsDefaultDirectory) = (shared.Path, shared.IsDefault),
                    () => (link.ConfigDirectory, link.IsDefaultDirectory) = (path, isDefault));
            }
            catch (SetupException)
            {
                // The link keeps its old folder; the profile follows its account there and through its copy.
            }
        }

        _applied();
    }

    private void Report(Guid targetId, CliSwitchResult result, bool automatic, bool queued, bool fromActivation)
    {
        var target = _settings.Profiles.FirstOrDefault(p => p.Id == targetId);
        _messages[targetId] = fromActivation && result.Outcome == CliSwitchOutcome.NeedsConfirmation
            ? Strings.Format("windows.cli.switch.untracked_kept", result.Detail ?? "")
            : Describe(result, target);
        _messages.Remove(Guid.Empty);
        Changed?.Invoke();
        if (target is null || !target.NotificationSettings.Enabled)
        {
            return;
        }

        // A switch the user is not watching happen on the CLI Account page is announced: an automatic one, one that
        // waited for Claude Code, or one asked for by picking the profile. An automatic or picked one is also announced,
        // once, when it is found to wait for Claude Code.
        var announced = automatic || queued || fromActivation;
        if (result.Outcome == CliSwitchOutcome.Switched && announced)
        {
            _automation.Notifier.Show(new AppAlert(
                Strings.Get("windows.cli.switch.notification.title"), Strings.Format("windows.cli.switch.done", target.Name), target.Id, "none"));
        }
        else if (result.Outcome == CliSwitchOutcome.Deferred && (automatic || fromActivation))
        {
            _automation.Notifier.Show(new AppAlert(
                Strings.Get("windows.cli.switch.notification.title"), Strings.Format("windows.cli.switch.deferred", target.Name), target.Id,
                target.NotificationSettings.SoundName));
        }
        else if (announced
                 && result.Outcome is CliSwitchOutcome.Refused or CliSwitchOutcome.Conflict or CliSwitchOutcome.RepairNeeded or CliSwitchOutcome.NeedsConfirmation)
        {
            _automation.Notifier.Show(new AppAlert(
                Strings.Get("windows.cli.switch.notification.title"), _messages[targetId], target.Id, target.NotificationSettings.SoundName));
        }
    }

    /// <summary>
    /// Writes a switch event to the network log shown in Debug Tools, so that whether a switch waits, and how it stopped
    /// waiting, can be told afterwards (the user's request of 2026-10-15). It holds the log's allowlisted fields and the
    /// target profile's local ID: the operation <c>cliSwitch</c>; the method QUEUE, CANCEL (the user's), DROP or RUN (an
    /// attempt that ended the request); who asked as the route (picked, automatic, or manual on the CLI Account page); and
    /// why it waits, why it was dropped, or the attempt's outcome as the category. Retries that find Claude Code still
    /// running change nothing and are not logged.
    /// </summary>
    private void Log(string method, Guid targetId, bool automatic, bool fromActivation, string? category, long durationMs = 0) =>
        _log.Append(new RequestLogEntry(
            _time.GetUtcNow(), "cliSwitch", method, automatic ? "automatic" : fromActivation ? "picked" : "manual", null, durationMs,
            "Claude Code folder", category, targetId));

    private void Log(string method, PendingCliSwitch pending, string? category, long durationMs = 0) =>
        Log(method, pending.TargetProfileId, pending.Automatic, pending.FromActivation, category, durationMs);

    /// <summary>An attempt's outcome for the log: null for a switch made, otherwise its refusal or outcome, e.g. <c>alreadyInUse</c>.</summary>
    private static string? Category(CliSwitchResult result) => result.Outcome == CliSwitchOutcome.Switched
        ? null
        : JsonNamingPolicy.CamelCase.ConvertName(result.Refusal?.ToString() ?? result.Outcome.ToString());

    /// <summary>Copies of every Anthropic profile's Claude Code link, so the switch never reads objects the UI edits.</summary>
    private List<LinkedProfile> LinkedProfiles() =>
    [
        .. _settings.Profiles
            .Where(p => p.CliLink is not null && p.ProviderKind == Provider.Anthropic)
            .Select(p => new LinkedProfile(p.Id, ProfileWatch.From(p).Cli!)),
    ];
}
