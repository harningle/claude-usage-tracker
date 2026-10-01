using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests;

/// <summary>
/// A <see cref="CliLinkService"/> over synthetic files: an encrypted secret store in a temporary folder, a
/// scripted token endpoint, a process table the test controls and an inventory of the default folder plus
/// any folders the test adds. No real network, process or Claude Code login is involved.
/// </summary>
internal sealed class LinkHarness
{
    public LinkHarness(string root, string userProfile, TimeProvider time, ScriptedHandler? tokens = null)
    {
        Secrets = new ProfileSecretStore(new SecretStore(Path.Combine(root, "secrets")));
        TokenHandler = tokens ?? new ScriptedHandler((_, _) => throw new InvalidOperationException("No token request was expected."));
        Log = new RequestLog(Path.Combine(root, "diagnostics"));
        Tokens = new OAuthTokenClient(new HttpClient(TokenHandler), time, Log);
        Guard = new CliProcessGuard(() => [.. Processes], () => Launchers);
        Service = new CliLinkService(Secrets, time, userProfile, Lock, Tokens, Guard, () => CliStore.Inventory(userProfile, null, Known));
    }

    public ProfileSecretStore Secrets { get; }

    public CredentialLock Lock { get; } = new();

    public RequestLog Log { get; }

    /// <summary>The process table the guard sees.</summary>
    public List<ProcessEntry> Processes { get; } = [];

    /// <summary>App-owned launcher images and the config directory each serves.</summary>
    public Dictionary<string, string> Launchers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Claude Code folders besides the default that the inventory knows.</summary>
    public List<string> Known { get; } = [];

    public ScriptedHandler TokenHandler { get; }

    public OAuthTokenClient Tokens { get; }

    public CliProcessGuard Guard { get; }

    public CliLinkService Service { get; }

    public int TokenRequests => TokenHandler.Requests.Count;

    /// <summary>A Claude Code process started from a terminal: it may use any folder.</summary>
    public void ClaudeRunning() =>
        Processes.Add(new ProcessEntry(4242, 100, CliProcessGuard.ImageName, true, @"C:\Users\user\.local\bin\claude.exe", DateTime.UtcNow));

    /// <summary>A token endpoint that answers each grant with the next response.</summary>
    public static ScriptedHandler TokenEndpoint(params Func<HttpResponseMessage>[] responses)
    {
        var index = 0;
        return new ScriptedHandler((_, _) => Task.FromResult(responses[Math.Min(Interlocked.Increment(ref index) - 1, responses.Length - 1)]()));
    }

    public static HttpResponseMessage Grant(string access, string? refresh, int expiresIn = 28800) =>
        Json(HttpStatusCode.OK,
            refresh is null
                ? $$"""{"access_token":"{{access}}","expires_in":{{expiresIn}},"token_type":"Bearer"}"""
                : $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""");

    public static HttpResponseMessage InvalidGrant() =>
        Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Refresh token revoked"}""");

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A Claude Code <c>.credentials.json</c> with a synthetic login and an MCP login that must never be copied.</summary>
    public static string Credentials(string access, string refresh, DateTimeOffset expires) =>
        $$"""{"claudeAiOauth":{"accessToken":"{{access}}","refreshToken":"{{refresh}}","expiresAt":{{expires.ToUnixTimeMilliseconds()}},"scopes":["user:inference","user:profile"],"subscriptionType":"team"},""" +
        "\"mcpOAuth\":{\"server\":{\"accessToken\":\"mcp-secret\"}}}";

    public static string Account(string uuid, string email) =>
        $$$"""{"numStartups":1,"oauthAccount":{"accountUuid":"{{{uuid}}}","emailAddress":"{{{email}}}","organizationName":"ExampleOrg"}}""";
}
