using System.IO;
using System.Runtime.InteropServices;
using ClaudeUsageTracker.App.Localisation;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ClaudeUsageTracker.App.Notifications;

/// <summary>
/// Windows notifications through the Windows App SDK's <c>AppNotificationManager</c>, unpackaged and self-contained,
/// as the Milestone 1 spike verified (plan.md section 2). <see cref="Register"/> registers this executable's path as
/// the notification COM server, so a click reaches the running instance, or starts the app when none runs.
/// Arguments carry only the profile ID, an update's release tag and whether the notification came from fixture mode;
/// never usage data.
/// </summary>
public sealed class ToastNotifier : INotifier, IDisposable
{
    private const string ProfileArgument = "profile";
    private const string ReleaseArgument = "release";
    private const string ModeArgument = "mode";
    private const string FixtureMode = "fixture";

    private readonly bool _fixture;
    private readonly Action<Action> _post;
    private string? _registrationError;
    private bool _registered;

    private ToastNotifier(bool fixture, Action<Action> post)
    {
        _fixture = fixture;
        _post = post;
    }

    /// <summary>Raised on the UI thread when a notification of the running app is clicked, with its profile.</summary>
    public event Action<Guid?>? Clicked;

    /// <summary>Raised on the UI thread when an update notification of the running app is clicked, with its release tag.</summary>
    public event Action<string>? ReleaseClicked;

    /// <summary>
    /// Registers for notification clicks. The click handler is attached first, so clicks reach this process (the
    /// documented order). A failure is kept as <see cref="Problem"/>; notifications are then not posted.
    /// </summary>
    /// <param name="iconFile">A PNG for the notification header, or null for the shell's default.</param>
    public static ToastNotifier Register(string displayName, string? iconFile, bool fixture, Action<Action> post)
    {
        var notifier = new ToastNotifier(fixture, post);
        try
        {
            AppNotificationManager.Default.NotificationInvoked += notifier.OnInvoked;
            if (iconFile is not null && File.Exists(iconFile))
            {
                AppNotificationManager.Default.Register(displayName, new Uri(iconFile));
            }
            else
            {
                AppNotificationManager.Default.Register();
            }

            notifier._registered = true;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or DllNotFoundException or TypeInitializationException)
        {
            notifier._registrationError = Strings.Format("windows.notifications.unavailable", e.Message);
        }

        return notifier;
    }

    /// <summary>
    /// How this process was started by a notification click while the app was not running: the clicked profile or
    /// update's release, and whether the notification came from fixture mode. Null when it was not started by a notification.
    /// </summary>
    public (Guid? ProfileId, bool FromFixture, string? Release)? ColdActivation()
    {
        if (!_registered)
        {
            return null;
        }

        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            return activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs args
                ? Parse(args.Arguments)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public string? Problem
    {
        get
        {
            if (_registrationError is not null)
            {
                return _registrationError;
            }

            try
            {
                return AppNotificationManager.Default.Setting switch
                {
                    AppNotificationSetting.Enabled => null,
                    AppNotificationSetting.DisabledForUser => Strings.Get("windows.notifications.off_for_user"),
                    AppNotificationSetting.DisabledForApplication => Strings.Get("windows.notifications.off_for_app"),
                    AppNotificationSetting.DisabledByGroupPolicy => Strings.Get("windows.notifications.off_by_policy"),
                    _ => Strings.Get("windows.notifications.off_for_app"),
                };
            }
            catch (COMException e)
            {
                return Strings.Format("windows.notifications.unavailable", e.Message);
            }
        }
    }

    public void Show(AppAlert alert)
    {
        if (!_registered)
        {
            return;
        }

        var builder = new AppNotificationBuilder()
            .AddText(alert.Title)
            .AddText(_fixture ? $"{Strings.Get("windows.fixture.label")}: {alert.Body}" : alert.Body);
        if (alert.ProfileId is { } profileId)
        {
            builder.AddArgument(ProfileArgument, profileId.ToString("D"));
        }

        if (alert.Release is { } release)
        {
            builder.AddArgument(ReleaseArgument, release);
        }

        if (_fixture)
        {
            builder.AddArgument(ModeArgument, FixtureMode);
        }

        if (NotificationSounds.For(alert.SoundName).SchemeEvent is null)
        {
            builder.MuteAudio();
        }
        else
        {
            builder.SetAudioEvent(alert.SoundName switch
            {
                "im" => AppNotificationSoundEvent.IM,
                "mail" => AppNotificationSoundEvent.Mail,
                "reminder" => AppNotificationSoundEvent.Reminder,
                "sms" => AppNotificationSoundEvent.SMS,
                _ => AppNotificationSoundEvent.Default,
            });
        }

        if (alert.Tag is { } tag)
        {
            builder.SetTag(tag);
        }

        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (COMException)
        {
            // Windows refused the notification (for example notifications are switched off); Problem says why.
        }
    }

    public void Remove(string tag)
    {
        if (_registered)
        {
            _ = AppNotificationManager.Default.RemoveByTagAsync(tag).AsTask().ContinueWith(
                t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>
    /// Ends this process's registration as the click handler, so a later click starts the app again (documented
    /// for unpackaged apps). The registry registration stays; it belongs to this executable's path.
    /// </summary>
    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch (COMException)
            {
            }
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var (profileId, fromFixture, release) = Parse(args.Arguments);
        // A fixture notification clicked in a live instance, or the reverse, only opens the popover.
        if (fromFixture == _fixture && release is not null)
        {
            _post(() => ReleaseClicked?.Invoke(release));
            return;
        }

        var target = fromFixture == _fixture ? profileId : null;
        _post(() => Clicked?.Invoke(target));
    }

    private static (Guid? ProfileId, bool FromFixture, string? Release) Parse(IDictionary<string, string> arguments) =>
        (arguments.TryGetValue(ProfileArgument, out var raw) && Guid.TryParse(raw, out var id) ? id : null,
         arguments.TryGetValue(ModeArgument, out var mode) && mode == FixtureMode,
         arguments.TryGetValue(ReleaseArgument, out var release) ? release : null);
}
