using System.Windows;
using System.Windows.Threading;

namespace ClaudeUsageTracker.Tests;

/// <summary>
/// The test run's one WPF application, holding only the app's styles and light theme, on its own STA thread. A
/// process can have only one, and its resources belong to the thread that made it, so tests that need them run
/// there. Never the app's own App class: its startup would run, and that is the real tray app polling the real
/// account.
/// </summary>
internal static class WpfApplication
{
    private static readonly Lazy<Dispatcher> Dispatcher = new(Start);

    /// <summary>Runs <paramref name="test"/> on the application's thread; its exceptions fault the task.</summary>
    public static Task Run(Action test) => Dispatcher.Value.InvokeAsync(test).Task;

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var dictionary in new[] { "Themes/Styles.xaml", "Themes/Light.xaml" })
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/ClaudeUsageTracker.App;component/{dictionary}"),
                });
            }

            ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
