using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Json;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Core.Notifications;

/// <summary>
/// The decisions of upstream <c>AutoStartSessionService</c>, the only feature that sends a model request. It runs only
/// for profiles where the user switched it on, never as part of usage polling (plan.md section 2).
/// </summary>
public static class SessionAutoStart
{
    /// <summary>Upstream's prompt and model: one short message to a small model, so it costs little.</summary>
    public const string Prompt = "Hi";

    public const string Model = "claude-haiku-4-5-20251001";

    /// <summary>Upstream checks every five minutes.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>After a start whose response gave no reset time, no further start for one session length.</summary>
    public static readonly TimeSpan SessionLength = TimeSpan.FromHours(5);

    /// <summary>
    /// After a failed attempt, the next one waits this long. Upstream tried again at every five-minute check and posted
    /// a failure notification each time; a failed attempt may still have reached the model.
    /// </summary>
    public static readonly TimeSpan FailureHold = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether a session should be started now: the session window is reported at 0 % and no window is open (no reset
    /// time, or one in the past). Upstream gated on the same open-window signal so that an open but unused window,
    /// which also reads 0 %, is not started again. Unlike upstream, a session percentage that was not reported at all
    /// starts nothing: missing is not 0 %.
    /// </summary>
    /// <param name="usage">A fresh observation; cached or stale values must never decide this.</param>
    public static bool ShouldStart(UsageData usage, DateTimeOffset now) =>
        usage.SessionPercentage == 0 && (usage.SessionResetTime is not { } reset || reset <= now);

    /// <summary>How far a Codex reset time may be from one session length after its observation and still count as unstarted.</summary>
    public static readonly TimeSpan CodexIdleTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether a Codex session should be started now. Codex reports an unstarted 5-hour window at 0 % with a reset time one
    /// session length after the request, which moves on with every read; the first request fixes it at one session length
    /// after that request (seen on 2026-10-06). So a window at 0 % counts as unstarted when its reset time is missing, past,
    /// or within <see cref="CodexIdleTolerance"/> of one session length after <paramref name="observedAt"/>. Not an upstream
    /// feature: its Codex provider cannot auto-start.
    /// </summary>
    /// <param name="usage">A fresh observation; cached or stale values must never decide this.</param>
    /// <param name="observedAt">When <paramref name="usage"/> was fetched.</param>
    public static bool ShouldStartCodex(UsageData usage, DateTimeOffset observedAt) =>
        usage.SessionPercentage == 0
        && (usage.SessionResetTime is not { } reset || reset <= observedAt
            || (reset - (observedAt + SessionLength)).Duration() <= CodexIdleTolerance);

    /// <summary>
    /// Upstream <c>parseCompletionResponseForResetTime</c>: the reset time of the new session window, from the last
    /// server-sent event of the completion whose <c>messageLimit.windows["5h"].resets_at</c> is a number of seconds
    /// since the Unix epoch. Null when no event carries one.
    /// </summary>
    public static DateTimeOffset? ResetTimeFromCompletion(string body)
    {
        foreach (var line in body.Split('\n').Reverse())
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            JsonObject? data;
            try
            {
                data = JsonNode.Parse(trimmed[6..]) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (data?["messageLimit"]?["windows"]?["5h"] is JsonObject window
                && JsonRead.Double(window, "resets_at") is { } seconds && double.IsFinite(seconds) && seconds > 0
                && seconds < DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            }
        }

        return null;
    }
}
