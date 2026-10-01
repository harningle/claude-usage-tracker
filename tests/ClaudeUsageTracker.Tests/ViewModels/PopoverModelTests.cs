using System.Globalization;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.ViewModels;

/// <summary>
/// The popover's cards and banners, against upstream's row order, formatting and banner precedence, with
/// the Windows states for what upstream hid. Values are synthetic fixtures.
/// </summary>
public class PopoverModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly Guid ProfileId = Guid.NewGuid();

    private static ProfileUsageState Linked(UsageData? usage = null, DateTimeOffset? observedAt = null) => new()
    {
        ProfileId = ProfileId,
        IsLinked = true,
        Link = LinkStatus.Ready,
        LastAttemptAt = Now,
        Usage = usage is null ? null : new CachedUsage
        {
            ProfileId = ProfileId, ObservedAt = observedAt ?? Now, Source = "oauthUsage", Usage = usage,
        },
    };

    private static PopoverModel Build(ProfileUsageState? state, IconConfiguration? display = null,
        PopoverTimeDisplay timeDisplay = PopoverTimeDisplay.ResetTime) =>
        PopoverModelBuilder.Build(state, display ?? new IconConfiguration(), timeDisplay, true, Now, TimeZoneInfo.Utc, English);

    private static readonly UsageData Typical = new()
    {
        // 52 min into the 5 h window (17.3 % elapsed), 3.5 days into the week (50 %).
        SessionPercentage = 39.6,
        SessionResetTime = Now.AddHours(4).AddMinutes(8),
        WeeklyPercentage = 60,
        WeeklyResetTime = Now.AddDays(3.5),
    };

    [Fact]
    public void UnlinkedProfileAsksForSetup()
    {
        var model = Build(new ProfileUsageState { ProfileId = ProfileId });

        Assert.Empty(model.Rows);
        Assert.Equal(BannerAction.OpenSetup, model.Banner?.Action);
        Assert.Null(model.Placeholder);
    }

    [Fact]
    public void FirstCheckShowsLoadingNotZeroCards()
    {
        var model = Build(new ProfileUsageState { ProfileId = ProfileId, IsLinked = true });

        Assert.Empty(model.Rows);
        Assert.Equal("Loading usage…", model.Placeholder);
    }

    [Fact]
    public void FailedFirstCheckSaysThereIsNoData()
    {
        var model = Build(new ProfileUsageState
        {
            ProfileId = ProfileId, IsLinked = true, LastAttemptAt = Now, LastFailure = FetchFailure.Network, ConsecutiveFailures = 1,
        });

        Assert.Empty(model.Rows);
        Assert.Equal("No usage data yet.", model.Placeholder);
    }

    [Fact]
    public void SessionAndWeeklyCardsFollowUpstream()
    {
        var model = Build(Linked(Typical));

        Assert.Collection(model.Rows,
            session =>
            {
                Assert.Equal("Session Usage", session.Title);
                Assert.Equal("5-hour rolling window", session.Subtitle);
                Assert.Null(session.Tag);
                Assert.Equal("39%", session.PercentText);
                Assert.Equal(0.396, session.BarFraction!.Value, 6);
                Assert.Equal(52.0 / 300, session.MarkerFraction!.Value, 6);
                // 39.6 % used at 17.3 % elapsed projects to 228 %: critical by pace.
                Assert.Equal(UsageStatusLevel.Critical, session.Level);
                Assert.Equal(PaceStatus.Runaway, session.Pace);
                Assert.Equal("Resets Today 14:08", session.ResetText);
            },
            weekly =>
            {
                Assert.Equal("All models", weekly.Title);
                Assert.Equal("Weekly", weekly.Tag);
                Assert.Equal("60%", weekly.PercentText);
                // 60 % at 50 % elapsed projects to 120 %.
                Assert.Equal(UsageStatusLevel.Critical, weekly.Level);
                Assert.Equal(PaceStatus.Runaway, weekly.Pace);
                Assert.Equal("Resets Oct 4, 22:00", weekly.ResetText);
            });
        Assert.Null(model.Banner);
    }

    [Fact]
    public void ModelCardsAppearWhenReportedEvenAtZero()
    {
        var model = Build(Linked(Typical with
        {
            DesignWeeklyPercentage = 0,
            FableWeeklyPercentage = 10,
            OpusWeeklyPercentage = 20,
            SonnetWeeklyPercentage = 30,
            SonnetWeeklyResetTime = Now.AddDays(2),
        }));

        Assert.Equal(["Session Usage", "Design", "All models", "Fable", "Opus", "Sonnet"], model.Rows.Select(r => r.Title));
        Assert.Equal("0%", model.Rows[1].PercentText);
        Assert.Null(model.Rows[4].ResetText);
        Assert.Null(model.Rows[5].Tag);
        Assert.Null(model.Rows[1].MarkerFraction);
    }

    [Fact]
    public void ExtraUsageCardFollowsUpstream()
    {
        var model = Build(Linked(Typical with
        {
            SonnetWeeklyPercentage = 30,
            CostUsed = 1.86m,
            CostLimit = 5.00m,
            CostCurrency = "USD",
        }));

        var extra = model.Rows[^1];
        Assert.Equal("Extra Usage", extra.Title);
        Assert.Equal("1.86 / 5.00 USD", extra.Subtitle);
        Assert.Null(extra.Tag);
        Assert.Equal("37%", extra.PercentText);
        Assert.Equal(UsageStatusLevel.Safe, extra.Level);
        Assert.Null(extra.MarkerFraction);
        Assert.Null(extra.Pace);
        Assert.Null(extra.ResetText);
        Assert.Equal("Sonnet", model.Rows[^2].Title);
    }

    [Fact]
    public void ZeroExtraUsageIsShownAsZero()
    {
        var extra = Build(Linked(Typical with { CostUsed = 0.00m, CostLimit = 5.00m, CostCurrency = "USD" })).Rows[^1];

        Assert.Equal("0.00 / 5.00 USD", extra.Subtitle);
        Assert.Equal("0%", extra.PercentText);
    }

    [Theory]
    [InlineData(null, "USD")]
    [InlineData(0.0, "USD")]
    [InlineData(5.0, null)]
    public void ExtraUsageCardNeedsAPositiveLimitAndCurrency(double? limit, string? currency)
    {
        var model = Build(Linked(Typical with { CostUsed = 0m, CostLimit = (decimal?)limit, CostCurrency = currency }));

        Assert.DoesNotContain(model.Rows, r => r.Title == "Extra Usage");
    }

    [Fact]
    public void MissingValuesAreShownAsUnavailable()
    {
        var model = Build(Linked(new UsageData { SessionPercentage = 12 }));

        var weekly = model.Rows[1];
        Assert.Equal("—", weekly.PercentText);
        Assert.Null(weekly.BarFraction);
        Assert.Null(weekly.Level);
        Assert.Null(weekly.ResetText);
    }

    [Fact]
    public void RemainingModeInvertsPercentAndMarker()
    {
        var model = Build(Linked(Typical), new IconConfiguration { ShowRemainingPercentage = true });

        Assert.Equal("60%", model.Rows[0].PercentText);
        Assert.Equal(1 - 52.0 / 300, model.Rows[0].MarkerFraction!.Value, 6);
    }

    [Fact]
    public void MarkersAndPaceCanBeTurnedOff()
    {
        var model = Build(Linked(Typical), new IconConfiguration { ShowTimeMarker = false, ShowPaceMarker = false, UsePaceColoring = false });

        Assert.Null(model.Rows[0].MarkerFraction);
        Assert.Null(model.Rows[0].Pace);
        Assert.Equal(UsageStatusLevel.Safe, model.Rows[0].Level);
    }

    [Theory]
    [InlineData(PopoverTimeDisplay.ResetTime, "Resets Today 14:08")]
    [InlineData(PopoverTimeDisplay.RemainingTime, "Resets in 4h 8m")]
    [InlineData(PopoverTimeDisplay.Both, "Resets in 4h 8m (Today 14:08)")]
    public void ResetTextFollowsThePreference(PopoverTimeDisplay display, string expected) =>
        Assert.Equal(expected, Build(Linked(Typical), timeDisplay: display).Rows[0].ResetText);

    [Fact]
    public void PassedSessionResetShowsZeroAndSaysSo()
    {
        var model = Build(Linked(Typical with { SessionResetTime = Now.AddMinutes(-2) }));

        Assert.Equal("0%", model.Rows[0].PercentText);
        Assert.Equal("Window reset, waiting for new data", model.Rows[0].ResetText);
    }

    [Theory]
    [InlineData(299, null)]
    [InlineData(301, "Updated 5m ago")]
    [InlineData(7500, "Updated 125m ago")]
    public void OldDataIsLabelledStale(int ageSeconds, string? expected) =>
        Assert.Equal(expected, Build(Linked(Typical, Now.AddSeconds(-ageSeconds))).Banner?.Message);

    [Fact]
    public void RepeatedFailuresAreReportedFromTheThird()
    {
        Assert.Null(Build(Linked(Typical) with { LastFailure = FetchFailure.Network, ConsecutiveFailures = 2 }).Banner);

        var banner = Build(Linked(Typical) with { LastFailure = FetchFailure.Network, ConsecutiveFailures = 3 }).Banner;
        Assert.Equal("Refresh failed 3 times", banner?.Message);
        Assert.Equal(BannerAction.Refresh, banner?.Action);
    }

    [Fact]
    public void WaitingForClaudeCodeIsNotAnAuthenticationError()
    {
        var banner = Build(Linked(Typical, Now.AddMinutes(-20)) with
        {
            Link = LinkStatus.WaitingForCliRefresh, ConsecutiveFailures = 4, LastFailure = FetchFailure.Network,
        }).Banner;

        Assert.Equal("Waiting for Claude Code to refresh its login", banner?.Message);
        Assert.Equal(BannerTone.Info, banner?.Tone);
    }

    [Fact]
    public void RejectedCredentialsAreDistinctFromExpiry()
    {
        var banner = Build(Linked(Typical) with { LastFailure = FetchFailure.CredentialsRejected, ConsecutiveFailures = 1 }).Banner;

        Assert.Equal("Claude Code's login was rejected. Sign in again in Claude Code.", banner?.Message);
    }

    [Fact]
    public void RateLimitShowsWhenTheNextCheckIs()
    {
        var banner = Build(Linked(Typical) with
        {
            LastFailure = FetchFailure.RateLimited, ConsecutiveFailures = 1, RetryNotBefore = Now.AddMinutes(20),
        }).Banner;

        Assert.Equal("Rate limited. Next check at 10:20.", banner?.Message);
    }

    [Fact]
    public void ChangedAccountKeepsNoOtherBanner()
    {
        var banner = Build(Linked(Typical) with { Link = LinkStatus.AccountChanged }).Banner;

        Assert.Equal("Claude Code is now signed in to a different account.", banner?.Message);
        Assert.Equal(BannerAction.OpenSettings, banner?.Action);
    }
}
