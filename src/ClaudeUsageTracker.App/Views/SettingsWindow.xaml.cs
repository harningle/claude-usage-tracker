using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ClaudeUsageTracker.App.Automation;
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
    AppSettings,
    ManageProfiles,
    ClaudeCode,
    Hud,
    Shortcuts,
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
        PreviewKeyDown += OnRecordingKeyDown;
        // A shortcut is recorded only while this window has the keyboard.
        Deactivated += (_, _) => _viewModel.StopRecording();
        Closed += (_, _) => _viewModel.StopRecording();
    }

    /// <summary>"Use in Claude Code" on the CLI Account page.</summary>
    public event Action? SwitchCliRequested;

    /// <summary>Cancel the switch that waits for Claude Code to close.</summary>
    public event EventHandler? CancelCliSwitchRequested;

    /// <summary>The user has signed in again in Claude Code after an unfinished switch; the user has confirmed.</summary>
    public event EventHandler? SignedInAgainRequested;

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
        SettingsSection.AppSettings => NavAppSettings,
        SettingsSection.ManageProfiles => NavProfiles,
        SettingsSection.ClaudeCode => NavClaudeCode,
        SettingsSection.Hud => NavHud,
        SettingsSection.Shortcuts => NavShortcuts,
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
                     (PageHistory, SettingsSection.History), (PageAppSettings, SettingsSection.AppSettings), (PageProfiles, SettingsSection.ManageProfiles),
                     (PageShortcuts, SettingsSection.Shortcuts), (PagePopover, SettingsSection.Popover), (PageDebug, SettingsSection.Debug),
                     (PageAbout, SettingsSection.About), (PageClaudeCode, SettingsSection.ClaudeCode), (PageHud, SettingsSection.Hud),
                 })
        {
            page.Visibility = shown == section ? Visibility.Visible : Visibility.Collapsed;
        }

        if (section != SettingsSection.Shortcuts)
        {
            _viewModel.StopRecording();
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
        else if (section == SettingsSection.ClaudeCode)
        {
            // What is installed is checked when its page opens.
            _viewModel.Statusline.Refresh();
        }
        else if (section == SettingsSection.Hud)
        {
            _viewModel.Hud.Refresh();
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

    private void OnSwitchCli(object sender, RoutedEventArgs e) => SwitchCliRequested?.Invoke();

    private void OnCancelCliSwitch(object sender, RoutedEventArgs e) => CancelCliSwitchRequested?.Invoke(this, EventArgs.Empty);

    private void OnSignedInAgain(object sender, RoutedEventArgs e)
    {
        if (Confirm(Strings.Get("windows.cli.switch.signed_in_button"), Strings.Get("windows.cli.switch.signed_in_confirm")))
        {
            SignedInAgainRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnAddThreshold(object sender, RoutedEventArgs e)
    {
        // As upstream, an invalid value is simply cleared.
        _viewModel.AddCustomThreshold(NewThreshold.Text);
        NewThreshold.Text = "";
    }

    private void OnThresholdKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnAddThreshold(sender, e);
            e.Handled = true;
        }
    }

    private void OnRemoveThreshold(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int value })
        {
            _viewModel.RemoveCustomThreshold(value);
        }
    }

    private void OnPreviewSound(object sender, RoutedEventArgs e) => _viewModel.PreviewSound();

    private void OnInstallLauncher(object sender, RoutedEventArgs e) => _viewModel.Launcher.Install();

    private void OnRemoveLauncher(object sender, RoutedEventArgs e) => _viewModel.Launcher.Remove();

    private void OnCopyLauncher(object sender, RoutedEventArgs e) => Clipboard.SetText(_viewModel.Launcher.Command);

    /// <summary>
    /// Upstream's Apply. A folder that already has a status line of its own is asked about, one by one; it is
    /// replaced only on a yes.
    /// </summary>
    private void OnApplyStatusline(object sender, RoutedEventArgs e)
    {
        var foreign = _viewModel.Statusline.Apply([]);
        var agreed = foreign
            .Where(folder => Confirm(
                Strings.Get("windows.claudecode.foreign.title"),
                Strings.Format("windows.claudecode.foreign.message", folder.ConfigDirectory, folder.Command ?? "")))
            .Select(folder => folder.ConfigDirectory)
            .ToList();
        if (agreed.Count > 0)
        {
            foreign = _viewModel.Statusline.Apply(agreed);
        }

        if (foreign.Count > 0)
        {
            _viewModel.Statusline.KeptForeign(foreign.Select(folder => folder.ConfigDirectory));
        }
    }

    private void OnResetStatusline(object sender, RoutedEventArgs e) => _viewModel.Statusline.Reset();

    private void OnDismissStatuslineMessage(object sender, RoutedEventArgs e) => _viewModel.Statusline.DismissMessage();

    private void OnUpdateHelper(object sender, RoutedEventArgs e) => _viewModel.Statusline.UpdateHelper();

    private void OnRepairIntegration(object sender, RoutedEventArgs e) => _viewModel.Statusline.Repair();

    private void OnChangeHooks(object sender, RoutedEventArgs e) => _viewModel.Hud.ChangeHooks();

    private void OnRetryHud(object sender, RoutedEventArgs e) => _viewModel.Hud.Retry();

    private void OnHudNewPort(object sender, RoutedEventArgs e) => _viewModel.Hud.UseAnotherPort();

    /// <summary>Upstream's recorder: a click starts recording, a second click stops it.</summary>
    private void OnRecordShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyAction action })
        {
            return;
        }

        if (_viewModel.ShortcutRows.FirstOrDefault(r => r.Action == action)?.IsRecording == true)
        {
            _viewModel.StopRecording();
        }
        else
        {
            _viewModel.StartRecording(action);
        }
    }

    private void OnClearShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HotkeyAction action })
        {
            _viewModel.ClearShortcut(action);
        }
    }

    /// <summary>While a shortcut is recorded, every key goes to the recorder, including Space and Enter.</summary>
    private void OnRecordingKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingShortcut)
        {
            return;
        }

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        e.Handled = _viewModel.Record(KeyInterop.VirtualKeyFromKey(key), Shortcuts.ModifiersOf(Keyboard.Modifiers));
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
