using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.Tests.Storage;

/// <summary>
/// Windows equivalent of upstream <c>testProfileSecretRoundTrip</c>, plus the DPAPI file rules in
/// plan.md section 2. All secrets are synthetic.
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private SecretStore NewStore() => new(Path.Combine(_temp.Path, "secrets"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ProfileSecretRoundTrip()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        var plaintext = Encoding.UTF8.GetBytes("""{"accessToken":"sk-ant-oat01-synthetic"}""");

        store.Write(id, plaintext);

        Assert.True(store.Exists(id));
        Assert.Equal(plaintext, store.Read(id));
    }

    [Fact]
    public void FileHoldsNoPlaintext()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        store.Write(id, Encoding.UTF8.GetBytes("synthetic-token-value-1234567890"));

        var file = File.ReadAllBytes(store.PathFor(id));

        Assert.DoesNotContain("synthetic-token-value", Encoding.UTF8.GetString(file), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token-value", Encoding.Unicode.GetString(file), StringComparison.Ordinal);
    }

    [Fact]
    public void LargeBundleRoundTrips()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        var plaintext = new byte[512 * 1024];
        new Random(42).NextBytes(plaintext);

        store.Write(id, plaintext);

        Assert.Equal(plaintext, store.Read(id));
    }

    [Fact]
    public void MissingFileReadsAsNull() => Assert.Null(NewStore().Read(Guid.NewGuid()));

    [Fact]
    public void RewriteReplacesWholeFileAndLeavesNoTemporaryFiles()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        store.Write(id, Encoding.UTF8.GetBytes(new string('a', 10_000)));
        store.Write(id, Encoding.UTF8.GetBytes("short"));

        Assert.Equal("short", Encoding.UTF8.GetString(store.Read(id)!));
        Assert.Single(Directory.GetFiles(Path.Combine(_temp.Path, "secrets")));
    }

    [Fact]
    public void FileCopiedToAnotherProfileDoesNotDecrypt()
    {
        var store = NewStore();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        store.Write(source, Encoding.UTF8.GetBytes("synthetic"));
        File.Copy(store.PathFor(source), store.PathFor(target));

        var error = Assert.Throws<SecretStoreException>(() => store.Read(target));
        Assert.Contains("cannot be decrypted", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(new byte[] { (byte)'C', (byte)'U', (byte)'T', (byte)'S', 1, 0, 0, 0 })]
    public void CorruptFileIsReportedNotReset(byte[] contents)
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        File.WriteAllBytes(store.PathFor(id), contents);

        Assert.Throws<SecretStoreException>(() => store.Read(id));
        Assert.Equal(contents, File.ReadAllBytes(store.PathFor(id)));
    }

    [Fact]
    public void NewerFormatIsReported()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        File.WriteAllBytes(store.PathFor(id), [(byte)'C', (byte)'U', (byte)'T', (byte)'S', 2, 9, 9, 9]);

        var error = Assert.Throws<SecretStoreException>(() => store.Read(id));
        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryGrantsOnlyTheCurrentUser()
    {
        NewStore();

        var security = new DirectoryInfo(Path.Combine(_temp.Path, "secrets")).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.False(rule.IsInherited);
    }

    [Fact]
    public void ExistingDirectoryIsTightened()
    {
        var directory = Path.Combine(_temp.Path, "secrets");
        Directory.CreateDirectory(directory);
        Assert.False(new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected);

        NewStore();

        Assert.True(new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected);
    }
}
