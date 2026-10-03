using System.Runtime.InteropServices;
using System.Windows.Input;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Tray;

namespace ClaudeUsageTracker.App.Automation;

/// <summary>Upstream <c>ShortcutAction</c>.</summary>
public enum HotkeyAction
{
    TogglePopover,
    Refresh,
    OpenSettings,
    NextProfile,
}

/// <summary>Reading, changing and describing the four shortcuts.</summary>
public static class Shortcuts
{
    public static IReadOnlyList<HotkeyAction> Actions { get; } = Enum.GetValues<HotkeyAction>();

    public static KeyCombo? Get(this ShortcutSettings settings, HotkeyAction action) => action switch
    {
        HotkeyAction.TogglePopover => settings.TogglePopover,
        HotkeyAction.Refresh => settings.Refresh,
        HotkeyAction.OpenSettings => settings.OpenSettings,
        _ => settings.NextProfile,
    };

    public static void Set(this ShortcutSettings settings, HotkeyAction action, KeyCombo? combo)
    {
        switch (action)
        {
            case HotkeyAction.TogglePopover:
                settings.TogglePopover = combo;
                break;
            case HotkeyAction.Refresh:
                settings.Refresh = combo;
                break;
            case HotkeyAction.OpenSettings:
                settings.OpenSettings = combo;
                break;
            default:
                settings.NextProfile = combo;
                break;
        }
    }

    /// <summary>
    /// Whether a recorded key can be a global shortcut. Upstream required any modifier; here Shift alone is not
    /// enough, because a global Shift+letter would stop that capital letter being typed anywhere. A key that is
    /// itself a modifier is not a shortcut.
    /// </summary>
    public static bool IsAcceptable(int virtualKey, int modifiers) =>
        (modifiers & (KeyCombo.Control | KeyCombo.Alt | KeyCombo.Windows)) != 0 && !IsModifierKey(virtualKey) && virtualKey is > 0 and < 0xFF;

    public static bool IsModifierKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>The <see cref="KeyCombo"/> modifier flags of WPF's modifier state.</summary>
    public static int ModifiersOf(ModifierKeys keys) =>
        (keys.HasFlag(ModifierKeys.Control) ? KeyCombo.Control : 0)
        | (keys.HasFlag(ModifierKeys.Alt) ? KeyCombo.Alt : 0)
        | (keys.HasFlag(ModifierKeys.Shift) ? KeyCombo.Shift : 0)
        | (keys.HasFlag(ModifierKeys.Windows) ? KeyCombo.Windows : 0);

    /// <summary>Windows' way of writing a shortcut, e.g. "Ctrl+Shift+U", with the key named in the keyboard layout's language.</summary>
    public static string Describe(KeyCombo combo)
    {
        var parts = new List<string>();
        if ((combo.Modifiers & KeyCombo.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((combo.Modifiers & KeyCombo.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((combo.Modifiers & KeyCombo.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((combo.Modifiers & KeyCombo.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(combo.VirtualKey));
        return string.Join("+", parts);
    }

    private static string KeyName(int virtualKey)
    {
        // Navigation keys share scan codes with the number pad; the extended bit names the dedicated keys.
        var extended = virtualKey is (>= 0x21 and <= 0x28) or 0x2D or 0x2E or 0x6F or 0x90;
        var scanCode = NativeMethods.MapVirtualKeyW((uint)virtualKey, 0);
        var buffer = new char[64];
        var length = scanCode == 0 ? 0 : NativeMethods.GetKeyNameTextW((int)((scanCode << 16) | (extended ? 1u << 24 : 0)), buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : KeyInterop.KeyFromVirtualKey(virtualKey).ToString();
    }
}

/// <summary>
/// Global shortcuts (upstream <c>ShortcutManager</c>, which used Carbon <c>RegisterEventHotKey</c>): Win32
/// <c>RegisterHotKey</c> on the tray's window, so no keyboard hook and no special permission is involved. A shortcut
/// another app already holds is refused by Windows and reported, where upstream only logged it.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    private readonly IntPtr _window;
    private readonly HashSet<int> _registered = [];

    public Hotkeys(TrayHost host)
    {
        _window = host.Handle;
        host.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread when a registered shortcut is pressed.</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>Registers every set shortcut afresh. Returns the actions whose shortcut Windows refused.</summary>
    public IReadOnlyList<HotkeyAction> Apply(ShortcutSettings settings)
    {
        Suspend();
        var refused = new List<HotkeyAction>();
        foreach (var action in Shortcuts.Actions)
        {
            if (settings.Get(action) is not { } combo)
            {
                continue;
            }

            var id = (int)action + 1;
            if (NativeMethods.RegisterHotKey(_window, id, (uint)combo.Modifiers | NativeMethods.ModNoRepeat, (uint)combo.VirtualKey))
            {
                _registered.Add(id);
            }
            else
            {
                refused.Add(action);
            }
        }

        return refused;
    }

    /// <summary>Releases every shortcut, e.g. while a new one is being recorded.</summary>
    public void Suspend()
    {
        foreach (var id in _registered)
        {
            NativeMethods.UnregisterHotKey(_window, id);
        }

        _registered.Clear();
    }

    public void Dispose() => Suspend();

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && _registered.Contains(wParam.ToInt32()))
        {
            Pressed?.Invoke((HotkeyAction)(wParam.ToInt32() - 1));
            handled = true;
        }

        return IntPtr.Zero;
    }
}

/// <summary>
/// The system events upstream reacted to, on Windows: resume from sleep (upstream's wake refresh), the display
/// coming back on (which is how resume from Modern Standby shows), a network becoming available (upstream's
/// <c>NWPathMonitor</c>), and a remote desktop or console reconnect or unlock (upstream's headless-display
/// handling). Explorer restarts are handled by the tray itself (<c>TaskbarCreated</c>).
/// </summary>
public sealed class SystemWatcher : IDisposable
{
    private readonly IntPtr _window;
    private readonly IntPtr _suspendResume;
    private readonly IntPtr _display;
    private readonly bool _sessions;
    private readonly Action<Action> _post;
    private bool _displayReported;

    public SystemWatcher(TrayHost host, Action<Action> post)
    {
        _window = host.Handle;
        _post = post;
        host.AddHook(WndProc);
        _suspendResume = NativeMethods.RegisterSuspendResumeNotification(_window, 0);
        var display = NativeMethods.ConsoleDisplayState;
        _display = NativeMethods.RegisterPowerSettingNotification(_window, ref display, 0);
        _sessions = NativeMethods.WTSRegisterSessionNotification(_window, 0);
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    /// <summary>The PC resumed from sleep or its display came back on.</summary>
    public event Action? Resumed;

    /// <summary>A network became available.</summary>
    public event Action? NetworkAvailable;

    /// <summary>The session was connected (locally or by remote desktop) or unlocked.</summary>
    public event Action? SessionConnected;

    /// <summary>The session was disconnected or locked.</summary>
    public event Action? SessionDisconnected;

    public void Dispose()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        if (_sessions)
        {
            NativeMethods.WTSUnRegisterSessionNotification(_window);
        }

        if (_display != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerSettingNotification(_display);
        }

        if (_suspendResume != IntPtr.Zero)
        {
            NativeMethods.UnregisterSuspendResumeNotification(_suspendResume);
        }
    }

    private void OnNetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _post(() => NetworkAvailable?.Invoke());
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case NativeMethods.WmPowerBroadcast:
                var kind = wParam.ToInt32();
                if (kind is NativeMethods.PbtApmResumeAutomatic or NativeMethods.PbtApmResumeSuspend)
                {
                    Resumed?.Invoke();
                }
                else if (kind == NativeMethods.PbtPowerSettingChange && DisplayState(lParam) is { } state)
                {
                    // Registering reports the current state once; only a later change to on is a resume.
                    if (_displayReported && state == 1)
                    {
                        Resumed?.Invoke();
                    }

                    _displayReported = true;
                }

                break;
            case NativeMethods.WmWtsSessionChange:
                switch (wParam.ToInt32())
                {
                    case NativeMethods.WtsConsoleConnect or NativeMethods.WtsRemoteConnect or NativeMethods.WtsSessionUnlock:
                        SessionConnected?.Invoke();
                        break;
                    case NativeMethods.WtsConsoleDisconnect or NativeMethods.WtsRemoteDisconnect or NativeMethods.WtsSessionLock:
                        SessionDisconnected?.Invoke();
                        break;
                }

                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>The console display state of a <c>POWERBROADCAST_SETTING</c> (0 off, 1 on, 2 dimmed), or null for another setting.</summary>
    private static int? DisplayState(IntPtr setting)
    {
        if (setting == IntPtr.Zero || Marshal.PtrToStructure<Guid>(setting) != NativeMethods.ConsoleDisplayState
            || Marshal.ReadInt32(setting, 16) < 4)
        {
            return null;
        }

        return Marshal.ReadInt32(setting, 20);
    }
}
