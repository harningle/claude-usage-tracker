using System.IO;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Notifications;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Notifications;

/// <summary>
/// Notifications and profile auto-switching from fresh usage (upstream <c>NotificationManager</c> and
/// <c>MenuBarManager.checkAutoSwitchIfNeeded</c>). It acts only on usage that a check of this run has just fetched:
/// cached or stale values never trigger anything (Milestone 4 acceptance). What was notified is remembered in
/// <see cref="AutomationStateStore"/>, so a restart neither repeats an alert nor misses one in a new window. Use it
/// from the UI thread only.
/// </summary>
public sealed class AutomationService
{
    /// <summary>Upstream treats a refresh within this long of a profile switch or saved credentials as user-triggered.</summary>
    internal static readonly TimeSpan UserTriggerWindow = TimeSpan.FromSeconds(5);

    /// <summary>Upstream removes its "Usage Updated" notification after this long.</summary>
    internal static readonly TimeSpan UsageUpdatedLifetime = TimeSpan.FromSeconds(2);

    /// <summary>Upstream warns this long before the Console session key expires.</summary>
    internal static readonly TimeSpan ConsoleExpiryWarning = TimeSpan.FromHours(24);

    private readonly AppSettings _settings;
    private readonly INotifier _notifier;
    private readonly AutomationStateStore _store;
    private readonly TimeProvider _time;
    private readonly Func<Guid, UsageData?> _lastKnown;
    private readonly Action<ProfileSettings, ProfileSettings> _autoSwitch;
    private readonly AutomationState _state;
    private readonly Dictionary<Guid, double> _previousEffective = [];
    private readonly HashSet<Guid> _autoSwitched = [];
    private readonly Dictionary<Guid, DateTimeOffset> _userTriggered = [];

    /// <param name="lastKnown">A profile's last known usage, live or cached, for choosing an auto-switch target.</param>
    /// <param name="autoSwitch">Activates the second profile because the first reached its session limit.</param>
    public AutomationService(
        AppSettings settings, INotifier notifier, AutomationStateStore store, TimeProvider time,
        Func<Guid, UsageData?> lastKnown, Action<ProfileSettings, ProfileSettings> autoSwitch)
    {
        _settings = settings;
        _notifier = notifier;
        _store = store;
        _time = time;
        _lastKnown = lastKnown;
        _autoSwitch = autoSwitch;
        try
        {
            _state = store.Load();
        }
        catch (SettingsStoreException e)
        {
            // Reported and never overwritten; this run remembers in memory only, so a restart may repeat an alert.
            StateProblem = e.Message;
            _state = new AutomationState();
        }
    }

    /// <summary>The automation state file could not be used; it is left as it is.</summary>
    public string? StateProblem { get; private set; }

    public INotifier Notifier => _notifier;

    /// <summary>The persisted memory of one profile, for the session auto-start.</summary>
    public ProfileAutomationState StateOf(Guid profileId) => _state.For(profileId);

    /// <summary>A profile switch or saved credentials: the next successful check soon after is shown as "Usage Updated".</summary>
    public void UserTriggered(Guid profileId) => _userTriggered[profileId] = _time.GetUtcNow();

    /// <summary>Upstream confirms with a notification when the user switches notifications on.</summary>
    public void NotificationsSwitchedOn(ProfileSettings profile) =>
        _notifier.Show(new AppAlert(
            Strings.Get("notification.enabled.title"), Strings.Get("notification.enabled.message"), profile.Id,
            profile.NotificationSettings.SoundName));

    /// <summary>A check of this run fetched new usage for a profile.</summary>
    public void UsageFetched(ProfileSettings profile, UsageData usage)
    {
        var now = _time.GetUtcNow();
        var notifications = profile.NotificationSettings;
        var memory = _state.For(profile.Id);
        var known = new SessionAlertState(memory.SessionWindowResetAt, memory.NotifiedLevels);
        var previous = _previousEffective.TryGetValue(profile.Id, out var value) ? value : (double?)null;
        var evaluation = UsageAlerts.Evaluate(notifications.Thresholds, known, previous, usage, now);
        foreach (var alert in evaluation.Alerts)
        {
            _notifier.Show(new AppAlert(
                $"{profile.Name} - {Title(alert.Kind)}", Message(alert, now), profile.Id, notifications.SoundName));
        }

        if (evaluation.Effective is { } effective)
        {
            _previousEffective[profile.Id] = effective;
            if (effective < AutoSwitch.Limit)
            {
                // Below the limit again, also while another profile is the active one: its next limit may switch again.
                _autoSwitched.Remove(profile.Id);
            }
        }

        if (evaluation.State.WindowResetAt != memory.SessionWindowResetAt || !evaluation.State.NotifiedLevels.SequenceEqual(memory.NotifiedLevels))
        {
            memory.SessionWindowResetAt = evaluation.State.WindowResetAt;
            memory.NotifiedLevels = [.. evaluation.State.NotifiedLevels];
            Save();
        }

        if (_userTriggered.Remove(profile.Id, out var triggeredAt) && now - triggeredAt <= UserTriggerWindow && notifications.Enabled)
        {
            UsageUpdated(profile);
        }

        if (profile.Id == _settings.ActiveProfile?.Id && evaluation.Effective is { } activeEffective)
        {
            CheckAutoSwitch(profile, activeEffective, now);
        }
    }

    /// <summary>Posts the Console session-expiry warning once per expiry, within upstream's 24 hours before it.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        foreach (var profile in _settings.Profiles)
        {
            if (profile.Console?.SessionKeyExpiry is not { } expiry || now >= expiry || now < expiry - ConsoleExpiryWarning
                || !profile.NotificationSettings.Enabled)
            {
                continue;
            }

            var memory = _state.For(profile.Id);
            if (memory.ConsoleExpiryNotifiedFor == expiry)
            {
                continue;
            }

            _notifier.Show(new AppAlert(
                $"{profile.Name} - {Strings.Get("windows.notification.session_key_expiring.title")}",
                Strings.Format("windows.notification.session_key_expiring.message", expiry.TimeRemainingString(now)),
                profile.Id, profile.NotificationSettings.SoundName));
            memory.ConsoleExpiryNotifiedFor = expiry;
            Save();
        }
    }

    /// <summary>Upstream <c>sendAutoStartNotification</c>, gated by the profile's notification setting.</summary>
    public void AutoStartNotification(ProfileSettings profile, string? failure)
    {
        if (!profile.NotificationSettings.Enabled)
        {
            return;
        }

        var (title, message) = failure is null
            ? (Strings.Get("notification.session_auto_started.title"), Strings.Get("notification.session_auto_started.message"))
            : (Strings.Get("notification.session_auto_start_failed.title"),
               $"{Strings.Get("notification.session_auto_start_failed.message")} {failure}");
        _notifier.Show(new AppAlert($"{profile.Name} - {title}", message, profile.Id, profile.NotificationSettings.SoundName));
    }

    /// <summary>Saves the remembered state; does nothing while the file on disk is damaged.</summary>
    public void Save()
    {
        if (StateProblem is not null)
        {
            return;
        }

        foreach (var removed in _state.Profiles.Keys.Where(id => _settings.Profiles.All(p => p.Id != id)).ToList())
        {
            _state.Profiles.Remove(removed);
        }

        try
        {
            _store.Save(_state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The state in memory is right; the next change saves it again. A restart before then may repeat an alert.
        }
    }

    /// <summary>
    /// Upstream: when the active profile's session reaches 100 %, activate the next available profile, once until
    /// the session drops below 100 % again, and notify with the settings of the profile that reached the limit.
    /// </summary>
    private void CheckAutoSwitch(ProfileSettings active, double effective, DateTimeOffset now)
    {
        if (!_settings.AutoSwitchProfileEnabled || _settings.Profiles.Count <= 1)
        {
            return;
        }

        if (effective < AutoSwitch.Limit || _autoSwitched.Contains(active.Id))
        {
            return;
        }

        // Unlike upstream, a profile with no other to switch to is not marked: the next check tries again, so a switch
        // follows once another profile's window has reset.
        var profiles = _settings.Profiles;
        if (AutoSwitch.FindNext(profiles, profiles.IndexOf(active), p => p.UsageSource is not null, p => _lastKnown(p.Id), now) is not { } index)
        {
            return;
        }

        _autoSwitched.Add(active.Id);
        var target = profiles[index];
        _autoSwitch(active, target);
        if (active.NotificationSettings.Enabled)
        {
            _notifier.Show(new AppAlert(
                Strings.Get("notification.profile_auto_switched.title"),
                Strings.Format("notification.profile_auto_switched.message", active.Name, target.Name),
                target.Id, active.NotificationSettings.SoundName));
        }
    }

    /// <summary>Upstream's brief, silent "Usage Updated" after a user-triggered refresh, removed after two seconds.</summary>
    private void UsageUpdated(ProfileSettings profile)
    {
        var tag = $"usage-updated-{profile.Id:N}";
        _notifier.Show(new AppAlert(
            Strings.Get("notification.usage_updated.title"), Strings.Get("notification.usage_updated.body"), profile.Id, "none", tag));
        _ = Task.Delay(UsageUpdatedLifetime, _time).ContinueWith(_ => _notifier.Remove(tag), TaskScheduler.Default);
    }

    private static string Title(UsageAlertKind kind) => kind switch
    {
        UsageAlertKind.SessionInfo => Strings.Get("windows.notification.session_info.title"),
        UsageAlertKind.SessionWarning => Strings.Get("notification.session_warning.title"),
        UsageAlertKind.SessionCritical => Strings.Get("notification.session_critical.title"),
        _ => Strings.Get("notification.session_reset.title"),
    };

    /// <summary>Upstream's messages: the percentage with one decimal and "Resets in …" when a reset time is known.</summary>
    private static string Message(UsageAlert alert, DateTimeOffset now)
    {
        if (alert.Kind == UsageAlertKind.SessionReset)
        {
            return Strings.Get("notification.session_reset.message");
        }

        var percentage = string.Format(Strings.Culture, "{0:0.0}%", alert.Percentage);
        var reset = alert.ResetAt is { } at ? Strings.Format("menubar.resets_in", at.TimeRemainingString(now)) : "";
        var key = alert.Kind switch
        {
            UsageAlertKind.SessionInfo => "windows.notification.session_info.message",
            UsageAlertKind.SessionWarning => "notification.session_warning.message",
            _ => "notification.session_critical.message",
        };
        return Strings.Format(key, percentage, reset).TrimEnd();
    }
}
