using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.History;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.History;

/// <summary>
/// History sampling, retention and export (upstream <c>UsageHistoryService</c>, <c>UsageHistoryData</c> and
/// the reset detection in <c>MenuBarManager</c>, none of which upstream tests).
/// </summary>
public class UsageHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static UsageData Usage(double? session, DateTimeOffset? sessionReset, double? weekly = 40, DateTimeOffset? weeklyReset = null) =>
        new()
        {
            SessionPercentage = session,
            SessionResetTime = sessionReset,
            WeeklyPercentage = weekly,
            WeeklyResetTime = weeklyReset ?? Now.AddDays(3),
            SonnetWeeklyPercentage = 12,
        };

    [Fact]
    public void FirstObservationTakesBothPeriodicSamples()
    {
        var history = new UsageHistoryData();

        var changed = UsageHistoryRecorder.RecordUsage(history, null, Usage(30, Now.AddHours(2)), Provider.Anthropic, Now);

        Assert.True(changed);
        var session = Assert.Single(history.SessionSnapshots);
        Assert.Equal(30, session.SessionPercentage);
        Assert.Equal(Now, session.Timestamp);
        Assert.Equal(Now, session.TriggeringResetTime);
        Assert.Equal("anthropic", session.Provider);
        var weekly = Assert.Single(history.WeeklySnapshots);
        Assert.Equal(40, weekly.WeeklyPercentage);
        Assert.Equal(12, weekly.SonnetWeeklyPercentage);
        Assert.Equal(Now, history.LastSessionRecordAt);
        Assert.Equal(Now, history.LastWeeklyRecordAt);
    }

    [Fact]
    public void PeriodicSamplesWaitForTheirIntervals()
    {
        var history = new UsageHistoryData();
        var usage = Usage(30, Now.AddHours(2));
        UsageHistoryRecorder.RecordUsage(history, null, usage, Provider.Anthropic, Now);

        Assert.False(UsageHistoryRecorder.RecordUsage(history, usage, usage, Provider.Anthropic, Now.AddMinutes(9)));
        Assert.True(UsageHistoryRecorder.RecordUsage(history, usage, usage, Provider.Anthropic, Now.AddMinutes(10)));
        Assert.Equal(2, history.SessionSnapshots.Count);
        Assert.Single(history.WeeklySnapshots);

        Assert.True(UsageHistoryRecorder.RecordUsage(history, usage, usage, Provider.Anthropic, Now.AddHours(2)));
        Assert.Equal(2, history.WeeklySnapshots.Count);
    }

    [Fact]
    public void SessionResetRecordsThePreviousUsageAndSkipsThatCyclesSample()
    {
        var history = new UsageHistoryData { LastSessionRecordAt = Now.AddHours(-1), LastWeeklyRecordAt = Now };
        var before = Usage(80, Now.AddMinutes(-1));
        var after = Usage(3, Now.AddHours(5));

        Assert.True(UsageHistoryRecorder.RecordUsage(history, before, after, Provider.Anthropic, Now));

        var snapshot = Assert.Single(history.Snapshots);
        Assert.Equal(ResetType.SessionReset, snapshot.ResetType);
        Assert.Equal(80, snapshot.SessionPercentage);
        Assert.Equal(Now.AddMinutes(-1), snapshot.TriggeringResetTime);
        Assert.Equal(Now, snapshot.Timestamp);
        Assert.Equal(Now.AddHours(-1), history.LastSessionRecordAt);
    }

    [Fact]
    public void AResetTimeThatDisappearsIsAReset()
    {
        var history = new UsageHistoryData { LastSessionRecordAt = Now, LastWeeklyRecordAt = Now };

        UsageHistoryRecorder.RecordUsage(history, Usage(80, Now.AddMinutes(-1)), Usage(0, null), Provider.Anthropic, Now);

        Assert.Equal(80, Assert.Single(history.SessionSnapshots).SessionPercentage);
    }

    [Fact]
    public void AResetWithNoPreviousUsageRecordsNothing()
    {
        var history = new UsageHistoryData { LastSessionRecordAt = Now, LastWeeklyRecordAt = Now };

        Assert.False(UsageHistoryRecorder.RecordUsage(history, Usage(0, null), Usage(3, Now.AddHours(5)), Provider.Anthropic, Now));
        Assert.Empty(history.Snapshots);
    }

    [Fact]
    public void WeeklyResetRecordsThePreviousWeekWithItsModels()
    {
        var history = new UsageHistoryData { LastSessionRecordAt = Now, LastWeeklyRecordAt = Now };
        var before = Usage(10, Now.AddHours(2), weekly: 95, weeklyReset: Now.AddMinutes(-2));
        var after = Usage(10, Now.AddHours(2), weekly: 1, weeklyReset: Now.AddDays(7));

        UsageHistoryRecorder.RecordUsage(history, before, after, Provider.Anthropic, Now);

        var snapshot = Assert.Single(history.WeeklySnapshots);
        Assert.Equal(95, snapshot.WeeklyPercentage);
        Assert.Equal(12, snapshot.SonnetWeeklyPercentage);
        Assert.Equal(Now.AddMinutes(-2), snapshot.TriggeringResetTime);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(-3600, true)]
    public void ResetTimesMoveOnlyByAMinuteOrMore(int seconds, bool expected) =>
        Assert.Equal(expected, UsageHistoryRecorder.ResetChanged(Now, Now.AddSeconds(seconds)));

    [Fact]
    public void MissingValuesAreNeverSampledAsZero()
    {
        var history = new UsageHistoryData();

        Assert.False(UsageHistoryRecorder.RecordUsage(
            history, null, new UsageData { SessionResetTime = Now.AddHours(1) }, Provider.Anthropic, Now));
        Assert.Empty(history.Snapshots);
        Assert.Null(history.LastSessionRecordAt);
    }

    [Fact]
    public void CodexSamplesCarryNoTokensOrModels()
    {
        var history = new UsageHistoryData();
        var usage = Usage(30, Now.AddHours(2)) with { SessionTokensUsed = 5, WeeklyTokensUsed = 9 };

        UsageHistoryRecorder.RecordUsage(history, null, usage, Provider.Codex, Now);

        Assert.All(history.Snapshots, s =>
        {
            Assert.Equal("codex", s.Provider);
            Assert.Null(s.SessionTokensUsed);
            Assert.Null(s.WeeklyTokensUsed);
            Assert.Null(s.SonnetWeeklyPercentage);
        });
    }

    [Fact]
    public void OldestSessionSamplesAreDroppedBeyondTheLimit()
    {
        var history = new UsageHistoryData { LastWeeklyRecordAt = Now.AddYears(1) };
        var usage = Usage(30, Now.AddYears(2));
        for (var i = 0; i <= UsageHistoryRecorder.MaxSessionSnapshots + 4; i++)
        {
            UsageHistoryRecorder.RecordUsage(history, usage, usage, Provider.Anthropic, Now.AddMinutes(10 * i));
        }

        Assert.Equal(UsageHistoryRecorder.MaxSessionSnapshots, history.SessionSnapshots.Count);
        Assert.Equal(Now.AddMinutes(50), history.SessionSnapshots[^1].Timestamp);
    }

    [Fact]
    public void BillingCycleIsRecordedWhenItsResetMovesAndItHadSpend()
    {
        var history = new UsageHistoryData();
        var october = new ApiUsage { CurrentSpendCents = 2500, PrepaidCreditsCents = 5000, Currency = "USD", ResetsAt = Now.AddHours(-1) };
        var november = october with { CurrentSpendCents = 10, ResetsAt = Now.AddDays(30) };

        Assert.False(UsageHistoryRecorder.RecordBilling(history, null, october, Now));
        Assert.False(UsageHistoryRecorder.RecordBilling(history, october, october with { CurrentSpendCents = 2600 }, Now));
        Assert.False(UsageHistoryRecorder.RecordBilling(history, october with { CurrentSpendCents = 0 }, november, Now));
        Assert.True(UsageHistoryRecorder.RecordBilling(history, october, november, Now));

        var snapshot = Assert.Single(history.BillingCycleSnapshots);
        Assert.Equal(2500, snapshot.ApiSpendCents);
        Assert.Equal(5000, snapshot.ApiPrepaidCreditsCents);
        Assert.Equal("USD", snapshot.ApiCurrency);
        Assert.Null(snapshot.Provider);
        Assert.Equal(Now.AddHours(-1), snapshot.TriggeringResetTime);
    }

    [Fact]
    public void SnapshotsWhoseResetLiesAfterTheirTimestampAreIgnored()
    {
        var history = new UsageHistoryData();
        history.Snapshots.Add(new UsageSnapshot { Timestamp = Now, ResetType = ResetType.SessionReset, SessionPercentage = 5, TriggeringResetTime = Now.AddMinutes(5) });
        history.Snapshots.Add(new UsageSnapshot { Timestamp = Now, ResetType = ResetType.SessionReset, SessionPercentage = 6, TriggeringResetTime = Now.AddSeconds(30) });

        Assert.Equal(6, Assert.Single(history.SessionSnapshots).SessionPercentage);
    }

    private static UsageHistoryData Sample()
    {
        var history = new UsageHistoryData { LastSessionRecordAt = Now, LastWeeklyRecordAt = Now.AddHours(-1) };
        history.Snapshots.Add(new UsageSnapshot
        {
            Id = new Guid("11111111-2222-3333-4444-555555555555"), Timestamp = Now, ResetType = ResetType.SessionReset,
            Provider = "anthropic", SessionPercentage = 39.25, SessionTokensUsed = 1200, TriggeringResetTime = Now.AddMinutes(-1),
        });
        history.Snapshots.Add(new UsageSnapshot
        {
            Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), Timestamp = Now.AddHours(1), ResetType = ResetType.WeeklyReset,
            Provider = "anthropic", WeeklyPercentage = 60, OpusWeeklyPercentage = 5.55, SonnetWeeklyPercentage = 12,
            DesignWeeklyPercentage = 1, FableWeeklyPercentage = 73, TriggeringResetTime = Now,
        });
        history.Snapshots.Add(new UsageSnapshot
        {
            Id = new Guid("99999999-8888-7777-6666-555555555555"), Timestamp = Now.AddHours(2), ResetType = ResetType.BillingCycle,
            ApiSpendCents = 4250, ApiPrepaidCreditsCents = 5000, ApiCurrency = "USD", TriggeringResetTime = Now,
        });
        return history;
    }

    [Fact]
    public void StoredHistoryRoundTrips()
    {
        var parsed = UsageHistoryJson.Parse(UsageHistoryJson.Serialise(Sample()));

        Assert.Equal(Sample().Snapshots, parsed.Snapshots);
        Assert.Equal(Now, parsed.LastSessionRecordAt);
        Assert.Equal(Now.AddHours(-1), parsed.LastWeeklyRecordAt);
    }

    /// <summary>Port of upstream <c>testLegacyUsageSnapshotDecodesWithNilProvider</c>.</summary>
    [Fact]
    public void LegacyUsageSnapshotDecodesWithNilProvider()
    {
        var parsed = UsageHistoryJson.Parse(
            """{"snapshots":[{"id":"11111111-2222-3333-4444-555555555555","timestamp":"2026-10-01T12:00:00Z","resetType":"sessionReset","sessionPercentage":42,"triggeringResetTime":"2026-10-01T11:59:00Z"}]}""");

        var snapshot = Assert.Single(parsed.Snapshots);
        Assert.Null(snapshot.Provider);
        Assert.Equal(42, snapshot.SessionPercentage);
        Assert.Null(parsed.LastSessionRecordAt);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":2,"snapshots":[]}""")]
    [InlineData("""{"snapshots":[{"id":"x","timestamp":"2026-10-01T12:00:00Z","resetType":"sessionReset","triggeringResetTime":"2026-10-01T12:00:00Z"}]}""")]
    [InlineData("""{"snapshots":[{"id":"11111111-2222-3333-4444-555555555555","timestamp":"2026-10-01T12:00:00Z","resetType":"hourlyReset","triggeringResetTime":"2026-10-01T12:00:00Z"}]}""")]
    public void DamagedOrNewerHistoryIsRejectedNotEmptied(string json) =>
        Assert.Throws<FormatException>(() => UsageHistoryJson.Parse(json));

    [Fact]
    public void JsonExportHasUpstreamsShape()
    {
        var root = JsonNode.Parse(UsageHistoryJson.Export(Sample()))!.AsObject();

        Assert.Equal(["snapshots"], root.Select(p => p.Key));
        var first = root["snapshots"]![0]!.AsObject();
        Assert.Equal(
            ["id", "provider", "resetType", "sessionPercentage", "sessionTokensUsed", "timestamp", "triggeringResetTime"],
            first.Select(p => p.Key));
        Assert.Equal("11111111-2222-3333-4444-555555555555", first["id"]!.GetValue<string>());
        Assert.Equal("sessionReset", first["resetType"]!.GetValue<string>());
        Assert.Equal(39.25, first["sessionPercentage"]!.GetValue<double>());
        Assert.Equal("2026-10-01T12:00:00Z", first["timestamp"]!.GetValue<string>());
        Assert.Equal("2026-10-01T11:59:00Z", first["triggeringResetTime"]!.GetValue<string>());
        Assert.Equal(4250, root["snapshots"]![2]!["apiSpendCents"]!.GetValue<long>());
        Assert.Contains("\n", UsageHistoryJson.Export(Sample()), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonExportCanBeLimitedToOneType()
    {
        var root = JsonNode.Parse(UsageHistoryJson.Export(Sample(), ResetType.BillingCycle))!;

        Assert.Equal("billingCycle", Assert.Single(root["snapshots"]!.AsArray())!["resetType"]!.GetValue<string>());
    }

    [Fact]
    public void CsvExportListsNewestFirstWithBlanksForMissingValues()
    {
        var csv = UsageHistoryCsv.Export(Sample(), TimeZoneInfo.Utc);

        Assert.Equal(
            "Timestamp,Reset Type,Session %,Session Tokens,Weekly %,Weekly Tokens,Opus %,Sonnet %,Design %,Fable %,API Spend,Currency\n" +
            "2026-10-01 14:00:00,billingCycle,,,,,,,,,42.5,USD\n" +
            "2026-10-01 13:00:00,weeklyReset,,,60.0,,5.6,12.0,1.0,73.0,,\n" +
            "2026-10-01 12:00:00,sessionReset,39.3,1200,,,,,,,,\n",
            csv);
    }

    [Fact]
    public void CsvTimesUseTheGivenZone()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

        Assert.Contains("2026-10-01 21:00:00,sessionReset", UsageHistoryCsv.Export(Sample(), tokyo, ResetType.SessionReset), StringComparison.Ordinal);
    }
}
