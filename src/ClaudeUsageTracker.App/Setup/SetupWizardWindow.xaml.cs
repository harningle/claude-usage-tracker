using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using ClaudeUsageTracker.App.Localisation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App.Setup;

/// <summary>
/// Shows one panel per <see cref="WizardStep"/>. Closing the window at any step saves nothing; only Done
/// does. A test still in flight when the window closes is cancelled.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardViewModel _wizard;
    private readonly CancellationTokenSource _closing = new();

    public SetupWizardWindow(SetupWizardViewModel wizard)
    {
        InitializeComponent();
        _wizard = wizard;
        DataContext = wizard;
        wizard.PropertyChanged += OnWizardChanged;
        wizard.Saved += _ => Close();
        Closed += (_, _) => _closing.Cancel();
        Render();
    }

    private void OnWizardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SetupWizardViewModel.Step) or nameof(SetupWizardViewModel.Message)
            or nameof(SetupWizardViewModel.Selected) or nameof(SetupWizardViewModel.Candidates)
            or nameof(SetupWizardViewModel.Route) or nameof(SetupWizardViewModel.IsBusy)
            or nameof(SetupWizardViewModel.SelectedOrganization) or nameof(SetupWizardViewModel.Organizations))
        {
            Dispatcher.BeginInvoke(Render);
        }
    }

    private void Render()
    {
        var step = _wizard.Step;
        var console = _wizard.Route == WizardRoute.Console;
        ChoosePanel.Visibility = Show(step == WizardStep.ChooseProvider);
        BusyPanel.Visibility = Show(step is WizardStep.Detecting or WizardStep.Testing or WizardStep.Saving);
        DetectedPanel.Visibility = Show(step == WizardStep.Detected);
        NotFoundPanel.Visibility = Show(step == WizardStep.NotFound);
        EnterKeyPanel.Visibility = Show(step == WizardStep.EnterKey);
        OrganizationPanel.Visibility = Show(step == WizardStep.SelectOrganization);
        CodexDetectedPanel.Visibility = Show(step == WizardStep.CodexDetected);
        CodexNotDetectedPanel.Visibility = Show(step == WizardStep.CodexNotDetected);
        FailedPanel.Visibility = Show(step == WizardStep.TestFailed);
        ReviewPanel.Visibility = Show(step == WizardStep.Review);
        SaveError.Visibility = Show(step == WizardStep.Review && _wizard.Message is not null);
        Progress.Visibility = Show(step != WizardStep.ChooseProvider);
        EnterKeyPanel.IsEnabled = !_wizard.IsBusy;
        BusyText.Text = step switch
        {
            WizardStep.Testing => Strings.Get("wizard.testing"),
            WizardStep.Saving => Strings.Get("wizard.saving"),
            _ => Strings.Get("setup.cli_detecting"),
        };
        Subtitle.Text = step == WizardStep.ChooseProvider
            ? Strings.Get("setup.choose_provider.subtitle")
            : Strings.Get("windows.setup.subtitle");
        RenderProgress(_wizard.ProgressStep);

        // Step 1 of the claude.ai and Console routes reads the same, apart from which site it names.
        KeyTitle.Text = Strings.Get(console ? "windows.setup.console.signin_title" : "personal.signin_sheet_title");
        KeyDescription.Text = Strings.Get(console ? "windows.setup.console.signin_description" : "windows.setup.claude_ai.signin_description");
        SignInButton.Content = Strings.Get(console ? "windows.setup.console.signin_button" : "personal.signin_button");
        KeyHelp.Text = Strings.Get(console ? "windows.setup.console.key_help" : "personal.help_session_key");
        KeyStorageNote.Text = Strings.Get(console ? "windows.setup.console.storage_note" : "windows.setup.claude_ai.storage_note");
        OrganizationSubtitle.Text = Strings.Get(console ? "windows.setup.console.select_org_subtitle" : "wizard.select_org_subtitle");
        CodexAccount.Text = _wizard.CodexEmail is { } email ? Strings.Format("codex.signed_in_as", email) : "";

        if (step == WizardStep.Detected)
        {
            CheckSelected(CandidateList, _wizard.Selected);
        }
        else if (step == WizardStep.SelectOrganization)
        {
            CheckSelected(OrganizationList, _wizard.SelectedOrganization);
        }
    }

    /// <summary>Checks the selected item's card once its container exists.</summary>
    private void CheckSelected(ItemsControl list, object? selected) =>
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in list.Items)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter presenter && FindRadio(presenter) is { } radio)
                {
                    radio.IsChecked = Equals(item, selected);
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void RenderProgress(int current)
    {
        var accent = (System.Windows.Media.Brush)FindResource("Accent");
        var done = (System.Windows.Media.Brush)FindResource("Status.Safe");
        var idle = (System.Windows.Media.Brush)FindResource("Track");
        var steps = new[] { (Step1, Step1Text), (Step2, Step2Text), (Step3, Step3Text) };
        for (var i = 0; i < steps.Length; i++)
        {
            var (circle, text) = steps[i];
            var number = i + 1;
            circle.Background = number < current ? done : number == current ? accent : idle;
            text.Foreground = number <= current ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("Foreground.Secondary");
            // Upstream shows a check mark on completed steps.
            text.Text = number < current ? "" : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            text.FontFamily = number < current ? (System.Windows.Media.FontFamily)FindResource("IconFont") : (System.Windows.Media.FontFamily)FindResource("UiFont");
        }

        Line1.Fill = current > 1 ? done : idle;
        Line2.Fill = current > 2 ? done : idle;
    }

    private static RadioButton? FindRadio(DependencyObject node)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is RadioButton radio)
            {
                return radio;
            }

            if (FindRadio(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnAnthropic(object sender, RoutedEventArgs e) => _wizard.ChooseAnthropic();

    private void OnCodex(object sender, RoutedEventArgs e) => _wizard.ChooseCodex();

    private void OnManual(object sender, RoutedEventArgs e) => _wizard.SetUpWithClaudeAi();

    private void OnCheckAgain(object sender, RoutedEventArgs e) => _wizard.Detect();

    private void OnBack(object sender, RoutedEventArgs e) => _wizard.Back();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e) => Run(() => _wizard.SaveAsync(_closing.Token));

    private void OnStartTracking(object sender, RoutedEventArgs e) => Run(() => _wizard.TestAsync(_closing.Token));

    private void OnSignIn(object sender, RoutedEventArgs e) => Run(() => _wizard.SignInAsync(_closing.Token));

    private void OnTestKey(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password;
        KeyBox.Clear();
        Run(() => _wizard.TestKeyAsync(key, _closing.Token));
    }

    private void OnTestCodex(object sender, RoutedEventArgs e) => Run(() => _wizard.TestCodexAsync(CodexBox.Text, _closing.Token));

    private void OnCandidateChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: CandidateView candidate })
        {
            _wizard.Selected = candidate;
        }
    }

    private void OnOrganizationChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: OrganizationView organization })
        {
            _wizard.SelectedOrganization = organization;
        }
    }

    /// <summary>
    /// Runs a step of the wizard. A step still running when the window closes is cancelled and saves
    /// nothing; a browser that cannot start is reported instead of ending the app.
    /// </summary>
    private async void Run(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The window closed during the step; nothing was saved.
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or COMException)
        {
            MessageBox.Show(this, Strings.Format("windows.signin.browser_failed", error.Message), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Strings.Get("windows.setup.choose_folder.title") };
        if (dialog.ShowDialog(this) == true)
        {
            _wizard.AddFolder(dialog.FolderName);
        }
    }
}
