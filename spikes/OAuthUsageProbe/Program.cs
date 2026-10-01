// Milestone 1 spike: one or more read-only GETs of /api/oauth/usage with the access token from a
// Claude Code config directory. It never refreshes (refreshing would rotate the CLI's refresh
// token), never writes the credential file, never calls /v1/messages and never prints tokens.
// Output is allowlisted metadata: status, timing, header names and the JSON shape.
// --shape true prints the whole body as field names and value types only (numbers as integer or
// decimal, strings as a kind such as "timestamp"), never values; ID-like keys are masked.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

var options = args.Chunk(2).ToDictionary(p => p[0].TrimStart('-'), p => p.ElementAtOrDefault(1) ?? "");
var configDirectory = options.GetValueOrDefault("config-dir",
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
var repeat = int.Parse(options.GetValueOrDefault("repeat", "1"), CultureInfo.InvariantCulture);
var interval = TimeSpan.FromSeconds(int.Parse(options.GetValueOrDefault("interval-seconds", "30"), CultureInfo.InvariantCulture));
// Upstream's CLI OAuth user agent (ClaudeAPIService.buildAuthenticatedRequest).
var userAgent = options.GetValueOrDefault("user-agent", "claude-code/2.1.5");
var shapeOnly = options.GetValueOrDefault("shape", "false") == "true";

var credentialPath = Path.Combine(configDirectory, ".credentials.json");
string accessToken;
using (var stream = new FileStream(credentialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
using (var credentials = JsonDocument.Parse(stream))
{
    if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
    {
        Console.WriteLine("No claudeAiOauth object in .credentials.json; nothing to test.");
        return 3;
    }

    accessToken = oauth.TryGetProperty("accessToken", out var token) && token.ValueKind == JsonValueKind.String
        ? token.GetString()!
        : throw new InvalidDataException("claudeAiOauth.accessToken is missing.");
    var expiresAt = oauth.TryGetProperty("expiresAt", out var expiry) && expiry.TryGetInt64(out var ms)
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
        : (DateTimeOffset?)null;
    Console.WriteLine($"Credential metadata ({configDirectory}):");
    Console.WriteLine($"  subscriptionType: {Text(oauth, "subscriptionType")}, rateLimitTier: {Text(oauth, "rateLimitTier")}");
    Console.WriteLine($"  scopes: {(oauth.TryGetProperty("scopes", out var scopes) ? string.Join(' ', scopes.EnumerateArray().Select(s => s.GetString())) : "(none)")}");
    Console.WriteLine($"  access token expires: {(expiresAt is { } e ? $"{e:u} ({(e - DateTimeOffset.UtcNow).TotalMinutes:0} min from now)" : "unknown")}");
    if (expiresAt is null || expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
    {
        Console.WriteLine("Access token expired or expiring; stopping. This probe never refreshes a CLI-owned token.");
        return 3;
    }
}

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var statuses = new List<int>();
for (var i = 1; i <= repeat; i++)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
    request.Headers.Accept.ParseAdd("application/json");

    var stopwatch = Stopwatch.StartNew();
    using var response = await client.SendAsync(request);
    var body = await response.Content.ReadAsByteArrayAsync();
    stopwatch.Stop();
    statuses.Add((int)response.StatusCode);

    Console.WriteLine();
    Console.WriteLine($"[{i}/{repeat}] {DateTimeOffset.UtcNow:u} GET /api/oauth/usage -> {(int)response.StatusCode} {response.ReasonPhrase}, " +
                      $"{response.Content.Headers.ContentType?.MediaType ?? "(no content type)"}, {body.Length} bytes, {stopwatch.ElapsedMilliseconds} ms");
    var interesting = response.Headers.Select(h => h.Key)
        .Where(k => k.StartsWith("anthropic-ratelimit", StringComparison.OrdinalIgnoreCase)
                    || k is "retry-after" or "cf-mitigated" or "x-should-retry" or "request-id" or "server")
        .Order();
    Console.WriteLine($"  headers present: {string.Join(", ", interesting)}");
    if (response.Headers.RetryAfter is { } retryAfter)
    {
        Console.WriteLine($"  retry-after: {retryAfter}");
    }

    if (shapeOnly)
    {
        DescribeShape(body);
    }
    else
    {
        DescribeBody(body);
    }
    if (i < repeat)
    {
        await Task.Delay(interval);
    }
}

Console.WriteLine();
Console.WriteLine($"Statuses: {string.Join(' ', statuses)}");
return statuses.All(s => s == 200) ? 0 : 1;

static string Text(JsonElement obj, string name) =>
    obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "(absent)";

// Shape only: key names, value kinds and utilisation numbers; error type/message for failures.
// Names and types only: no value of any field is printed.
static void DescribeShape(byte[] body)
{
    using var document = JsonDocument.Parse(body);
    Walk(document.RootElement, "  ", "(root)");

    static void Walk(JsonElement element, string indent, string name)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Console.WriteLine($"{indent}{name}: object");
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, indent + "  ", MaskKey(property.Name));
                }

                break;
            case JsonValueKind.Array:
                Console.WriteLine($"{indent}{name}: array[{element.GetArrayLength()}]");
                // Every distinct element shape once, so entries with different fields all appear.
                var seen = new HashSet<string>();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var signature = Signature(item);
                    if (seen.Add(signature))
                    {
                        Walk(item, indent + "  ", $"[{index}]");
                    }

                    index++;
                }

                break;
            default:
                Console.WriteLine($"{indent}{name}: {Kind(element)}");
                break;
        }
    }

    static string Signature(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Select(p => p.Name + ":" + Signature(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Signature).Distinct()) + "]",
        _ => Kind(element),
    };

    static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out _) ? "number (integer)" : "number (decimal)",
        JsonValueKind.String => StringKind(element.GetString()!),
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => element.ValueKind.ToString(),
    };

    static string StringKind(string text) =>
        text.Length == 0 ? "string (empty)"
        : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && text.Contains('T') ? "string (timestamp)"
        : text.Length == 3 && text.All(char.IsAsciiLetterUpper) ? "string (three capital letters)"
        : Guid.TryParse(text, out _) ? "string (UUID)"
        : "string";

    static string MaskKey(string key) =>
        Guid.TryParse(key, out _) || (key.Length >= 16 && key.All(char.IsAsciiHexDigit)) ? "<id-like key>" : key;
}

static void DescribeBody(byte[] body)
{
    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(body);
    }
    catch (JsonException)
    {
        var prefix = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512));
        Console.WriteLine($"  body is not JSON; Cloudflare challenge text: {prefix.Contains("Just a moment", StringComparison.Ordinal)}");
        return;
    }

    using (document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            Console.WriteLine($"  body is JSON {root.ValueKind}");
            return;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"  error.type: {Text(error, "type")}; error.message: {Text(error, "message")}");
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            var detail = value.ValueKind switch
            {
                JsonValueKind.Object when value.TryGetProperty("utilization", out var u) =>
                    $"utilization {u.ValueKind}{(u.ValueKind == JsonValueKind.Number ? $" = {u.GetDouble()}" : "")}, resets_at {(value.TryGetProperty("resets_at", out var r) ? r.ValueKind.ToString() : "absent")}",
                JsonValueKind.Object => "object {" + string.Join(", ", value.EnumerateObject().Select(p => $"{p.Name}: {p.Value.ValueKind}")) + "}",
                JsonValueKind.Array => $"array[{value.GetArrayLength()}]" + (value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.Object
                    ? " of {" + string.Join(", ", value[0].EnumerateObject().Select(p => p.Name)) + "}"
                    : ""),
                _ => value.ValueKind.ToString(),
            };
            Console.WriteLine($"  {property.Name}: {detail}");
        }
    }
}
