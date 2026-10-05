using System.IO;
using ClaudeUsageTracker.App.Diagnostics;

namespace ClaudeUsageTracker.Tests.Diagnostics;

/// <summary>The record an error that ends the app leaves: where it happened, never what its message carried.</summary>
public sealed class CrashRecordTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 10, 31, 9, TimeSpan.Zero);

    private static void ThrowWithData() => throw new InvalidOperationException("sk-ant-oat01-secret for user@example.com");

    private static Exception Thrown()
    {
        try
        {
            try
            {
                ThrowWithData();
            }
            catch (InvalidOperationException inner)
            {
                throw new IOException("C:\\Users\\someone\\.claude\\.credentials.json", inner);
            }
        }
        catch (IOException outer)
        {
            return outer;
        }

        throw new InvalidOperationException("Nothing was thrown.");
    }

    [Fact]
    public void TheRecordNamesEachErrorAndWhereItWasThrownButNoMessage()
    {
        var text = CrashRecord.Format(Thrown(), At);

        Assert.StartsWith("2026-10-04T10:31:09.0000000+00:00", text);
        Assert.Contains("System.IO.IOException", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains(nameof(ThrowWithData), text);
        Assert.DoesNotContain("sk-ant", text);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("credentials.json", text);
    }

    [Fact]
    public void TheRecordReplacesAnEarlierOneInTheDiagnosticsFolder()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "diagnostics");

        CrashRecord.Write(folder, new InvalidOperationException("first"), At);
        var path = CrashRecord.Write(folder, Thrown(), At);

        Assert.Equal(Path.Combine(folder, CrashRecord.FileName), path);
        Assert.Contains("System.IO.IOException", File.ReadAllText(path!));
        Assert.Single(Directory.GetFiles(folder));
    }
}
