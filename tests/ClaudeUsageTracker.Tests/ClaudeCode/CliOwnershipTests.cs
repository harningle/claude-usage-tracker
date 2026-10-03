using System.IO;
using System.Net;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Interop;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.App.Usage;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>
/// Who renews a profile's Claude Code login (plan.md section 2): the guarded idle takeover with write-back,
/// app ownership once no known folder holds the account, and the rules that keep a refresh token from ever
/// being sent twice or a newer CLI login from being replaced. Token answers are scripted; every login,
/// folder and process is synthetic.
/// </summary>
public sealed class CliOwnershipTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    public CliOwnershipTests() => Directory.CreateDirectory(DefaultDirectory);

    public void Dispose() => _temp.Dispose();

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string DefaultDirectory => Path.Combine(UserProfile, ".claude");
    private string DefaultFile => Path.Combine(DefaultDirectory, ".credentials.json");
    private CliConfigDirectory Default => CliStore.Inventory(UserProfile, null, [])[0];

    private LinkHarness Harness(ScriptedHandler? tokens = null) => new(_temp.Path, UserProfile, _time, tokens);

    private void Write(string file, string access, string refresh, DateTimeOffset expires)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, LinkHarness.Credentials(access, refresh, expires));
    }

    /// <summary>Links a profile to the default folder's account and lets its login expire.</summary>
    private async Task<(Guid ProfileId, CliLink Link)> LinkExpired(LinkHarness harness, Guid? profileId = null)
    {
        Write(DefaultFile, "at-1", "rt-1", Now.AddHours(8));
        File.WriteAllText(Path.Combine(UserProfile, ".claude.json"), LinkHarness.Account("acc-1", "user@example.com"));
        var id = profileId ?? Guid.NewGuid();
        var snapshot = harness.Service.Read(Default);
        var link = harness.Service.CreateLink(snapshot);
        await harness.Service.WriteMirrorAsync(id, snapshot, CancellationToken.None);
        _time.Now = Now.AddHours(9);
        return (id, link);
    }

    private Task<LinkCheck> Check(LinkHarness harness, Guid profileId, CliLink link) =>
        harness.Service.CheckAsync(profileId, link, CancellationToken.None);

    [Fact]
    public async Task ExpiredLoginIsRenewedWhenNoClaudeCodeRunsAndHandedBack()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);

        var check = await Check(harness, profileId, link);

        Assert.Equal(LinkStatus.Ready, check.Status);
        Assert.Equal("at-2", check.Credentials?.AccessToken);
        Assert.Null(check.Notice);
        Assert.Null(check.MirrorError);
        var request = Assert.Single(harness.TokenHandler.Requests);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", request.RequestUri?.ToString());
        Assert.Equal(System.Net.Http.HttpMethod.Post, request.Method);
        // Claude Code's file now holds the renewed login, with its other members untouched.
        var file = File.ReadAllText(DefaultFile);
        Assert.Contains("\"at-2\"", file, StringComparison.Ordinal);
        Assert.Contains("\"rt-2\"", file, StringComparison.Ordinal);
        Assert.Contains("mcp-secret", file, StringComparison.Ordinal);
        var copy = harness.Secrets.Load(profileId).Cli;
        Assert.Contains("at-2", copy?.OAuthJson, StringComparison.Ordinal);
        Assert.Null(copy?.Pending);
        Assert.Equal(CredentialOwnership.CliLinked, copy?.Ownership);
    }

    [Fact]
    public async Task RenewedLoginIsHandedBackToEveryFolderHoldingTheSameToken()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var copied = Path.Combine(_temp.Path, "copied-claude");
        harness.Known.Add(copied);
        var (profileId, link) = await LinkExpired(harness);
        Write(Path.Combine(copied, ".credentials.json"), "at-1", "rt-1", Now.AddHours(8));

        await Check(harness, profileId, link);

        Assert.Contains("\"at-2\"", File.ReadAllText(Path.Combine(copied, ".credentials.json")), StringComparison.Ordinal);
        Assert.Equal(1, harness.TokenRequests);
    }

    [Fact]
    public async Task ResponseWithoutARotatedTokenKeepsTheOldRefreshToken()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", null)));
        var (profileId, link) = await LinkExpired(harness);

        await Check(harness, profileId, link);

        Assert.Contains("\"refreshToken\":\"rt-1\"", File.ReadAllText(DefaultFile).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningOrUnreadableProcessesBlockTheRenewal()
    {
        var harness = Harness();
        var (profileId, link) = await LinkExpired(harness);
        // A claude.exe whose owner and start time could not be read might be using the folder.
        harness.Processes.Add(new ProcessEntry(77, 1, CliProcessGuard.ImageName, null, null, null));

        var check = await Check(harness, profileId, link);

        Assert.Equal(LinkStatus.WaitingForCliRefresh, check.Status);
        Assert.Equal(0, harness.TokenRequests);
        Assert.Contains("\"at-1\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginUsedElsewhereIsNeverRenewed()
    {
        var harness = Harness();
        var (profileId, link) = await LinkExpired(harness);
        link.UsedElsewhere = true;

        Assert.Equal(LinkStatus.WaitingForCliRefresh, (await Check(harness, profileId, link)).Status);
        Assert.Equal(0, harness.TokenRequests);
    }

    [Fact]
    public async Task UnreadableKnownFolderBlocksTheRenewal()
    {
        var harness = Harness();
        var broken = Path.Combine(_temp.Path, "broken-claude");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, ".credentials.json"), "{ torn");
        harness.Known.Add(broken);
        var (profileId, link) = await LinkExpired(harness);

        var check = await Check(harness, profileId, link);

        Assert.Equal(LinkStatus.WaitingForCliRefresh, check.Status);
        Assert.Equal(LinkNotice.OtherFolderUnreadable, check.Notice);
        Assert.Equal(0, harness.TokenRequests);
    }

    [Fact]
    public async Task RejectedRefreshTokenIsReportedAndNeverSentAgain()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(LinkHarness.InvalidGrant));
        var (profileId, link) = await LinkExpired(harness);

        Assert.Equal(LinkStatus.LoginRejected, (await Check(harness, profileId, link)).Status);
        Assert.Equal(LinkStatus.LoginRejected, (await Check(harness, profileId, link)).Status);

        Assert.Equal(1, harness.TokenRequests);
        Assert.Contains("\"at-1\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRenewalIsRetriedOnlyAfterFiveMinutes()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(
            () => LinkHarness.Json(HttpStatusCode.ServiceUnavailable, "{}"), () => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);

        var failed = await Check(harness, profileId, link);
        Assert.Equal(LinkStatus.WaitingForCliRefresh, failed.Status);
        Assert.Equal(LinkNotice.RenewalFailed, failed.Notice);
        await Check(harness, profileId, link);
        Assert.Equal(1, harness.TokenRequests);

        _time.Now += RenewalBackoff.FirstDelay + TimeSpan.FromSeconds(1);
        Assert.Equal(LinkStatus.Ready, (await Check(harness, profileId, link)).Status);
        Assert.Equal(2, harness.TokenRequests);
    }

    [Fact]
    public async Task RenewalsTheServiceKeepsRefusingWaitLongerEachTime()
    {
        // As on 2026-10-03: the service answers every renewal with a rate limit.
        var harness = Harness(LinkHarness.TokenEndpoint(
            () => LinkHarness.Json(HttpStatusCode.TooManyRequests, "{}"), () => LinkHarness.Json(HttpStatusCode.TooManyRequests, "{}"),
            () => LinkHarness.Grant("at-2", "rt-2"), () => LinkHarness.Json(HttpStatusCode.TooManyRequests, "{}")));
        var (profileId, link) = await LinkExpired(harness);
        await Check(harness, profileId, link);

        _time.Now += RenewalBackoff.FirstDelay + TimeSpan.FromSeconds(1);
        await Check(harness, profileId, link);
        Assert.Equal(2, harness.TokenRequests);

        // The second refusal doubles the wait.
        _time.Now += RenewalBackoff.FirstDelay + TimeSpan.FromSeconds(1);
        Assert.Equal(LinkNotice.RenewalFailed, (await Check(harness, profileId, link)).Notice);
        Assert.Equal(2, harness.TokenRequests);

        _time.Now += RenewalBackoff.FirstDelay;
        Assert.Equal(LinkStatus.Ready, (await Check(harness, profileId, link)).Status);
        Assert.Equal(3, harness.TokenRequests);

        // That success starts over: when the renewed login expires and is refused, the wait is the first one again.
        _time.Now += TimeSpan.FromHours(9);
        await Check(harness, profileId, link);
        Assert.Equal(4, harness.TokenRequests);
        _time.Now += RenewalBackoff.FirstDelay + TimeSpan.FromSeconds(1);
        await Check(harness, profileId, link);
        Assert.Equal(5, harness.TokenRequests);
    }

    [Fact]
    public async Task ARenewalWithoutAnAnswerIsRetriedAfterFiveMinutesEachTime()
    {
        var harness = Harness(new ScriptedHandler((_, _) => throw new HttpRequestException("offline")));
        var (profileId, link) = await LinkExpired(harness);
        await Check(harness, profileId, link);

        foreach (var expected in new[] { 2, 3, 4 })
        {
            _time.Now += RenewalBackoff.FirstDelay + TimeSpan.FromSeconds(1);
            Assert.Equal(LinkNotice.RenewalFailed, (await Check(harness, profileId, link)).Notice);
            Assert.Equal(expected, harness.TokenRequests);
        }
    }

    [Fact]
    public async Task HandBackThatCannotBeWrittenIsKeptPendingAndTheUsedTokenIsNeverResent()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);

        LinkCheck check;
        using (new FileStream(DefaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Claude Code's file is held open, so it cannot be replaced.
            check = await Check(harness, profileId, link);
            Assert.Equal(LinkStatus.Ready, check.Status);
            Assert.Equal("at-2", check.Credentials?.AccessToken);
            Assert.Equal(LinkNotice.HandBackPending, check.Notice);
            Assert.NotNull(harness.Secrets.Load(profileId).Cli?.Pending);

            // The folder still holds the used token: it is polled with the renewed copy, never renewed again.
            var again = await Check(harness, profileId, link);
            Assert.Equal("at-2", again.Credentials?.AccessToken);
            Assert.Equal(1, harness.TokenRequests);
        }

        // After a restart the pending hand-back is finished from the saved copy.
        var restarted = Harness();
        var settled = await Check(restarted, profileId, link);

        Assert.Equal(LinkStatus.Ready, settled.Status);
        Assert.Null(settled.Notice);
        Assert.Contains("\"at-2\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);
        Assert.Null(restarted.Secrets.Load(profileId).Cli?.Pending);
        Assert.Equal(0, restarted.TokenRequests);
    }

    [Fact]
    public async Task NewerCliLoginIsNeverReplacedByAPendingHandBack()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);
        using (new FileStream(DefaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Check(harness, profileId, link);
        }

        // Meanwhile Claude Code signed in again by itself.
        Write(DefaultFile, "at-9", "rt-9", Now.AddHours(17));
        var check = await Check(harness, profileId, link);

        Assert.Equal(LinkStatus.Ready, check.Status);
        Assert.Equal("at-9", check.Credentials?.AccessToken);
        Assert.Contains("\"at-9\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);
        var copy = harness.Secrets.Load(profileId).Cli;
        Assert.Null(copy?.Pending);
        Assert.Contains("at-9", copy?.OAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedOutFolderIsNotRecreatedByAHandBack()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);
        using (new FileStream(DefaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Check(harness, profileId, link);
        }

        // Claude Code signed out meanwhile.
        File.Delete(DefaultFile);
        await Check(harness, profileId, link);

        Assert.False(File.Exists(DefaultFile));
        Assert.Null(harness.Secrets.Load(profileId).Cli?.Pending);
    }

    [Fact]
    public async Task TwoProfilesOfTheSameLoginRenewItOnlyOnce()
    {
        var release = new TaskCompletionSource();
        var tokens = new ScriptedHandler(async (_, _) =>
        {
            await release.Task;
            return LinkHarness.Grant("at-2", "rt-2");
        });
        var harness = Harness(tokens);
        var (first, link) = await LinkExpired(harness);
        var second = Guid.NewGuid();
        _time.Now = Now;
        await harness.Service.WriteMirrorAsync(second, harness.Service.Read(Default), CancellationToken.None);
        _time.Now = Now.AddHours(9);

        var checks = new[] { Check(harness, first, link), Check(harness, second, link) };
        await Task.Delay(200, TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(checks);

        Assert.Equal(1, harness.TokenRequests);
        Assert.Equal(LinkStatus.Ready, (await Check(harness, second, link)).Status);
        Assert.Equal("at-2", (await Check(harness, second, link)).Credentials?.AccessToken);
    }

    [Fact]
    public async Task ConcurrentTakeoverNeverReusesATokenWhenTheHandBackFails()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var tokens = new ScriptedHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task;
                return LinkHarness.Grant("at-2", "rt-2");
            }

            return LinkHarness.Grant("at-3", "rt-3");
        });
        var harness = Harness(tokens);
        var (first, link) = await LinkExpired(harness);
        var second = Guid.NewGuid();
        _time.Now = Now;
        await harness.Service.WriteMirrorAsync(second, harness.Service.Read(Default), CancellationToken.None);
        _time.Now = Now.AddHours(9);

        using (new FileStream(DefaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Claude Code's file cannot be replaced, so it still holds the token after the first renewal.
            var checks = new[] { Check(harness, first, link), Check(harness, second, link) };
            await Task.Delay(200, TestContext.Current.CancellationToken);
            release.SetResult();
            await Task.WhenAll(checks);
        }

        Assert.Equal(1, harness.TokenRequests);
    }

    [Fact]
    public async Task ProfilesOwningTheSameAccountShareOneRenewal()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (first, link) = await LinkExpired(harness);
        var second = Guid.NewGuid();
        _time.Now = Now;
        await harness.Service.WriteMirrorAsync(second, harness.Service.Read(Default), CancellationToken.None);
        _time.Now = Now.AddHours(1);
        // Claude Code moved to another account, so both profiles' copies become the tracker's own.
        Write(DefaultFile, "at-other", "rt-other", Now.AddHours(20));
        File.WriteAllText(Path.Combine(UserProfile, ".claude.json"), LinkHarness.Account("acc-2", "other@example.com"));
        Assert.Equal(CredentialOwnership.AppOwned, (await Check(harness, first, link)).Ownership);
        Assert.Equal(CredentialOwnership.AppOwned, (await Check(harness, second, link)).Ownership);

        _time.Now = Now.AddHours(9);
        Assert.Equal("at-2", (await Check(harness, first, link)).Credentials?.AccessToken);
        var other = await Check(harness, second, link);
        // And after a restart, when nothing is remembered in memory.
        var restarted = Harness();
        var afterRestart = await Check(restarted, second, link);

        Assert.Equal("at-2", other.Credentials?.AccessToken);
        Assert.Equal("at-2", afterRestart.Credentials?.AccessToken);
        Assert.Equal(1, harness.TokenRequests);
        Assert.Equal(0, restarted.TokenRequests);
    }

    [Fact]
    public async Task RenewalReachesAProfileThatHasNotYetSeenTheLoginBecomeTheTrackersOwn()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (first, link) = await LinkExpired(harness);
        var second = Guid.NewGuid();
        _time.Now = Now;
        await harness.Service.WriteMirrorAsync(second, harness.Service.Read(Default), CancellationToken.None);
        _time.Now = Now.AddHours(9);
        // Claude Code signed out. Only the first profile is checked, so only its copy becomes the tracker's own
        // and is renewed; the second profile's copy is still marked as Claude Code's.
        File.Delete(DefaultFile);
        Assert.Equal("at-2", (await Check(harness, first, link)).Credentials?.AccessToken);

        var other = await Check(harness, second, link);
        // And after a restart, when nothing is remembered in memory.
        var restarted = Harness();
        var afterRestart = await Check(restarted, second, link);

        Assert.Equal("at-2", other.Credentials?.AccessToken);
        Assert.Equal("at-2", afterRestart.Credentials?.AccessToken);
        Assert.Equal(1, harness.TokenRequests);
        Assert.Equal(0, restarted.TokenRequests);
    }

    [Fact]
    public async Task NoFurtherRenewalWhileAFolderOfTheLineageStillAwaitsItsHandBack()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2"), () => LinkHarness.Grant("at-3", "rt-3")));
        var copied = Path.Combine(_temp.Path, "copied-claude");
        var copiedFile = Path.Combine(copied, ".credentials.json");
        harness.Known.Add(copied);
        var (profileId, link) = await LinkExpired(harness);
        Write(copiedFile, "at-1", "rt-1", Now.AddHours(8));

        using (new FileStream(copiedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The linked folder gets the renewed login; the other folder cannot be written yet.
            Assert.Equal(LinkNotice.HandBackPending, (await Check(harness, profileId, link)).Notice);
            Assert.Contains("\"at-2\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);

            // The renewed login expires before the other folder is free: it is not renewed again, and that
            // folder is not forgotten.
            _time.Now = Now.AddHours(18);
            var waiting = await Check(harness, profileId, link);

            Assert.Equal(LinkStatus.WaitingForCliRefresh, waiting.Status);
            Assert.Equal(LinkNotice.HandBackPending, waiting.Notice);
            Assert.Equal(1, harness.TokenRequests);
            Assert.Single(harness.Secrets.Load(profileId).Cli?.Pending?.Targets ?? []);
        }

        // Once it is free it gets the renewed login, and from then on the whole lineage is renewed together.
        var check = await Check(harness, profileId, link);

        Assert.Equal("at-3", check.Credentials?.AccessToken);
        Assert.Contains("\"at-3\"", File.ReadAllText(copiedFile), StringComparison.Ordinal);
        Assert.Contains("\"at-3\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);
        Assert.Equal(2, harness.TokenRequests);
    }

    [Fact]
    public async Task PendingHandBackPassesOnALoginClaudeCodeRenewedMeanwhile()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var copied = Path.Combine(_temp.Path, "copied-claude");
        var copiedFile = Path.Combine(copied, ".credentials.json");
        harness.Known.Add(copied);
        var (profileId, link) = await LinkExpired(harness);
        Write(copiedFile, "at-1", "rt-1", Now.AddHours(8));
        using (new FileStream(copiedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Check(harness, profileId, link);
        }

        // Before the other folder gets its hand-back, Claude Code renews the linked folder's login by itself,
        // using up rt-2.
        Write(DefaultFile, "at-3", "rt-3", Now.AddHours(17));
        await Check(harness, profileId, link);
        var check = await Check(harness, profileId, link);

        // The other folder gets the newest login, never the token Claude Code has already used.
        Assert.Equal("at-3", check.Credentials?.AccessToken);
        Assert.Contains("\"rt-3\"", File.ReadAllText(copiedFile), StringComparison.Ordinal);
        Assert.Null(harness.Secrets.Load(profileId).Cli?.Pending);
        Assert.Equal(1, harness.TokenRequests);
    }

    [Fact]
    public async Task AfterARestartATokenAnotherProfileIsHandingBackIsNotSentAgain()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (first, link) = await LinkExpired(harness);
        var second = Guid.NewGuid();
        _time.Now = Now;
        await harness.Service.WriteMirrorAsync(second, harness.Service.Read(Default), CancellationToken.None);
        _time.Now = Now.AddHours(9);
        using (new FileStream(DefaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The first profile renews, but cannot hand the login back yet.
            await Check(harness, first, link);
        }

        // A restart, and other profiles are checked before the first one finishes its hand-back. The second
        // was given the renewal; a third is linked now, from the folder that still holds the used-up token.
        var restarted = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-3", "rt-3")));
        var third = Guid.NewGuid();
        await restarted.Service.WriteMirrorAsync(third, restarted.Service.Read(Default), CancellationToken.None);
        var shared = await Check(restarted, second, link);
        var waiting = await Check(restarted, third, link);

        Assert.Equal("at-2", shared.Credentials?.AccessToken);
        Assert.Null(waiting.Credentials);
        Assert.Equal(LinkNotice.HandBackPending, waiting.Notice);
        Assert.Equal(0, restarted.TokenRequests);
        var handedBack = await Check(restarted, first, link);
        Assert.Equal("at-2", handedBack.Credentials?.AccessToken);
        Assert.Equal("at-2", (await Check(restarted, third, link)).Credentials?.AccessToken);
        Assert.Equal(0, restarted.TokenRequests);
    }

    [Fact]
    public async Task UnreadableCopyOfAnotherProfileBlocksEveryRenewal()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (first, link) = await LinkExpired(harness);
        var second = Guid.NewGuid();
        _time.Now = Now;
        await harness.Service.WriteMirrorAsync(second, harness.Service.Read(Default), CancellationToken.None);
        _time.Now = Now.AddHours(9);
        using (new FileStream(DefaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The first profile renews, but cannot hand the login back yet.
            await Check(harness, first, link);
        }

        // After a restart, the first profile's saved copy, with its pending record, cannot be read.
        var restarted = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-3", "rt-3")));
        var firstFile = new SecretStore(Path.Combine(_temp.Path, "secrets")).PathFor(first);
        LinkCheck waiting;
        using (new FileStream(firstFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            waiting = await Check(restarted, second, link);
        }

        Assert.Equal(0, restarted.TokenRequests);
        Assert.Equal(LinkNotice.OtherCopyUnreadable, waiting.Notice);
        Assert.Equal("at-2", (await Check(restarted, first, link)).Credentials?.AccessToken);
        Assert.Equal("at-2", (await Check(restarted, second, link)).Credentials?.AccessToken);
        Assert.Equal(0, restarted.TokenRequests);
    }

    [Fact]
    public async Task AccountThatLeftEveryKnownFolderBecomesTheTrackersOwn()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);
        _time.Now = Now.AddHours(1);
        // Claude Code switched to another account; no folder holds acc-1 any more.
        Write(DefaultFile, "at-other", "rt-other", Now.AddHours(9));
        File.WriteAllText(Path.Combine(UserProfile, ".claude.json"), LinkHarness.Account("acc-2", "other@example.com"));

        var owned = await Check(harness, profileId, link);

        Assert.Equal(LinkStatus.Ready, owned.Status);
        Assert.Equal(CredentialOwnership.AppOwned, owned.Ownership);
        Assert.Equal("at-1", owned.Credentials?.AccessToken);
        Assert.Equal(CredentialOwnership.AppOwned, harness.Secrets.Load(profileId).Cli?.Ownership);

        // Once it expires the tracker renews it itself, and Claude Code's file (another account) is left alone.
        _time.Now = Now.AddHours(9);
        var renewed = await Check(harness, profileId, link);

        Assert.Equal(LinkStatus.Ready, renewed.Status);
        Assert.Equal("at-2", renewed.Credentials?.AccessToken);
        Assert.Contains("\"at-other\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);
        Assert.Equal(1, harness.TokenRequests);
    }

    [Fact]
    public async Task AccountStillHeldByAnotherKnownFolderStaysClaudeCodes()
    {
        var harness = Harness();
        var other = Path.Combine(_temp.Path, "other-claude");
        harness.Known.Add(other);
        var (profileId, link) = await LinkExpired(harness);
        _time.Now = Now.AddHours(1);
        Write(Path.Combine(other, ".credentials.json"), "at-1", "rt-1", Now.AddHours(8));
        File.Delete(DefaultFile);

        var check = await Check(harness, profileId, link);

        Assert.Equal(LinkStatus.Ready, check.Status);
        Assert.Equal(CredentialOwnership.CliLinked, check.Ownership);
        Assert.Equal(CredentialOwnership.CliLinked, harness.Secrets.Load(profileId).Cli?.Ownership);
    }

    [Fact]
    public async Task OwnedLoginMarkedAsUsedElsewhereIsNotRenewed()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);
        _time.Now = Now.AddHours(1);
        File.Delete(DefaultFile);
        Assert.Equal(CredentialOwnership.AppOwned, (await Check(harness, profileId, link)).Ownership);

        // The user then says the login is also used on another PC.
        link.UsedElsewhere = true;
        _time.Now = Now.AddHours(9);
        var check = await Check(harness, profileId, link);

        Assert.Null(check.Credentials);
        Assert.Equal(CredentialOwnership.CliLinked, check.Ownership);
        Assert.Equal(CredentialOwnership.CliLinked, harness.Secrets.Load(profileId).Cli?.Ownership);
        Assert.Equal(0, harness.TokenRequests);
    }

    [Fact]
    public async Task OwnedLoginBackInAnotherKnownFolderIsClaudeCodesAgain()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var other = Path.Combine(_temp.Path, "other-claude");
        harness.Known.Add(other);
        var (profileId, link) = await LinkExpired(harness);
        _time.Now = Now.AddHours(1);
        File.Delete(DefaultFile);
        Assert.Equal(CredentialOwnership.AppOwned, (await Check(harness, profileId, link)).Ownership);

        // The login is put back into another known folder, and Claude Code runs there.
        Write(Path.Combine(other, ".credentials.json"), "at-1", "rt-1", Now.AddHours(8));
        harness.ClaudeRunning();
        _time.Now = Now.AddHours(9);
        var check = await Check(harness, profileId, link);

        Assert.Null(check.Credentials);
        Assert.Equal(CredentialOwnership.CliLinked, check.Ownership);
        Assert.Equal(CredentialOwnership.CliLinked, harness.Secrets.Load(profileId).Cli?.Ownership);
        Assert.Equal(0, harness.TokenRequests);
    }

    [Fact]
    public async Task OwnedLoginThatIsRejectedSaysSoAndIsNotRetried()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(LinkHarness.InvalidGrant));
        var (profileId, link) = await LinkExpired(harness);
        _time.Now = Now.AddHours(1);
        File.Delete(DefaultFile);
        Assert.Equal(CredentialOwnership.AppOwned, (await Check(harness, profileId, link)).Ownership);

        _time.Now = Now.AddHours(9);
        Assert.Equal(LinkStatus.LoginRejected, (await Check(harness, profileId, link)).Status);
        Assert.Equal(LinkStatus.LoginRejected, (await Check(harness, profileId, link)).Status);
        Assert.Equal(1, harness.TokenRequests);
    }

    [Fact]
    public async Task RenewedLoginThatCannotBeSavedIsKeptAndNothingElseIsRenewed()
    {
        var harness = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-2", "rt-2")));
        var (profileId, link) = await LinkExpired(harness);
        var secretFile = new SecretStore(Path.Combine(_temp.Path, "secrets")).PathFor(profileId);

        LinkCheck check;
        using (new FileStream(secretFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            check = await Check(harness, profileId, link);
            // Claude Code still gets the renewed login; the copy waits in memory.
            Assert.Equal(LinkStatus.Ready, check.Status);
            Assert.NotNull(check.MirrorError);
            Assert.Contains("\"at-2\"", File.ReadAllText(DefaultFile), StringComparison.Ordinal);
        }

        var saved = await Check(harness, profileId, link);

        Assert.Null(saved.MirrorError);
        Assert.Contains("at-2", harness.Secrets.Load(profileId).Cli?.OAuthJson, StringComparison.Ordinal);
        Assert.Equal(1, harness.TokenRequests);
    }
}
