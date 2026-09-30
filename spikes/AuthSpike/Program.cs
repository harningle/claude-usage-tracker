// Milestone 1 spike (plan.md section 2, "Browser authentication: a transport gate").
// Usage: AuthSpike [--profiles a,b,c] [--start https://claude.ai/]
// Each profile gets its own WebView2 user-data folder under
// %LOCALAPPDATA%\ClaudeUsageTracker.Spikes\auth\profiles. Results are appended, allowlisted
// metadata only, to %LOCALAPPDATA%\ClaudeUsageTracker.Spikes\auth\results.jsonl.
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AuthSpike;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string Option(string name, string fallback) =>
            Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        var names = Option("--profiles", "a").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var start = Option("--start", "https://claude.ai/");

        var app = new Application();
        var logBox = new TextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), TextWrapping = TextWrapping.Wrap };
        Directory.CreateDirectory(Path.GetDirectoryName(Probes.ResultsPath)!);

        void Log(string message)
        {
            var line = $"{DateTimeOffset.Now:HH:mm:ss} {message}";
            logBox.AppendText(line + Environment.NewLine);
            logBox.ScrollToEnd();
            File.AppendAllText(Probes.ResultsPath, JsonSerializer.Serialize(new { time = DateTimeOffset.Now, note = message }) + Environment.NewLine);
        }

        void Record(string profile, IEnumerable<ProbeResult> results)
        {
            foreach (var r in results)
            {
                Log($"[{profile}] {r.Route,-48} {r.Endpoint,-13} {r.Status?.ToString() ?? "---"} {r.ContentType ?? "-"} {r.Protocol} " +
                    $"{r.Ms} ms shape {r.Shape}{(r.CfMitigated ? " CF-MITIGATED" : "")}{(r.ChallengeText ? " CHALLENGE-PAGE" : "")}{(r.Error is null ? "" : " error " + r.Error)}");
                File.AppendAllText(Probes.ResultsPath, JsonSerializer.Serialize(new { time = DateTimeOffset.Now, profile, result = r }) + Environment.NewLine);
            }
        }

        var sessions = names.Select(n => new ProfileSession(n, Log)).ToList();
        // Side by side rather than tabs: a WebView2 outside the visual tree cannot create its window,
        // which stalled initialisation of unselected tabs.
        var views = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        var selector = new ComboBox { Margin = new Thickness(3), MinWidth = 90 };
        foreach (var session in sessions)
        {
            views.Children.Add(new GroupBox { Header = $"profile {session.Name}", Content = session.View });
            selector.Items.Add(session.Name);
        }

        selector.SelectedIndex = 0;

        async Task Guarded(string what, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                Log($"{what} FAILED: {e.GetType().Name}: {e.Message}");
            }
        }

        // One browser/network operation at a time: polling, comparisons, the isolation check and
        // clearing never overlap, so slow cycles cannot pile up and no profile is disposed mid-request.
        var gate = new SemaphoreSlim(1, 1);

        void MemoryReport()
        {
            long total = 0;
            foreach (var s in sessions)
            {
                var (count, working, privateMb, kinds) = s.Memory();
                total += privateMb;
                Log($"[{s.Name}] memory: {count} processes ({kinds}), working set {working} MB, private {privateMb} MB");
            }

            using var self = System.Diagnostics.Process.GetCurrentProcess();
            Log($"memory: all browser groups private {total} MB; host process private {self.PrivateMemorySize64 / (1024 * 1024)} MB");
        }

        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        var polls = 0;
        poll.Tick += async (_, _) =>
        {
            if (!gate.Wait(0))
            {
                Log("poll skipped: the previous operation is still running");
                return;
            }

            try
            {
                polls++;
                foreach (var s in sessions.Where(s => s.View.CoreWebView2 is not null).ToList())
                {
                    await Guarded($"[{s.Name}] poll", async () => Record(s.Name, await s.CompareAsync(console: false)));
                }

                if (polls % 2 == 0)
                {
                    MemoryReport();
                }
            }
            finally
            {
                gate.Release();
            }
        };

        var toolbar = new WrapPanel { Margin = new Thickness(4) };
        toolbar.Children.Add(new TextBlock { Text = "Profile:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) });
        toolbar.Children.Add(selector);
        // `bind` runs synchronously on click, before any wait for the gate, so a queued action keeps
        // the target it was clicked for. It returns null to cancel the click.
        Button AddButton(string text, Func<Func<Task>?> bind, bool exclusive)
        {
            var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += async (_, _) =>
            {
                var run = bind();
                if (run is null)
                {
                    return;
                }

                if (exclusive)
                {
                    await gate.WaitAsync();
                }

                try
                {
                    await Guarded(text, run);
                }
                finally
                {
                    if (exclusive)
                    {
                        gate.Release();
                    }
                }
            };
            toolbar.Children.Add(button);
            return button;
        }

        Button ButtonFor(string text, Func<Task> action, bool exclusive = false) => AddButton(text, () => action, exclusive);

        ProfileSession? SelectedAtClick(string text)
        {
            var session = sessions.FirstOrDefault(s => s.Name == (string?)selector.SelectedItem);
            if (session is null)
            {
                Log($"{text}: no profile selected");
            }

            return session;
        }

        Button ProfileButton(string text, Func<ProfileSession, Task> action, bool exclusive = false) =>
            AddButton(text, () => SelectedAtClick(text) is { } session ? () => action(session) : null, exclusive);

        ProfileButton("claude.ai sign-in", s => { s.Core.Navigate("https://claude.ai/login"); return Task.CompletedTask; });
        ProfileButton("claude.ai home", s => { s.Core.Navigate("https://claude.ai/"); return Task.CompletedTask; });
        ProfileButton("Park on claude.ai/robots.txt", s => { s.Core.Navigate("https://claude.ai/robots.txt"); return Task.CompletedTask; });
        ProfileButton("Compare claude.ai", async s => Record(s.Name, await s.CompareAsync(console: false)), exclusive: true);
        ProfileButton("Console sign-in", s => { s.Core.Navigate("https://console.anthropic.com/login"); return Task.CompletedTask; });
        ProfileButton("Compare Console", async s => Record(s.Name, await s.CompareAsync(console: true)), exclusive: true);
        var pollButton = ButtonFor("Start polling (30 s)", () => Task.CompletedTask);
        pollButton.Click += (_, _) =>
        {
            poll.IsEnabled = !poll.IsEnabled;
            pollButton.Content = poll.IsEnabled ? "Stop polling" : "Start polling (30 s)";
            Log(poll.IsEnabled ? "polling started (browser fetch + replay every 30 s)" : "polling stopped");
        };
        ButtonFor("Memory", () => { MemoryReport(); return Task.CompletedTask; });
        // Fresh claude.ai observations per profile, compared pairwise in memory; only verdicts are
        // logged. Different session cookies show storage isolation; different account IDs show
        // different accounts; shared organisations alone prove nothing (accounts can share one).
        ButtonFor("Check isolation", async () =>
        {
            var observed = new List<(string Name, string? Key, List<string> Orgs, string? Account)>();
            foreach (var s in sessions.ToList())
            {
                await Guarded($"[{s.Name}] observe", async () =>
                {
                    var (key, orgs, account, status) = await s.ObserveClaudeAiAsync();
                    observed.Add((s.Name, key, orgs, account));
                    Log($"[{s.Name}] observed now: sessionKey {(key is null ? "absent" : "present")}, {orgs.Count} organisations, " +
                        $"/api/account {status?.ToString() ?? "failed"}{(account is null ? " (no account ID)" : "")}");
                });
            }

            for (var i = 0; i < observed.Count; i++)
            {
                for (var j = i + 1; j < observed.Count; j++)
                {
                    var (a, b) = (observed[i], observed[j]);
                    var cookies = a.Key is null || b.Key is null ? "not both signed in" : a.Key != b.Key ? "different" : "SAME";
                    var accounts = a.Account is null || b.Account is null ? "unknown" : a.Account != b.Account ? "different" : "SAME";
                    Log($"isolation {a.Name} vs {b.Name}: session cookies {cookies}; accounts {accounts}; " +
                        $"shared organisations {a.Orgs.Intersect(b.Orgs).Count()} (sharing can be legitimate)");
                }
            }
        }, exclusive: true);
        // Bound at click time; a second click while a clear of that profile is pending is refused
        // rather than queued, so repeated clicks cannot clear successive profiles.
        var clearing = new HashSet<ProfileSession>();
        var clearButton = AddButton("Clear selected profile", () =>
        {
            if (SelectedAtClick("Clear selected profile") is not { } session)
            {
                return null;
            }

            if (!clearing.Add(session))
            {
                Log($"[{session.Name}] clear already pending; click ignored");
                return null;
            }

            return async () =>
            {
                views.Children.RemoveAt(sessions.IndexOf(session));
                selector.Items.Remove(session.Name);
                selector.SelectedIndex = 0;
                sessions.Remove(session);
                await session.ClearAsync();
            };
        }, exclusive: true);

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        var logHost = new Border { Height = 220, Child = logBox };
        DockPanel.SetDock(logHost, Dock.Bottom);
        layout.Children.Add(logHost);
        layout.Children.Add(views);
        var window = new Window { Title = "AuthSpike - WebView2 fetch vs HttpClient replay", Width = 1500, Height = 950, Content = layout };

        window.Loaded += async (_, _) =>
        {
            Log($"runtime: WebView2 {CoreWebView2Environment.GetAvailableBrowserVersionString()}, .NET {Environment.Version}, " +
                $"{RuntimeInformation.OSDescription}; profiles {string.Join(",", names)}; results {Probes.ResultsPath}");
            foreach (var session in sessions)
            {
                await Guarded($"[{session.Name}] initialise", () => session.InitialiseAsync(start));
            }

            MemoryReport();
            if (args.Contains("--auto-compare-and-exit"))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                foreach (var session in sessions)
                {
                    await Guarded($"[{session.Name}] compare", async () => Record(session.Name, await session.CompareAsync(console: false)));
                }

                MemoryReport();
                if (args.Contains("--clear-after"))
                {
                    foreach (var session in sessions)
                    {
                        await session.ClearAsync();
                    }
                }

                app.Shutdown();
            }

            // Reproduces the queued-clear race: another operation holds the gate, Clear is clicked twice
            // for the first profile, then the selection moves on before the gate is released. Only the
            // first profile may be cleared. Remaining profiles are cleared afterwards to leave no state.
            if (args.Contains("--self-test-clear") && sessions.Count >= 2)
            {
                var (first, second) = (sessions[0].Name, sessions[1].Name);
                await gate.WaitAsync();
                selector.SelectedItem = first;
                clearButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                clearButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                selector.SelectedItem = second;
                gate.Release();
                await gate.WaitAsync(); // queued behind the pending clear, so this returns after it
                gate.Release();
                var remaining = string.Join(",", sessions.Select(s => s.Name));
                Log($"self-test-clear: {(remaining == string.Join(",", names.Skip(1)) ? "PASS" : "FAIL")}; " +
                    $"clicked for {first}, selection moved to {second}; remaining profiles [{remaining}]");
                foreach (var session in sessions.ToList())
                {
                    await session.ClearAsync();
                }

                app.Shutdown();
            }
        };
        app.Run(window);
    }
}
