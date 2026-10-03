using ClaudeUsageTracker.App.Usage;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>
/// The wait after failed login renewals. On 2026-10-03 the login service refused 46 renewals in a row, one every
/// five minutes; refusals now lengthen the wait, while failures that reached nobody do not.
/// </summary>
public sealed class RenewalBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Guid _profile = Guid.NewGuid();

    /// <summary>Fails once more and returns how long the profile is then held.</summary>
    private TimeSpan WaitAfterFailure(RenewalBackoff backoff, bool answered)
    {
        backoff.Failed(_profile, answered);
        var started = _time.Now;
        while (backoff.Holds(_profile))
        {
            _time.Now += TimeSpan.FromMinutes(1);
        }

        return _time.Now - started;
    }

    [Fact]
    public void EachRefusalDoublesTheWaitUpToAnHour()
    {
        var backoff = new RenewalBackoff(_time);

        var waits = Enumerable.Range(0, 7).Select(_ => WaitAfterFailure(backoff, answered: true).TotalMinutes);

        Assert.Equal([5, 10, 20, 40, 60, 60, 60], waits);
    }

    [Fact]
    public void AFailureWithoutAnAnswerWaitsFiveMinutesAndLengthensNothing()
    {
        var backoff = new RenewalBackoff(_time);

        Assert.Equal(5, WaitAfterFailure(backoff, answered: false).TotalMinutes);
        Assert.Equal(5, WaitAfterFailure(backoff, answered: false).TotalMinutes);
        Assert.Equal(5, WaitAfterFailure(backoff, answered: true).TotalMinutes);
        Assert.Equal(10, WaitAfterFailure(backoff, answered: true).TotalMinutes);
        // Offline for a moment: the short wait, and the refusals so far are kept.
        Assert.Equal(5, WaitAfterFailure(backoff, answered: false).TotalMinutes);
        Assert.Equal(20, WaitAfterFailure(backoff, answered: true).TotalMinutes);
    }

    [Fact]
    public void ASuccessStartsOverAndProfilesAreIndependent()
    {
        var backoff = new RenewalBackoff(_time);
        var other = Guid.NewGuid();
        WaitAfterFailure(backoff, answered: true);
        backoff.Failed(_profile, answered: true);
        Assert.True(backoff.Holds(_profile));
        Assert.False(backoff.Holds(other));

        backoff.Clear(_profile);

        Assert.False(backoff.Holds(_profile));
        Assert.Equal(5, WaitAfterFailure(backoff, answered: true).TotalMinutes);
    }
}
