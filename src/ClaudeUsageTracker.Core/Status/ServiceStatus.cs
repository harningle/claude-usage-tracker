using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTracker.Core.Status;

/// <summary>Statuspage indicator. Port of upstream <c>ClaudeStatus.StatusIndicator</c>.</summary>
public enum ServiceStatusIndicator
{
    None,
    Minor,
    Major,
    Critical,
    Unknown,
}

/// <param name="Description">The status page's own text, shown verbatim as upstream does.</param>
public sealed record ServiceStatus(ServiceStatusIndicator Indicator, string Description)
{
    public const string ClaudeStatusUrl = "https://status.claude.com/api/v2/status.json";
    public const string ClaudeStatusPage = "https://status.claude.com";

    /// <summary>
    /// Parses a Statuspage <c>status.json</c> body (<c>{"status": {"indicator", "description"}}</c>);
    /// null when either field is missing. An unrecognised indicator is <see cref="ServiceStatusIndicator.Unknown"/>.
    /// </summary>
    public static ServiceStatus? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject root
                && root["status"] is JsonObject status
                && status["indicator"] is JsonValue indicatorValue && indicatorValue.TryGetValue(out string? indicator)
                && status["description"] is JsonValue descriptionValue
                && descriptionValue.TryGetValue(out string? description))
            {
                var parsed = indicator switch
                {
                    "none" => ServiceStatusIndicator.None,
                    "minor" => ServiceStatusIndicator.Minor,
                    "major" => ServiceStatusIndicator.Major,
                    "critical" => ServiceStatusIndicator.Critical,
                    _ => ServiceStatusIndicator.Unknown,
                };
                return new ServiceStatus(parsed, description);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
