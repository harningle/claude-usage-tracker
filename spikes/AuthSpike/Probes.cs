using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace AuthSpike;

/// <summary>
/// One request's allowlisted outcome. Never holds cookie values, tokens, bodies or account data;
/// organisation IDs stay in memory only (see <see cref="Probes"/>).
/// </summary>
internal sealed record ProbeResult(
    string Route, string Endpoint, int? Status, string? ContentType, bool CfMitigated, bool ChallengeText,
    long Ms, string? Protocol, string Shape, string? Error);

internal static class Probes
{
    public static readonly string[] ReplayCookieNames = ["sessionKey", "cf_clearance", "__cf_bm", "anthropic-device-id"];

    // Upstream's hard-coded claude.ai user agent (ClaudeAPIService.swift), for comparison only.
    public const string UpstreamSafariUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";

    /// <summary>
    /// Same-origin fetch in the page. Returns only status, headers of interest, timing and shape;
    /// the organisation IDs come back separately so the host can build the usage path.
    /// </summary>
    public static async Task<(List<ProbeResult> Results, List<string> OrgIds)> BrowserAsync(CoreWebView2 core, string apiPrefix, bool usage)
    {
        const string script = """
            (async () => {
              async function probe(path) {
                const t0 = performance.now();
                try {
                  const r = await fetch(path, { credentials: 'include', headers: { 'Accept': 'application/json' }, signal: AbortSignal.timeout(30000) });
                  const text = await r.text();
                  let json = null; try { json = JSON.parse(text); } catch (e) {}
                  return { status: r.status, contentType: r.headers.get('content-type'), cfMitigated: r.headers.get('cf-mitigated') !== null,
                           challenge: text.slice(0, 1024).includes('Just a moment'), ms: Math.round(performance.now() - t0), json };
                } catch (e) { return { error: String(e), ms: Math.round(performance.now() - t0) }; }
              }
              function shape(json) {
                if (Array.isArray(json)) return 'array[' + json.length + ']';
                if (json && typeof json === 'object') {
                  const keys = Object.keys(json);
                  const fh = json.five_hour && typeof json.five_hour === 'object' ? ', five_hour.utilization ' + typeof json.five_hour.utilization : '';
                  return 'object{' + keys.length + ' keys' + fh + '}';
                }
                return json === null ? 'not json' : typeof json;
              }
              const out = { results: [], orgIds: [] };
              const orgs = await probe('__PREFIX__/organizations');
              out.results.push({ endpoint: 'organizations', ...orgs, shape: shape(orgs.json), json: undefined });
              if (Array.isArray(orgs.json)) out.orgIds = orgs.json.map(o => o && o.uuid).filter(u => typeof u === 'string');
              if (__USAGE__ && out.orgIds.length > 0) {
                const u = await probe('__PREFIX__/organizations/' + encodeURIComponent(out.orgIds[0]) + '/usage');
                out.results.push({ endpoint: 'usage', ...u, shape: shape(u.json), json: undefined });
              }
              return JSON.stringify(out);
            })()
            """;
        var output = await EvaluateAsync(core, script.Replace("__PREFIX__", apiPrefix).Replace("__USAGE__", usage ? "true" : "false"));
        var results = output["results"]!.AsArray().Select(r => new ProbeResult(
            "browser", (string)r!["endpoint"]!, (int?)r["status"], (string?)r["contentType"], (bool?)r["cfMitigated"] ?? false,
            (bool?)r["challenge"] ?? false, (long?)r["ms"] ?? -1, "browser", (string?)r["shape"] ?? "?", (string?)r["error"])).ToList();
        var orgIds = output["orgIds"]!.AsArray().Select(o => (string)o!).ToList();
        return (results, orgIds);
    }

    /// <summary>
    /// The signed-in claude.ai account's UUID from <c>/api/account</c>, fetched in the page. Kept in
    /// memory for comparison only. Null when the endpoint is unavailable or has no <c>uuid</c>.
    /// </summary>
    public static async Task<(int? Status, string? AccountId)> AccountIdAsync(CoreWebView2 core)
    {
        const string script = """
            (async () => {
              try {
                const r = await fetch('/api/account', { credentials: 'include', headers: { 'Accept': 'application/json' }, signal: AbortSignal.timeout(30000) });
                let id = null; try { const j = await r.json(); if (j && typeof j.uuid === 'string') id = j.uuid; } catch (e) {}
                return JSON.stringify({ status: r.status, id });
              } catch (e) { return JSON.stringify({ status: null, id: null }); }
            })()
            """;
        var output = await EvaluateAsync(core, script);
        return ((int?)output["status"], (string?)output["id"]);
    }

    private static async Task<JsonNode> EvaluateAsync(CoreWebView2 core, string expression)
    {
        var parameters = new JsonObject { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true };
        var reply = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters.ToJsonString()))!;
        var value = (string?)reply["result"]?["value"] ?? throw new InvalidOperationException("Runtime.evaluate returned no value (exception in page?)");
        return JsonNode.Parse(value)!;
    }

    /// <summary>Replays the request with HttpClient, a per-profile cookie container and chosen headers.</summary>
    public static async Task<List<ProbeResult>> ReplayAsync(string route, IEnumerable<CoreWebView2Cookie> cookies, string userAgent,
        string origin, string apiBase, string? orgId)
    {
        var container = new CookieContainer();
        foreach (var cookie in cookies)
        {
            container.Add(cookie.ToSystemNetCookie());
        }

        using var handler = new SocketsHttpHandler
        {
            CookieContainer = container,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var results = new List<ProbeResult> { await SendAsync(client, route, "organizations", $"{apiBase}/organizations", userAgent, origin) };
        if (orgId is not null)
        {
            results.Add(await SendAsync(client, route, "usage", $"{apiBase}/organizations/{Uri.EscapeDataString(orgId)}/usage", userAgent, origin));
        }

        return results;
    }

    private static async Task<ProbeResult> SendAsync(HttpClient client, string route, string endpoint, string url, string userAgent, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        // Upstream's claude.ai request headers, with the user agent under test.
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("Referer", origin);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();
            return new ProbeResult(route, endpoint, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                response.Headers.Contains("cf-mitigated"), body.AsSpan(0, Math.Min(body.Length, 1024)).Contains("Just a moment", StringComparison.Ordinal),
                stopwatch.ElapsedMilliseconds, $"HTTP/{response.Version}", Shape(body), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(route, endpoint, null, null, false, false, stopwatch.ElapsedMilliseconds, null, "none", e.GetType().Name);
        }
    }

    private static string Shape(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Array => $"array[{root.GetArrayLength()}]",
                JsonValueKind.Object => $"object{{{root.EnumerateObject().Count()} keys" +
                    (root.TryGetProperty("five_hour", out var fiveHour) && fiveHour.ValueKind == JsonValueKind.Object && fiveHour.TryGetProperty("utilization", out var u)
                        ? $", five_hour.utilization {(u.ValueKind == JsonValueKind.Number ? "number" : u.ValueKind.ToString().ToLowerInvariant())}"
                        : "") + "}",
                _ => root.ValueKind.ToString(),
            };
        }
        catch (JsonException)
        {
            return "not json";
        }
    }

    /// <summary>Cookie metadata only: name, domain, path, remaining lifetime and flags.</summary>
    public static string Describe(IEnumerable<CoreWebView2Cookie> cookies) => string.Join("; ", cookies.Select(c =>
        $"{c.Name} @{c.Domain}{c.Path} " +
        (c.IsSession ? "session" : $"expires in {(c.Expires.ToUniversalTime() - DateTime.UtcNow).TotalMinutes:0} min") +
        (c.IsHttpOnly ? " httponly" : "") + (c.IsSecure ? " secure" : "")));

    public static string ResultsPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageTracker.Spikes", "auth", "results.jsonl");
}
