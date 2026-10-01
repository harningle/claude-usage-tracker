using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Status;

namespace ClaudeUsageTracker.Tests.Refresh;

public class RefreshPolicyTests
{
    private static readonly TimeSpan Thirty = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SuccessUsesTheInterval() => Assert.Equal(Thirty, RefreshPolicy.NextDelay(Thirty, 0, null, null));

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(5, 900)]
    [InlineData(40, 900)]
    public void FailuresBackOffExponentiallyToACap(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            RefreshPolicy.NextDelay(Thirty, failures, FetchFailure.Network, null));

    [Theory]
    [InlineData(1, 600)]
    [InlineData(10, 900)]
    public void LongIntervalsBackOffToTheSameCap(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            RefreshPolicy.NextDelay(TimeSpan.FromSeconds(300), failures, FetchFailure.ServerError, null));

    [Fact]
    public void RateLimitHonoursRetryAfter() =>
        Assert.Equal(TimeSpan.FromMinutes(20),
            RefreshPolicy.NextDelay(Thirty, 1, FetchFailure.RateLimited, TimeSpan.FromMinutes(20)));

    [Fact]
    public void ServerErrorHonoursRetryAfter() =>
        Assert.Equal(TimeSpan.FromMinutes(10),
            RefreshPolicy.NextDelay(Thirty, 1, FetchFailure.ServerError, TimeSpan.FromMinutes(10)));

    [Fact]
    public void ServerErrorWithoutGuidanceBacksOff() =>
        Assert.Equal(TimeSpan.FromSeconds(60), RefreshPolicy.NextDelay(Thirty, 1, FetchFailure.ServerError, null));

    [Fact]
    public void RateLimitWithoutGuidanceWaitsFiveMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(5), RefreshPolicy.NextDelay(Thirty, 1, FetchFailure.RateLimited, null));

    [Fact]
    public void ShortRetryAfterNeverBeatsTheBackoff() =>
        Assert.Equal(TimeSpan.FromSeconds(120),
            RefreshPolicy.NextDelay(Thirty, 2, FetchFailure.RateLimited, TimeSpan.FromSeconds(1)));

    [Fact]
    public void ExcessiveRetryAfterIsCapped() =>
        Assert.Equal(RefreshPolicy.MaximumRetryAfter,
            RefreshPolicy.NextDelay(Thirty, 1, FetchFailure.RateLimited, TimeSpan.FromDays(30)));

    [Theory]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(45, 45)]
    [InlineData(301, 300)]
    public void IntervalIsClampedToTheSettingsRange(int seconds, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), RefreshPolicy.ClampInterval(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("120", 120)]
    [InlineData(" 0 ", 0)]
    [InlineData("Thu, 01 Oct 2026 12:10:00 GMT", 600)]
    [InlineData("Thu, 01 Oct 2026 11:00:00 GMT", 0)]
    public void RetryAfterParses(string value, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RefreshPolicy.ParseRetryAfter(value, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void UnusableRetryAfterIsNull(string? value) => Assert.Null(RefreshPolicy.ParseRetryAfter(value, Now));

    [Fact]
    public void HugeRetryAfterIsCapped() =>
        Assert.Equal(RefreshPolicy.MaximumRetryAfter, RefreshPolicy.ParseRetryAfter("99999999", Now));
}

/// <summary>Characterises upstream <c>ClaudeStatusService</c>'s decoding, which upstream did not test.</summary>
public class ServiceStatusTests
{
    [Theory]
    [InlineData("none", ServiceStatusIndicator.None)]
    [InlineData("minor", ServiceStatusIndicator.Minor)]
    [InlineData("major", ServiceStatusIndicator.Major)]
    [InlineData("critical", ServiceStatusIndicator.Critical)]
    [InlineData("maintenance", ServiceStatusIndicator.Unknown)]
    public void IndicatorsMap(string indicator, ServiceStatusIndicator expected)
    {
        var status = ServiceStatus.Parse(
            """{"page": {"id": "x"}, "status": {"indicator": """ + $"\"{indicator}\"" +
            """, "description": "All Systems Operational"}}""");

        Assert.Equal(new ServiceStatus(expected, "All Systems Operational"), status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"status": {"indicator": "none"}}""")]
    [InlineData("""{"status": {"description": "x"}}""")]
    [InlineData("<html></html>")]
    public void IncompleteBodiesAreNull(string json) => Assert.Null(ServiceStatus.Parse(json));
}
