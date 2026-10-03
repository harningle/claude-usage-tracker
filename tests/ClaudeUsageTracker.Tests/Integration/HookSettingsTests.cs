using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Integration;
using ClaudeUsageTracker.Core.Hud;

namespace ClaudeUsageTracker.Tests.Integration;

/// <summary>
/// The tracker's hook entries in Claude Code's <c>settings.json</c>. Port of upstream
/// <c>NotchHookInstallerTests</c>, with the port's conflict-aware editing.
/// </summary>
public sealed class HookSettingsTests : IDisposable
{
    private const int Port = 23456;
    private const string Token = "testtoken";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Path_ => Path.Combine(_temp.Path, ".claude", "settings.json");

    private void Write(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, json);
    }

    private JsonObject Read() => ClaudeSettingsFile.Read(Path_)!;

    private bool Install(int port = Port, string token = Token, params int[] owned) =>
        ClaudeSettingsFile.Update(Path_, settings => HookSettings.Install(settings, port, token, owned));

    private bool Uninstall(params int[] owned) =>
        ClaudeSettingsFile.Update(Path_, settings => HookSettings.Remove(settings, owned.Length == 0 ? [Port] : owned));

    private HookInstallStatus Status(params int[] owned) =>
        HookSettings.Status(ClaudeSettingsFile.Read(Path_), Port, Token, owned.Length == 0 ? [Port] : owned);

    private static List<JsonObject> Hooks(JsonObject settings) =>
        [.. (settings["hooks"] as JsonObject ?? new JsonObject()).SelectMany(e => (JsonArray)e.Value!).SelectMany(g => (JsonArray)g!["hooks"]!).Select(h => (JsonObject)h!)];

    private static List<string> OurUrls(JsonObject settings) =>
        [.. Hooks(settings).Select(h => (string?)h["url"]).OfType<string>().Where(u => u.Contains($":{Port}/"))];

    [Fact]
    public void FreshInstallCreatesEightTokenedHooks()
    {
        Assert.Equal(HookInstallStatus.NotInstalled, Status());

        Assert.True(Install());

        var urls = OurUrls(Read());
        Assert.Equal(8, urls.Count);
        Assert.All(urls, url => Assert.StartsWith($"http://127.0.0.1:{Port}/hook/{Token}/", url));
        Assert.DoesNotContain(urls, url => url.Contains("permission-request"));
        Assert.Equal(HookInstallStatus.Installed, Status());
    }

    [Fact]
    public void TheEntriesAreUpstreamsPassiveHttpHooksAndNothingElse()
    {
        Install();

        var hooks = (JsonObject)Read()["hooks"]!;
        Assert.Equal(
            ["SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "Notification"],
            hooks.Select(e => e.Key));
        Assert.Equal(HookEvent.PathSuffixes, HookSettings.Events.Select(e => e.Suffix));
        // No hook that could approve, block or add context is installed.
        Assert.DoesNotContain("PermissionRequest", hooks.Select(e => e.Key));
        Assert.Equal(
            $$"""[{"matcher":"","hooks":[{"type":"http","url":"http://127.0.0.1:{{Port}}/hook/{{Token}}/pre-tool-use","timeout":3}]}]""",
            hooks["PreToolUse"]!.ToJsonString());
    }

    [Fact]
    public void InstallPreservesForeignSettingsAndHooks()
    {
        Write("""
            {
              "model": "opus",
              "statusLine": { "type": "command", "command": "bash x.sh" },
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "C:/tools/lint.exe" } ] } ],
                "PermissionRequest": [ { "matcher": "", "hooks": [ { "type": "command", "command": "ask.exe" } ] } ]
              },
              "env": { "NAME": "Zoë <&> 'quoted'" }
            }
            """);

        Assert.True(Install());

        var settings = Read();
        Assert.Equal(["model", "statusLine", "hooks", "env"], settings.Select(p => p.Key));
        Assert.Equal("opus", (string?)settings["model"]);
        Assert.Equal("bash x.sh", (string?)settings["statusLine"]!["command"]);
        var preToolUse = (JsonArray)settings["hooks"]!["PreToolUse"]!;
        // The foreign group is intact and comes first; the tracker's is appended.
        Assert.Equal(2, preToolUse.Count);
        Assert.Equal("Bash", (string?)preToolUse[0]!["matcher"]);
        Assert.Equal("C:/tools/lint.exe", (string?)preToolUse[0]!["hooks"]![0]!["command"]);
        Assert.Equal("ask.exe", (string?)settings["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]);
        // Text is written as it was, not as escapes.
        Assert.Contains("Zoë <&> 'quoted'", File.ReadAllText(Path_));
    }

    [Fact]
    public void UninstallRemovesOnlyOurHooks()
    {
        Write("""{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"lint.exe"}]}]}}""");
        Install();

        Assert.True(Uninstall());

        var settings = Read();
        Assert.Empty(OurUrls(settings));
        Assert.Equal("""{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"lint.exe"}]}]}""", settings["hooks"]!.ToJsonString());
        Assert.Equal(HookInstallStatus.NotInstalled, Status());
    }

    [Fact]
    public void UninstallOnMissingFileSucceeds()
    {
        Assert.False(Uninstall());

        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void UninstallLeavesNoEmptyHooksMember()
    {
        Write("""{"model":"opus"}""");
        Install();

        Uninstall();

        Assert.Equal("""{"model":"opus"}""", Read().ToJsonString());
    }

    [Fact]
    public void PartialDetection()
    {
        Install();
        // One event removed by hand.
        var settings = Read();
        ((JsonObject)settings["hooks"]!).Remove("Stop");
        Write(settings.ToJsonString());

        Assert.Equal(HookInstallStatus.Partial, Status());

        // A repair completes the set without doubling the rest.
        Assert.True(Install());
        Assert.Equal(8, OurUrls(Read()).Count);
        Assert.Equal(HookInstallStatus.Installed, Status());
    }

    [Fact]
    public void EntriesForAnEarlierPortOrSecretAreOutdatedAndReplaced()
    {
        Install(port: 20001, token: "old");
        Assert.Equal(HookInstallStatus.Outdated, Status(20001));

        Assert.True(Install(Port, Token, 20001));

        var urls = Hooks(Read()).Select(h => (string?)h["url"]).ToList();
        Assert.Equal(8, urls.Count);
        Assert.All(urls, url => Assert.StartsWith($"http://127.0.0.1:{Port}/hook/{Token}/", url));

        // The same port with a new secret is outdated too.
        Assert.Equal(HookInstallStatus.Outdated, HookSettings.Status(Read(), Port, "renewed", [Port]));
    }

    [Fact]
    public void AnotherToolsLoopbackHooksAreNotTheTrackers()
    {
        Write("""{"hooks":{"Stop":[{"matcher":"","hooks":[{"type":"http","url":"http://127.0.0.1:9999/hook/abc/stop","timeout":3}]}]}}""");
        Install();

        Uninstall();

        Assert.Equal("http://127.0.0.1:9999/hook/abc/stop", (string?)Read()["hooks"]!["Stop"]![0]!["hooks"]![0]!["url"]);
    }

    [Fact]
    public void InstallingAgainWritesNothing()
    {
        Install();
        var written = File.GetLastWriteTimeUtc(Path_);
        var bytes = File.ReadAllBytes(Path_);

        Assert.False(Install());

        Assert.Equal(written, File.GetLastWriteTimeUtc(Path_));
        Assert.Equal(bytes, File.ReadAllBytes(Path_));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"hooks": "none"}""")]
    [InlineData("""{"hooks": {"Stop": "none"}}""")]
    [InlineData("""{"a": 1, "a": 2}""")]
    [InlineData("""{"permissions": {"allow": [], "allow": ["Bash"]}}""")]
    public void SettingsOfAnUnexpectedShapeAreRefusedAndLeftAsTheyAre(string content)
    {
        Write(content);

        Assert.Throws<ClaudeSettingsException>(() => Install());

        Assert.Equal(content, File.ReadAllText(Path_));
    }

    [Fact]
    public void AByteOrderMarkAndTheFinalLineBreakAreHandled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
        File.WriteAllBytes(Path_, [.. Encoding.UTF8.Preamble, .. "{\"model\":\"opus\"}\n"u8]);

        Install();

        var text = File.ReadAllText(Path_);
        Assert.EndsWith("}\n", text);
        Assert.Equal("opus", (string?)Read()["model"]);
        Assert.Equal(HookInstallStatus.Installed, Status());
    }

    [Fact]
    public void AChangeMadeMeanwhileIsNotOverwritten()
    {
        Write("""{"model":"opus"}""");
        var calls = 0;

        ClaudeSettingsFile.Update(Path_, settings =>
        {
            if (calls++ == 0)
            {
                // Claude Code saves its own change while the tracker is preparing its edit.
                File.WriteAllText(Path_, """{"model":"sonnet","theme":"dark"}""");
            }

            return HookSettings.Install(settings, Port, Token, []);
        });

        Assert.Equal(2, calls);
        var settings = Read();
        Assert.Equal("sonnet", (string?)settings["model"]);
        Assert.Equal("dark", (string?)settings["theme"]);
        Assert.Equal(HookInstallStatus.Installed, Status());
    }

    [Fact]
    public void AFileThatKeepsChangingIsGivenUpOnWithoutAWrite()
    {
        Write("""{"n":0}""");
        var n = 0;

        Assert.Throws<ClaudeSettingsException>(() => ClaudeSettingsFile.Update(Path_, settings =>
        {
            File.WriteAllText(Path_, $$"""{"n":{{++n}}}""");
            return HookSettings.Install(settings, Port, Token, []);
        }));

        Assert.Equal($$"""{"n":{{n}}}""", File.ReadAllText(Path_));
    }
}
