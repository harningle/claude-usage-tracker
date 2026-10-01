using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Localisation;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Providers;
using Microsoft.Win32;

namespace ClaudeUsageTracker.App.Views;

public enum SettingsSection
{
    ClaudeAi,
    Console,
    CliAccount,
    Codex,
    Appearance,
    General,
    History,
    ManageProfiles,
    Popover,
    Debug,
    About,
}

/// <summary>The sidebar settings window. Upstream opens on Appearance; links open the default browser.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private SettingsSection _section;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Navigate(SettingsSection.Appearance);
    }

    /// <summary>Set up or change one credential of the active profile, through the wizard.</summary>
    public event Action<WizardRoute>? SetupRequested;

    /// <summary>Remove one credential of the active profile; the user has confirmed.</summary>
    public event Action<WizardRoute>? RemoveRequested;

    /// <summary>Delete a profile; the user has confirmed.</summary>
    public event Action<Guid>? DeleteProfileRequested;

    public event EventHandler? WizardRequested;
    public event EventHandler? QuitRequested;

    /// <summary>The page being shown, so the owner can refresh only what is visible.</summary>
    public SettingsSection Section => _section;

    public void Navigate(SettingsSection section)
    {
        NavButton(section).IsChecked = true;
        ShowSection(section);
    }

    /// <summary>
    /// After the active profile changed: a credential page of the other provider no longer applies, so the
    /// window returns to Appearance.
    /// </summary>
    public void ProfileChanged()
    {
        if (NavButton(_section).Visibility != Visibility.Visible)
        {
            Navigate(SettingsSection.Appearance);
        }
        else
        {
            ShowSection(_section);
        }
    }

    private RadioButton NavButton(SettingsSection section) => section switch
    {
        SettingsSection.ClaudeAi => NavClaudeAi,
        SettingsSection.Console => NavConsole,
        SettingsSection.CliAccount => NavCli,
        SettingsSection.Codex => NavCodex,
        SettingsSection.General => NavGeneral,
        SettingsSection.History => NavHistory,
        SettingsSection.ManageProfiles => NavProfiles,
        SettingsSection.Popover => NavPopover,
        SettingsSection.Debug => NavDebug,
        SettingsSection.About => NavAbout,
        _ => NavAppearance,
    };

    private void ShowSection(SettingsSection section)
    {
        _section = section;
        foreach (var (page, shown) in new (FrameworkElement, SettingsSection)[]
                 {
                     (PageClaudeAi, SettingsSection.ClaudeAi), (PageConsole, SettingsSection.Console), (PageCli, SettingsSection.CliAccount),
                     (PageCodex, SettingsSection.Codex), (PageAppearance, SettingsSection.Appearance), (PageGeneral, SettingsSection.General),
                     (PageHistory, SettingsSection.History), (PageProfiles, SettingsSection.ManageProfiles), (PagePopover, SettingsSection.Popover),
                     (PageDebug, SettingsSection.Debug), (PageAbout, SettingsSection.About),
                 })
        {
            page.Visibility = shown == section ? Visibility.Visible : Visibility.Collapsed;
        }

        // The history and the log are read when their page opens, not kept current behind the others.
        if (section == SettingsSection.History)
        {
            _viewModel.ReloadHistory();
        }
        else if (section == SettingsSection.Debug)
        {
            _viewModel.ReloadLog();
        }
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            if (ReferenceEquals(NavButton(section), sender))
            {
                ShowSection(section);
                return;
            }
        }
    }

    /// <summary>Upstream's sidebar profile switcher, as a native menu.</summary>
    private void OnProfileMenu(object sender, RoutedEventArgs e)
    {
        var profiles = _viewModel.Profiles;
        var corner = ProfileButton.PointToScreen(new Point(0, ProfileButton.ActualHeight));
        var chosen = NativeMethods.ShowMenu(
            new WindowInteropHelper(this).Handle, (int)corner.X, (int)corner.Y,
            [.. profiles.Select((p, i) => (i + 1, (string?)p.Name))],
            profiles.ToList().FindIndex(p => p.IsActive) + 1);
        if (chosen > 0)
        {
            _viewModel.ActivateProfile(profiles[chosen - 1].Id);
            ProfileChanged();
        }
    }

    private void OnCredentialSetup(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WizardRoute route })
        {
            SetupRequested?.Invoke(route);
        }
    }

    private void OnCredentialRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WizardRoute route })
        {
            return;
        }

        var (title, message) = route switch
        {
            WizardRoute.ClaudeAi => ("personal.confirm_remove_title", "windows.claude_ai.remove_confirm.message"),
            WizardRoute.Console => ("api.remove_title", "windows.api.remove_confirm.message"),
            WizardRoute.Codex => ("windows.codex.remove_confirm.title", "windows.codex.remove_confirm.message"),
            _ => ("windows.cli.remove_confirm.title", "windows.cli.remove_confirm.message"),
        };
        if (Confirm(Strings.Get(title), Strings.Get(message)))
        {
            RemoveRequested?.Invoke(route);
        }
    }

    private void OnHistoryBack(object sender, RoutedEventArgs e) => _viewModel.History.Back();

    private void OnHistoryForward(object sender, RoutedEventArgs e) => _viewModel.History.Forward();

    private void OnHistoryNow(object sender, RoutedEventArgs e) => _viewModel.History.Now();

    private void OnExportHistoryJson(object sender, RoutedEventArgs e) => ExportHistory(csv: false);

    private void OnExportHistoryCsv(object sender, RoutedEventArgs e) => ExportHistory(csv: true);

    private void ExportHistory(bool csv)
    {
        var extension = csv ? "csv" : "json";
        Export(
            $"claude-usage-history-{DateTime.Now:yyyy-MM-dd}.{extension}", extension,
            path => _viewModel.History.Export(path, csv));
    }

    private void OnExportLog(object sender, RoutedEventArgs e) =>
        Export($"claude-usage-requests-{DateTime.Now:yyyy-MM-dd}.jsonl", "jsonl", _viewModel.ExportLog);

    /// <summary>Asks where to save and writes the file; a failure is shown, never ignored.</summary>
    private void Export(string fileName, string extension, Func<string, int> write)
    {
        var dialog = new SaveFileDialog { FileName = fileName, DefaultExt = extension, Filter = $"*.{extension}|*.{extension}" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var count = write(dialog.FileName);
            MessageBox.Show(this, Strings.Format("windows.export.done", count, dialog.FileName), Strings.Get("history.export.title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Storage.HistoryStoreException)
        {
            MessageBox.Show(this, Strings.Format("windows.export.failed", error.Message), Strings.Get("history.export.title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        if (!Confirm(Strings.Get("debug.clear_all_logs"), Strings.Get("debug.clear_logs_message")))
        {
            return;
        }

        try
        {
            _viewModel.ClearLog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, Strings.Get("debug.clear_all_logs"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCreateProfile(object sender, RoutedEventArgs e)
    {
        _viewModel.CreateProfile(NewProfileName.Text, NewProfileCodex.IsChecked == true ? Provider.Codex : Provider.Anthropic);
        NewProfileName.Text = "";
    }

    private void OnProfileNameCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: Guid id } box)
        {
            _viewModel.RenameProfile(id, box.Text);
        }
    }

    private void OnProfileNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            // Committing is what leaving the box does; move focus on so Enter and Tab behave alike.
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private void OnActivateProfile(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
        {
            _viewModel.ActivateProfile(id);
            ProfileChanged();
        }
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id } && _viewModel.Profiles.FirstOrDefault(p => p.Id == id) is { } profile
            && Confirm(Strings.Get("profiles.delete_title"), Strings.Format("profiles.delete_confirm", profile.Name)))
        {
            DeleteProfileRequested?.Invoke(id);
        }
    }

    private void OnToggleDisplayed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: Guid id })
        {
            _viewModel.ToggleDisplayed(id);
        }
    }

    private bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

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
