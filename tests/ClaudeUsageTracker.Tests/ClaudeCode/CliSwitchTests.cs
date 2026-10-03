using System.IO;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Tests.Usage;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>
/// Switching the shared Claude Code folder to a profile's account (plan.md section 2, "CLI files, safe switching"):
/// deferred while Claude Code may run, refused when anything is in doubt, backed up, written with every other member
/// kept, verified, undone only over its own write, and settled after a crash. Every login, folder and process is synthetic.
/// </summary>
public sealed class CliSwitchTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Guid _outgoing = Guid.NewGuid();
    private readonly Guid _target = Guid.NewGuid();
    private CliLink _outgoingLink = null!;
    private CliLink _targetLink = null!;

    public CliSwitchTests() => Directory.CreateDirectory(DefaultDirectory);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        _temp.Dispose();
    }

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string DefaultDirectory => Path.Combine(UserProfile, ".claude");
    private string CredentialsFile => Path.Combine(DefaultDirectory, ".credentials.json");
    private string AccountFile => Path.Combine(UserProfile, ".claude.json");
    private CliConfigDirectory Default => CliStore.Inventory(UserProfile, null, [])[0];

    private LinkHarness Harness(ScriptedHandler? tokens = null) => new(_temp.Path, UserProfile, _time, tokens);

    private IReadOnlyList<LinkedProfile> Profiles => [new(_outgoing, _outgoingLink), new(_target, _targetLink)];

    private static string AccountJson(string uuid, string email) =>
        $$$"""{"numStartups":7,"projects":{"C:\\work":{"allowedTools":[]}},"oauthAccount":{"accountUuid":"{{{uuid}}}","emailAddress":"{{{email}}}","organizationName":"Org","billingType":"stripe"},"tipsHistory":{"a":1}}""";

    private void SignIn(string uuid, string email, string access, string refresh, DateTimeOffset? expires = null)
    {
        File.WriteAllText(CredentialsFile, LinkHarness.Credentials(access, refresh, expires ?? Now.AddHours(8)));
        File.WriteAllText(AccountFile, AccountJson(uuid, email));
    }

    /// <summary>
    /// The target's account was in the folder earlier and is mirrored; then the outgoing account signed in there and is
    /// mirrored too. The folder now holds the outgoing account; the target lives only in its profile's copy.
    /// </summary>
    private async Task<LinkHarness> TwoAccounts(ScriptedHandler? tokens = null, DateTimeOffset? targetExpires = null)
    {
        var harness = Harness(tokens);
        SignIn("acc-b", "b@example.com", "at-b", "rt-b", targetExpires);
        var target = harness.Service.Read(Default);
        _targetLink = harness.Service.CreateLink(target);
        await harness.Service.WriteMirrorAsync(_target, target, CancellationToken.None);

        SignIn("acc-x", "x@example.com", "at-x", "rt-x");
        var outgoing = harness.Service.Read(Default);
        _outgoingLink = harness.Service.CreateLink(outgoing);
        await harness.Service.WriteMirrorAsync(_outgoing, outgoing, CancellationToken.None);
        return harness;
    }

    private Task<CliSwitchResult> Switch(LinkHarness harness, string? confirmed = null) =>
        harness.Service.SwitchAsync(_target, Profiles, Default, confirmed, null, CancellationToken.None);

    private static JsonObject Json(string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;

    private int Backups => Directory.Exists(Path.Combine(_temp.Path, "secrets", "backups"))
        ? Directory.GetFiles(Path.Combine(_temp.Path, "secrets", "backups"), "*.secret").Length
        : 0;

    [Fact]
    public async Task SwitchWritesTheTargetLoginAndAccountAndKeepsEverythingElse()
    {
        var harness = await TwoAccounts();

        var result = await Switch(harness);

        Assert.Equal(CliSwitchOutcome.Switched, result.Outcome);
        Assert.Equal(_target, result.ProfileId);
        var credentials = Json(CredentialsFile);
        Assert.Equal("at-b", (string?)credentials["claudeAiOauth"]?["accessToken"]);
        Assert.Equal("rt-b", (string?)credentials["claudeAiOauth"]?["refreshToken"]);
        Assert.Equal("mcp-secret", (string?)credentials["mcpOAuth"]?["server"]?["accessToken"]);
        var account = Json(AccountFile);
        Assert.Equal("acc-b", (string?)account["oauthAccount"]?["accountUuid"]);
        // Upstream's raw record: unknown members of the target's own record come back too.
        Assert.Equal("stripe", (string?)account["oauthAccount"]?["billingType"]);
        Assert.Equal(7, (int?)account["numStartups"]);
        Assert.NotNull(account["projects"]?["C:\\work"]);
        Assert.Equal(1, (int?)account["tipsHistory"]?["a"]);
        // The member order of Claude Code's file is kept.
        Assert.Equal(["numStartups", "projects", "oauthAccount", "tipsHistory"], account.Select(p => p.Key));

        var copy = harness.Secrets.Load(_target).Cli!;
        Assert.Equal(CredentialOwnership.CliLinked, copy.Ownership);
        Assert.Equal(DefaultDirectory, copy.SourceConfigDirectory, ignoreCase: true);
        Assert.False(harness.Service.SwitchUnsettled);
        Assert.Equal(1, Backups);
        Assert.Equal(0, harness.TokenRequests);
    }

    [Fact]
    public async Task TheOutgoingAccountsLatestLoginIsSavedBeforeItIsReplaced()
    {
        var harness = await TwoAccounts();
        // Claude Code renewed the outgoing login after the tracker last mirrored it.
        File.WriteAllText(CredentialsFile, LinkHarness.Credentials("at-x2", "rt-x2", Now.AddHours(8)));

        Assert.Equal(CliSwitchOutcome.Switched, (await Switch(harness)).Outcome);

        Assert.Contains("rt-x2", harness.Secrets.Load(_outgoing).Cli!.OAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterTheSwitchEachProfileFollowsItsOwnAccount()
    {
        var harness = await TwoAccounts();
        await Switch(harness);

        var outgoing = await harness.Service.CheckAsync(_outgoing, _outgoingLink, CancellationToken.None);
        var target = await harness.Service.CheckAsync(_target, _targetLink, CancellationToken.None);

        // Nothing else holds the outgoing account and no Claude Code runs, so its copy becomes the tracker's own and is polled.
        Assert.Equal(LinkStatus.Ready, outgoing.Status);
        Assert.Equal(CredentialOwnership.AppOwned, outgoing.Ownership);
        Assert.Equal("at-x", outgoing.Credentials?.AccessToken);
        Assert.Equal(LinkStatus.Ready, target.Status);
        Assert.Equal("at-b", target.Credentials?.AccessToken);
        Assert.Equal(CredentialOwnership.CliLinked, target.Ownership);
    }

    [Fact]
    public async Task RunningClaudeCodeDefersTheSwitchAndChangesNothing()
    {
        var harness = await TwoAccounts();
        var before = (File.ReadAllBytes(CredentialsFile), File.ReadAllBytes(AccountFile));
        harness.ClaudeRunning();

        var result = await Switch(harness);

        Assert.Equal(CliSwitchOutcome.Deferred, result.Outcome);
        Assert.Equal(before.Item1, File.ReadAllBytes(CredentialsFile));
        Assert.Equal(before.Item2, File.ReadAllBytes(AccountFile));
        Assert.Equal(0, Backups);
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task ALoginNoProfileFollowsNeedsConfirmationForTheSameLogin()
    {
        var harness = await TwoAccounts();
        SignIn("acc-u", "u@example.com", "at-u", "rt-u");
        var before = File.ReadAllBytes(CredentialsFile);

        var asked = await Switch(harness);

        Assert.Equal(CliSwitchOutcome.NeedsConfirmation, asked.Outcome);
        Assert.Contains("u@example.com", asked.Detail, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(CredentialsFile));

        // A login that changed after the question needs asking again.
        File.WriteAllText(CredentialsFile, LinkHarness.Credentials("at-u2", "rt-u2", Now.AddHours(8)));
        File.SetLastWriteTimeUtc(CredentialsFile, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(CliSwitchOutcome.NeedsConfirmation, (await Switch(harness, asked.OccupantStamp)).Outcome);

        var again = await Switch(harness);
        Assert.Equal(CliSwitchOutcome.Switched, (await Switch(harness, again.OccupantStamp)).Outcome);
        Assert.Equal(1, Backups);
    }

    [Fact]
    public async Task AFolderAlreadyOnTheAccountIsLeftAlone()
    {
        var harness = await TwoAccounts();
        SignIn("acc-b", "b@example.com", "at-b", "rt-b");

        Assert.Equal(CliSwitchOutcome.AlreadyInUse, (await Switch(harness)).Outcome);
        Assert.Equal(0, Backups);
    }

    [Fact]
    public async Task AnAccountAnotherKnownFolderHoldsIsRefused()
    {
        var harness = await TwoAccounts();
        var other = Path.Combine(_temp.Path, "work-claude");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, ".credentials.json"), LinkHarness.Credentials("at-b", "rt-b", Now.AddHours(8)));
        File.WriteAllText(Path.Combine(other, ".claude.json"), AccountJson("acc-b", "b@example.com"));
        harness.Known.Add(other);

        var result = await Switch(harness);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.HeldByOtherFolder), (result.Outcome, result.Refusal));
        Assert.Contains("\"at-x\"", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoginAlsoUsedElsewhereIsRefused()
    {
        var harness = await TwoAccounts();
        _targetLink.UsedElsewhere = true;

        var result = await Switch(harness);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.UsedElsewhere), (result.Outcome, result.Refusal));
    }

    [Fact]
    public async Task APendingHandBackIsSettledBeforeAnySwitch()
    {
        var harness = await TwoAccounts();
        var bundle = harness.Secrets.Load(_outgoing);
        var copy = bundle.Cli!;
        bundle.Cli = new CliSecret(copy.OAuthJson, copy.Ownership, copy.SourceConfigDirectory, Now,
            new PendingWriteBack("rt-old", Now, [DefaultDirectory]), copy.OAuthAccountJson);
        harness.Secrets.Save(_outgoing, bundle);

        var result = await Switch(harness);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.HandBackPending), (result.Outcome, result.Refusal));
    }

    [Fact]
    public async Task AnExpiredTargetLoginIsRenewedOnceBeforeItIsHandedOver()
    {
        var harness = await TwoAccounts(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-b2", "rt-b2")), Now.AddHours(1));
        _time.Now = Now.AddHours(2);

        Assert.Equal(CliSwitchOutcome.Switched, (await Switch(harness)).Outcome);

        Assert.Equal(1, harness.TokenRequests);
        Assert.Equal("at-b2", (string?)Json(CredentialsFile)["claudeAiOauth"]?["accessToken"]);
        Assert.Contains("rt-b2", harness.Secrets.Load(_target).Cli!.OAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedTargetLoginIsNotHandedOver()
    {
        var harness = await TwoAccounts(LinkHarness.TokenEndpoint(LinkHarness.InvalidGrant), Now.AddHours(1));
        _time.Now = Now.AddHours(2);
        var before = File.ReadAllBytes(CredentialsFile);

        var result = await Switch(harness);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.LoginRejected), (result.Outcome, result.Refusal));
        Assert.Equal(before, File.ReadAllBytes(CredentialsFile));
        Assert.Equal(0, Backups);
    }

    [Fact]
    public async Task ACredentialsChangeJustBeforeTheWriteStopsTheSwitch()
    {
        var harness = await TwoAccounts();
        harness.Service.BeforeReplacing = file =>
        {
            if (file == CredentialsFile)
            {
                File.WriteAllText(CredentialsFile, LinkHarness.Credentials("at-x3", "rt-x3", Now.AddHours(8)));
            }
        };

        var result = await Switch(harness);

        Assert.Equal(CliSwitchOutcome.Conflict, result.Outcome);
        Assert.Contains("\"at-x3\"", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
        Assert.Equal("acc-x", (string?)Json(AccountFile)["oauthAccount"]?["accountUuid"]);
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task AnAccountFileChangeUndoesTheSwitchsOwnCredentialsWrite()
    {
        var harness = await TwoAccounts();
        var original = File.ReadAllBytes(CredentialsFile);
        harness.Service.BeforeReplacing = file =>
        {
            if (file == AccountFile)
            {
                File.WriteAllText(AccountFile, AccountJson("acc-x", "x@example.com").Replace("\"numStartups\":7", "\"numStartups\":8", StringComparison.Ordinal));
            }
        };

        var result = await Switch(harness);

        Assert.Equal(CliSwitchOutcome.Conflict, result.Outcome);
        Assert.Equal(original, File.ReadAllBytes(CredentialsFile));
        Assert.Equal(8, (int?)Json(AccountFile)["numStartups"]);
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task AnAccountFileThatCannotBeWrittenUndoesTheCredentialsWrite()
    {
        var harness = await TwoAccounts();
        var original = File.ReadAllBytes(CredentialsFile);
        harness.Service.BeforeReplacing = file =>
        {
            if (file == AccountFile)
            {
                File.SetAttributes(AccountFile, FileAttributes.ReadOnly);
            }
        };

        var result = await Switch(harness);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.StorageFailed), (result.Outcome, result.Refusal));
        Assert.Equal(original, File.ReadAllBytes(CredentialsFile));
        Assert.Equal("acc-x", (string?)Json(AccountFile)["oauthAccount"]?["accountUuid"]);
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task ACredentialsFileThatCannotBeWrittenEndsTheSwitchWithNothingChanged()
    {
        var harness = await TwoAccounts();
        var original = File.ReadAllBytes(CredentialsFile);
        harness.Service.BeforeReplacing = file =>
        {
            if (file == CredentialsFile)
            {
                File.SetAttributes(CredentialsFile, FileAttributes.ReadOnly);
            }
        };

        var result = await Switch(harness);

        // Nothing landed, so nothing is left blocked: every renewal elsewhere carries on.
        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.StorageFailed), (result.Outcome, result.Refusal));
        Assert.Equal(original, File.ReadAllBytes(CredentialsFile));
        Assert.False(harness.Service.SwitchUnsettled);
    }

    /// <summary>Runs a switch that stops between its two writes, as when the app ends there.</summary>
    private async Task CutShortBetweenTheWrites(LinkHarness harness)
    {
        harness.Service.BeforeReplacing = file =>
        {
            if (file == AccountFile)
            {
                throw new InvalidOperationException("Simulated end of the process.");
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Switch(harness));
        Assert.True(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task ACutShortSwitchIsCompletedAfterClaudeCodeRewroteItsAccountFile()
    {
        var harness = await TwoAccounts();
        await CutShortBetweenTheWrites(harness);
        // Claude Code started meanwhile and rewrote .claude.json, keeping its account record.
        File.WriteAllText(AccountFile, AccountJson("acc-x", "x@example.com").Replace("\"numStartups\":7", "\"numStartups\":8", StringComparison.Ordinal));

        var settled = await Harness().Service.SettleSwitchAsync(Profiles, false, CancellationToken.None);

        Assert.Equal(CliSwitchOutcome.Switched, settled?.Outcome);
        Assert.Equal("acc-b", (string?)Json(AccountFile)["oauthAccount"]?["accountUuid"]);
        Assert.Equal(8, (int?)Json(AccountFile)["numStartups"]);
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task AnUnclearCutShortSwitchStaysBlockedUntilTheUserHasSignedInAgain()
    {
        var harness = await TwoAccounts();
        await CutShortBetweenTheWrites(harness);
        // Unknown tokens beside the outgoing account's record: the target's login renewed by Claude Code, or a new sign-in.
        File.WriteAllText(CredentialsFile, LinkHarness.Credentials("at-unknown", "rt-unknown", Now.AddHours(8)));
        var restarted = Harness();

        var settled = await restarted.Service.SettleSwitchAsync(Profiles, false, CancellationToken.None);

        Assert.Equal((CliSwitchOutcome.RepairNeeded, CliSwitchRefusal.SignInAgain), (settled?.Outcome, settled?.Refusal));
        Assert.True(restarted.Service.SwitchUnsettled);
        // Meanwhile the outgoing profile never takes the folder's login as its own.
        await restarted.Service.CheckAsync(_outgoing, _outgoingLink, CancellationToken.None);
        Assert.DoesNotContain("at-unknown", restarted.Secrets.Load(_outgoing).Cli!.OAuthJson, StringComparison.Ordinal);

        var cleared = await restarted.Service.SettleSwitchAsync(Profiles, true, CancellationToken.None);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.Superseded), (cleared?.Outcome, cleared?.Refusal));
        Assert.False(restarted.Service.SwitchUnsettled);
        Assert.Contains("at-unknown", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkingAFolderWhoseSwitchIsUnfinishedIsRefused()
    {
        var harness = await TwoAccounts();
        await CutShortBetweenTheWrites(harness);

        await Assert.ThrowsAsync<SecretStoreException>(() =>
            harness.Service.WriteMirrorAsync(Guid.NewGuid(), harness.Service.Read(Default), CancellationToken.None));
    }

    [Fact]
    public async Task ALoginClaudeCodeRenewsDuringTheTargetsRenewalIsNotLost()
    {
        var tokens = LinkHarness.TokenEndpoint(() =>
        {
            // Claude Code renews the outgoing login while the tracker renews the target's.
            File.WriteAllText(CredentialsFile, LinkHarness.Credentials("at-x2", "rt-x2", Now.AddHours(10)));
            return LinkHarness.Grant("at-b2", "rt-b2");
        });
        var harness = await TwoAccounts(tokens, Now.AddHours(1));
        _time.Now = Now.AddHours(2);

        var first = await Switch(harness);
        Assert.Equal(CliSwitchOutcome.Deferred, first.Outcome);
        Assert.Contains("\"at-x2\"", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);

        Assert.Equal(CliSwitchOutcome.Switched, (await Switch(harness)).Outcome);
        Assert.Contains("rt-x2", harness.Secrets.Load(_outgoing).Cli!.OAuthJson, StringComparison.Ordinal);
        Assert.Equal(1, harness.TokenRequests);
    }

    [Fact]
    public async Task AClaudeCodeStartedDuringTheTargetsRenewalStopsTheSwitch()
    {
        LinkHarness harness = null!;
        var tokens = LinkHarness.TokenEndpoint(() =>
        {
            harness.ClaudeRunning();
            return LinkHarness.Grant("at-b2", "rt-b2");
        });
        harness = await TwoAccounts(tokens, Now.AddHours(1));
        _time.Now = Now.AddHours(2);
        var before = (File.ReadAllBytes(CredentialsFile), File.ReadAllBytes(AccountFile));

        var result = await Switch(harness);

        Assert.Equal(CliSwitchOutcome.Deferred, result.Outcome);
        Assert.Equal(before.Item1, File.ReadAllBytes(CredentialsFile));
        Assert.Equal(before.Item2, File.ReadAllBytes(AccountFile));
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task ARenewalWithoutAnAnswerIsNotWaitingForClaudeCode()
    {
        var harness = await TwoAccounts(
            LinkHarness.TokenEndpoint(() => LinkHarness.Json(System.Net.HttpStatusCode.InternalServerError, "{}")), Now.AddHours(1));
        _time.Now = Now.AddHours(2);

        var result = await Switch(harness);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.RenewalFailed), (result.Outcome, result.Refusal));
        Assert.Equal(0, Backups);
    }

    [Fact]
    public async Task AnAccountFileTooLargeToBeClaudeCodesIsRefused()
    {
        var harness = await TwoAccounts();
        using (var stream = new FileStream(AccountFile, FileMode.Create))
        {
            stream.SetLength(65L * 1024 * 1024);
        }

        var result = await Switch(harness);

        Assert.Equal(CliSwitchOutcome.Refused, result.Outcome);
        Assert.Contains("\"at-x\"", File.ReadAllText(CredentialsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwitchCutShortIsBlockedUntilItIsCompletedAtTheNextStart()
    {
        var harness = await TwoAccounts();
        // The app stops between the two writes.
        harness.Service.BeforeReplacing = file =>
        {
            if (file == AccountFile)
            {
                throw new InvalidOperationException("Simulated end of the process.");
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Switch(harness));
        Assert.True(harness.Service.SwitchUnsettled);

        // The folder holds the target's tokens beside the outgoing account's record. Nothing may mirror them as the
        // outgoing account's login.
        var restarted = Harness();
        var check = await restarted.Service.CheckAsync(_outgoing, _outgoingLink, CancellationToken.None);
        Assert.Equal(LinkNotice.SwitchIncomplete, check.Notice);
        Assert.Contains("at-x", restarted.Secrets.Load(_outgoing).Cli!.OAuthJson, StringComparison.Ordinal);

        var settled = await restarted.Service.SettleSwitchAsync(Profiles, false, CancellationToken.None);

        Assert.Equal(CliSwitchOutcome.Switched, settled?.Outcome);
        Assert.Equal(_target, settled?.ProfileId);
        Assert.Equal("acc-b", (string?)Json(AccountFile)["oauthAccount"]?["accountUuid"]);
        Assert.Equal(7, (int?)Json(AccountFile)["numStartups"]);
        Assert.False(restarted.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task ANewerLoginAfterACutShortSwitchIsLeftAlone()
    {
        var harness = await TwoAccounts();
        harness.Service.BeforeReplacing = file =>
        {
            if (file == AccountFile)
            {
                throw new InvalidOperationException("Simulated end of the process.");
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Switch(harness));
        // The user signed in again with another account before the tracker restarted.
        SignIn("acc-z", "z@example.com", "at-z", "rt-z");
        var before = (File.ReadAllBytes(CredentialsFile), File.ReadAllBytes(AccountFile));

        var settled = await Harness().Service.SettleSwitchAsync(Profiles, false, CancellationToken.None);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.Superseded), (settled?.Outcome, settled?.Refusal));
        Assert.Equal(before.Item1, File.ReadAllBytes(CredentialsFile));
        Assert.Equal(before.Item2, File.ReadAllBytes(AccountFile));
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task ASwitchStoppedBeforeItsFirstWriteChangesNothingWhenSettled()
    {
        var harness = await TwoAccounts();
        var before = File.ReadAllBytes(CredentialsFile);
        harness.Service.BeforeReplacing = _ => throw new InvalidOperationException("Simulated end of the process.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Switch(harness));

        var settled = await Harness().Service.SettleSwitchAsync(Profiles, false, CancellationToken.None);

        Assert.Equal((CliSwitchOutcome.Refused, CliSwitchRefusal.SwitchUnsettled), (settled?.Outcome, settled?.Refusal));
        Assert.Equal(before, File.ReadAllBytes(CredentialsFile));
        Assert.False(harness.Service.SwitchUnsettled);
    }

    [Fact]
    public async Task AnUnsettledSwitchBlocksEveryRenewal()
    {
        var harness = await TwoAccounts(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-x9", "rt-x9")));
        harness.Service.BeforeReplacing = _ => throw new InvalidOperationException("Simulated end of the process.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Switch(harness));
        _time.Now = Now.AddHours(9);

        var restarted = Harness(LinkHarness.TokenEndpoint(() => LinkHarness.Grant("at-x9", "rt-x9")));
        await restarted.Service.CheckAsync(_outgoing, _outgoingLink, CancellationToken.None);
        await restarted.Service.CheckAsync(_target, _targetLink, CancellationToken.None);

        Assert.Equal(0, restarted.TokenRequests);
    }

    [Fact]
    public async Task OnlyTheNewestBackupsAreKept()
    {
        var harness = await TwoAccounts();
        for (var i = 0; i < CliSwitchRecords.KeptBackups + 2; i++)
        {
            SignIn("acc-x", "x@example.com", $"at-x{i}", $"rt-x{i}");
            _time.Now = Now.AddMinutes(i + 1);
            Assert.Equal(CliSwitchOutcome.Switched, (await Switch(harness)).Outcome);
        }

        Assert.Equal(CliSwitchRecords.KeptBackups, Backups);
    }
}
