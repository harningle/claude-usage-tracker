using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Profiles;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.App.Views;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;
using ClaudeUsageTracker.Tests.Notifications;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.ViewModels;

/// <summary>
/// Keyboard and screen-reader access (plan.md section 2): every control a user can operate in the popover, each
/// settings page and the wizard's steps has an accessible name that is more than an icon glyph, and can be reached
/// with Tab. The windows are realised off screen with synthetic data; parts of a control's own template (a scroll
/// bar's arrows, a combo box's drop-down button) belong to that control and are not checked separately.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AccessibilityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ScratchKey _registry = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose()
    {
        _registry.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public Task EveryPopoverControlIsNamedAndReachable() => WpfApplication.Run(() =>
    {
        var profile = new ProfileSettings { Name = "Work", Console = new ConsoleLink() };
        var other = new ProfileSettings { Name = "Home" };
        var settings = new AppSettings { Profiles = [profile, other], ActiveProfileId = profile.Id };
        var model = new PopoverViewModel();
        model.Update(settings, profile, State(profile.Id), null, Now);
        var window = new PopoverWindow(model, new PopoverToggle(TimeProvider.System));

        Assert.Empty(Audit(window));
        window.Close();
    });

    [Fact]
    public Task EverySettingsPageIsNamedAndReachable() => WpfApplication.Run(() =>
    {
        var settings = new AppSettings();
        var anthropic = new ProfileSettings { Name = "Work" };
        var codex = new ProfileSettings { Name = "Codex", Provider = Provider.Codex.RawValue() };
        settings.Profiles.AddRange([anthropic, codex]);
        settings.ActiveProfileId = anthropic.Id;
        var window = new SettingsWindow(SettingsModel(settings));

        var problems = new List<string>();
        foreach (var section in Enum.GetValues<SettingsSection>().Where(s => s != SettingsSection.Codex))
        {
            window.Navigate(section);
            problems.AddRange(Audit(window).Select(p => $"{section}: {p}"));
        }

        settings.ActiveProfileId = codex.Id;
        window.ProfileChanged();
        window.Navigate(SettingsSection.Codex);
        problems.AddRange(Audit(window).Select(p => $"Codex: {p}"));

        Assert.Empty(problems);
        window.Close();
    });

    [Fact]
    public Task EveryWizardStepIsNamedAndReachable() => WpfApplication.Run(() =>
    {
        var problems = new List<string>();
        void Check(Window window, SetupWizardViewModel wizard, WizardStep step)
        {
            Assert.Equal(step, wizard.Step);
            problems.AddRange(Audit(window).Select(p => $"{step}: {p}"));
        }

        var signedOut = Wizard("signed-out", withLogin: false);
        var window = new SetupWizardWindow(signedOut);
        Check(window, signedOut, WizardStep.ChooseProvider);
        signedOut.ChooseAnthropic();
        Check(window, signedOut, WizardStep.NotFound);
        signedOut.Back();
        signedOut.SetUpWithClaudeAi();
        Check(window, signedOut, WizardStep.EnterKey);
        Wait(signedOut.SignInAsync(CancellationToken.None));
        Check(window, signedOut, WizardStep.SelectOrganization);
        window.Close();

        var signedIn = Wizard("signed-in", withLogin: true);
        window = new SetupWizardWindow(signedIn);
        signedIn.ChooseAnthropic();
        Check(window, signedIn, WizardStep.Detected);
        Wait(signedIn.TestAsync(CancellationToken.None));
        Check(window, signedIn, WizardStep.Review);
        window.Close();

        var codex = Wizard("codex", withLogin: false);
        window = new SetupWizardWindow(codex);
        codex.ChooseCodex();
        problems.AddRange(Audit(window).Select(p => $"Codex: {p}"));
        window.Close();

        Assert.Empty(problems);
    });

    /// <summary>Shows the window off screen, lets it lay out, and lists the controls that fail.</summary>
    private static List<string> Audit(Window window)
    {
        if (!window.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowActivated = false;
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
        }

        Settle();
        var problems = new List<string>();
        var count = 0;
        Walk(window, problems, ref count);
        if (count == 0)
        {
            // A window that did not lay out would otherwise pass without a single check.
            problems.Add("no control was checked");
        }

        return problems;
    }

    private static void Walk(DependencyObject node, List<string> problems, ref int count)
    {
        if (node is FrameworkElement { IsVisible: true } element && IsOperable(element) && element.TemplatedParent is not Control)
        {
            count++;
            var name = UIElementAutomationPeer.CreatePeerForElement(element)?.GetName();
            if (!IsMeaningful(name))
            {
                problems.Add($"{Describe(element)} has no accessible name" + (string.IsNullOrEmpty(name) ? "" : $" (only \"{Escape(name)}\")"));
            }

            if (element.IsEnabled && !(element.Focusable && KeyboardNavigation.GetIsTabStop(element)))
            {
                problems.Add($"{Describe(element)} cannot be reached with Tab");
            }
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), problems, ref count);
        }
    }

    private static bool IsOperable(FrameworkElement element) =>
        element is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider;

    /// <summary>Text a screen reader can say: not empty and not only private-use icon glyphs (Segoe Fluent Icons).</summary>
    private static bool IsMeaningful(string? name) =>
        name is not null && name.Any(c => !char.IsWhiteSpace(c) && c is not (>= '\uE000' and <= '\uF8FF'));

    /// <summary>The control's type and name, and the nearest named element it is in.</summary>
    private static string Describe(FrameworkElement element)
    {
        var text = string.IsNullOrEmpty(element.Name) ? element.GetType().Name : $"{element.GetType().Name} {element.Name}";
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is FrameworkElement { Name.Length: > 0 } named)
            {
                return $"{text} in {named.Name}";
            }
        }

        return text;
    }

    private static string Escape(string text) => string.Concat(text.Select(c => c is >= '\uE000' and <= '\uF8FF' ? $"\\u{(int)c:X4}" : c.ToString()));

    private SettingsViewModel SettingsModel(AppSettings settings)
    {
        var links = new LinkHarness(_temp.Path, Path.Combine(_temp.Path, "home"), _time);
        var store = new SettingsStore(Path.Combine(_temp.Path, "settings.json"));
        var cache = new UsageCacheStore(Path.Combine(_temp.Path, "cache"));
        var history = new UsageHistoryService(new UsageHistoryStore(Path.Combine(_temp.Path, "history")), _time);
        var profiles = new ProfileService(store, settings, links.Service, links.Secrets, links.Lock, cache, history, _time);
        return new SettingsViewModel(
            settings, () => store.Save(settings), () => { }, profiles, new HistoryViewModel(history, _time, () => true),
            new RequestLog(Path.Combine(_temp.Path, "diagnostics")), taskbarDark: true,
            new SettingsIntegrations
            {
                LaunchAtLogin = new LaunchAtLogin(() => _registry.Open("Run"), () => null, @"C:\Apps\ClaudeUsageTracker.App.exe"),
                Notifier = new RecordingNotifier(),
                AutomationProblem = () => null,
                NotificationsSwitchedOn = _ => { },
                ApplyShortcuts = () => [],
                SuspendShortcuts = () => { },
                Pages = new Integration.IntegrationHarness(Path.Combine(_temp.Path, "integration"), settings, _time).Pages,
            });
    }

    /// <param name="withLogin">Whether the default Claude Code folder holds a usable synthetic login.</param>
    private SetupWizardViewModel Wizard(string name, bool withLogin)
    {
        var data = Path.Combine(_temp.Path, name);
        var home = Path.Combine(data, "home");
        var claude = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claude);
        if (withLogin)
        {
            File.WriteAllText(Path.Combine(claude, ".credentials.json"),
                $$$"""{"claudeAiOauth":{"accessToken":"at-1","refreshToken":"rt-1","expiresAt":{{{Now.AddHours(8).ToUnixTimeMilliseconds()}}},"subscriptionType":"team"}}""");
            File.WriteAllText(Path.Combine(home, ".claude.json"),
                """{"oauthAccount":{"accountUuid":"acc-1","emailAddress":"user@example.com","organizationName":"ExampleOrg"}}""");
        }

        var http = new HttpClient(new ScriptedHandler((_, _) =>
            Task.FromResult(LinkHarness.Json(System.Net.HttpStatusCode.OK, """{"five_hour":{"utilization":39},"seven_day":{"utilization":60}}"""))));
        var log = new RequestLog(Path.Combine(data, "diagnostics"));
        var links = new LinkHarness(data, home, _time);
        var setup = new SetupService(new SettingsStore(Path.Combine(data, "settings.json")), new AppSettings(), links.Service, links.Secrets, links.Lock,
            new UsageCacheStore(Path.Combine(data, "cache")), _time);
        var transport = new ScriptedTransport((_, operation, _) => ScriptedTransport.SignedIn(operation));
        var services = new WizardServices(
            new ClaudeAiClient(transport, _time, log), new ConsoleClient(http, _time, log), new CodexClient(http, _time, log), new FakeSignIn(),
            Path.Combine(home, ".codex"));
        return new SetupWizardViewModel(links.Service, new UsageClient(http, _time, log), setup, services, _time, home, null);
    }

    /// <summary>Waits for a wizard step that awaits on this dispatcher, which keeps running meanwhile.</summary>
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static ProfileUsageState State(Guid id) => new()
    {
        ProfileId = id,
        IsLinked = true,
        Source = UsageSourceKind.ClaudeCode,
        Link = LinkStatus.Ready,
        LastAttemptAt = Now,
        Usage = new CachedUsage
        {
            ProfileId = id, ObservedAt = Now, Source = "oauthUsage",
            Usage = new UsageData { SessionPercentage = 40, WeeklyPercentage = 60, SonnetWeeklyPercentage = 10, OpusWeeklyPercentage = 5 },
        },
        Api = new CachedApiUsage
        {
            ProfileId = id, ObservedAt = Now, OrganizationId = "org",
            Usage = new ApiUsage { CurrentSpendCents = 1234, PrepaidCreditsCents = 8766, Currency = "USD" },
        },
    };

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }
}
