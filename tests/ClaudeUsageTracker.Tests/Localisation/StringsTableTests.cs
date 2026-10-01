using System.Globalization;
using ClaudeUsageTracker.Core.Localisation;

namespace ClaudeUsageTracker.Tests.Localisation;

public class StringsTableTests
{
    [Fact]
    public void ParsesEntriesCommentsAndEscapes()
    {
        var table = StringsTable.Parse("""
            /* MARK: - App Info */
            "app.name" = "Claude Usage Tracker";
            // MARK: - Status Banners
            "quoted" = "Show \"Weekly:\" label";
            "lines"="one\ntwo";
            """);

        Assert.Equal(3, table.Count);
        Assert.True(table.TryGet("app.name", out var name));
        Assert.Equal("Claude Usage Tracker", name);
        Assert.True(table.TryGet("quoted", out var quoted));
        Assert.Equal("Show \"Weekly:\" label", quoted);
        Assert.True(table.TryGet("lines", out var lines));
        Assert.Equal("one\ntwo", lines);
        Assert.False(table.TryGet("missing", out _));
    }

    [Theory]
    [InlineData("\"a\" = \"b\"")]
    [InlineData("\"a\" \"b\";")]
    [InlineData("\"a\" = \"b;")]
    [InlineData("/* open")]
    [InlineData("\"a\" = \"b\\q\";")]
    public void MalformedInputThrows(string text) => Assert.Throws<FormatException>(() => StringsTable.Parse(text));

    [Fact]
    public void RepeatedKeyTakesItsLastValue()
    {
        var table = StringsTable.Parse("""
            "setup.step.enter_session_key" = "Enter Your Session Key";
            "setup.step.enter_session_key" = "Enter Key";
            """);

        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet("setup.step.enter_session_key", out var value));
        Assert.Equal("Enter Key", value);
    }

    [Theory]
    [InlineData("Delete \"%@\"?", new object[] { "Work" }, "Delete \"Work\"?")]
    [InlineData("%d of %d", new object[] { 2, 5 }, "2 of 5")]
    [InlineData("%ld items", new object[] { 3L }, "3 items")]
    [InlineData("%.3f seconds", new object[] { 0.12345 }, "0.123 seconds")]
    [InlineData("%2$@ before %1$@", new object[] { "b", "a" }, "a before b")]
    [InlineData("100%% of %@", new object[] { "limit" }, "100% of limit")]
    [InlineData("{braces} %@", new object[] { "kept" }, "{braces} kept")]
    public void FormatsUpstreamSpecifiers(string template, object[] args, string expected) =>
        Assert.Equal(expected, StringsTable.Format(template, CultureInfo.InvariantCulture, args));

    [Fact]
    public void FormatWithTooFewArgumentsThrows() =>
        Assert.Throws<FormatException>(() => StringsTable.Format("%@ and %@", CultureInfo.InvariantCulture, "one"));
}
