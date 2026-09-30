using System.Windows;

namespace ClaudeUsageTracker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Tray lifecycle, popover and settings arrive in Milestone 2 (plan.md section 3).
        // Until then the scaffold exits instead of lingering as an invisible process.
        Shutdown(1);
    }
}
