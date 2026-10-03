using ClaudeUsageTracker.App.Setup;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;

namespace ClaudeUsageTracker.Tests;

/// <summary>A claude.ai transport that answers from a script, in place of a profile's WebView2 browser.</summary>
internal sealed class ScriptedTransport(Func<Guid, ClaudeAiOperation, string?, BrowserResponse> respond) : IClaudeAiTransport
{
    private long _epoch;

    public List<(Guid ProfileId, ClaudeAiOperation Operation, string? OrganizationId)> Requests { get; } = [];

    public long SessionEpoch(Guid profileId) => Interlocked.Read(ref _epoch);

    /// <summary>As after a sign-in: the session changed.</summary>
    public void SessionChanged() => Interlocked.Increment(ref _epoch);

    public Task<BrowserResponse> SendAsync(Guid profileId, ClaudeAiOperation operation, string? organizationId, CancellationToken cancellation)
    {
        lock (Requests)
        {
            Requests.Add((profileId, operation, organizationId));
        }

        return Task.FromResult(respond(profileId, operation, organizationId));
    }

    /// <summary>Answers the auto-start requests; none is expected unless a test sets it.</summary>
    public Func<AutoStartStep, Guid, BrowserResponse> AutoStart { get; set; } =
        (_, _) => throw new InvalidOperationException("No auto-start request was expected.");

    public List<(AutoStartStep Step, Guid ConversationId)> AutoStartRequests { get; } = [];

    public Task<BrowserResponse> SendAutoStartAsync(
        Guid profileId, AutoStartStep step, string organizationId, Guid conversationId, CancellationToken cancellation)
    {
        lock (AutoStartRequests)
        {
            AutoStartRequests.Add((step, conversationId));
        }

        return Task.FromResult(AutoStart(step, conversationId));
    }

    public static BrowserResponse Ok(string body) => new(200, "application/json", false, body, null, null);

    public static BrowserResponse Status(int status, string body = "{}") => new(status, "application/json", false, body, null, null);

    /// <summary>A signed-in session that sees one organisation with some usage.</summary>
    public static BrowserResponse SignedIn(ClaudeAiOperation operation) => operation switch
    {
        ClaudeAiOperation.Organizations => Ok("""[{"uuid":"org-1","name":"Example Team"},{"uuid":"org-2","name":"Other"}]"""),
        ClaudeAiOperation.Usage => Ok("""{"five_hour":{"utilization":21,"resets_at":"2026-10-01T15:00:00Z"},"seven_day":{"utilization":40}}"""),
        _ => Status(404),
    };
}

/// <summary>Synthetic Anthropic Console answers, shaped as in the Console billing tests.</summary>
internal static class ConsoleEndpoint
{
    public const string Costs = """
        {"costs":{"2026-10-01":[{"key_id":"key-cli","model_name":"Claude Sonnet 5.5 Usage","total":120.5},{"key_id":"key-api","model_name":"Claude Haiku 4.5 Usage","total":30}],
                  "2026-10-02":[{"key_id":"key-cli","model_name":"Claude Sonnet 5.5 Usage","total":79.5}]}}
        """;

    /// <summary>The answer to one Console request, or null for a request the Console does not serve.</summary>
    public static (System.Net.HttpStatusCode Status, string Body)? Answer(Uri url) => url.AbsolutePath switch
    {
        "/api/organizations" => (System.Net.HttpStatusCode.OK, """[{"uuid":"org-api","name":"API Org"}]"""),
        var p when p.EndsWith("/current_spend", StringComparison.Ordinal) =>
            (System.Net.HttpStatusCode.OK, """{"amount":1234,"resets_at":"2026-11-01T00:00:00.000Z"}"""),
        var p when p.EndsWith("/prepaid/credits", StringComparison.Ordinal) => (System.Net.HttpStatusCode.OK, """{"amount":8766,"currency":"USD"}"""),
        var p when p.EndsWith("/usage_cost", StringComparison.Ordinal) => (System.Net.HttpStatusCode.OK, Costs),
        var p when p.EndsWith("/api_keys", StringComparison.Ordinal) =>
            (System.Net.HttpStatusCode.OK, """{"data":[{"id":"key-cli","name":"CLI key"},{"id":"key-api","name":"Server"}]}"""),
        _ => null,
    };
}

/// <summary>A browser sign-in that the test controls, in place of the sign-in windows.</summary>
internal sealed class FakeSignIn : IBrowserSignIn
{
    public bool ClaudeAiResult { get; set; } = true;

    public ConsoleSecret? ConsoleResult { get; set; } = new("sk-ant-sid01-synthetic-console", null);

    public List<string> PastedKeys { get; } = [];

    public int Undone { get; private set; }

    public Task<bool> SignInToClaudeAiAsync(Guid profileId) => Task.FromResult(ClaudeAiResult);

    public Task<ConsoleSecret?> SignInToConsoleAsync(Guid profileId) => Task.FromResult(ConsoleResult);

    public Task<Func<Task>> UseSessionKeyAsync(Guid profileId, string sessionKey)
    {
        PastedKeys.Add(sessionKey);
        return Task.FromResult<Func<Task>>(() =>
        {
            Undone++;
            return Task.CompletedTask;
        });
    }
}
