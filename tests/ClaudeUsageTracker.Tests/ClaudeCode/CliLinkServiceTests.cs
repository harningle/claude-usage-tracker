using System.IO;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.Tests.ClaudeCode;

/// <summary>
/// File-based CLI linking: read-only snapshots, account checks and the DPAPI mirror. Every login and
/// account here is synthetic and lives in a temporary directory.
/// </summary>
public sealed class CliLinkServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Now);

    private string UserProfile => Path.Combine(_temp.Path, "home");
    private string DefaultDirectory => Path.Combine(UserProfile, ".claude");
    private SecretStore Secrets => new(Path.Combine(_temp.Path, "secrets"));

    public CliLinkServiceTests() => Directory.CreateDirectory(DefaultDirectory);

    public void Dispose() => _temp.Dispose();

    private static string Credentials(string access, string refresh, DateTimeOffset expires, string extra = "") =>
        $$"""{"claudeAiOauth":{"accessToken":"{{access}}","refreshToken":"{{refresh}}","expiresAt":{{expires.ToUnixTimeMilliseconds()}},"scopes":["user:inference","user:profile"],"subscriptionType":"team"}{{extra}}}""";

    private static string Account(string uuid, string email) =>
        $$$"""{"numStartups":1,"oauthAccount":{"accountUuid":"{{{uuid}}}","emailAddress":"{{{email}}}","organizationName":"ExampleOrg"}}""";

    private void WriteDefault(string credentials, string? account)
    {
        File.WriteAllText(Path.Combine(DefaultDirectory, ".credentials.json"), credentials);
        if (account is not null)
        {
            File.WriteAllText(Path.Combine(UserProfile, ".claude.json"), account);
        }
    }

    private CliConfigDirectory Default => CliStore.Inventory(UserProfile, null, [])[0];

    [Fact]
    public void InventoryHasTheDefaultOverrideAndLinkedDirectoriesOnce()
    {
        var overrideDir = Path.Combine(_temp.Path, "work");
        var inventory = CliStore.Inventory(
            UserProfile, overrideDir + "\\", [overrideDir.ToUpperInvariant(), Path.Combine(_temp.Path, "other"), DefaultDirectory]);

        Assert.Equal(3, inventory.Count);
        Assert.True(inventory[0].IsDefault);
        Assert.Equal("default", inventory[0].Shorthand);
        Assert.Equal("work", inventory[1].Shorthand);
        Assert.Equal(Path.Combine(_temp.Path, "other"), inventory[2].Path);
    }

    [Fact]
    public void MissingFileIsNotSignedIn() =>
        Assert.Equal(CliLoginState.NotSignedIn, CliStore.Read(Default, Now).State);

    [Fact]
    public void UsableLoginReadsTheDefaultAccountFromTheUserProfile()
    {
        WriteDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));

        var snapshot = CliStore.Read(Default, Now);

        Assert.Equal(CliLoginState.Usable, snapshot.State);
        Assert.Equal("acc-1", snapshot.Account?.Identity);
        Assert.Equal("user@example.com — ExampleOrg · default", snapshot.Label);
    }

    [Fact]
    public void OverrideDirectoryNeverBorrowsTheDefaultAccount()
    {
        WriteDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-default", "default@example.com"));
        var work = Path.Combine(_temp.Path, ".claude-work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, ".credentials.json"), Credentials("at-2", "rt-2", Now.AddHours(8)));

        var snapshot = CliStore.Read(CliStore.Inventory(UserProfile, work, [])[1], Now);

        Assert.Equal(CliLoginState.Usable, snapshot.State);
        Assert.Null(snapshot.Account);
        Assert.Equal(".claude-work — team", snapshot.Label);
    }

    [Fact]
    public void ByteOrderMarkIsTolerated()
    {
        WriteDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));
        foreach (var file in new[] { Path.Combine(DefaultDirectory, ".credentials.json"), Path.Combine(UserProfile, ".claude.json") })
        {
            File.WriteAllText(file, File.ReadAllText(file), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        var snapshot = CliStore.Read(Default, Now);

        Assert.Equal(CliLoginState.Usable, snapshot.State);
        Assert.Equal("acc-1", snapshot.Account?.Identity);
    }

    [Theory]
    [InlineData("not json", CliLoginState.Invalid)]
    [InlineData("""{"claudeAiOauth":{}}""", CliLoginState.Invalid)]
    public void UnusableFilesAreClassified(string contents, CliLoginState expected)
    {
        WriteDefault(contents, null);

        Assert.Equal(expected, CliStore.Read(Default, Now).State);
    }

    [Fact]
    public void ExpiringLoginIsExpired()
    {
        WriteDefault(Credentials("at-1", "rt-1", Now.AddSeconds(30)), null);

        Assert.Equal(CliLoginState.Expired, CliStore.Read(Default, Now).State);
    }

    [Fact]
    public void ReadingNeverChangesTheCliFiles()
    {
        var credentials = Credentials("at-1", "rt-1", Now.AddHours(8));
        WriteDefault(credentials, Account("acc-1", "user@example.com"));
        var path = Path.Combine(DefaultDirectory, ".credentials.json");
        var written = File.GetLastWriteTimeUtc(path);

        CliStore.Read(Default, Now);

        Assert.Equal(credentials, File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    private LinkHarness Harness() => new(_temp.Path, UserProfile, _time);

    private async Task<(CliLinkService Service, Guid ProfileId, CliLink Link, LinkHarness Harness)> LinkDefault(string credentials, string? account)
    {
        WriteDefault(credentials, account);
        var harness = Harness();
        var service = harness.Service;
        var snapshot = service.Read(Default);
        var profileId = Guid.NewGuid();
        var link = service.CreateLink(snapshot);
        await service.WriteMirrorAsync(profileId, snapshot, CancellationToken.None);
        return (service, profileId, link, harness);
    }

    [Fact]
    public async Task LinkRecordsTheAccountAndMirrorsOnlyTheClaudeLogin()
    {
        var (_, profileId, link, _) = await LinkDefault(
            Credentials("at-1", "rt-1", Now.AddHours(8), extra: ""","mcpOAuth":{"server":{"accessToken":"mcp-secret"}}"""),
            Account("acc-1", "user@example.com"));

        Assert.Equal("acc-1", link.Identity);
        Assert.Equal("team", link.SubscriptionType);
        Assert.True(link.IsDefaultDirectory);
        var mirror = Encoding.UTF8.GetString(Secrets.Read(profileId)!);
        Assert.Contains("at-1", mirror, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp-secret", mirror, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredLoginCannotBeLinked()
    {
        WriteDefault(Credentials("at-1", "rt-1", Now.AddMinutes(-5)), null);
        var service = Harness().Service;

        Assert.Throws<InvalidOperationException>(() => service.CreateLink(service.Read(Default)));
    }

    [Fact]
    public async Task SameAccountIsReady()
    {
        var (service, profileId, link, _) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));

        var check = await service.CheckAsync(profileId, link, CancellationToken.None);

        Assert.Equal(LinkStatus.Ready, check.Status);
        Assert.Equal("at-1", check.Credentials?.AccessToken);
        Assert.Null(check.MirrorError);
    }

    [Fact]
    public async Task CliRotationOfTheSameAccountUpdatesTheMirror()
    {
        var (service, profileId, link, _) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));
        WriteDefault(Credentials("at-2", "rt-2", Now.AddHours(9)), null);

        var check = await service.CheckAsync(profileId, link, CancellationToken.None);

        Assert.Equal(LinkStatus.Ready, check.Status);
        Assert.Equal("at-2", check.Credentials?.AccessToken);
        Assert.Contains("at-2", Encoding.UTF8.GetString(Secrets.Read(profileId)!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentAccountIsReportedAndTheProfileKeepsFollowingItsAccount()
    {
        var (service, profileId, link, harness) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));
        harness.ClaudeRunning();
        WriteDefault(Credentials("at-other", "rt-other", Now.AddHours(8)), Account("acc-2", "other@example.com"));

        var check = await service.CheckAsync(profileId, link, CancellationToken.None);

        // The other account's login is never used; the profile's own copy still is, while it lasts.
        Assert.Equal(LinkStatus.AccountChanged, check.Status);
        Assert.Equal("at-1", check.Credentials?.AccessToken);
        Assert.Equal(CredentialOwnership.CliLinked, check.Ownership);
        var mirror = Encoding.UTF8.GetString(Secrets.Read(profileId)!);
        Assert.Contains("at-1", mirror, StringComparison.Ordinal);
        Assert.DoesNotContain("at-other", mirror, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredSameAccountWaitsWhileClaudeCodeRuns()
    {
        var (service, profileId, link, harness) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));
        harness.ClaudeRunning();
        _time.Now = Now.AddHours(9);

        var check = await service.CheckAsync(profileId, link, CancellationToken.None);

        Assert.Equal(LinkStatus.WaitingForCliRefresh, check.Status);
        Assert.Null(check.Credentials);
        Assert.Equal(0, harness.TokenRequests);
    }

    [Fact]
    public async Task WithoutIdentityOnlyAnUnchangedRefreshTokenProvesTheAccount()
    {
        var (service, profileId, link, _) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), account: null);
        Assert.Null(link.Identity);

        Assert.Equal(LinkStatus.Ready, (await service.CheckAsync(profileId, link, CancellationToken.None)).Status);

        WriteDefault(Credentials("at-2", "rt-2", Now.AddHours(8)), null);
        var check = await service.CheckAsync(profileId, link, CancellationToken.None);
        Assert.Equal(LinkStatus.AccountUnverified, check.Status);
    }

    [Fact]
    public async Task UnusableCopyIsReportedAndNeverOverwritten()
    {
        var (_, profileId, link, _) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));
        byte[] newerFormat = [(byte)'C', (byte)'U', (byte)'T', (byte)'S', 2, 1, 2, 3];
        File.WriteAllBytes(Secrets.PathFor(profileId), newerFormat);
        WriteDefault(Credentials("at-2", "rt-2", Now.AddHours(9)), null);
        // A fresh service, as after a restart: nothing cached in memory.
        var service = Harness().Service;

        var check = await service.CheckAsync(profileId, link, CancellationToken.None);

        Assert.Equal(LinkStatus.Ready, check.Status);
        Assert.Contains("newer version", check.MirrorError, StringComparison.Ordinal);
        Assert.Equal(newerFormat, File.ReadAllBytes(Secrets.PathFor(profileId)));
    }

    [Fact]
    public async Task CancelledCheckNeverWritesTheCopy()
    {
        var (service, profileId, link, _) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));
        await service.DeleteMirrorAsync(profileId, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(profileId, link, cancelled.Token));

        Assert.False(Secrets.Exists(profileId));
    }

    [Fact]
    public async Task SignedOutCliIsNotSignedInWhileClaudeCodeRuns()
    {
        var (service, profileId, link, harness) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));
        harness.ClaudeRunning();
        File.Delete(Path.Combine(DefaultDirectory, ".credentials.json"));

        var check = await service.CheckAsync(profileId, link, CancellationToken.None);

        Assert.Equal(LinkStatus.NotSignedIn, check.Status);
        Assert.Equal(CredentialOwnership.CliLinked, check.Ownership);
        Assert.Equal(CredentialOwnership.CliLinked, harness.Secrets.Load(profileId).Cli?.Ownership);
    }

    [Fact]
    public async Task DeleteMirrorRemovesTheSecretButNotTheCliLogin()
    {
        var (service, profileId, _, _) = await LinkDefault(Credentials("at-1", "rt-1", Now.AddHours(8)), Account("acc-1", "user@example.com"));

        await service.DeleteMirrorAsync(profileId, CancellationToken.None);

        Assert.False(Secrets.Exists(profileId));
        Assert.True(File.Exists(Path.Combine(DefaultDirectory, ".credentials.json")));
    }
}
