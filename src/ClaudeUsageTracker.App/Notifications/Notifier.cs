using System.IO;
using System.Media;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App.Notifications;

/// <summary>One notification to post.</summary>
/// <param name="ProfileId">The profile a click opens, or null for none.</param>
/// <param name="SoundName">A <see cref="Storage.NotificationSettings.SoundName"/> raw value.</param>
/// <param name="Tag">Identifies the notification for later removal.</param>
/// <param name="Release">A release tag whose page a click opens, instead of the popover.</param>
public sealed record AppAlert(string Title, string Body, Guid? ProfileId, string SoundName, string? Tag = null, string? Release = null);

/// <summary>Posts notifications. The app uses Windows notifications; tests record what would be posted.</summary>
public interface INotifier
{
    /// <summary>Why notifications cannot be shown now (registration failed, switched off in Windows), or null.</summary>
    string? Problem { get; }

    void Show(AppAlert alert);

    /// <summary>Removes a posted notification by its tag, if it is still shown.</summary>
    void Remove(string tag);
}

/// <summary>
/// The notification sounds offered on Windows: the system's own non-looping notification sound events (the looping
/// alarm and call sounds are for alarm and call notifications) and silence. Upstream offered the files in
/// <c>/System/Library/Sounds</c>, which are Apple's and are not redistributed (plan.md section 2).
/// </summary>
public static class NotificationSounds
{
    /// <param name="SchemeEvent">The sound event in the Windows sound scheme, or null for silence.</param>
    public sealed record Option(string RawValue, string NameKey, string? SchemeEvent);

    public static readonly IReadOnlyList<Option> Options =
    [
        new("default", "notifications.sound.default", "Notification.Default"),
        new("im", "windows.notifications.sound.im", "Notification.IM"),
        new("mail", "windows.notifications.sound.mail", "Notification.Mail"),
        new("reminder", "windows.notifications.sound.reminder", "Notification.Reminder"),
        new("sms", "windows.notifications.sound.sms", "Notification.SMS"),
        new("none", "notifications.sound.none", null),
    ];

    /// <summary>The option for a raw value; upstream's macOS sound names and unknown values play the default sound.</summary>
    public static Option For(string? raw) => Options.FirstOrDefault(o => o.RawValue == raw) ?? Options[0];

    /// <summary>
    /// Plays the file the user's sound scheme assigns to the option's event, which is what the notification plays.
    /// Nothing plays for silence or for an event the scheme leaves empty.
    /// </summary>
    public static void Preview(string? raw)
    {
        if (For(raw).SchemeEvent is not { } schemeEvent)
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey($@"AppEvents\Schemes\Apps\.Default\{schemeEvent}\.Current");
        if (key?.GetValue(null) is string { Length: > 0 } file && File.Exists(Environment.ExpandEnvironmentVariables(file)))
        {
            try
            {
                using var player = new SoundPlayer(Environment.ExpandEnvironmentVariables(file));
                player.Play();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
            {
                // A file the player cannot read plays nothing, as the notification would.
            }
        }
    }
}
