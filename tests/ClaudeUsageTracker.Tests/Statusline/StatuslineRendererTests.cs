using System.Text.RegularExpressions;
using ClaudeUsageTracker.Core.Statusline;

namespace ClaudeUsageTracker.Tests.Statusline;

/// <summary>
/// The status line against upstream's bash script (<c>StatuslineService.bashScript</c>), which has no tests
/// upstream: components, order, separators, bars, pace markers, times and ANSI colours.
/// </summary>
public partial class StatuslineRendererTests
{
    private const string Reset = "\e[0m";
    private const string Blue = "\e[0;34m", Green = "\e[0;32m", Gray = "\e[0;90m", Yellow = "\e[0;33m", Cyan = "\e[0;36m", Magenta = "\e[0;35m";

    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(2), "Test", "Test");

    private static StatuslineValue Value(double percentage, DateTimeOffset? resetsAt = null, StatuslineSource source = StatuslineSource.Cache) =>
        new(percentage, resetsAt, source);

    private static StatuslineContent Content(
        StatuslineValue? session = null, StatuslineValue? weekly = null, StatuslineCost? cost = null, string? profile = null,
        string? directory = "claude-usage", string? branch = "main", string? model = "Opus", long tokens = 96_000, long? size = 200_000) =>
        new(directory, branch, model, tokens, size, new StatuslineUsage(session, weekly, cost, profile));

    private static string Render(StatuslineConfig config, StatuslineContent content) => StatuslineRenderer.Render(config, content, Now, Zone);

    private static string Plain(string text) => Ansi().Replace(text, "");

    private static StatuslineConfig Only(Action<StatuslineConfig> show)
    {
        var config = new StatuslineConfig
        {
            ShowModel = false, ShowDirectory = false, ShowBranch = false, ShowContext = false, ShowUsage = false,
        };
        show(config);
        return config;
    }

    [Fact]
    public void DefaultLineHasUpstreamsComponentsOrderAndColours()
    {
        // 29 % with the reset 2 h away: 3 h of the 5 h window have passed, so the marker replaces the seventh block.
        var line = Render(new StatuslineConfig(), Content(Value(29, Now.AddHours(2))));

        Assert.Equal(
            $"{Blue}claude-usage{Reset}{Gray} │ {Reset}{Green}⎇ main{Reset}{Gray} │ {Reset}{Yellow}Opus{Reset}{Gray} │ {Reset}"
            + $"{Cyan}Ctx: 48%{Reset}{Gray} │ {Reset}"
            + $"\e[38;5;34mUsage: 29% ▓▓▓░░░\e[38;5;34m┃{Reset}\e[38;5;34m░░░ → Reset: 04:00 PM{Reset}",
            line);
    }

    [Fact]
    public void EveryComponentAppearsInUpstreamsOrder()
    {
        var config = new StatuslineConfig { ShowProfile = true, ShowWeekly = true, ShowExtraUsage = true, ShowPaceMarker = false, ShowWeeklyPaceMarker = false };

        var line = Plain(Render(config, Content(
            Value(29, Now.AddHours(2)), Value(45, Now.AddDays(3)), new StatuslineCost(9.49m, 50m, "USD"), "Work")));

        Assert.Equal(
            "claude-usage │ ⎇ main │ Opus │ Work │ Ctx: 48% │ Usage: 29% ▓▓▓░░░░░░░ → Reset: 04:00 PM │ Weekly: 45% ▓▓▓▓▓░░░░░ → Tue 02:00 PM │ 9.49 USD",
            line);
    }

    [Fact]
    public void ComponentsThatAreSwitchedOffOrUnknownLeaveNoSeparator()
    {
        var config = new StatuslineConfig { ShowBranch = false, ShowContext = false, ShowProgressBar = false, ShowResetTime = false };

        Assert.Equal("claude-usage │ Opus │ Usage: 29%", Plain(Render(config, Content(Value(29)))));
        Assert.Equal("Usage: 29%", Plain(Render(config, Content(Value(29), directory: null, model: ""))));
        // No branch outside a repository, and no profile name without a cache for the folder.
        Assert.Equal("claude-usage │ Opus │ Usage: 29%",
            Plain(Render(new StatuslineConfig { ShowContext = false, ShowProgressBar = false, ShowProfile = true }, Content(Value(29), branch: null))));
    }

    [Fact]
    public void NothingSelectedGivesAnEmptyLine() =>
        Assert.Equal("", Render(Only(_ => { }), Content(Value(29))));

    [Fact]
    public void UnknownUsageIsATildeNeverZero()
    {
        Assert.Equal($"{Yellow}Usage: ~{Reset}", Render(Only(c => c.ShowUsage = true), Content()));
        Assert.Equal($"{Yellow}~{Reset}", Render(Only(c => { c.ShowUsage = true; c.ShowUsageLabel = false; }), Content()));
    }

    [Fact]
    public void WeeklyAndExtraUsageNeedTheirValuesAndTheUsageSwitch()
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowWeekly = true; c.ShowExtraUsage = true; c.ShowProgressBar = false; });
        Assert.Equal("Usage: 10%", Plain(Render(config, Content(Value(10)))));

        config.ShowUsage = false;
        Assert.Equal("", Render(config, Content(Value(10), Value(45), new StatuslineCost(1, 2, "USD"))));
    }

    [Theory]
    [InlineData(0, "░░░░░░░░░░")]
    [InlineData(4, "░░░░░░░░░░")]
    [InlineData(5, "▓░░░░░░░░░")]
    [InlineData(29, "▓▓▓░░░░░░░")]
    [InlineData(95, "▓▓▓▓▓▓▓▓▓▓")]
    [InlineData(100, "▓▓▓▓▓▓▓▓▓▓")]
    [InlineData(140, "▓▓▓▓▓▓▓▓▓▓")]
    public void BarRoundsToTheNearestBlock(double percentage, string bar)
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowResetTime = false; c.ShowUsageLabel = false; });

        Assert.Equal($"{(int)percentage}% {bar}", Plain(Render(config, Content(Value(percentage)))));
    }

    [Theory]
    [InlineData(0, 22)]
    [InlineData(10, 22)]
    [InlineData(11, 28)]
    [InlineData(30, 34)]
    [InlineData(40, 100)]
    [InlineData(50, 142)]
    [InlineData(60, 178)]
    [InlineData(70, 172)]
    [InlineData(80, 166)]
    [InlineData(90, 160)]
    [InlineData(91, 124)]
    [InlineData(250, 124)]
    public void UsageColourFollowsUpstreamsTenLevels(double percentage, int colour)
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowProgressBar = false; });

        Assert.StartsWith($"\e[38;5;{colour}mUsage: ", Render(config, Content(Value(percentage))));
    }

    [Fact]
    public void PercentagesAreTruncatedLikeUpstreamsCache() =>
        Assert.Equal("Usage: 23%", Plain(Render(Only(c => { c.ShowUsage = true; c.ShowProgressBar = false; }), Content(Value(23.9)))));

    [Theory]
    [InlineData(299, 0)]      // one minute into the window
    [InlineData(150, 5)]      // half way
    [InlineData(1, 9)]        // the last minute: clamped into the bar
    public void PaceMarkerSitsAtTheElapsedShareOfTheWindow(int minutesLeft, int position)
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowResetTime = false; c.ShowUsageLabel = false; });

        var bar = Plain(Render(config, Content(Value(0, Now.AddMinutes(minutesLeft)))))["0% ".Length..];

        Assert.Equal(new string('░', position) + "┃" + new string('░', 9 - position), bar);
    }

    [Fact]
    public void NoPaceMarkerOutsideARunningWindow()
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowResetTime = false; });

        Assert.DoesNotContain("┃", Render(config, Content(Value(40))));
        Assert.DoesNotContain("┃", Render(config, Content(Value(40, Now.AddMinutes(-1)))));
        Assert.DoesNotContain("┃", Render(config, Content(Value(40, Now.AddHours(5)))));
        config.ShowPaceMarker = false;
        Assert.DoesNotContain("┃", Render(config, Content(Value(40, Now.AddHours(1)))));
    }

    [Theory]
    [InlineData(10, 34)]    // projected 20 %: comfortable
    [InlineData(30, 37)]    // 60 %: on track
    [InlineData(40, 178)]   // 80 %: warming
    [InlineData(48, 208)]   // 96 %: pressing
    [InlineData(55, 160)]   // 110 %: critical
    [InlineData(70, 135)]   // 140 %: runaway
    public void PaceTierColoursTheMarkerWhenSwitchedOn(double percentage, int colour)
    {
        var config = Only(c => { c.ShowUsage = true; c.PaceMarkerStepColors = true; });

        // Half the window has passed, so the projection is twice the usage.
        Assert.Contains($"\e[38;5;{colour}m┃{Reset}", Render(config, Content(Value(percentage, Now.AddMinutes(150)))));
    }

    [Fact]
    public void MarkerTakesTheUsageColourWithoutTiersAndEarlyInTheWindow()
    {
        var config = Only(c => c.ShowUsage = true);
        Assert.Contains($"\e[38;5;124m┃{Reset}\e[38;5;124m", Render(config, Content(Value(95, Now.AddMinutes(150)))));

        // Upstream judges the pace only after 9 minutes of the session.
        config.PaceMarkerStepColors = true;
        Assert.Contains($"\e[38;5;22m┃{Reset}", Render(config, Content(Value(5, Now.AddMinutes(300 - 8)))));
        Assert.Contains($"\e[38;5;135m┃{Reset}", Render(config, Content(Value(5, Now.AddMinutes(300 - 9)))));
    }

    [Theory]
    [InlineData(false, "→ Reset: 04:00 PM")]
    [InlineData(true, "→ Reset: 16:00")]
    public void ResetTimeIsLocalAndFollowsTheClockChoice(bool use24Hour, string expected)
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowProgressBar = false; c.Use24HourTime = use24Hour; });

        Assert.Equal($"Usage: 29% {expected}", Plain(Render(config, Content(Value(29, Now.AddHours(2))))));
    }

    [Fact]
    public void ResetTimeRoundsToTheNearestMinute()
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowProgressBar = false; c.Use24HourTime = true; c.ShowResetLabel = false; });

        Assert.Equal("Usage: 29% → 16:00", Plain(Render(config, Content(Value(29, Now.AddHours(2).AddSeconds(-15))))));
        Assert.Equal("Usage: 29% → 16:00", Plain(Render(config, Content(Value(29, Now.AddHours(2).AddSeconds(29))))));
        Assert.Equal("Usage: 29% → 16:01", Plain(Render(config, Content(Value(29, Now.AddHours(2).AddSeconds(30))))));
    }

    [Fact]
    public void ContextShowsPercentOrTokensWithUpstreamsColours()
    {
        var config = Only(c => c.ShowContext = true);
        Assert.Equal($"{Cyan}Ctx: 50%{Reset}", Render(config, Content(tokens: 100_000)));
        Assert.Equal($"{Yellow}Ctx: 75%{Reset}", Render(config, Content(tokens: 150_000)));
        Assert.Equal($"\e[38;5;160mCtx: 76%{Reset}", Render(config, Content(tokens: 153_000)));
        // Before the first response the context is empty, as Claude Code itself reports it.
        Assert.Equal("Ctx: 0%", Plain(Render(config, Content(tokens: 0))));
        Assert.Equal("", Render(config, Content(size: null)));

        config.ContextAsTokens = true;
        config.ShowContextLabel = false;
        Assert.Equal("96K", Plain(Render(config, Content())));
        Assert.Equal("999", Plain(Render(config, Content(tokens: 999))));
    }

    [Fact]
    public void WeeklyHasItsOwnWindowMarkerAndWeekdayTime()
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowWeekly = true; c.ShowProgressBar = false; c.ShowResetTime = false; c.Use24HourTime = true; });

        // 3.5 of 7 days have passed: the marker replaces the sixth block.
        var line = Plain(Render(config, Content(Value(10), Value(45.7, Now.AddDays(3.5)))));

        Assert.Equal("Usage: 10% │ Weekly: 45% ▓▓▓▓▓┃░░░░ → Wed 02:00", line);

        config.ShowWeeklyBar = false;
        config.ShowWeeklyResetTime = false;
        config.ShowWeeklyLabel = false;
        Assert.Equal("Usage: 10% │ 45%", Plain(Render(config, Content(Value(10), Value(45.7, Now.AddDays(3.5))))));
    }

    [Fact]
    public void WeeklyPaceIsJudgedAfterUpstreamsHalfPercentOfTheWeek()
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowWeekly = true; c.PaceMarkerStepColors = true; });
        var week = TimeSpan.FromDays(7);

        Assert.Contains($"\e[38;5;22m┃{Reset}", Render(config, Content(Value(10), Value(5, Now + week - TimeSpan.FromSeconds(3023)))));
        Assert.Contains($"\e[38;5;135m┃{Reset}", Render(config, Content(Value(10), Value(5, Now + week - TimeSpan.FromSeconds(3024)))));
    }

    [Theory]
    [InlineData(4.9, 50, "4.90 USD", 22)]
    [InlineData(49.5, 50, "49.50 USD", 124)]
    [InlineData(75, 50, "75.00 USD", 124)]
    public void ExtraUsageShowsTheAmountColouredByItsShareOfTheLimit(double used, double limit, string text, int colour)
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowExtraUsage = true; c.ShowProgressBar = false; });

        var line = Render(config, Content(Value(10), cost: new StatuslineCost((decimal)used, (decimal)limit, "USD")));

        Assert.EndsWith($"\e[38;5;{colour}m{text}{Reset}", line);
    }

    [Fact]
    public void StaleValuesAreLabelled()
    {
        var config = Only(c => { c.ShowUsage = true; c.ShowWeekly = true; c.ShowProgressBar = false; c.ShowWeeklyBar = false; c.Use24HourTime = true; });

        var line = Plain(Render(config, Content(
            Value(29, Now.AddHours(2), StatuslineSource.StaleCache), Value(45, Now.AddDays(3), StatuslineSource.Stdin))));

        Assert.Equal("Usage: 29% → Reset: 16:00 (stale) │ Weekly: 45% → Tue 14:00", line);
    }

    [Fact]
    public void MonochromeUsesNoColourAtAll()
    {
        var config = new StatuslineConfig { ColorMode = StatuslineConfig.Monochrome, PaceMarkerStepColors = true, ShowWeekly = true, ShowExtraUsage = true };

        var line = Render(config, Content(Value(70, Now.AddMinutes(150)), Value(45, Now.AddDays(3)), new StatuslineCost(1, 2, "USD")));

        Assert.DoesNotMatch(@"\e\[[1-9]", line);
        Assert.Contains("┃", line);
    }

    [Fact]
    public void SingleColourPaintsEverythingButKeepsThePaceTiers()
    {
        var config = new StatuslineConfig { ColorMode = StatuslineConfig.SingleColor, SingleColorHex = "#00BFFF", PaceMarkerStepColors = true };
        const string single = "\e[38;2;0;191;255m";

        var line = Render(config, Content(Value(70, Now.AddMinutes(150))));

        Assert.StartsWith($"{single}claude-usage{Reset}{single} │ {Reset}{single}⎇ main{Reset}", line);
        Assert.Contains($"{single}Usage: 70% ", line);
        Assert.Contains($"\e[38;5;135m┃{Reset}{single}", line);

        config.PaceMarkerStepColors = false;
        Assert.Contains($"{single}┃{Reset}{single}", Render(config, Content(Value(70, Now.AddMinutes(150)))));
    }

    [Fact]
    public void PerElementDefaultsLookLikeTheColouredModeAndOverridesApply()
    {
        var config = new StatuslineConfig { ColorMode = StatuslineConfig.PerElement, ShowProfile = true, ShowWeekly = true, ShowExtraUsage = true };
        var content = Content(Value(70, Now.AddMinutes(150)), Value(45), new StatuslineCost(1, 2, "USD"), "Work");

        var line = Render(config, content);
        Assert.StartsWith(
            "\e[38;2;0;0;238mclaude-usage\e[0m\e[38;2;128;128;128m │ \e[0m\e[38;2;0;187;0m⎇ main\e[0m\e[38;2;128;128;128m │ \e[0m"
            + "\e[38;2;187;187;0mOpus\e[0m\e[38;2;128;128;128m │ \e[0m\e[38;2;187;0;187mWork\e[0m\e[38;2;128;128;128m │ \e[0m\e[38;2;0;187;187mCtx: 48%\e[0m",
            line);
        Assert.Contains("\e[38;5;172mUsage: 70% ", line);
        Assert.Contains("\e[38;5;142mWeekly: 45%", line);

        config.ElementColors.UsageBaseHex = "#112233";
        config.ElementColors.PaceBaseHex = "#445566";
        config.ElementColors.WeeklyBaseHex = "#778899";
        config.ElementColors.ExtraUsageBaseHex = "#AABBCC";
        line = Render(config, content);
        Assert.Contains("\e[38;2;17;34;51mUsage: 70% ", line);
        // The script upstream installs ignores this colour; its preview shows it, and so does the port.
        Assert.Contains($"\e[38;2;68;85;102m┃{Reset}\e[38;2;17;34;51m", line);
        Assert.Contains("\e[38;2;119;136;153mWeekly: 45%", line);
        Assert.EndsWith($"\e[38;2;170;187;204m1.00 USD{Reset}", line);
    }

    [Fact]
    public void AnUnknownColourModeBehavesAsColoured() =>
        Assert.StartsWith($"{Blue}claude-usage{Reset}", Render(new StatuslineConfig { ColorMode = "rainbow" }, Content(Value(1))));

    [Theory]
    [InlineData("#00BFFF", "\e[38;2;0;191;255m")]
    [InlineData("00bfff", "\e[38;2;0;191;255m")]
    [InlineData("#FFF", "")]
    [InlineData("#GGGGGG", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void HexColoursBecomeTrueColourCodes(string? hex, string expected) =>
        Assert.Equal(expected, StatuslineRenderer.HexToAnsi(hex));

    [GeneratedRegex(@"\e\[[0-9;]*m")]
    private static partial Regex Ansi();
}
