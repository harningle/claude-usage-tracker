using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App.Themes;

/// <summary>
/// Follows Windows' colour modes: app windows use the app mode (<c>AppsUseLightTheme</c>), tray icons the
/// taskbar mode (<c>SystemUsesLightTheme</c>), as upstream followed the macOS appearance and menu bar.
/// </summary>
public sealed class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    private readonly ResourceDictionary _slot;
    private bool? _appliedDark;

    public ThemeManager(Application application)
    {
        _slot = new ResourceDictionary();
        application.Resources.MergedDictionaries.Insert(0, _slot);
        Apply();
    }

    public bool AppsDark { get; private set; }

    /// <summary>Whether the taskbar is dark, which decides the monochrome tray foreground.</summary>
    public bool TaskbarDark { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Re-reads both modes; raises <see cref="Changed"/> when either changed.</summary>
    public void Apply()
    {
        var appsDark = !ReadLight("AppsUseLightTheme");
        var taskbarDark = !ReadLight("SystemUsesLightTheme");
        var changed = appsDark != AppsDark || taskbarDark != TaskbarDark;
        AppsDark = appsDark;
        TaskbarDark = taskbarDark;
        if (_appliedDark != appsDark)
        {
            _slot.MergedDictionaries.Clear();
            _slot.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(appsDark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml"),
            });
            _appliedDark = appsDark;
        }

        if (changed)
        {
            foreach (Window window in Application.Current.Windows)
            {
                StyleTitleBar(window);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Gives a window the dark or light title bar matching the app mode.</summary>
    public void StyleTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = AppsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    /// <summary>Windows 11 rounded corners for a borderless popup; ignored on Windows 10.</summary>
    public static void RoundCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var preference = DwmCornerRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    private static bool ReadLight(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        // Windows' default when the value is absent is light.
        return key?.GetValue(name) is not int value || value != 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
