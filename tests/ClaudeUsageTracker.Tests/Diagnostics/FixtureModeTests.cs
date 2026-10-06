using System.IO;
using ClaudeUsageTracker.App.Automation;
using ClaudeUsageTracker.App.Diagnostics;

namespace ClaudeUsageTracker.Tests.Diagnostics;

/// <summary>
/// Fixture mode's arguments: a malformed fixture argument must stop startup rather than fall through to live
/// mode with the real Claude Code login.
/// </summary>
public sealed class FixtureModeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NoFixtureArgumentMeansLiveMode() =>
        Assert.Null(FixtureMode.FromArguments(["--software-render"]));

    [Fact]
    public void FixtureFolderIsUsed()
    {
        var fixture = FixtureMode.FromArguments(["--fixture", _temp.Path, "--software-render"]);

        Assert.Equal(Path.Combine(_temp.Path, "home"), fixture?.UserProfile);
        Assert.Equal(Path.Combine(_temp.Path, "data"), fixture?.DataRoot);
    }

    [Theory]
    [InlineData("--fixture")]
    [InlineData("--fixture", "--software-render")]
    [InlineData("--fixture=folder")]
    [InlineData("--fixtures", "folder")]
    public void MalformedFixtureArgumentIsRejected(params string[] args) =>
        Assert.Throws<ArgumentException>(() => FixtureMode.FromArguments(args));

    [Fact]
    public void RepeatedFixtureArgumentIsRejected() =>
        Assert.Throws<ArgumentException>(() => FixtureMode.FromArguments(["--fixture", _temp.Path, "--fixture", _temp.Path]));

    [Fact]
    public void MissingFixtureFolderIsRejected() =>
        Assert.Throws<ArgumentException>(() => FixtureMode.FromArguments(["--fixture", Path.Combine(_temp.Path, "missing")]));

    /// <summary>The Codex auto-start answers from the fixture's files and never runs the Codex CLI.</summary>
    [Fact]
    public async Task CodexAutoStartAnswersFromTheResponseFiles()
    {
        var fixture = FixtureMode.FromArguments(["--fixture", _temp.Path])!;
        var exec = fixture.CodexExec();
        var responses = Directory.CreateDirectory(fixture.Responses).FullName;

        Assert.Equal(new CodexExecResult(CodexExecOutcome.NotFound, null), await exec("unused", CancellationToken.None));

        File.WriteAllText(Path.Combine(responses, "codex-auto-start.jsonl"), "{\"type\":\"turn.started\"}\n{\"type\":\"turn.completed\",\"usage\":{}}\n");
        Assert.Equal(new CodexExecResult(CodexExecOutcome.Completed, 0), await exec("unused", CancellationToken.None));

        File.WriteAllText(Path.Combine(responses, "codex-auto-start.exit"), "1");
        Assert.Equal(new CodexExecResult(CodexExecOutcome.Failed, 1), await exec("unused", CancellationToken.None));
    }
}
