namespace ClaudeUsageTracker.App.Usage;

/// <summary>
/// The wait after a failed login renewal, per profile. A renewal the login service answered with a failure (a rate
/// limit, a server error, a refused request) doubles the wait each time, from <see cref="FirstDelay"/> up to
/// <see cref="LongestDelay"/>, so a service that keeps refusing is not asked every few minutes. A renewal that got no
/// answer (offline, a timeout) reached nobody: it waits <see cref="FirstDelay"/> and lengthens nothing. A success
/// starts over. Callers synchronise access.
/// </summary>
internal sealed class RenewalBackoff(TimeProvider time)
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan LongestDelay = TimeSpan.FromHours(1);

    private readonly Dictionary<Guid, (DateTimeOffset NotBefore, int Refusals)> _holds = [];

    /// <summary>Whether the profile's last failed renewal is too recent for another attempt.</summary>
    public bool Holds(Guid profileId) => _holds.TryGetValue(profileId, out var hold) && time.GetUtcNow() < hold.NotBefore;

    /// <param name="answered">The login service answered; false when no response arrived.</param>
    public void Failed(Guid profileId, bool answered)
    {
        var refusals = _holds.GetValueOrDefault(profileId).Refusals + (answered ? 1 : 0);
        _holds[profileId] = (time.GetUtcNow() + (answered ? DelayAfter(refusals) : FirstDelay), refusals);
    }

    public void Clear(Guid profileId) => _holds.Remove(profileId);

    /// <summary>5, 10, 20 and 40 minutes, then an hour.</summary>
    internal static TimeSpan DelayAfter(int refusals) =>
        TimeSpan.FromTicks(Math.Min(FirstDelay.Ticks << Math.Clamp(refusals - 1, 0, 8), LongestDelay.Ticks));
}
