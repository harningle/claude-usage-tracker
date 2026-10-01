using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeUsageTracker.App.Browser;

/// <summary>
/// The browser side of setup. claude.ai: the user signs in inside the profile's own WebView2 context, where
/// the session then stays. Console: the user signs in inside a throw-away context, the session cookie is
/// handed to the wizard (the Console is polled through <c>HttpClient</c>, plan.md section 2) and the
/// context is deleted.
/// </summary>
public sealed class BrowserSignIn(BrowserSessions sessions, string root, Func<Window?> owner) : IBrowserSignIn
{
    public const string ClaudeAiOrigin = "https://claude.ai";
    public const string ConsoleOrigin = "https://platform.claude.com";

    public async Task<bool> SignInToClaudeAiAsync(Guid profileId)
    {
        var environment = await sessions.EnvironmentAsync(profileId);
        var window = new BrowserSignInWindow(environment, ClaudeAiOrigin, Strings.Get("personal.signin_sheet_title")) { Owner = owner() };
        window.ShowDialog();
        if (window.SessionCookie is null)
        {
            return false;
        }

        sessions.SessionChanged(profileId);
        return true;
    }

    public async Task<ConsoleSecret?> SignInToConsoleAsync(Guid profileId)
    {
        var folder = ConsoleFolder(root, profileId);
        var environment = await CoreWebView2Environment.CreateAsync(null, folder);
        var exited = new TaskCompletionSource();
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        var window = new BrowserSignInWindow(environment, ConsoleOrigin, Strings.Get("windows.setup.console.signin_title")) { Owner = owner() };
        window.ShowDialog();

        // The cookie is all that is kept; the context it came from is removed once its processes have gone.
        await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        DeleteLeftovers(root);
        return window.SessionCookie is { } cookie
            ? new ConsoleSecret(cookie.Value, cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()))
            : null;
    }

    public async Task<Func<Task>> UseSessionKeyAsync(Guid profileId, string sessionKey)
    {
        var previous = await sessions.SetSessionKeyAsync(profileId, sessionKey);
        return () => sessions.RestoreSessionKeyAsync(profileId, previous);
    }

    /// <summary>Removes Console sign-in contexts that could not be deleted when their window closed.</summary>
    public static void DeleteLeftovers(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(root, "*.console"))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use; the next start tries again.
            }
        }
    }

    private static string ConsoleFolder(string root, Guid profileId) => Path.Combine(root, profileId.ToString("N") + ".console");
}

/// <summary>
/// A window with a browser on one site's sign-in page. It closes by itself once the site's
/// <c>sessionKey</c> cookie appears (upstream's sign-in sheets do the same). When the context is already
/// signed in, it asks instead whether to continue with that account or sign in with another.
/// </summary>
public sealed class BrowserSignInWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly string _origin;
    private readonly WebView2 _browser = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _continue = new() { Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _other = new() { Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private CoreWebView2Cookie? _found;
    private bool _signedInAtOpen;

    public BrowserSignInWindow(CoreWebView2Environment environment, string origin, string title)
    {
        _environment = environment;
        _origin = origin;
        Title = title;
        Width = 520;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Window.Background");

        _status.SetResourceReference(StyleProperty, "Text.Body");
        _status.Text = Strings.Get("windows.signin.loading");
        _continue.SetResourceReference(StyleProperty, "PrimaryButton");
        _continue.Content = Strings.Get("windows.signin.continue");
        _continue.Click += (_, _) => Accept();
        _other.SetResourceReference(StyleProperty, "SecondaryButton");
        _other.Content = Strings.Get("windows.signin.other_account");
        _other.Click += OnOtherAccount;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _continue, _other } };
        DockPanel.SetDock(buttons, Dock.Right);
        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8), Children = { buttons, _status } };
        DockPanel.SetDock(bar, Dock.Top);
        Content = new DockPanel { Children = { bar, _browser } };

        Loaded += OnLoaded;
        Closed += (_, _) => _browser.Dispose();
    }

    /// <summary>The site's session cookie when the user signed in (or chose to continue signed in); null when they closed the window.</summary>
    public CoreWebView2Cookie? SessionCookie { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _browser.EnsureCoreWebView2Async(_environment);
            _signedInAtOpen = await FindSessionAsync() is not null;
            _browser.CoreWebView2.NavigationCompleted += async (_, _) => await CheckAsync();
            _browser.CoreWebView2.Navigate(_origin + "/login");
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _status.Text = Strings.Format("windows.signin.browser_failed", error.Message);
        }
    }

    private async Task<CoreWebView2Cookie?> FindSessionAsync() =>
        (await _browser.CoreWebView2.CookieManager.GetCookiesAsync(_origin)).FirstOrDefault(c => c.Name == "sessionKey" && c.Value.Length > 0);

    private async Task CheckAsync()
    {
        if (!IsLoaded || _browser.CoreWebView2 is null)
        {
            return;
        }

        _found = await FindSessionAsync();
        if (_found is null)
        {
            _status.Text = Strings.Get("windows.signin.prompt");
            _continue.Visibility = _other.Visibility = Visibility.Collapsed;
        }
        else if (_signedInAtOpen)
        {
            _status.Text = Strings.Get("windows.signin.already");
            _continue.Visibility = _other.Visibility = Visibility.Visible;
        }
        else
        {
            Accept();
        }
    }

    private void Accept()
    {
        SessionCookie = _found;
        Close();
    }

    /// <summary>Signs this context out of the site, so another account can sign in.</summary>
    private void OnOtherAccount(object sender, RoutedEventArgs e)
    {
        _browser.CoreWebView2.CookieManager.DeleteAllCookies();
        _signedInAtOpen = false;
        _found = null;
        _browser.CoreWebView2.Navigate(_origin + "/login");
    }
}
