using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.Notifications;
using Microsoft.Win32;

namespace ClaudeUsageTracker.Tests.Notifications;

/// <summary>A scratch key under <c>HKCU\Software\ClaudeUsageTracker.Tests</c>, deleted on dispose; never a real registration.</summary>
internal sealed class ScratchKey : IDisposable
{
    private const string Parent = @"Software\ClaudeUsageTracker.Tests";

    public ScratchKey()
    {
        Name = $@"{Parent}\{Guid.NewGuid():N}";
        Key = Registry.CurrentUser.CreateSubKey(Name, writable: true);
    }

    public string Name { get; }

    public RegistryKey Key { get; }

    public RegistryKey Open(string subKey) => Registry.CurrentUser.CreateSubKey($@"{Name}\{subKey}", writable: true);

    public void Dispose()
    {
        Key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(Name, throwOnMissingSubKey: false);
    }
}

/// <summary>Removal of this app's stale notification registrations (Milestone 1, D6), on a scratch copy of their layout.</summary>
public sealed class NotificationRegistrationsTests : IDisposable
{
    private const string Current = @"C:\Apps\Tracker\ClaudeUsageTracker.App.exe";
    private const string Moved = @"C:\Old\Tracker\ClaudeUsageTracker.App.exe";
    private const string OtherCopy = @"D:\Debug\ClaudeUsageTracker.App.exe";
    private const string Foreign = @"C:\Gone\SomethingElse.exe";

    private readonly ScratchKey _classes = new();

    public void Dispose() => _classes.Dispose();

    /// <summary>What <c>Register()</c> writes for one executable path.</summary>
    private (string Aumid, string Clsid) Registration(string executable)
    {
        var aumid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var clsid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        using (var key = _classes.Open($@"AppUserModelId\{aumid}"))
        {
            key.SetValue("DisplayName", "Claude Usage Tracker");
            key.SetValue("CustomActivator", clsid);
        }

        using (var server = _classes.Open($@"CLSID\{clsid}\LocalServer32"))
        {
            server.SetValue(null, $"\"{executable}\" ----AppNotificationActivated:");
        }

        _classes.Open($@"AppUserModelId\{executable.Replace('\\', '.')}").Dispose();
        return (aumid, clsid);
    }

    private bool Exists(string subKey)
    {
        using var key = _classes.Key.OpenSubKey(subKey);
        return key is not null;
    }

    [Fact]
    public void OnlyThisAppsRegistrationsForMissingExecutablesAreRemoved()
    {
        var stale = Registration(Moved);
        var live = Registration(OtherCopy);
        var own = Registration(Current);
        var foreign = Registration(Foreign);

        var removed = NotificationRegistrations.RemoveStale(_classes.Key, Current, path => path == OtherCopy || path == Current);

        Assert.Equal([Moved], removed);
        Assert.False(Exists($@"AppUserModelId\{stale.Aumid}"));
        Assert.False(Exists($@"CLSID\{stale.Clsid}"));
        Assert.False(Exists($@"AppUserModelId\{Moved.Replace('\\', '.')}"));
        foreach (var (aumid, clsid) in new[] { live, own, foreign })
        {
            Assert.True(Exists($@"AppUserModelId\{aumid}"));
            Assert.True(Exists($@"CLSID\{clsid}"));
        }

        Assert.True(Exists($@"AppUserModelId\{OtherCopy.Replace('\\', '.')}"));
    }

    [Fact]
    public void AnEntryWithoutAnActivatorOrWithAnotherCommandIsLeftAlone()
    {
        _classes.Open(@"AppUserModelId\Some.App").Dispose();
        using (var key = _classes.Open(@"AppUserModelId\{11111111-1111-1111-1111-111111111111}"))
        {
            key.SetValue("CustomActivator", "{22222222-2222-2222-2222-222222222222}");
        }

        using (var server = _classes.Open(@"CLSID\{22222222-2222-2222-2222-222222222222}\LocalServer32"))
        {
            server.SetValue(null, $"\"{Moved}\" -Embedding");
        }

        Assert.Empty(NotificationRegistrations.RemoveStale(_classes.Key, Current, _ => false));
        Assert.True(Exists(@"AppUserModelId\{11111111-1111-1111-1111-111111111111}"));
    }

    [Theory]
    [InlineData("\"C:\\A\\b.exe\" ----AppNotificationActivated:", @"C:\A\b.exe")]
    [InlineData("\"C:\\A B\\b.exe\"  ----AppNotificationActivated: ", @"C:\A B\b.exe")]
    [InlineData("C:\\A\\b.exe ----AppNotificationActivated:", null)]
    [InlineData("\"C:\\A\\b.exe\" -Embedding", null)]
    public void TheActivatorCommandIsParsedStrictly(string command, string? executable) =>
        Assert.Equal(executable, NotificationRegistrations.ExecutableOf(command));
}

/// <summary>Launch at login on scratch copies of the user's <c>Run</c> and Task Manager's <c>StartupApproved</c> keys.</summary>
public sealed class LaunchAtLoginTests : IDisposable
{
    private const string This = @"C:\Apps\Tracker\ClaudeUsageTracker.App.exe";

    private readonly ScratchKey _root = new();

    public void Dispose() => _root.Dispose();

    private LaunchAtLogin Startup(string executable = This) => new(() => _root.Open("Run"), () => _root.Open("Approved"), executable);

    [Fact]
    public void SwitchingOnRegistersThisCopyQuotedAndOffRemovesIt()
    {
        var startup = Startup();
        Assert.Equal(LaunchAtLoginState.Off, startup.State);

        startup.Set(true);
        using (var run = _root.Open("Run"))
        {
            Assert.Equal($"\"{This}\"", run.GetValue(LaunchAtLogin.ValueName));
        }

        Assert.Equal(LaunchAtLoginState.On, startup.State);
        startup.Set(false);
        Assert.Equal(LaunchAtLoginState.Off, startup.State);
    }

    [Fact]
    public void AnotherCopysEntryIsReportedNotRemovedAndReplacedOnlyWhenSwitchedOn()
    {
        Startup(@"C:\Old\ClaudeUsageTracker.App.exe").Set(true);
        var startup = Startup();

        Assert.Equal(LaunchAtLoginState.OtherCopy, startup.State);
        Assert.Equal(@"C:\Old\ClaudeUsageTracker.App.exe", startup.RegisteredPath);
        startup.Set(false);
        Assert.Equal(LaunchAtLoginState.OtherCopy, startup.State);

        startup.Set(true);
        Assert.Equal(LaunchAtLoginState.On, startup.State);
    }

    [Theory]
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.DisabledInTaskManager)]
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.DisabledInTaskManager)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.On)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.On)]
    public void TaskManagersChoiceIsReportedAndLeftAlone(byte[] approval, LaunchAtLoginState expected)
    {
        var startup = Startup();
        startup.Set(true);
        using (var approved = _root.Open("Approved"))
        {
            approved.SetValue(LaunchAtLogin.ValueName, approval, RegistryValueKind.Binary);
        }

        Assert.Equal(expected, startup.State);
        startup.Set(true);
        using (var approved = _root.Open("Approved"))
        {
            Assert.Equal(approval, approved.GetValue(LaunchAtLogin.ValueName));
        }
    }
}
