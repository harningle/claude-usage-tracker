using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClaudeUsageTracker.App.Interop;

namespace ClaudeUsageTracker.App.Tray;

public enum TrayActivationKind
{
    /// <summary>Left click or tap (NIN_SELECT).</summary>
    Select,

    /// <summary>Enter or Space on a keyboard-focused icon (NIN_KEYSELECT).</summary>
    KeySelect,

    /// <summary>Right click, Shift+F10 or the context-menu key (WM_CONTEXTMENU).</summary>
    ContextMenu,
}

/// <param name="Anchor">Physical screen coordinates Explorer reported with the event.</param>
public sealed record TrayActivation(TrayActivationKind Kind, Point Anchor);

/// <summary>
/// The known callback window for the app's own <c>Shell_NotifyIcon</c> icons. It is a hidden top-level
/// window rather than a message-only one, because only top-level windows receive the
/// <c>TaskbarCreated</c> broadcast after Explorer restarts.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x80;

    private readonly HwndSource _source;
    private readonly int _taskbarCreated;
    private readonly Dictionary<int, TrayIcon> _icons = [];
    private int _nextCallbackMessage = NativeMethods.WmApp + 1;

    public TrayHost()
    {
        _source = new HwndSource(new HwndSourceParameters("ClaudeUsageTracker.Tray")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>Raised when the taskbar's DPI or the Windows colour mode changes, so icons can be redrawn.</summary>
    public event EventHandler? AppearanceChanged;

    /// <summary>Square icon size in physical pixels for the taskbar's current DPI.</summary>
    public int IconPixelSize
    {
        get
        {
            var dpi = NativeMethods.GetDpiForWindow(Handle);
            return NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxSmIcon, dpi == 0 ? 96 : dpi);
        }
    }

    public TrayIcon CreateIcon(Guid guid)
    {
        var icon = new TrayIcon(this, guid, _nextCallbackMessage++);
        _icons.Add(icon.CallbackMessage, icon);
        return icon;
    }

    internal void Forget(TrayIcon icon) => _icons.Remove(icon.CallbackMessage);

    public void Dispose()
    {
        foreach (var icon in _icons.Values.ToList())
        {
            icon.Dispose();
        }

        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_icons.TryGetValue(message, out var icon))
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event, wParam holds the anchor point.
            var kind = (int)(lParam.ToInt64() & 0xFFFF) switch
            {
                NativeMethods.NinSelect => TrayActivationKind.Select,
                NativeMethods.NinKeySelect => TrayActivationKind.KeySelect,
                NativeMethods.WmContextMenu => TrayActivationKind.ContextMenu,
                _ => (TrayActivationKind?)null,
            };
            if (kind is { } k)
            {
                var anchor = new Point((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                icon.Raise(new TrayActivation(k, anchor));
            }

            handled = true;
        }
        else if (message == _taskbarCreated)
        {
            foreach (var each in _icons.Values)
            {
                each.ReAdd();
            }

            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (message == NativeMethods.WmDpiChanged
                 || (message == NativeMethods.WmSettingChange
                     && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"))
        {
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}

/// <summary>One owned notification-area icon. Not thread-safe: use it from the UI thread.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TrayHost _host;
    private IntPtr _icon;
    private string _tooltip = "";
    private bool _added;
    private bool _disposed;

    internal TrayIcon(TrayHost host, Guid guid, int callbackMessage)
    {
        _host = host;
        Guid = guid;
        CallbackMessage = callbackMessage;
    }

    public Guid Guid { get; }

    internal int CallbackMessage { get; }

    public event EventHandler<TrayActivation>? Activated;

    /// <summary>The tooltip limit of NOTIFYICONDATA.szTip, excluding the terminator.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>
    /// Shows or updates the icon. Takes ownership of <paramref name="icon"/>. Returns false when the
    /// shell rejected it, e.g. before Explorer has started; <c>TaskbarCreated</c> retries.
    /// </summary>
    public bool Update(IntPtr icon, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _icon;
        _icon = icon;
        _tooltip = Truncate(tooltip);
        var shown = _added ? Modify() : Add();
        if (previous != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(previous);
        }

        return shown;
    }

    /// <summary>The icon's rectangle in physical screen pixels, or null when the shell cannot report one.</summary>
    public Int32Rect? Bounds()
    {
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(),
            hWnd = _host.Handle,
            guidItem = Guid,
        };
        return _added && NativeMethods.Shell_NotifyIconGetRect(ref identifier, out var rect) == 0
            ? new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = Data();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _host.Forget(this);
    }

    /// <summary>Cuts to the shell's limit without splitting a surrogate pair.</summary>
    public static string Truncate(string tooltip)
    {
        if (tooltip.Length <= MaxTooltipLength)
        {
            return tooltip;
        }

        var length = char.IsHighSurrogate(tooltip[MaxTooltipLength - 1]) ? MaxTooltipLength - 1 : MaxTooltipLength;
        return tooltip[..length];
    }

    internal void Raise(TrayActivation activation) => Activated?.Invoke(this, activation);

    internal void ReAdd()
    {
        _added = false;
        if (_icon != IntPtr.Zero)
        {
            Add();
        }
    }

    private bool Add()
    {
        var data = Data();
        // An icon with this GUID can survive a crash of a previous run; remove it before adding.
        NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref data);
        data = Full();
        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NimAdd, ref data))
        {
            return false;
        }

        data.uVersion = NativeMethods.NotifyIconVersion4;
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NimSetVersion, ref data);
        if (!_added)
        {
            data = Data();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref data);
        }

        return _added;
    }

    private bool Modify()
    {
        var data = Full();
        if (NativeMethods.Shell_NotifyIconW(NativeMethods.NimModify, ref data))
        {
            return true;
        }

        // The shell lost the icon without a TaskbarCreated broadcast; add it again.
        _added = false;
        return Add();
    }

    private NativeMethods.NotifyIconData Full()
    {
        var data = Data();
        data.uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip
                      | NativeMethods.NifGuid | NativeMethods.NifShowTip;
        data.uCallbackMessage = (uint)CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        return data;
    }

    private NativeMethods.NotifyIconData Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        hWnd = _host.Handle,
        uFlags = NativeMethods.NifGuid,
        guidItem = Guid,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };
}
