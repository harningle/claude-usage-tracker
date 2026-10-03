using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Diagnostics;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.App.ViewModels;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.History;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Refresh;
using ClaudeUsageTracker.Core.Usage;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.ViewModels;

/// <summary>The popover's Milestone 3 parts: the API cards, provider capabilities and the banners of the new sources.</summary>
public class PopoverMilestoneThreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly Guid ProfileId = Guid.NewGuid();

    private static readonly ApiUsage Billing = new()
    {
        CurrentSpendCents = 1234,
        PrepaidCreditsCents = 8766,
        Currency = "USD",
        ResetsAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        ApiTokenCostCents = 230,
        DailyCostCents = new() { ["2026-10-01"] = 150.5m, ["2026-10-02"] = 79.5m },
        CostBySource =
        [
            new ApiCostSource { KeyId = "key-cli", KeyName = "CLI key", SourceType = ApiCostSourceType.Cli, TotalCents = 200, CostByModel = new() { ["Sonnet"] = 200 } },
            new ApiCostSource { KeyId = "key-api", KeyName = "Server", SourceType = ApiCostSourceType.Api, TotalCents = 30, CostByModel = new() { ["Haiku"] = 30 } },
        ],
        ApiCostByModel = new() { ["Sonnet"] = 200, ["Haiku"] = 30 },
    };

    private static ProfileUsageState State(UsageSourceKind? source, UsageData? usage = null, ApiUsage? api = null, LinkStatus link = LinkStatus.Ready) => new()
    {
        ProfileId = ProfileId,
        IsLinked = true,
        Source = source,
        Link = link,
        LastAttemptAt = Now,
        Usage = usage is null ? null : new CachedUsage { ProfileId = ProfileId, ObservedAt = Now, Source = "x", Usage = usage },
        Api = api is null ? null : new CachedApiUsage { ProfileId = ProfileId, ObservedAt = Now, OrganizationId = "org-api", Usage = api },
    };

    private static PopoverModel Build(ProfileUsageState state, Provider provider = Provider.Anthropic, MultiProfileDisplayConfig? multi = null) =>
        PopoverModelBuilder.Build(state, new IconConfiguration(), PopoverTimeDisplay.ResetTime, true, Now, TimeZoneInfo.Utc, English, provider, multi);

    [Fact]
    public void ConsoleBillingShowsTheCreditsAndCostCards()
    {
        var model = Build(State(UsageSourceKind.ClaudeCode, new UsageData { SessionPercentage = 10 }, Billing));

        Assert.Equal("12%", model.ApiCredits?.PercentText);
        Assert.Equal("$12.34", model.ApiCredits?.UsedText);
        Assert.Equal("$87.66", model.ApiCredits?.RemainingText);
        Assert.NotNull(model.ApiCredits?.ResetText);
        Assert.Equal("$2.30", model.ApiCost?.TotalText);
        // Upstream's chart spans the month to date; days without cost are zero, not left out.
        Assert.Equal(15, model.ApiCost?.Daily.Count);
        Assert.Equal([1.505m, 0.795m, 0m], model.ApiCost?.Daily.Take(3).Select(d => d.Amount));
        Assert.Equal(new DateOnly(2026, 10, 15), model.ApiCost?.Daily[^1].Date);
        // Several keys: the per-key breakdown replaces the flat model list, as upstream.
        Assert.Equal(["CLI key", "Server"], model.ApiCost?.Sources.Select(s => s.Name));
        Assert.Empty(model.ApiCost!.Models);
    }

    [Fact]
    public void BillingOnlyProfileShowsTheCardsWithoutUsageCards()
    {
        var model = Build(State(null, api: Billing));

        Assert.Empty(model.Rows);
        Assert.Null(model.Placeholder);
        Assert.NotNull(model.ApiCredits);
    }

    [Fact]
    public void FailedBillingKeepsTheLastValuesAndSaysWhy()
    {
        var model = Build(State(UsageSourceKind.ClaudeCode, new UsageData(), Billing) with { ApiFailure = FetchFailure.CredentialsRejected });

        Assert.Equal("$12.34", model.ApiCredits?.UsedText);
        Assert.Contains("session expired", model.ApiCredits?.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexShowsPlanAndCreditsAndNeverConsoleCardsOrModelRows()
    {
        var usage = new UsageData { SessionPercentage = 25, WeeklyPercentage = 50, OpusWeeklyPercentage = 5, PlanType = "chatgpt_plus", CreditsBalance = 12.5m };

        var model = Build(State(UsageSourceKind.Codex, usage, Billing), Provider.Codex);

        Assert.Equal("Chatgpt Plus", model.Plan);
        Assert.Equal("Credits: 12.50", model.Credits);
        Assert.Null(model.ApiCredits);
        Assert.DoesNotContain(model.Rows, r => r.Title == "Opus Usage");
    }

    [Fact]
    public void MultiProfileOptionsReplaceTheProfilesOwnInThePopover()
    {
        var usage = new UsageData { SessionPercentage = 40 };

        var model = Build(State(UsageSourceKind.ClaudeCode, usage), multi: new MultiProfileDisplayConfig { ShowRemainingPercentage = true });

        Assert.Equal("60%", model.Rows[0].PercentText);
    }

    [Theory]
    [InlineData(UsageSourceKind.ClaudeCode, LinkStatus.LoginRejected, "login was rejected")]
    [InlineData(UsageSourceKind.ClaudeCode, LinkStatus.RefreshFailed, "renew the login")]
    [InlineData(UsageSourceKind.ClaudeAi, LinkStatus.NotSignedIn, "Credentials expired")]
    [InlineData(UsageSourceKind.ClaudeAi, LinkStatus.AccountChanged, "can't see this organization")]
    [InlineData(UsageSourceKind.Codex, LinkStatus.WaitingForCliRefresh, "Codex CLI to refresh")]
    [InlineData(UsageSourceKind.Codex, LinkStatus.LoginRejected, "Codex login expired")]
    [InlineData(UsageSourceKind.Codex, LinkStatus.NotSignedIn, "No Codex credentials found")]
    public void EachSourceExplainsItsLoginInItsOwnWords(UsageSourceKind source, LinkStatus link, string expected)
    {
        var banner = PopoverModelBuilder.Banner(State(source, link: link), Now, true, TimeZoneInfo.Utc, English);

        Assert.Contains(expected, banner?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingHandBackIsAnnouncedWhileUsageIsShown()
    {
        var state = State(UsageSourceKind.ClaudeCode, new UsageData { SessionPercentage = 10 }) with { Notice = LinkNotice.HandBackPending };

        var model = Build(state);

        Assert.NotEmpty(model.Rows);
        Assert.Contains("could not write it back", model.Banner?.Message, StringComparison.Ordinal);
    }
}

/// <summary>The History page: chart window, navigation and an export that holds exactly the stored history.</summary>
public sealed class HistoryViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _temp.Dispose();

    private UsageHistoryStore Store => new(Path.Combine(_temp.Path, "history"));

    private (HistoryViewModel Model, ProfileSettings Profile) Create()
    {
        var profile = new ProfileSettings { Name = "Work", Console = new ConsoleLink() };
        var history = new UsageHistoryData();
        for (var hours = 1; hours <= 48; hours++)
        {
            history.Snapshots.Add(new UsageSnapshot
            {
                Timestamp = Now.AddHours(-hours), ResetType = ResetType.SessionReset, TriggeringResetTime = Now.AddHours(-hours), SessionPercentage = hours,
            });
        }

        history.Snapshots.Add(new UsageSnapshot
        {
            Timestamp = Now.AddDays(-14), ResetType = ResetType.BillingCycle, TriggeringResetTime = Now.AddDays(-14),
            ApiSpendCents = 4200, ApiPrepaidCreditsCents = 5000, ApiCurrency = "USD",
        });
        Store.Save(profile.Id, history);
        var model = new HistoryViewModel(new UsageHistoryService(Store, _time), _time, () => true);
        model.Load(profile);
        return (model, profile);
    }

    [Fact]
    public void ChartShowsTheChosenWindowAndMovesByHalfAWindow()
    {
        var (model, _) = Create();

        Assert.Equal(24, model.Session.Count);
        Assert.True(model.CanGoBack);
        Assert.False(model.CanGoForward);

        model.Back();
        Assert.Equal(Now.AddHours(-12), model.End);
        Assert.True(model.CanGoForward);

        model.Scale5Hours = true;
        Assert.Equal(Now, model.End);
        Assert.Equal(5, model.Session.Count);
    }

    [Fact]
    public void BillingChartShowsEachCycleAsSpend()
    {
        var (model, _) = Create();

        var bar = Assert.Single(model.Billing);
        Assert.Equal(42, bar.Value);
        Assert.True(model.ShowBilling);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportHoldsExactlyTheStoredHistory(bool csv)
    {
        var (model, profile) = Create();
        var path = Path.Combine(_temp.Path, csv ? "export.csv" : "export.json");

        var count = model.Export(path, csv);

        var stored = Store.Load(profile.Id);
        Assert.Equal(49, count);
        Assert.Equal(csv ? UsageHistoryCsv.Export(stored, TimeZoneInfo.Local) : UsageHistoryJson.Export(stored), File.ReadAllText(path));
    }

    [Fact]
    public void FailedRecordingIsShownOnThePage()
    {
        var (model, _) = Create();
        Assert.Null(model.Problem);

        model.RecordingError = "The history file could not be saved.";

        Assert.Equal("The history file could not be saved.", model.Problem);
    }

    [Fact]
    public void DamagedHistoryIsShownAsAnErrorAndNotReplaced()
    {
        var profile = new ProfileSettings { Name = "Work" };
        Directory.CreateDirectory(Path.Combine(_temp.Path, "history"));
        File.WriteAllText(Store.PathFor(profile.Id), "{ damaged");
        var model = new HistoryViewModel(new UsageHistoryService(Store, _time), _time, () => true);

        model.Load(profile);

        Assert.NotNull(model.Error);
        Assert.Equal(model.Error, model.Problem);
        Assert.Empty(model.Session);
        Assert.Equal("{ damaged", File.ReadAllText(Store.PathFor(profile.Id)));
    }
}

/// <summary>Fixture mode answers every request from files and never names a real endpoint it does not serve.</summary>
public class FixtureResponseTests
{
    [Theory]
    [InlineData("https://api.anthropic.com/api/oauth/usage", "usage")]
    [InlineData("https://status.claude.com/api/v2/status.json", "status")]
    [InlineData("https://platform.claude.com/v1/oauth/token", "token")]
    [InlineData("https://auth.openai.com/oauth/token", "codex-token")]
    [InlineData("https://chatgpt.com/backend-api/wham/usage", "codex-usage")]
    [InlineData("https://platform.claude.com/api/organizations", "console-organizations")]
    [InlineData("https://platform.claude.com/api/organizations/org-1/current_spend", "console-current-spend")]
    [InlineData("https://platform.claude.com/api/organizations/org-1/prepaid/credits", "console-prepaid-credits")]
    [InlineData("https://platform.claude.com/api/organizations/org-1/workspaces/default/usage_cost?x=1", "console-usage-cost")]
    [InlineData("https://platform.claude.com/api/organizations/org-1/api_keys?status=active", "console-api-keys")]
    public void RequestsMapToResponseFiles(string url, string expected) =>
        Assert.Equal(expected, FixtureMode.ResponseName(new Uri(url)));

    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages")]
    [InlineData("https://platform.claude.com/api/organizations/org-1/members")]
    public void OtherRequestsAreNotServed(string url) => Assert.Null(FixtureMode.ResponseName(new Uri(url)));
}

/// <summary>Codex usage: the CLI's file is read through; pasted credentials are renewed once and never resent.</summary>
public sealed class CodexUsageSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Pasted = """{"tokens":{"access_token":"codex-at","refresh_token":"codex-rt","account_id":"chatgpt-1"},"last_refresh":"2026-09-30T12:00:00Z"}""";
    private const string UsageBody = """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000,"reset_at":1790000000}}}""";

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Guid _profileId = Guid.NewGuid();

    public void Dispose() => _temp.Dispose();

    // Pasted credentials last renewed more than eight days ago, so each resolution renews them.
    private const string Stale = """{"tokens":{"access_token":"codex-at","refresh_token":"codex-rt","account_id":"chatgpt-1"},"last_refresh":"2026-09-01T12:00:00Z"}""";

    private (CodexUsageSource Source, LinkHarness Links, ScriptedHandler Usage) Create(
        ScriptedHandler tokens, Func<string?, HttpResponseMessage> usage, string pasted = Pasted)
    {
        var links = new LinkHarness(_temp.Path, Path.Combine(_temp.Path, "home"), _time, tokens);
        var handler = new ScriptedHandler((request, _) => Task.FromResult(usage(request.Headers.Authorization?.Parameter)));
        var source = new CodexUsageSource(links.Secrets, links.Lock, links.Tokens,
            new CodexClient(new HttpClient(handler), _time, new RequestLog(_temp.Path)), _time, () => Path.Combine(_temp.Path, "home", ".codex"));
        var bundle = links.Secrets.Load(_profileId);
        bundle.CodexAuthJson = pasted;
        links.Secrets.Save(_profileId, bundle);
        return (source, links, handler);
    }

    private string SecretFile => new SecretStore(Path.Combine(_temp.Path, "secrets")).PathFor(_profileId);

    [Fact]
    public async Task RenewedCredentialsThatCouldNotBeSavedAreSavedLater()
    {
        var (source, links, _) = Create(
            LinkHarness.TokenEndpoint(() => LinkHarness.Json(HttpStatusCode.OK, """{"access_token":"codex-at-2","refresh_token":"codex-rt-2"}""")),
            _ => LinkHarness.Json(HttpStatusCode.OK, UsageBody), Stale);
        var watch = Watch();

        SourceCredentials first;
        using (new FileStream(SecretFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            first = await source.ResolveAsync(watch, CancellationToken.None);
        }

        // The renewed login is used and the failure is shown; once the file is free it is saved.
        Assert.Equal("codex-at-2", ((ClaudeUsageTracker.Core.Providers.Codex.CodexCredentials)first.Secret!).AccessToken);
        Assert.NotNull(first.StorageError);
        var second = await source.ResolveAsync(watch, CancellationToken.None);

        Assert.Null(second.StorageError);
        Assert.Contains("codex-rt-2", links.Secrets.Load(_profileId).CodexAuthJson, StringComparison.Ordinal);
        Assert.Equal(1, links.TokenRequests);
    }

    [Fact]
    public async Task CredentialsPastedAfterAnUnsavedRenewalAreNotOverwritten()
    {
        var (source, links, _) = Create(
            LinkHarness.TokenEndpoint(() => LinkHarness.Json(HttpStatusCode.OK, """{"access_token":"codex-at-2","refresh_token":"codex-rt-2"}""")),
            _ => LinkHarness.Json(HttpStatusCode.OK, UsageBody), Stale);
        var watch = Watch();
        using (new FileStream(SecretFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await source.ResolveAsync(watch, CancellationToken.None);
        }

        // The user pastes another account's credentials before the renewed ones could be saved.
        const string replacement = """{"tokens":{"access_token":"other-at","refresh_token":"other-rt","account_id":"chatgpt-2"},"last_refresh":"2026-09-30T12:00:00Z"}""";
        var bundle = links.Secrets.Load(_profileId);
        bundle.CodexAuthJson = replacement;
        links.Secrets.Save(_profileId, bundle);

        var resolved = await source.ResolveAsync(watch, CancellationToken.None);

        Assert.Equal("other-at", ((ClaudeUsageTracker.Core.Providers.Codex.CodexCredentials)resolved.Secret!).AccessToken);
        Assert.Null(resolved.StorageError);
        Assert.Equal(replacement, links.Secrets.Load(_profileId).CodexAuthJson);
        Assert.Equal(1, links.TokenRequests);
    }

    [Fact]
    public async Task RejectedRefreshTokenIsNotSentAgainUntilTheCredentialsChange()
    {
        var (source, links, _) = Create(
            LinkHarness.TokenEndpoint(() => LinkHarness.Json(HttpStatusCode.BadRequest, """{"error":"refresh_token_reused"}""")),
            _ => LinkHarness.Json(HttpStatusCode.OK, UsageBody), Stale);
        var watch = Watch();

        Assert.Equal(LinkStatus.LoginRejected, (await source.ResolveAsync(watch, CancellationToken.None)).Status);
        Assert.Equal(LinkStatus.LoginRejected, (await source.ResolveAsync(watch, CancellationToken.None)).Status);

        Assert.Equal(1, links.TokenRequests);
    }

    [Fact]
    public async Task FailedRenewalIsTriedAgainOnlyAfterFiveMinutes()
    {
        var (source, links, _) = Create(
            LinkHarness.TokenEndpoint(() => LinkHarness.Json(HttpStatusCode.ServiceUnavailable, "{}")),
            _ => LinkHarness.Json(HttpStatusCode.OK, UsageBody), Stale);
        var watch = Watch();

        // A failed renewal still leaves a token worth trying.
        Assert.Equal(LinkStatus.Ready, (await source.ResolveAsync(watch, CancellationToken.None)).Status);
        await source.ResolveAsync(watch, CancellationToken.None);
        Assert.Equal(1, links.TokenRequests);

        _time.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        await source.ResolveAsync(watch, CancellationToken.None);
        Assert.Equal(2, links.TokenRequests);

        // The second refusal doubles the wait.
        _time.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        await source.ResolveAsync(watch, CancellationToken.None);
        Assert.Equal(2, links.TokenRequests);

        _time.Now += TimeSpan.FromMinutes(5);
        await source.ResolveAsync(watch, CancellationToken.None);
        Assert.Equal(3, links.TokenRequests);
    }

    private ProfileWatch Watch() => ProfileWatch.From(new ProfileSettings
    {
        Id = _profileId,
        Name = "Codex",
        Provider = Provider.Codex.RawValue(),
        Codex = new CodexLink { Source = CodexLink.ManualSource, AccountId = "chatgpt-1" },
    });

    [Fact]
    public async Task RejectedPastedTokenIsRenewedOnceSavedAndRetried()
    {
        var (source, links, usage) = Create(
            LinkHarness.TokenEndpoint(() => LinkHarness.Json(HttpStatusCode.OK, """{"access_token":"codex-at-2","refresh_token":"codex-rt-2"}""")),
            token => token == "codex-at" ? LinkHarness.Json(HttpStatusCode.Unauthorized, "{}") : LinkHarness.Json(HttpStatusCode.OK, UsageBody));
        var watch = Watch();

        var credentials = await source.ResolveAsync(watch, CancellationToken.None);
        var fetch = await source.FetchAsync(watch, credentials, null, CancellationToken.None);

        Assert.IsType<FetchSucceeded>(fetch.Result);
        Assert.Equal(1, links.TokenRequests);
        Assert.Equal(2, usage.Requests.Count);
        Assert.Contains("codex-rt-2", links.Secrets.Load(_profileId).CodexAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredPastedLoginIsRejectedWithoutLooping()
    {
        var (source, links, _) = Create(
            LinkHarness.TokenEndpoint(() => LinkHarness.Json(HttpStatusCode.BadRequest, """{"error":"refresh_token_reused"}""")),
            _ => LinkHarness.Json(HttpStatusCode.Unauthorized, "{}"));
        var watch = Watch();

        var credentials = await source.ResolveAsync(watch, CancellationToken.None);
        var fetch = await source.FetchAsync(watch, credentials, null, CancellationToken.None);

        Assert.Equal(LinkStatus.LoginRejected, fetch.Status);
        Assert.Equal(1, links.TokenRequests);
        Assert.Contains("codex-rt\"", links.Secrets.Load(_profileId).CodexAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsedRefreshTokenIsNeverSentAgain()
    {
        var bodies = new List<string>();
        var (source, _, _) = Create(
            new ScriptedHandler(async (request, _) =>
            {
                bodies.Add(await request.Content!.ReadAsStringAsync());
                return bodies.Count == 1
                    ? LinkHarness.Json(HttpStatusCode.OK, """{"access_token":"codex-at-2","refresh_token":"codex-rt-2"}""")
                    : LinkHarness.Json(HttpStatusCode.OK, """{"access_token":"codex-at-3","refresh_token":"codex-rt-3"}""");
            }),
            _ => LinkHarness.Json(HttpStatusCode.Unauthorized, "{}"));
        var watch = Watch();

        // The renewed credentials are rejected too; the next check must not send codex-rt again.
        await source.FetchAsync(watch, await source.ResolveAsync(watch, CancellationToken.None), null, CancellationToken.None);
        await source.FetchAsync(watch, await source.ResolveAsync(watch, CancellationToken.None), null, CancellationToken.None);

        Assert.Equal(2, bodies.Count);
        Assert.Single(bodies, b => b.Contains("\"codex-rt\"", StringComparison.Ordinal));
    }
}
