using System.Globalization;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.App.Hud;

/// <summary>One session as the HUD shows it. Port of upstream's compact views and <c>NotchSessionRow</c>.</summary>
/// <param name="Secondary">The current task, else the last prompt of an idle session, else the status.</param>
public sealed record HudSessionRow(
    string Id, string Name, SessionStatus Status, string StatusText, string Secondary, bool HasError, string Elapsed) : ListItem
{
    /// <summary>A Segoe Fluent Icons glyph standing in for upstream's SF Symbol.</summary>
    public string Glyph => Status switch
    {
        SessionStatus.Thinking => "",
        SessionStatus.WritingCode => "",
        SessionStatus.ReadingFiles => "",
        SessionStatus.RunningCommand => "",
        SessionStatus.NeedsAttention => "",
        _ => "",
    };

    /// <summary>Upstream's status tints, on the HUD's own dark surface.</summary>
    public string Colour => Status switch
    {
        SessionStatus.Thinking => "#BF5AF2",
        SessionStatus.WritingCode or SessionStatus.ReadingFiles => "#64D2FF",
        SessionStatus.RunningCommand => "#30D158",
        SessionStatus.NeedsAttention => "#FF9F0A",
        _ => "#98989D",
    };

    public string AccessibleName => $"{Name}, {Secondary}, {Elapsed}";
}

/// <summary>Whether the HUD is on screen, and whether it should hide again after the idle delay.</summary>
public sealed record HudVisibility(bool Visible, bool HideAfterIdleDelay);

/// <summary>The HUD's texts and its visibility rule, without any window. Ports of upstream <c>NotchHUDController</c> and its views.</summary>
public static class HudPresentation
{
    /// <summary>Upstream <c>idleHideDelay</c>: how long the HUD stays once every session is idle.</summary>
    public static readonly TimeSpan IdleHideDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upstream's policy: hidden without sessions; shown for any work or a session that needs attention; with
    /// every session idle it is shown and, when auto-hide is on, hidden after the delay. It never expands by itself.
    /// </summary>
    public static HudVisibility Visibility(IReadOnlyList<ClaudeCodeSession> sessions, bool autoHide) =>
        sessions.Count == 0 ? new HudVisibility(false, false)
        : new HudVisibility(true, autoHide && sessions.All(s => s.Status == SessionStatus.Idle));

    /// <summary>The sessions in start order, as upstream's expanded list.</summary>
    public static IReadOnlyList<HudSessionRow> Rows(HudSessionStore store, DateTimeOffset now) =>
        [.. store.Sessions.OrderBy(s => s.StartTime).Select(s => Row(s, now))];

    /// <summary>The session of the compact view: the most urgent one.</summary>
    public static HudSessionRow? Primary(HudSessionStore store, DateTimeOffset now) =>
        store.PrimarySession is { } session ? Row(session, now) : null;

    public static HudSessionRow Row(ClaudeCodeSession session, DateTimeOffset now)
    {
        var status = StatusText(session.Status);
        var task = session.CurrentTask is { } current ? TaskText(current) : null;
        var secondary = !string.IsNullOrEmpty(task) ? task
            : session.Status == SessionStatus.Idle && session.LastUserPrompt is { } prompt ? prompt
            : status;
        return new HudSessionRow(session.Id, session.DisplayName, session.Status, status, secondary, session.HasRecentError, Elapsed(now - session.StartTime));
    }

    public static string StatusText(SessionStatus status) => Strings.Get(status switch
    {
        SessionStatus.Thinking => "notch.status.thinking",
        SessionStatus.WritingCode => "notch.status.writing_code",
        SessionStatus.ReadingFiles => "notch.status.reading_files",
        SessionStatus.RunningCommand => "notch.status.running_command",
        SessionStatus.NeedsAttention => "notch.status.needs_attention",
        _ => "notch.status.idle",
    });

    /// <summary>Upstream's task strings, formatted here because Core carries only their keys (docs/parity.md, difference 5).</summary>
    public static string TaskText(HudTaskText task) =>
        task.LocalisationKey is not { } key ? task.Argument ?? ""
        : task.Argument is { } argument ? Strings.Format(key, argument)
        : Strings.Get(key);

    /// <summary>Upstream's elapsed label: "m:ss", or "1h 05m" from an hour on.</summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        var minutes = seconds / 60;
        return minutes >= 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60}h {minutes % 60:00}m")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds % 60:00}");
    }
}

/// <summary>What the HUD window binds to.</summary>
public sealed class HudViewModel : ObservableObject
{
    private IReadOnlyList<HudSessionRow> _rows = [];
    private HudSessionRow? _primary;
    private bool _isExpanded;

    public IReadOnlyList<HudSessionRow> Rows
    {
        get => _rows;
        private set
        {
            _rows = value;
            RaisePropertyChanged(nameof(Rows));
            RaisePropertyChanged(nameof(HasSeveral));
            RaisePropertyChanged(nameof(CountText));
            RaisePropertyChanged(nameof(Count));
        }
    }

    public HudSessionRow? Primary { get => _primary; private set => Set(ref _primary, value); }

    /// <summary>Set in fixture mode, so the HUD never passes for real activity.</summary>
    public string? FixtureLabel { get; init; }

    /// <summary>The expanded session list instead of the compact pill; toggled by a click, never by an event.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
            {
                RaisePropertyChanged(nameof(IsCompact));
            }
        }
    }

    public bool IsCompact => !_isExpanded;

    public bool HasSeveral => _rows.Count > 1;

    public string CountText => Strings.Format("windows.hud.count", _rows.Count);

    public string Count => _rows.Count.ToString(CultureInfo.InvariantCulture);

    public void Update(HudSessionStore store, DateTimeOffset now)
    {
        Rows = HudPresentation.Rows(store, now);
        Primary = HudPresentation.Primary(store, now);
    }
}
