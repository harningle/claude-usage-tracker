using System.Text;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.Tests.Statusline;

/// <summary>Which value the status line shows: the folder's fresh cache, else stdin, else the stale cache with a label (plan.md section 2).</summary>
public class StatuslineUsageTests
{
    private const string Folder = @"C:\Users\someone\.claude";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static StatuslineCache Cache(double ageSeconds, Action<StatuslineCache>? change = null)
    {
        var cache = new StatuslineCache
        {
            ConfigDirectory = Folder,
            ProfileId = Guid.NewGuid(),
            ProfileName = "Work",
            RefreshIntervalSeconds = 30,
            ObservedAt = Now.AddSeconds(-ageSeconds),
            SessionPercentage = 42,
            SessionResetsAt = Now.AddHours(1),
            WeeklyPercentage = 17,
            WeeklyResetsAt = Now.AddDays(2),
            CostUsed = 3.5m,
            CostLimit = 20m,
            CostCurrency = "USD",
        };
        change?.Invoke(cache);
        return cache;
    }

    private static StatuslineInput Input(double? fiveHour = null, double? sevenDay = null) => new(
        null, null, 0, null,
        fiveHour is { } five ? new StatuslineWindow(five, Now.AddHours(3)) : null,
        sevenDay is { } seven ? new StatuslineWindow(seven, Now.AddDays(5)) : null);

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void AFreshCacheWinsOverStdin(double age)
    {
        var usage = StatuslineUsage.Resolve(Cache(age), Folder, Input(80, 90), Now);

        Assert.Equal(new StatuslineValue(42, Now.AddHours(1), StatuslineSource.Cache), usage.Session);
        Assert.Equal(new StatuslineValue(17, Now.AddDays(2), StatuslineSource.Cache), usage.Weekly);
        Assert.Equal(new StatuslineCost(3.5m, 20m, "USD"), usage.Cost);
        Assert.Equal("Work", usage.ProfileName);
    }

    [Fact]
    public void ACacheOlderThanTwiceThePollingIntervalGivesWayToStdin()
    {
        var usage = StatuslineUsage.Resolve(Cache(61), Folder, Input(80, 90), Now);

        Assert.Equal(new StatuslineValue(80, Now.AddHours(3), StatuslineSource.Stdin), usage.Session);
        Assert.Equal(new StatuslineValue(90, Now.AddDays(5), StatuslineSource.Stdin), usage.Weekly);
        // Stdin has no extra-usage amounts, and an old amount is not shown as current.
        Assert.Null(usage.Cost);
        Assert.Equal("Work", usage.ProfileName);
    }

    [Fact]
    public void TheFreshnessFollowsTheProfilesOwnInterval()
    {
        var usage = StatuslineUsage.Resolve(Cache(500, c => c.RefreshIntervalSeconds = 300), Folder, Input(80), Now);

        Assert.Equal(StatuslineSource.Cache, usage.Session?.Source);
    }

    [Fact]
    public void AStaleCacheIsLabelledAndOnlyUsedWithoutStdin()
    {
        var usage = StatuslineUsage.Resolve(Cache(3600), Folder, Input(sevenDay: 90), Now);

        Assert.Equal(new StatuslineValue(42, Now.AddHours(1), StatuslineSource.StaleCache), usage.Session);
        Assert.Equal(StatuslineSource.Stdin, usage.Weekly?.Source);
    }

    [Fact]
    public void AStaleValueOfAWindowThatHasSinceResetIsNotShown()
    {
        var usage = StatuslineUsage.Resolve(Cache(7200, c => c.SessionResetsAt = Now.AddMinutes(-5)), Folder, Input(), Now);

        Assert.Null(usage.Session);
        Assert.Equal(StatuslineSource.StaleCache, usage.Weekly?.Source);
    }

    [Fact]
    public void AFreshSessionWhoseResetHasPassedShowsUpstreamsEffectiveZero()
    {
        var usage = StatuslineUsage.Resolve(Cache(20, c => c.SessionResetsAt = Now.AddSeconds(-1)), Folder, Input(80), Now);

        Assert.Equal(new StatuslineValue(0, null, StatuslineSource.Cache), usage.Session);
    }

    [Fact]
    public void MissingValuesStayMissing()
    {
        Assert.Equal(new StatuslineUsage(null, null, null, null), StatuslineUsage.Resolve(null, Folder, Input(), Now));

        // The cache names the folder's profile, but no usage has been fetched: nothing is invented.
        var named = Cache(0, c =>
        {
            c.ObservedAt = null;
            c.SessionPercentage = null;
            c.WeeklyPercentage = null;
            c.CostUsed = null;
        });
        Assert.Equal(new StatuslineUsage(null, null, null, "Work"), StatuslineUsage.Resolve(named, Folder, Input(), Now));

        // A fresh cache without a weekly value lets stdin supply that window.
        var usage = StatuslineUsage.Resolve(Cache(10, c => c.WeeklyPercentage = null), Folder, Input(80, 90), Now);
        Assert.Equal(StatuslineSource.Cache, usage.Session?.Source);
        Assert.Equal(StatuslineSource.Stdin, usage.Weekly?.Source);
    }

    [Fact]
    public void AnotherFoldersCacheIsNeverShown()
    {
        var other = Cache(0, c => c.ConfigDirectory = @"C:\Users\someone\.claude-work");

        var usage = StatuslineUsage.Resolve(other, Folder, Input(80), Now);

        Assert.Equal(StatuslineSource.Stdin, usage.Session?.Source);
        Assert.Null(usage.Weekly);
        Assert.Null(usage.Cost);
        Assert.Null(usage.ProfileName);
    }

    [Fact]
    public void TheFolderIsComparedWithoutCaseOrTrailingSeparator() =>
        Assert.Equal(StatuslineSource.Cache,
            StatuslineUsage.Resolve(Cache(0), @"c:\users\SOMEONE\.claude\", Input(), Now).Session?.Source);

    [Fact]
    public void ACacheInANewerFormatIsNotInterpreted()
    {
        var usage = StatuslineUsage.Resolve(Cache(0, c => c.Version = StatuslineCache.CurrentVersion + 1), Folder, Input(), Now);

        Assert.Equal(new StatuslineUsage(null, null, null, null), usage);
    }

    [Fact]
    public void ACacheFromTheFutureIsNotFresh()
    {
        Assert.Equal(StatuslineSource.Cache, StatuslineUsage.Resolve(Cache(-60), Folder, Input(80), Now).Session?.Source);
        Assert.Equal(StatuslineSource.Stdin, StatuslineUsage.Resolve(Cache(-3600), Folder, Input(80), Now).Session?.Source);
    }

    [Fact]
    public void ExtraUsageNeedsAllThreeFieldsAndAPositiveLimit()
    {
        Assert.Null(StatuslineUsage.Resolve(Cache(0, c => c.CostLimit = 0), Folder, Input(), Now).Cost);
        Assert.Null(StatuslineUsage.Resolve(Cache(0, c => c.CostCurrency = null), Folder, Input(), Now).Cost);
        Assert.Null(StatuslineUsage.Resolve(Cache(0, c => c.CostUsed = null), Folder, Input(), Now).Cost);
    }
}

/// <summary>Claude Code's status line input (https://code.claude.com/docs/en/statusline), every field optional.</summary>
public class StatuslineInputTests
{
    private static StatuslineInput Parse(string json) => StatuslineInput.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void DocumentedInputIsRead()
    {
        var input = Parse("""
            {"cwd":"/current/working/directory","session_id":"abc123","model":{"id":"claude-opus-5-5","display_name":"Opus"},
             "workspace":{"current_dir":"C:\\Users\\someone\\my project","project_dir":"/original"},"version":"2.1.90",
             "context_window":{"total_input_tokens":15500,"total_output_tokens":1200,"context_window_size":200000,"used_percentage":8,
               "current_usage":{"input_tokens":8500,"output_tokens":1200,"cache_creation_input_tokens":5000,"cache_read_input_tokens":2000}},
             "rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1738425600},"seven_day":{"used_percentage":41.2,"resets_at":1738857600}}}
            """);

        Assert.Equal(@"C:\Users\someone\my project", input.CurrentDirectory);
        Assert.Equal("Opus", input.ModelName);
        Assert.Equal(15_500, input.ContextTokens);
        Assert.Equal(200_000, input.ContextWindowSize);
        Assert.Equal(new StatuslineWindow(23.5, DateTimeOffset.FromUnixTimeSeconds(1738425600)), input.FiveHour);
        Assert.Equal(new StatuslineWindow(41.2, DateTimeOffset.FromUnixTimeSeconds(1738857600)), input.SevenDay);
    }

    [Fact]
    public void AbsentAndNullFieldsAreUnknownNotZero()
    {
        var input = Parse("""{"cwd":"C:/work","model":{},"context_window":{"current_usage":null,"used_percentage":null},"rate_limits":{"seven_day":{"used_percentage":3}}}""");

        Assert.Equal("C:/work", input.CurrentDirectory);
        Assert.Null(input.ModelName);
        Assert.Equal(0, input.ContextTokens);
        Assert.Null(input.ContextWindowSize);
        Assert.Null(input.FiveHour);
        Assert.Equal(new StatuslineWindow(3, null), input.SevenDay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"model\":")]
    public void TextThatIsNotAJsonObjectReadsAsNothingKnown(string text) =>
        Assert.Equal(StatuslineInput.Empty, Parse(text));

    [Fact]
    public void WronglyTypedFieldsAreIgnored()
    {
        var input = Parse("""{"model":{"display_name":7},"workspace":"x","rate_limits":{"five_hour":{"used_percentage":"23","resets_at":"soon"}},"context_window":{"context_window_size":-5}}""");

        Assert.Equal(StatuslineInput.Empty, input);
    }
}
