using System.IO;
using System.Security.Cryptography;
using System.Text;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.Tests.Storage;

/// <summary>
/// The per-profile secret bundle (plan.md section 2): one versioned, DPAPI-encrypted file replaced as a
/// whole, read back after every write, upgraded from the Milestone 2 format, and never reset when it
/// cannot be used. Every secret here is synthetic.
/// </summary>
public sealed class ProfileSecretsTests : IDisposable
{
    private static readonly DateTimeOffset Saved = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly Guid _profileId = Guid.NewGuid();

    public void Dispose() => _temp.Dispose();

    private SecretStore Raw => new(Path.Combine(_temp.Path, "secrets"));

    private ProfileSecretStore Store => new(Raw);

    private static CliSecret Cli(string access, PendingWriteBack? pending = null) => new(
        $$"""{"accessToken":"{{access}}","refreshToken":"rt-1","expiresAt":1790000000000}""",
        CredentialOwnership.CliLinked, @"C:\Users\user\.claude", Saved, pending);

    [Fact]
    public void EveryCredentialRoundTripsInOneFile()
    {
        var bundle = ProfileSecrets.Empty();
        bundle.Cli = Cli("at-1", new PendingWriteBack("rt-0", Saved, [@"C:\Users\user\.claude"]));
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic", Saved.AddDays(28));
        bundle.CodexAuthJson = """{"tokens":{"access_token":"codex-at"}}""";

        Store.Save(_profileId, bundle);
        var loaded = Store.Load(_profileId);

        Assert.Single(Directory.GetFiles(Path.Combine(_temp.Path, "secrets")));
        Assert.Contains("at-1", loaded.Cli?.OAuthJson, StringComparison.Ordinal);
        Assert.Equal("rt-0", loaded.Cli?.Pending?.ConsumedRefreshToken);
        Assert.Equal([@"C:\Users\user\.claude"], loaded.Cli?.Pending?.Targets);
        Assert.Equal("sk-ant-sid01-synthetic", loaded.Console?.SessionKey);
        Assert.Equal(Saved.AddDays(28), loaded.Console?.Expires);
        Assert.Equal("""{"tokens":{"access_token":"codex-at"}}""", loaded.CodexAuthJson);
    }

    [Fact]
    public void CodexRenewalRecordIsKeptWithItsCredentialsAndDroppedWhenTheyAreReplaced()
    {
        var other = Guid.NewGuid().ToString("N");
        var bundle = ProfileSecrets.Empty();
        bundle.CodexAuthJson = """{"tokens":{"access_token":"codex-at-2"}}""";
        bundle.CodexPending = new PendingWriteBack("codex-rt", Saved, [other]);

        Store.Save(_profileId, bundle);
        var loaded = Store.Load(_profileId);

        Assert.Equal("codex-rt", loaded.CodexPending?.ConsumedRefreshToken);
        Assert.Equal([other], loaded.CodexPending?.Targets);

        // Credentials pasted in their place are another login; the record belonged to the old one.
        loaded.CodexAuthJson = """{"tokens":{"access_token":"other-at"}}""";
        Assert.Null(loaded.CodexPending);
    }

    [Fact]
    public void FileHoldsNoPlaintextSecret()
    {
        var bundle = ProfileSecrets.Empty();
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic-plaintext-probe", null);
        Store.Save(_profileId, bundle);

        var bytes = File.ReadAllBytes(Raw.PathFor(_profileId));

        Assert.DoesNotContain("plaintext-probe", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-probe", Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void MilestoneTwoCopyIsUpgradedOnRead()
    {
        Raw.Write(_profileId, Encoding.UTF8.GetBytes(
            """{"version":1,"kind":"claudeCliOAuth","sourceConfigDirectory":"C:\\Users\\user\\.claude","mirroredAt":"2026-09-30T08:00:00Z","claudeAiOauth":{"accessToken":"at-m2","refreshToken":"rt-m2"}}"""));

        var cli = Store.Load(_profileId).Cli;

        Assert.Contains("at-m2", cli?.OAuthJson, StringComparison.Ordinal);
        Assert.Equal(CredentialOwnership.CliLinked, cli?.Ownership);
        Assert.Equal(@"C:\Users\user\.claude", cli?.SourceConfigDirectory);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero), cli?.SavedAt);
    }

    [Fact]
    public void NewerBundleIsReportedAndLeftUntouched()
    {
        var newer = Encoding.UTF8.GetBytes("""{"version":3,"somethingNew":{}}""");
        Raw.Write(_profileId, newer);
        var before = File.ReadAllBytes(Raw.PathFor(_profileId));

        var error = Assert.Throws<SecretStoreException>(() => Store.Load(_profileId));

        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Raw.PathFor(_profileId)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"kind":"claudeCliOAuth"}""")]
    [InlineData("""{"version":1,"kind":"somethingElse","claudeAiOauth":{}}""")]
    public void DamagedBundleIsReported(string plaintext)
    {
        Raw.Write(_profileId, Encoding.UTF8.GetBytes(plaintext));

        Assert.Throws<SecretStoreException>(() => Store.Load(_profileId));
    }

    [Fact]
    public void UnknownMembersSurviveASave()
    {
        Raw.Write(_profileId, Encoding.UTF8.GetBytes("""{"version":2,"futureCredential":{"token":"kept"}}"""));

        var bundle = Store.Load(_profileId);
        bundle.Console = new ConsoleSecret("sk-ant-sid01-synthetic", null);
        Store.Save(_profileId, bundle);

        Assert.Contains("\"futureCredential\"", Encoding.UTF8.GetString(Raw.Read(_profileId)!), StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingTheLastCredentialRemovesTheFile()
    {
        var bundle = ProfileSecrets.Empty();
        bundle.Cli = Cli("at-1");
        Store.Save(_profileId, bundle);

        bundle.Cli = null;
        Store.Save(_profileId, bundle);

        Assert.False(Store.Exists(_profileId));
    }

    [Fact]
    public void LargeBundleRoundTrips()
    {
        var bundle = ProfileSecrets.Empty();
        // A Codex auth.json with long JWTs, plus a large unknown member: far beyond any real bundle.
        bundle.CodexAuthJson = "{\"tokens\":{\"id_token\":\"" + new string('j', 900_000) + "\"}}";
        bundle.Cli = Cli(new string('a', 50_000));

        Store.Save(_profileId, bundle);
        var loaded = Store.Load(_profileId);

        Assert.Equal(bundle.CodexAuthJson, loaded.CodexAuthJson);
        Assert.Contains(new string('a', 50_000), loaded.Cli?.OAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedReplacementLeavesThePreviousBundleIntactAndNoTemporaryFile()
    {
        var first = ProfileSecrets.Empty();
        first.Cli = Cli("at-old");
        Store.Save(_profileId, first);
        var second = ProfileSecrets.Empty();
        second.Cli = Cli("at-new");

        using (new FileStream(Raw.PathFor(_profileId), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The file is held open (antivirus, a backup tool): the rename over it fails.
            var error = Assert.ThrowsAny<Exception>(() => Store.Save(_profileId, second));
            Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
        }

        Assert.Contains("at-old", Store.Load(_profileId).Cli?.OAuthJson, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(_temp.Path, "secrets")));
    }

    [Fact]
    public void TemporaryFileLeftByACrashIsIgnored()
    {
        var bundle = ProfileSecrets.Empty();
        bundle.Cli = Cli("at-1");
        Store.Save(_profileId, bundle);
        // A crash between writing the temporary file and renaming it.
        File.WriteAllBytes(Path.Combine(_temp.Path, "secrets", $".{_profileId:N}.secret.{Guid.NewGuid():N}.tmp"), [1, 2, 3]);

        Assert.Contains("at-1", Store.Load(_profileId).Cli?.OAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public void BundleOfAnotherProfileDoesNotDecrypt()
    {
        var bundle = ProfileSecrets.Empty();
        bundle.Cli = Cli("at-1");
        Store.Save(_profileId, bundle);
        var other = Guid.NewGuid();
        File.Copy(Raw.PathFor(_profileId), Raw.PathFor(other));

        Assert.Throws<SecretStoreException>(() => Store.Load(other));
        Assert.IsType<CryptographicException>(Assert.Throws<SecretStoreException>(() => Store.Load(other)).InnerException);
    }

    /// <summary>Windows equivalent of upstream <c>testFieldsAreIsolatedPerProfileAndField</c>.</summary>
    [Fact]
    public void CredentialsAreIsolatedPerProfileAndField()
    {
        var other = Guid.NewGuid();
        foreach (var (id, key) in new[] { (_profileId, "sk-ant-sid01-first"), (other, "sk-ant-sid01-second") })
        {
            var bundle = ProfileSecrets.Empty();
            bundle.Console = new ConsoleSecret(key, null);
            bundle.Cli = Cli("at-" + key);
            Store.Save(id, bundle);
        }

        var first = Store.Load(_profileId);
        first.Console = null;
        Store.Save(_profileId, first);

        Assert.Null(Store.Load(_profileId).Console);
        Assert.Contains("first", Store.Load(_profileId).Cli?.OAuthJson, StringComparison.Ordinal);
        Assert.Equal("sk-ant-sid01-second", Store.Load(other).Console?.SessionKey);

        Store.Delete(_profileId);
        Assert.False(Store.Exists(_profileId));
        Assert.Equal("sk-ant-sid01-second", Store.Load(other).Console?.SessionKey);
    }

    [Fact]
    public void ToStringNeverPrintsASecret()
    {
        var secret = new ConsoleSecret("sk-ant-sid01-synthetic-tostring", null);

        Assert.DoesNotContain("tostring", secret.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("at-1", Cli("at-1").ToString(), StringComparison.Ordinal);
    }
}
