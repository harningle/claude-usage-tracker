using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.ViewModels;

namespace ClaudeUsageTracker.App.Views;

public enum SettingsSection
{
    CliAccount,
    Appearance,
    General,
    Popover,
    About,
}

/// <summary>The sidebar settings window. Upstream opens on Appearance; links open the default browser.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsLinked))
            {
                UpdateLinkDot();
            }
        };
        UpdateLinkDot();
        Navigate(SettingsSection.Appearance);
    }

    public event EventHandler? SyncRequested;
    public event EventHandler? RemoveRequested;
    public event EventHandler? WizardRequested;
    public event EventHandler? QuitRequested;

    public void Navigate(SettingsSection section)
    {
        var button = section switch
        {
            SettingsSection.CliAccount => NavCli,
            SettingsSection.General => NavGeneral,
            SettingsSection.Popover => NavPopover,
            SettingsSection.About => NavAbout,
            _ => NavAppearance,
        };
        button.IsChecked = true;
        ShowSection(section);
    }

    private void ShowSection(SettingsSection section)
    {
        PageCli.Visibility = Visible(section == SettingsSection.CliAccount);
        PageAppearance.Visibility = Visible(section == SettingsSection.Appearance);
        PageGeneral.Visibility = Visible(section == SettingsSection.General);
        PagePopover.Visibility = Visible(section == SettingsSection.Popover);
        PageAbout.Visibility = Visible(section == SettingsSection.About);
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateLinkDot() =>
        LinkDot.SetResourceReference(Shape.FillProperty, _viewModel.IsLinked ? "Status.Safe" : "Foreground.Secondary");

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender == NavCli)
        {
            ShowSection(SettingsSection.CliAccount);
        }
        else if (sender == NavGeneral)
        {
            ShowSection(SettingsSection.General);
        }
        else if (sender == NavPopover)
        {
            ShowSection(SettingsSection.Popover);
        }
        else if (sender == NavAbout)
        {
            ShowSection(SettingsSection.About);
        }
        else
        {
            ShowSection(SettingsSection.Appearance);
        }
    }

    private void OnSync(object sender, RoutedEventArgs e) => SyncRequested?.Invoke(this, EventArgs.Empty);

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, Strings.Get("windows.cli.remove_confirm.message"), Strings.Get("windows.cli.remove_confirm.title"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            RemoveRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRunWizard(object sender, RoutedEventArgs e) => WizardRequested?.Invoke(this, EventArgs.Empty);

    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void OnLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
