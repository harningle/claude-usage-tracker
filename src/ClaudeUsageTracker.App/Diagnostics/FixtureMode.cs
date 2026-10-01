using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace ClaudeUsageTracker.App.Diagnostics;

/// <summary>
/// Development fixture mode, <c>--fixture &lt;folder&gt;</c>: the app uses <c>&lt;folder&gt;\home</c> as the user
/// profile (so Claude Code files come from a synthetic <c>home\.claude</c>), <c>&lt;folder&gt;\data</c> as its data
/// root, and answers requests from <c>&lt;folder&gt;\responses</c> without any network access. Everything it
/// shows is labelled as fixture data, so screenshots never pass for real account data (plan.md,
/// Milestone 2 acceptance).
/// </summary>
public sealed class FixtureMode
{
    private FixtureMode(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string UserProfile => Path.Combine(Root, "home");
    public string DataRoot => Path.Combine(Root, "data");
    public string Responses => Path.Combine(Root, "responses");

    public static FixtureMode? FromArguments(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf("--fixture");
        return index >= 0 && index + 1 < args.Count ? new FixtureMode(args[index + 1]) : null;
    }

    /// <summary>Serves <c>usage.json</c> and <c>status.json</c> (optional <c>usage.status</c> holds an HTTP status code).</summary>
    public HttpMessageHandler Handler() => new ResponsesHandler(Responses);

    private sealed class ResponsesHandler(string folder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri?.AbsolutePath switch
            {
                "/api/oauth/usage" => "usage",
                "/api/v2/status.json" => "status",
                _ => null,
            };
            if (name is null || !File.Exists(Path.Combine(folder, name + ".json")))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }

            var statusFile = Path.Combine(folder, name + ".status");
            var status = File.Exists(statusFile) && int.TryParse(File.ReadAllText(statusFile).Trim(), out var code) ? code : 200;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(File.ReadAllText(Path.Combine(folder, name + ".json")), Encoding.UTF8, "application/json"),
            });
        }
    }
}
