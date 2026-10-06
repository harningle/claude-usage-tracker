using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
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

    /// <summary>
    /// Keyboard entry, with Tab presses sent through WPF's input system as a keyboard sends them: going round each
    /// settings page from its first tab stop, Tab reaches every control a user can operate there, a list through one of
    /// its choices, and never drops the keyboard focus back to the window. The settings show every optional part.
    /// </summary>
    [Fact]
    public Task TabReachesEveryControlOfEverySettingsPage() => WpfApplication.Run(() =>
    {
        var (window, _) = FullSettingsWindow();
        var problems = new List<string>();
        foreach (var section in Enum.GetValues<SettingsSection>().Where(s => s != SettingsSection.Codex))
        {
            window.Navigate(section);
            Settle();
            var reached = TabRound(window);
            for (var i = 1; i < reached.Count; i++)
            {
                if (reached[i] is Window && reached[i - 1] is FrameworkElement before)
                {
                    problems.Add($"{section}: Tab from {Describe(before)} drops the keyboard focus to the window");
                }
            }

            var controls = new List<FrameworkElement>();
            Operable(window, controls);
            foreach (var control in controls.Where(c => c.IsEnabled && c is not ListBoxItem))
            {
                if (!reached.Any(e => ReferenceEquals(e, control) || (e is ListBoxItem item && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), control))))
                {
                    problems.Add($"{section}: Tab never reaches {Describe(control)} \"{AutomationProperties.GetName(control)}\"");
                }
            }
        }

        Assert.Empty(problems);
        window.Close();
    });

    /// <summary>
    /// The selectable lists (the icon styles, the notification sound, the status line's colours, the multi-profile style):
    /// from the selected choice, the arrow key that points at a neighbouring choice selects it, and the opposite key
    /// selects the first again. Every list in the window must be exercised, so one that the test's settings hide fails
    /// instead of passing unchecked.
    /// </summary>
    [Fact]
    public Task ArrowKeysChangeTheChoiceInEveryList() => WpfApplication.Run(() =>
    {
        var (window, _) = FullSettingsWindow();
        var lists = LogicalDescendants<ListBox>(window).ToList();
        var exercised = new HashSet<ListBox>();
        var problems = new List<string>();
        foreach (var section in Enum.GetValues<SettingsSection>().Where(s => s != SettingsSection.Codex))
        {
            window.Navigate(section);
            Settle();
            foreach (var list in lists.Where(l => l.IsVisible))
            {
                window.Activate();
                problems.AddRange(ArrowKeys(list).Select(p => $"{section}: the list \"{AutomationProperties.GetName(list)}\" {p}"));
                exercised.Add(list);
            }
        }

        Assert.Empty(problems);
        Assert.NotEmpty(lists);
        Assert.Equal(lists.Count, exercised.Count);
        window.Close();
    });

    /// <summary>
    /// Renaming from the keyboard on Manage Profiles: Tab from a profile's name moves on to the next control, and the
    /// box then shows the name as saved, trimmed, or the previous name when a blank one is refused. The rows are not
    /// rebuilt, so this no longer comes from a fresh box.
    /// </summary>
    [Fact]
    public Task RenamingFromTheKeyboardKeepsTheFocusAndShowsTheSavedName() => WpfApplication.Run(() =>
    {
        var (window, settings) = FullSettingsWindow();
        window.Navigate(SettingsSection.ManageProfiles);
        Settle();
        window.Activate();
        var box = RenameBoxes(window).First();

        Assert.True(box.Focus());
        box.Text = "  Renamed  ";
        Press(Key.Tab);
        Assert.Equal("Renamed", settings.Profiles[0].Name);
        Assert.Equal("Renamed", box.Text);
        Assert.IsNotType<Window>(Keyboard.FocusedElement);
        Assert.Same(box, RenameBoxes(window).First());

        Assert.True(box.Focus());
        box.Text = "   ";
        Press(Key.Tab);
        Assert.Equal("Renamed", settings.Profiles[0].Name);
        Assert.Equal("Renamed", box.Text);
        window.Close();
    });

    /// <summary>
    /// Hiding the last displayed profile with Space is refused (upstream: at least one), and its switch shows it displayed
    /// again instead of keeping the key press.
    /// </summary>
    [Fact]
    public Task ARefusedDisplayChangeShowsTheProfileStillDisplayed() => WpfApplication.Run(() =>
    {
        var (window, settings) = FullSettingsWindow();
        window.Navigate(SettingsSection.ManageProfiles);
        Settle();
        window.Activate();
        var switches = new List<FrameworkElement>();
        Operable(window, switches);
        var displayed = switches.OfType<CheckBox>().Where(c => c.Tag is Guid).ToList();
        Assert.Equal(settings.Profiles.Count, displayed.Count);

        CheckBox? refused = null;
        foreach (var check in displayed.Where(c => c.IsChecked == true))
        {
            Assert.True(check.Focus());
            Press(Key.Space);
            if (settings.Profiles.Single(p => p.Id == (Guid)check.Tag).IsSelectedForDisplay)
            {
                refused = check;
                break;
            }
        }

        Assert.NotNull(refused);
        Assert.True(refused.IsChecked);
        window.Close();
    });

    /// <summary>
    /// Auto-start sends through the profile's claude.ai session, so without one its switch is greyed, which also takes it
    /// out of the Tab order. It still shows what is stored, and a sign-in makes it usable.
    /// </summary>
    [Fact]
    public Task TheAutoStartSwitchIsGreyedWithoutAClaudeAiSignIn() => WpfApplication.Run(() =>
    {
        var profile = new ProfileSettings { Name = "Work", AutoStartSessionEnabled = true };
        var model = SettingsModel(new AppSettings { Profiles = [profile], ActiveProfileId = profile.Id });
        var window = new SettingsWindow(model);
        window.Navigate(SettingsSection.General);
        Show(window);
        var controls = new List<FrameworkElement>();
        Operable(window, controls);
        var toggle = controls.OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "Auto-Start New Session");

        Assert.False(toggle.IsEnabled);
        Assert.True(toggle.IsChecked);

        profile.ClaudeAi = new ClaudeAiLink { OrganizationId = "org" };
        model.Reload();
        Settle();
        Assert.True(toggle.IsEnabled);
        Assert.True(toggle.IsChecked);
        window.Close();
    });

    private static List<TextBox> RenameBoxes(Window window)
    {
        var controls = new List<FrameworkElement>();
        Operable(window, controls);
        return [.. controls.OfType<TextBox>().Where(t => t.Tag is Guid)];
    }

    /// <summary>
    /// The settings window with every optional part shown: API metric, notifications, several profiles displayed, and a
    /// claude.ai sign-in, without which the auto-start switch is greyed.
    /// </summary>
    private (SettingsWindow Window, AppSettings Settings) FullSettingsWindow()
    {
        var profile = new ProfileSettings { Name = "Work", Console = new ConsoleLink(), ClaudeAi = new ClaudeAiLink { OrganizationId = "org" } };
        var settings = new AppSettings
        {
            Profiles = [profile, new ProfileSettings { Name = "Home" }], ActiveProfileId = profile.Id, IsMultiProfile = true,
        };
        var model = SettingsModel(settings);
        model.SessionEnabled = model.WeekEnabled = model.ApiEnabled = model.NotificationsEnabled = true;
        var window = new SettingsWindow(model);
        Show(window);
        return (window, settings);
    }

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
        Show(window);
        var controls = new List<FrameworkElement>();
        Operable(window, controls);
        var problems = new List<string>();
        foreach (var control in controls)
        {
            var name = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();
            if (!IsMeaningful(name, control.DataContext))
            {
                problems.Add($"{Describe(control)} has no accessible name" + (string.IsNullOrEmpty(name) ? "" : $" (only \"{Escape(name)}\")"));
            }

            // A list is entered through its choices, which are its tab stops; TabReachesEveryControlOfEverySettingsPage
            // and ArrowKeysChangeTheChoiceInEveryList press the keys.
            if (control is not ListBox && control.IsEnabled && !(control.Focusable && KeyboardNavigation.GetIsTabStop(control)))
            {
                problems.Add($"{Describe(control)} cannot be reached with Tab");
            }
        }

        if (controls.Count == 0)
        {
            // A window that did not lay out would otherwise pass without a single check.
            problems.Add("no control was checked");
        }

        return problems;
    }

    private static void Show(Window window)
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
    }

    /// <summary>Where Tab takes the keyboard focus, from the window's first tab stop until it comes round to it again.</summary>
    private static List<IInputElement> TabRound(Window window)
    {
        window.Activate();
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        Settle();
        var first = Keyboard.FocusedElement ?? throw new InvalidOperationException("The window cannot take the keyboard focus.");
        var reached = new List<IInputElement> { first };
        for (var i = 0; i < 500; i++)
        {
            Press(Key.Tab);
            if (Keyboard.FocusedElement is not { } focused || ReferenceEquals(focused, first))
            {
                break;
            }

            reached.Add(focused);
        }

        return reached;
    }

    /// <summary>
    /// Moves the selection to the neighbouring choice with the arrow key that points at it, then back with the opposite
    /// key, starting from the selected choice.
    /// </summary>
    private static List<string> ArrowKeys(ListBox list)
    {
        var items = Enumerable.Range(0, list.Items.Count).Select(i => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).ToList();
        if (items.Count < 2)
        {
            return [$"has {items.Count} choices"];
        }

        var from = Math.Max(list.SelectedIndex, 0);
        var to = from + 1 < items.Count ? from + 1 : from - 1;
        if (!items[from].Focus())
        {
            return ["cannot take the keyboard focus"];
        }

        var problems = new List<string>();
        var offset = items[to].TranslatePoint(new Point(0, 0), items[from]);
        var key = Math.Abs(offset.Y) < items[from].RenderSize.Height / 2 ? offset.X > 0 ? Key.Right : Key.Left : offset.Y > 0 ? Key.Down : Key.Up;
        Press(key);
        if (list.SelectedIndex != to)
        {
            problems.Add($"selects choice {list.SelectedIndex} instead of {to} with {key}");
        }

        var back = key switch { Key.Right => Key.Left, Key.Left => Key.Right, Key.Down => Key.Up, _ => Key.Down };
        Press(back);
        if (list.SelectedIndex != from)
        {
            problems.Add($"selects choice {list.SelectedIndex} instead of {from} with {back}");
        }

        return problems;
    }

    /// <summary>Presses a key where the keyboard focus is, through WPF's input system as a keyboard does.</summary>
    private static void Press(Key key)
    {
        var focused = Keyboard.FocusedElement as DependencyObject ?? throw new InvalidOperationException("Nothing has the keyboard focus.");
        var source = PresentationSource.FromDependencyObject(focused) ?? throw new InvalidOperationException("The focused element is not shown.");
        foreach (var routed in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.PreviewKeyUpEvent })
        {
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routed });
        }

        Settle();
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject node) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in LogicalDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// The visible controls a user can operate, in the visual tree under <paramref name="node"/>: buttons of every kind,
    /// text and password boxes, combo boxes, sliders, lists and their choices. Parts of a control's own template are left
    /// to that control.
    /// </summary>
    private static void Operable(DependencyObject node, List<FrameworkElement> found)
    {
        if (node is FrameworkElement { IsVisible: true } element && element.TemplatedParent is not Control
            && element is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider or ListBox or ListBoxItem)
        {
            found.Add(element);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            Operable(VisualTreeHelper.GetChild(node, i), found);
        }
    }

    /// <summary>
    /// Text a screen reader can say: not empty, not only private-use icon glyphs (Segoe Fluent Icons), and not the type
    /// name that a bound record or object gives a list item or button nothing else names.
    /// </summary>
    private static bool IsMeaningful(string? name, object? data) =>
        name is not null
        && name.Any(c => !char.IsWhiteSpace(c) && c is not (>= '\uE000' and <= '\uF8FF'))
        && !(data is { } bound and not string
             && (name == bound.GetType().FullName || name.StartsWith(bound.GetType().Name + " {", StringComparison.Ordinal)));

    /// <summary>The control's type and name, and the list or the nearest named element it is in.</summary>
    private static string Describe(FrameworkElement element)
    {
        var text = string.IsNullOrEmpty(element.Name) ? element.GetType().Name : $"{element.GetType().Name} {element.Name}";
        if (element is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is { } list)
        {
            return $"{text} of the list \"{AutomationProperties.GetName(list)}\"";
        }

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
            settings,
            () =>
            {
                store.Save(settings);
                return true;
            },
            () => { }, profiles, new HistoryViewModel(history, _time, () => true),
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
