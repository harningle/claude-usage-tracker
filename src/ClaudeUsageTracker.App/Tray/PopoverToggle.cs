namespace ClaudeUsageTracker.App.Tray;

public enum PopoverToggleAction
{
    None,
    Open,
    Close,
}

/// <summary>
/// Tray activation and popover deactivation as one state machine (plan.md section 2). Clicking the
/// icon of an open popover first deactivates the popover, which closes it, and then delivers the
/// click; that click must not reopen it. A second keyboard activation that the shell delivers
/// straight after the first is ignored for the same reason.
/// </summary>
public sealed class PopoverToggle(TimeProvider time)
{
    /// <summary>How long after a deactivation close a click on the icon still counts as the dismissal.</summary>
    public static readonly TimeSpan DismissGrace = TimeSpan.FromMilliseconds(400);

    /// <summary>Activations closer together than this are one gesture.</summary>
    public static readonly TimeSpan RepeatGrace = TimeSpan.FromMilliseconds(250);

    private DateTimeOffset? _deactivatedAt;
    private DateTimeOffset? _openedAt;

    public bool IsOpen { get; private set; }

    /// <summary>Decides what a tray activation does and records the resulting state.</summary>
    public PopoverToggleAction Activate()
    {
        var now = time.GetUtcNow();
        if (IsOpen)
        {
            if (now - _openedAt < RepeatGrace)
            {
                return PopoverToggleAction.None;
            }

            IsOpen = false;
            return PopoverToggleAction.Close;
        }

        if (now - _deactivatedAt < DismissGrace)
        {
            _deactivatedAt = null;
            return PopoverToggleAction.None;
        }

        IsOpen = true;
        _openedAt = now;
        return PopoverToggleAction.Open;
    }

    /// <summary>The popover was opened by something other than a tray activation.</summary>
    public void Opened()
    {
        IsOpen = true;
        _openedAt = null;
    }

    /// <summary>The popover lost activation and closed itself.</summary>
    public void Deactivated()
    {
        if (IsOpen)
        {
            IsOpen = false;
            _deactivatedAt = time.GetUtcNow();
        }
    }

    /// <summary>The popover was closed explicitly (Escape, a command, or detaching).</summary>
    public void Closed()
    {
        IsOpen = false;
        _deactivatedAt = null;
    }
}
