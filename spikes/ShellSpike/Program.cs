// Milestone 1 spike (plan.md section 3). Modes:
//   probe-tray [--guid-path <exe path>]  add, locate and remove one tray icon, then exit
//   notify-and-exit                      register, post a notification, unregister, exit (cold activation test)
//   interactive (default)                tray icon + notification buttons; logs callbacks and activations
//   dump-registration                    list this spike's notification and tray registry entries
//   unregister-all                       remove this executable's notification registration
// Every run appends to %LOCALAPPDATA%\ClaudeUsageTracker.Spikes\shell\log.txt, so cold activations
// launched by a notification click are recorded even when nobody watches the console.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using ShellSpike;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTracker.Spikes", "shell", "log.txt");

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Log($"start pid {Environment.ProcessId} exe {Environment.ProcessPath} args [{string.Join(' ', args)}]");
        var mode = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "interactive";
        try
        {
            return mode switch
            {
                "probe-tray" => ProbeTray(Option(args, "--guid-path") ?? Environment.ProcessPath!,
                    int.Parse(Option(args, "--hold-ms") ?? "0")),
                "notify-and-exit" => NotifyAndExit(),
                "dump-registration" => DumpRegistration(),
                "cleanup-others" => CleanupOthers(),
                "unregister-all" => UnregisterAll(),
                _ => Interactive(),
            };
        }
        catch (Exception e)
        {
            Log($"FAILED: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static string? Option(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    public static void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}";
        Console.WriteLine(line);
        File.AppendAllText(LogPath, line + Environment.NewLine);
    }

    // --- Tray identity ---------------------------------------------------------------------

    private static int ProbeTray(string guidPath, int holdMs)
    {
        var source = new HwndSource(new HwndSourceParameters("ShellSpikeProbe") { ParentWindow = new IntPtr(-3) });
        var guid = Native.TrayGuid(guidPath, "profile-default|session");
        var added = TrayIcon.Add(source.Handle, guid, "Claude Usage Tracker tray spike");
        Log($"probe-tray: GUID {guid} derived from {guidPath}; NIM_ADD {(added ? "succeeded" : "FAILED")}");
        if (added)
        {
            Log($"probe-tray: Shell_NotifyIconGetRect -> {TrayIcon.Locate(source.Handle, guid)}");
            Thread.Sleep(holdMs);
            TrayIcon.Remove(source.Handle, guid);
        }

        source.Dispose();
        return added ? 0 : 1;
    }

    // --- Notifications ---------------------------------------------------------------------

    private static void RegisterNotifications(Action<AppNotificationActivatedEventArgs>? onInvoked)
    {
        Log($"AppNotificationManager.IsSupported {AppNotificationManager.IsSupported()}");
        AppNotificationManager.Default.NotificationInvoked += (_, e) =>
        {
            Log($"NotificationInvoked (running instance) argument [{e.Argument}]");
            onInvoked?.Invoke(e);
        };
        AppNotificationManager.Default.Register();
        Log("Register() returned");
        Log($"setting {AppNotificationManager.Default.Setting}");

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        Log($"activation kind {activation.Kind}");
        if (activation.Kind == ExtendedActivationKind.AppNotification
            && activation.Data is AppNotificationActivatedEventArgs notification)
        {
            Log($"COLD ACTIVATION from notification, argument [{notification.Argument}]");
        }
    }

    private static void ShowNotification(string purpose)
    {
        var notification = new AppNotificationBuilder()
            .AddArgument("purpose", purpose)
            .AddArgument("sentBy", Environment.ProcessPath!)
            .AddArgument("sentAt", DateTimeOffset.Now.ToString("HH:mm:ss"))
            .AddText("Claude Usage Tracker notification spike")
            .AddText($"{purpose}: sent by {Path.GetDirectoryName(Environment.ProcessPath)}")
            .BuildNotification();
        AppNotificationManager.Default.Show(notification);
        Log($"shown notification id {notification.Id} ({purpose})");
    }

    private static int NotifyAndExit()
    {
        RegisterNotifications(null);
        if (AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.AppNotification)
        {
            Log("launched by a notification click; not posting another");
            return 0;
        }

        ShowNotification("cold-activation test");
        AppNotificationManager.Default.Unregister();
        Log("unregistered the running COM server and exiting; click the notification to test cold activation");
        return 0;
    }

    private static int UnregisterAll()
    {
        AppNotificationManager.Default.UnregisterAll();
        Log("UnregisterAll completed");
        return 0;
    }

    private static int DumpRegistration()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        using var aumids = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        foreach (var aumid in aumids?.GetSubKeyNames() ?? [])
        {
            using var key = aumids!.OpenSubKey(aumid);
            var activator = key?.GetValue("CustomActivator") as string;
            using var server = activator is null ? null : Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{activator}\LocalServer32");
            var command = server?.GetValue(null) as string;
            if (aumid.Contains(name, StringComparison.OrdinalIgnoreCase) || command?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                Log($"AUMID {aumid}: DisplayName [{key?.GetValue("DisplayName")}] CustomActivator {activator} LocalServer32 [{command}]");
            }
        }

        using var icons = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        foreach (var id in icons?.GetSubKeyNames() ?? [])
        {
            using var key = icons!.OpenSubKey(id);
            if (key?.GetValue("ExecutablePath") is string path && path.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                var values = key.GetValueNames().Where(v => v != "IconSnapshot").Select(v => $"{v}={key.GetValue(v)}");
                Log($"NotifyIconSettings {id}: {string.Join("; ", values)}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Removes this spike's notification registrations that belong to any other executable path
    /// (stale copies left by moves). Explorer's per-path tray entries are left to Explorer.
    /// </summary>
    private static int CleanupOthers()
    {
        var self = Environment.ProcessPath!;
        var name = Path.GetFileNameWithoutExtension(self);
        using var aumids = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId", writable: true);
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID", writable: true);
        foreach (var aumid in aumids?.GetSubKeyNames() ?? [])
        {
            string? activator, command;
            using (var key = aumids!.OpenSubKey(aumid))
            {
                activator = key?.GetValue("CustomActivator") as string;
                using var server = activator is null ? null : classes?.OpenSubKey($@"{activator}\LocalServer32");
                command = server?.GetValue(null) as string;
            }

            var ours = command?.Contains($"\\{name}.exe\"", StringComparison.OrdinalIgnoreCase) == true
                       || aumid.EndsWith($".{name}.exe", StringComparison.OrdinalIgnoreCase);
            var current = command?.Contains($"\"{self}\"", StringComparison.OrdinalIgnoreCase) == true
                          || aumid.Equals(self.Replace('\\', '.'), StringComparison.OrdinalIgnoreCase);
            if (!ours || current)
            {
                continue;
            }

            aumids!.DeleteSubKeyTree(aumid);
            if (activator is not null && command is not null)
            {
                classes!.DeleteSubKeyTree(activator, throwOnMissingSubKey: false);
            }

            Log($"removed stale registration {aumid} ({command ?? "no activator"})");
        }

        return 0;
    }

    // --- Interactive harness -----------------------------------------------------------------

    private static int Interactive()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var log = new TextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 260, FontFamily = new("Consolas") };
        var window = new Window { Title = "ShellSpike", Width = 640, SizeToContent = SizeToContent.Height };
        void UiLog(string message) => app.Dispatcher.BeginInvoke(() => log.AppendText(message + Environment.NewLine));

        var guid = Native.TrayGuid(Environment.ProcessPath!, "profile-default|session");
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(handle).AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Native.CallbackMessage)
            {
                var kind = (int)(lParam.ToInt64() & 0xFFFF);
                var name = kind switch
                {
                    Native.NinSelect => "NIN_SELECT",
                    Native.NinKeySelect => "NIN_KEYSELECT",
                    Native.WmContextMenu => "WM_CONTEXTMENU",
                    _ => null,
                };
                if (name is not null)
                {
                    var anchor = $"x {(short)(wParam.ToInt64() & 0xFFFF)} y {(short)((wParam.ToInt64() >> 16) & 0xFFFF)}";
                    Log($"tray callback {name} at {anchor}; icon rect {TrayIcon.Locate(hwnd, guid)}");
                    UiLog($"tray callback {name} at {anchor}");
                }
            }
            else if (msg == taskbarCreated)
            {
                Log($"TaskbarCreated: re-adding icon -> {TrayIcon.Add(hwnd, guid, "Claude Usage Tracker tray spike")}");
            }

            return IntPtr.Zero;
        });

        var added = TrayIcon.Add(handle, guid, "Claude Usage Tracker tray spike");
        Log($"interactive: tray GUID {guid}; NIM_ADD {(added ? "succeeded" : "FAILED")}; rect {TrayIcon.Locate(handle, guid)}");
        RegisterNotifications(e => UiLog($"NotificationInvoked [{e.Argument}]"));

        Button ButtonFor(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log($"{text} FAILED: {e.Message}");
                }

                UiLog($"{text} done");
            };
            return button;
        }

        var buttons = new WrapPanel();
        buttons.Children.Add(ButtonFor("Show notification", () => ShowNotification("running-instance test")));
        buttons.Children.Add(ButtonFor("Dump registration", () => DumpRegistration()));
        buttons.Children.Add(ButtonFor("Exit (unregister)", () =>
        {
            AppNotificationManager.Default.Unregister();
            TrayIcon.Remove(handle, guid);
            app.Shutdown();
        }));
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = $"Exe: {Environment.ProcessPath}\nTray GUID: {guid}\nLog: {LogPath}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        panel.Children.Add(log);
        window.Content = panel;
        window.Closing += (_, e) =>
        {
            e.Cancel = true;
            window.Hide();
        };
        window.Show();
        app.Run();
        Log("interactive: exited");
        return 0;
    }
}

internal static class TrayIcon
{
    public static bool Add(IntPtr window, Guid guid, string tip)
    {
        var data = Data(window, guid);
        Native.Shell_NotifyIconW(Native.NimDelete, ref data); // clear a stale icon left by a crash
        data.uFlags = Native.NifMessage | Native.NifIcon | Native.NifTip | Native.NifGuid | Native.NifShowTip;
        data.uCallbackMessage = Native.CallbackMessage;
        data.hIcon = Native.LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        data.szTip = tip.Length > 127 ? tip[..127] : tip;
        if (!Native.Shell_NotifyIconW(Native.NimAdd, ref data))
        {
            return false;
        }

        data.uVersion = Native.NotifyIconVersion4;
        return Native.Shell_NotifyIconW(Native.NimSetVersion, ref data);
    }

    public static void Remove(IntPtr window, Guid guid)
    {
        var data = Data(window, guid);
        Native.Shell_NotifyIconW(Native.NimDelete, ref data);
    }

    public static string Locate(IntPtr window, Guid guid)
    {
        var identifier = new Native.NotifyIconIdentifier
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.NotifyIconIdentifier>(),
            hWnd = window,
            guidItem = guid,
        };
        var hr = Native.Shell_NotifyIconGetRect(ref identifier, out var rect);
        return hr == 0 ? rect.ToString() : $"HRESULT 0x{hr:X8}";
    }

    private static Native.NotifyIconData Data(IntPtr window, Guid guid) => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.NotifyIconData>(),
        hWnd = window,
        uFlags = Native.NifGuid,
        guidItem = guid,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };
}
