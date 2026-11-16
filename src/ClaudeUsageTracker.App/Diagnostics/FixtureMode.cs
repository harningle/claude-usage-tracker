using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Updates;

namespace ClaudeUsageTracker.App.Diagnostics;

/// <summary>
/// Development fixture mode, <c>--fixture &lt;folder&gt;</c>: the app uses <c>&lt;folder&gt;\home</c> as the user
/// profile (so Claude Code and Codex files come from a synthetic <c>home\.claude</c> and <c>home\.codex</c>),
/// <c>&lt;folder&gt;\data</c> as its data root, and answers every request from <c>&lt;folder&gt;\responses</c>
/// without any network access, browser or real process. Everything it shows is labelled as fixture data, so
/// screenshots never pass for real account data (plan.md, Milestone 2 acceptance).
/// </summary>
public sealed partial class FixtureMode
{
    private FixtureMode(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string UserProfile => Path.Combine(Root, "home");
    public string DataRoot => Path.Combine(Root, "data");
    public string Responses => Path.Combine(Root, "responses");

    /// <summary>
    /// The fixture folder from <c>--fixture &lt;folder&gt;</c>, or null when no fixture argument is given. A malformed
    /// fixture argument throws, because falling through would start live mode with the real login.
    /// </summary>
    public static FixtureMode? FromArguments(IReadOnlyList<string> args)
    {
        var flags = Enumerable.Range(0, args.Count)
            .Where(i => args[i].StartsWith("--fixture", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (flags.Count == 0)
        {
            return null;
        }

        if (flags is not [var index] || args[index] != "--fixture" || index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Fixture mode needs exactly one \"--fixture <folder>\".");
        }

        var folder = args[index + 1];
        return Directory.Exists(folder)
            ? new FixtureMode(folder)
            : throw new ArgumentException($"The fixture folder does not exist: {folder}");
    }

    /// <summary>
    /// The response file (without extension) that answers a request, or null for a request fixture mode
    /// does not serve. Each file is <c>&lt;name&gt;.json</c>, with an optional <c>&lt;name&gt;.status</c> holding an HTTP
    /// status code.
    /// </summary>
    public static string? ResponseName(Uri url) => (url.Host, url.AbsolutePath) switch
    {
        ("api.anthropic.com", "/api/oauth/usage") => "usage",
        ("api.github.com", GitHubReleases.Route) => "releases",
        (_, "/api/v2/status.json") => "status",
        ("platform.claude.com", "/v1/oauth/token") => "token",
        ("auth.openai.com", "/oauth/token") => "codex-token",
        (_, "/backend-api/wham/usage" or "/api/codex/usage") => "codex-usage",
        ("platform.claude.com", "/api/organizations") => "console-organizations",
        ("platform.claude.com", var path) when ConsolePath().Match(path) is { Success: true } match => "console-" + match.Groups[1].Value switch
        {
            "current_spend" => "current-spend",
            "prepaid/credits" => "prepaid-credits",
            "workspaces/default/usage_cost" => "usage-cost",
            _ => "api-keys",
        },
        _ => null,
    };

    /// <summary>Answers HTTP requests from the response files.</summary>
    public HttpMessageHandler Handler() => new ResponsesHandler(this);

    /// <summary>claude.ai requests from <c>claude-ai-*.json</c>, in place of the per-profile browsers.</summary>
    public IClaudeAiTransport ClaudeAi() => new FixtureClaudeAi(this);

    /// <summary>A sign-in that succeeds at once, so the claude.ai and Console routes can be shown without a browser.</summary>
    public IBrowserSignIn SignIn() => new FixtureSignIn();

    /// <summary>
    /// A process guard that never reads a real process. While <c>claude-running</c> exists in the responses folder it sees
    /// one Claude Code process using the shared folder, so a switch waits as it would beside an open Claude Code.
    /// </summary>
    public CliProcessGuard ProcessGuard() =>
        new(() => File.Exists(Path.Combine(Responses, "claude-running"))
                ? [new ProcessEntry(1, 0, CliProcessGuard.ImageName, true, null, DateTime.UnixEpoch)]
                : [],
            () => new Dictionary<string, LauncherImage>());

    /// <summary>
    /// The Codex auto-start, in place of running the Codex CLI: <c>codex-auto-start.jsonl</c> holds the events it would
    /// print and <c>codex-auto-start.exit</c> its exit code (default 0). Without the events file the CLI counts as not found.
    /// </summary>
    public Func<string, CancellationToken, Task<CodexExecResult>> CodexExec() => (_, _) =>
    {
        var events = Path.Combine(Responses, "codex-auto-start.jsonl");
        if (!File.Exists(events))
        {
            return Task.FromResult(new CodexExecResult(CodexExecOutcome.NotFound, null));
        }

        var exitFile = Path.Combine(Responses, "codex-auto-start.exit");
        var exitCode = File.Exists(exitFile) && int.TryParse(File.ReadAllText(exitFile).Trim(), out var code) ? code : 0;
        return Task.FromResult(Automation.CodexExec.Judge(exitCode, File.ReadLines(events).Any(Automation.CodexExec.IsTurnCompleted)));
    };

    private (int Status, string? Body) Read(string name)
    {
        var path = Path.Combine(Responses, name + ".json");
        if (!File.Exists(path))
        {
            return (404, null);
        }

        var statusFile = Path.Combine(Responses, name + ".status");
        var status = File.Exists(statusFile) && int.TryParse(File.ReadAllText(statusFile).Trim(), out var code) ? code : 200;
        return (status, File.ReadAllText(path));
    }

    [GeneratedRegex("^/api/organizations/[^/]+/(current_spend|prepaid/credits|workspaces/default/usage_cost|api_keys)$")]
    private static partial Regex ConsolePath();

    private sealed class ResponsesHandler(FixtureMode fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = request.RequestUri is { } url && ResponseName(url) is { } name ? fixture.Read(name) : (404, null);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixtureClaudeAi(FixtureMode fixture) : IClaudeAiTransport
    {
        public long SessionEpoch(Guid profileId) => 0;

        public Task<BrowserResponse> SendAsync(Guid profileId, ClaudeAiOperation operation, string? organizationId, CancellationToken cancellation)
        {
            var (status, body) = fixture.Read(operation switch
            {
                ClaudeAiOperation.Organizations => "claude-ai-organizations",
                ClaudeAiOperation.Usage => "claude-ai-usage",
                ClaudeAiOperation.OverageSpendLimit => "claude-ai-overage-spend-limit",
                _ => "claude-ai-overage-credit-grant",
            });
            return Task.FromResult(new BrowserResponse(status, "application/json", false, body ?? "{}", null, null));
        }

        public Task<BrowserResponse> SendAutoStartAsync(
            Guid profileId, AutoStartStep step, string organizationId, Guid conversationId, CancellationToken cancellation)
        {
            var (status, body) = fixture.Read(step switch
            {
                AutoStartStep.CreateConversation => "claude-ai-auto-start-conversation",
                AutoStartStep.Completion => "claude-ai-auto-start-completion",
                _ => "claude-ai-auto-start-delete",
            });
            return Task.FromResult(new BrowserResponse(status, "application/json", false, body ?? "{}", null, null));
        }
    }

    private sealed class FixtureSignIn : IBrowserSignIn
    {
        public Task<bool> SignInToClaudeAiAsync(Guid profileId) => Task.FromResult(true);

        // A fixed placeholder, never a real credential; the fixture's Console responses ignore it.
        public Task<ConsoleSecret?> SignInToConsoleAsync(Guid profileId) =>
            Task.FromResult<ConsoleSecret?>(new ConsoleSecret("fixture-console-session", null));

        public Task<Func<Task>> UseSessionKeyAsync(Guid profileId, string sessionKey) =>
            Task.FromResult<Func<Task>>(() => Task.CompletedTask);
    }
}
