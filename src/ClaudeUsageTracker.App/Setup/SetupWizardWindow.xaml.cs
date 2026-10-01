using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClaudeUsageTracker.App.Localisation;
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
            or nameof(SetupWizardViewModel.Selected) or nameof(SetupWizardViewModel.Candidates))
        {
            Dispatcher.BeginInvoke(Render);
        }
    }

    private void Render()
    {
        var step = _wizard.Step;
        ChoosePanel.Visibility = Show(step == WizardStep.ChooseProvider);
        BusyPanel.Visibility = Show(step is WizardStep.Detecting or WizardStep.Testing or WizardStep.Saving);
        DetectedPanel.Visibility = Show(step == WizardStep.Detected);
        NotFoundPanel.Visibility = Show(step == WizardStep.NotFound);
        FailedPanel.Visibility = Show(step == WizardStep.TestFailed);
        ReviewPanel.Visibility = Show(step == WizardStep.Review);
        SaveError.Visibility = Show(step == WizardStep.Review && _wizard.Message is not null);
        Progress.Visibility = Show(step != WizardStep.ChooseProvider);
        BusyText.Text = step switch
        {
            WizardStep.Testing => Strings.Get("wizard.testing"),
            WizardStep.Saving => Strings.Get("wizard.saving"),
            _ => Strings.Get("setup.cli_detecting"),
        };
        Subtitle.Text = step == WizardStep.ChooseProvider
            ? Strings.Get("setup.choose_provider.subtitle")
            : Strings.Get("setup.welcome.subtitle");
        RenderProgress(_wizard.ProgressStep);

        if (step == WizardStep.Detected)
        {
            // Check the selected candidate once its containers exist.
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var item in CandidateList.Items)
                {
                    if (CandidateList.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter presenter
                        && FindRadio(presenter) is { } radio)
                    {
                        radio.IsChecked = ReferenceEquals(item, _wizard.Selected);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

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

    private void OnCheckAgain(object sender, RoutedEventArgs e) => _wizard.Detect();

    private void OnBack(object sender, RoutedEventArgs e) => _wizard.Back();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e) => _wizard.Save();

    private void OnCandidateChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: CandidateView candidate })
        {
            _wizard.Selected = candidate;
        }
    }

    private async void OnStartTracking(object sender, RoutedEventArgs e)
    {
        try
        {
            await _wizard.TestAsync(_closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The window closed during the test; nothing was saved.
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
