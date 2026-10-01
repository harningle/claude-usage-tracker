using System.Globalization;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Tray;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Tests.Tray;

/// <summary>
/// What the tray shows: the active profile's metrics including Console credits, or in multi-profile mode one
/// icon per selected profile with upstream's multi-profile options. Values are synthetic.
/// </summary>
public class TrayModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static ProfileUsageState State(Guid id, double session = 40, double weekly = 60, ApiUsage? api = null) => new()
    {
        ProfileId = id,
        IsLinked = true,
        Source = UsageSourceKind.ClaudeCode,
        Link = LinkStatus.Ready,
        LastAttemptAt = Now,
        Usage = new CachedUsage
        {
            ProfileId = id, ObservedAt = Now, Source = "oauthUsage",
            Usage = new UsageData { SessionPercentage = session, SessionResetTime = Now.AddHours(2), WeeklyPercentage = weekly, WeeklyResetTime = Now.AddDays(3) },
        },
        Api = api is null ? null : new CachedApiUsage { ProfileId = id, ObservedAt = Now, OrganizationId = "org-api", Usage = api },
    };

    private static readonly ApiUsage Billing = new() { CurrentSpendCents = 1234, PrepaidCreditsCents = 8766, Currency = "USD" };

    private static IReadOnlyList<TraySlot> Build(AppSettings settings, Dictionary<Guid, ProfileUsageState> states) =>
        TrayModel.Build(settings, id => states.GetValueOrDefault(id), true, Now, TimeZoneInfo.Utc, English);

    [Fact]
    public void UnlinkedProfileShowsTheLogo()
    {
        var profile = new ProfileSettings { Name = "New" };

        var slot = Assert.Single(Build(new AppSettings { Profiles = [profile] }, []));

        Assert.Equal(TrayModel.LogoKey, slot.Key);
        Assert.Null(slot.Spec);
    }

    [Fact]
    public void ApiMetricAppearsOnlyWithConsoleBilling()
    {
        var profile = new ProfileSettings { Name = "Work", CliLink = new CliLink() };
        profile.IconConfig.Metric("api")!.IsEnabled = true;
        var settings = new AppSettings { Profiles = [profile] };
        var states = new Dictionary<Guid, ProfileUsageState> { [profile.Id] = State(profile.Id, api: Billing) };

        Assert.Equal(["session"], Build(settings, states).Select(s => s.Key.Split('|')[1]));

        profile.Console = new ConsoleLink { OrganizationId = "org-api" };
        var slots = Build(settings, states);

        Assert.Equal(["session", "api"], slots.Select(s => s.Key.Split('|')[1]));
        var api = slots[1];
        Assert.Contains("API Credits $12.34 / $100.00", api.Tooltip, StringComparison.Ordinal);
        Assert.Null(api.Spec?.MarkerFraction);
        Assert.Equal(0.1234, api.Spec?.Fraction ?? 0, 4);
        Assert.Equal(profile.Id, api.ProfileId);
    }

    [Fact]
    public void MultiProfileModeShowsOneIconPerSelectedProfile()
    {
        var work = new ProfileSettings { Name = "Work", CliLink = new CliLink() };
        var home = new ProfileSettings { Name = "Home", CliLink = new CliLink() };
        var hidden = new ProfileSettings { Name = "Hidden", CliLink = new CliLink(), IsSelectedForDisplay = false };
        var settings = new AppSettings
        {
            Profiles = [work, home, hidden],
            ActiveProfileId = home.Id,
            IsMultiProfile = true,
            MultiProfileDisplayConfig = new MultiProfileDisplayConfig { ShowActiveProfileIndicator = true },
        };
        var states = new Dictionary<Guid, ProfileUsageState> { [work.Id] = State(work.Id, 10, 20), [home.Id] = State(home.Id, 70, 90) };

        var slots = Build(settings, states);

        Assert.Equal([TrayIdentity.Slot(work.Id, "multi"), TrayIdentity.Slot(home.Id, "multi")], slots.Select(s => s.Key));
        Assert.Equal([work.Id, home.Id], slots.Select(s => s.ProfileId!.Value));
        Assert.All(slots, s => Assert.Equal(TrayIconStyle.Concentric, s.Spec?.Style));
        Assert.False(slots[0].Spec?.ActiveIndicator);
        Assert.True(slots[1].Spec?.ActiveIndicator);
        Assert.Equal(0.9, slots[1].Spec?.SecondaryFraction ?? 0, 3);
        Assert.StartsWith("Home\nSession Usage 70%\nAll models 90%", slots[1].Tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("progressBar", true, TrayIconStyle.DualBar)]
    [InlineData("progressBar", false, TrayIconStyle.ProgressBar)]
    [InlineData("compact", true, TrayIconStyle.Dot)]
    [InlineData("percentage", true, TrayIconStyle.PercentageOnly)]
    [InlineData("concentric", false, TrayIconStyle.Ring)]
    public void MultiProfileStylesMapToSquareIcons(string style, bool showWeek, TrayIconStyle expected)
    {
        var profile = new ProfileSettings { Name = "Work", CliLink = new CliLink() };
        var settings = new AppSettings
        {
            Profiles = [profile],
            IsMultiProfile = true,
            MultiProfileDisplayConfig = new MultiProfileDisplayConfig { IconStyle = style, ShowWeek = showWeek },
        };

        var slot = Assert.Single(Build(settings, new() { [profile.Id] = State(profile.Id) }));

        Assert.Equal(expected, slot.Spec?.Style);
        Assert.Equal(showWeek, slot.Spec?.HasSecondary);
    }

    [Fact]
    public void MultiProfileOptionsReplaceTheProfilesOwn()
    {
        var profile = new ProfileSettings { Name = "Work", CliLink = new CliLink() };
        profile.IconConfig.ShowRemainingPercentage = false;
        var settings = new AppSettings
        {
            Profiles = [profile],
            IsMultiProfile = true,
            MultiProfileDisplayConfig = new MultiProfileDisplayConfig { ShowRemainingPercentage = true, UseSystemColor = true },
        };

        var slot = Assert.Single(Build(settings, new() { [profile.Id] = State(profile.Id, 40) }));

        Assert.Equal(60, slot.Spec?.DisplayPercentage);
        Assert.True(slot.Spec?.Monochrome);
    }

    [Fact]
    public void UnsetUpProfileInMultiModeKeepsItsSlotWithTheLogo()
    {
        var profile = new ProfileSettings { Name = "New" };
        var settings = new AppSettings { Profiles = [profile], IsMultiProfile = true };

        var slot = Assert.Single(Build(settings, []));

        Assert.Null(slot.Spec);
        Assert.Equal(profile.Id, slot.ProfileId);
    }
}
