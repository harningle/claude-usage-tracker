using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Usage;

/// <summary>Port of upstream <c>UsageStatusCalculatorTests</c>, plus pace cases characterising upstream source.</summary>
public class UsageStatusCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // Used-based thresholds (showRemaining = false)

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(69)]
    public void UsedBasedThresholds_Safe(double used) =>
        Assert.Equal(UsageStatusLevel.Safe, UsageStatusCalculator.CalculateStatus(used, showRemaining: false));

    [Theory]
    [InlineData(70)]
    [InlineData(85)]
    [InlineData(89)]
    public void UsedBasedThresholds_Moderate(double used) =>
        Assert.Equal(UsageStatusLevel.Moderate, UsageStatusCalculator.CalculateStatus(used, showRemaining: false));

    [Theory]
    [InlineData(90)]
    [InlineData(95)]
    [InlineData(100)]
    public void UsedBasedThresholds_Critical(double used) =>
        Assert.Equal(UsageStatusLevel.Critical, UsageStatusCalculator.CalculateStatus(used, showRemaining: false));

    // Remaining-based thresholds (showRemaining = true)

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(70)]
    public void RemainingBasedThresholds_Safe(double used) =>
        Assert.Equal(UsageStatusLevel.Safe, UsageStatusCalculator.CalculateStatus(used, showRemaining: true));

    [Theory]
    [InlineData(71)]
    [InlineData(85)]
    [InlineData(90)]
    public void RemainingBasedThresholds_Moderate(double used) =>
        Assert.Equal(UsageStatusLevel.Moderate, UsageStatusCalculator.CalculateStatus(used, showRemaining: true));

    [Theory]
    [InlineData(91)]
    [InlineData(95)]
    [InlineData(100)]
    public void RemainingBasedThresholds_Critical(double used) =>
        Assert.Equal(UsageStatusLevel.Critical, UsageStatusCalculator.CalculateStatus(used, showRemaining: true));

    // Display percentage

    [Theory]
    [InlineData(65, 65)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void GetDisplayPercentage_UsedMode(double used, double expected) =>
        Assert.Equal(expected, UsageStatusCalculator.GetDisplayPercentage(used, showRemaining: false));

    [Theory]
    [InlineData(65, 35)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void GetDisplayPercentage_RemainingMode(double used, double expected) =>
        Assert.Equal(expected, UsageStatusCalculator.GetDisplayPercentage(used, showRemaining: true));

    // Edge cases

    [Theory]
    [InlineData(69.9, UsageStatusLevel.Safe)]
    [InlineData(70.0, UsageStatusLevel.Moderate)]
    [InlineData(89.9, UsageStatusLevel.Moderate)]
    [InlineData(90.0, UsageStatusLevel.Critical)]
    public void BoundaryConditions_UsedMode(double used, UsageStatusLevel expected) =>
        Assert.Equal(expected, UsageStatusCalculator.CalculateStatus(used, showRemaining: false));

    [Theory]
    [InlineData(69, UsageStatusLevel.Safe)]
    [InlineData(70, UsageStatusLevel.Safe)]
    [InlineData(71, UsageStatusLevel.Moderate)]
    [InlineData(90, UsageStatusLevel.Moderate)]
    [InlineData(91, UsageStatusLevel.Critical)]
    public void BoundaryConditions_RemainingMode(double used, UsageStatusLevel expected) =>
        Assert.Equal(expected, UsageStatusCalculator.CalculateStatus(used, showRemaining: true));

    [Fact]
    public void NegativePercentage() =>
        Assert.Equal(110.0, UsageStatusCalculator.GetDisplayPercentage(-10, showRemaining: true));

    [Fact]
    public void OverOneHundredPercentage() =>
        Assert.Equal(0.0, UsageStatusCalculator.GetDisplayPercentage(110, showRemaining: true));

    // Pace-aware colouring (no upstream test; characterises upstream source)

    [Theory]
    [InlineData(30, 0.5, false, UsageStatusLevel.Safe)] // projects 0.60
    [InlineData(35, 0.5, false, UsageStatusLevel.Moderate)] // projects exactly 0.70
    [InlineData(40, 0.5, true, UsageStatusLevel.Moderate)] // pace ignores the display mode
    [InlineData(45, 0.5, false, UsageStatusLevel.Critical)] // projects exactly 0.90
    [InlineData(12, 0.15, false, UsageStatusLevel.Moderate)] // pace applies from 15% elapsed
    [InlineData(80, 0.14, false, UsageStatusLevel.Moderate)] // too early: plain thresholds
    [InlineData(70, 1.0, true, UsageStatusLevel.Safe)] // period over: plain thresholds
    public void PaceAwareStatus(double used, double elapsed, bool showRemaining, UsageStatusLevel expected) =>
        Assert.Equal(expected, UsageStatusCalculator.CalculateStatus(used, showRemaining, elapsed));

    [Fact]
    public void ElapsedFractionWithinPeriod()
    {
        var reset = Now.AddHours(1);
        var duration = TimeSpan.FromHours(5);

        Assert.Equal(0.8, UsageStatusCalculator.ElapsedFraction(reset, duration, showRemaining: false, Now)!.Value, 10);
        Assert.Equal(0.2, UsageStatusCalculator.ElapsedFraction(reset, duration, showRemaining: true, Now)!.Value, 10);
    }

    [Fact]
    public void ElapsedFractionAfterReset()
    {
        var duration = TimeSpan.FromHours(5);

        Assert.Equal(1.0, UsageStatusCalculator.ElapsedFraction(Now, duration, showRemaining: false, Now));
        Assert.Equal(0.0, UsageStatusCalculator.ElapsedFraction(Now.AddMinutes(-1), duration, showRemaining: true, Now));
    }

    [Fact]
    public void ElapsedFractionClampsWhenResetIsBeyondThePeriod() =>
        Assert.Equal(
            0.0, UsageStatusCalculator.ElapsedFraction(Now.AddHours(6), TimeSpan.FromHours(5), showRemaining: false, Now));

    [Fact]
    public void ElapsedFractionIsUnknownWithoutResetOrDuration()
    {
        Assert.Null(UsageStatusCalculator.ElapsedFraction(null, TimeSpan.FromHours(5), showRemaining: false, Now));
        Assert.Null(UsageStatusCalculator.ElapsedFraction(Now.AddHours(1), TimeSpan.Zero, showRemaining: false, Now));
    }
}
