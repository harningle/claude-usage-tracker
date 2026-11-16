using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTracker.Core.Updates;

/// <summary>A release of the port newer than the running build.</summary>
/// <param name="Tag">The release's tag, e.g. <c>v1.0.0-beta.3</c>.</param>
public sealed record AvailableRelease(string Tag, ReleaseVersion Version);

/// <summary>The port's releases on GitHub, as the update check reads them.</summary>
public static class GitHubReleases
{
    /// <summary>GitHub's releases list for the port's repository, newest first, drafts hidden from anonymous requests.</summary>
    public const string ApiUrl = "https://api.github.com/repos/harningle/claude-usage-tracker/releases";

    /// <summary>The request's route in the request log.</summary>
    public const string Route = "/repos/harningle/claude-usage-tracker/releases";

    private const string TagPage = "https://github.com/harningle/claude-usage-tracker/releases/tag/";

    /// <summary>
    /// The newest release newer than <paramref name="current"/>, or null when there is none. Drafts and tags that are not
    /// versions are skipped. A pre-release (a version with a <c>-</c> part, which is how the release workflow marks
    /// them) is offered only to a pre-release build, so a stable build is never offered a beta.
    /// </summary>
    /// <exception cref="JsonException">The body is not a list of releases.</exception>
    public static AvailableRelease? Newest(string json, ReleaseVersion current)
    {
        if (JsonNode.Parse(json) is not JsonArray releases)
        {
            throw new JsonException("The releases list is not a JSON array.");
        }

        AvailableRelease? newest = null;
        foreach (var release in releases.OfType<JsonObject>())
        {
            if (release["draft"] is JsonValue draft && draft.TryGetValue(out bool isDraft) && isDraft)
            {
                continue;
            }

            if (release["tag_name"] is JsonValue tagValue && tagValue.TryGetValue(out string? tag)
                && ReleaseVersion.Parse(tag) is { } version
                && (current.IsPreRelease || !version.IsPreRelease)
                && version.CompareTo(current) > 0
                && (newest is null || version.CompareTo(newest.Version) > 0))
            {
                newest = new AvailableRelease(tag, version);
            }
        }

        return newest;
    }

    /// <summary>A release's page on GitHub, or null for a tag that is not a version (so nothing else is ever opened).</summary>
    public static Uri? Page(string tag) => ReleaseVersion.Parse(tag) is null ? null : new Uri(TagPage + tag);
}
