using ClaudeUsageTracker.Core.Authentication;

namespace ClaudeUsageTracker.Tests.Authentication;

/// <summary>Port of upstream <c>SessionKeyValidatorTests</c>. All keys are synthetic.</summary>
public class SessionKeyValidatorTests
{
    // Valid keys

    [Fact]
    public void ValidSessionKey()
    {
        const string key = "sk-ant-sid01-abcdefghijklmnopqrstuvwxyz1234567890";

        Assert.Equal(key, SessionKeyValidator.Validate(key));
        Assert.True(SessionKeyValidator.IsValid(key));
    }

    [Fact]
    public void ValidSessionKeyWithUnderscores() =>
        SessionKeyValidator.Validate("sk-ant-sid01_abcdefghijklmnopqrstuvwxyz_1234567890");

    [Fact]
    public void ValidSessionKeyWithHyphens() => SessionKeyValidator.Validate("sk-ant-sid01-abcd-efgh-ijkl-mnop-qrst");

    // Empty and whitespace

    [Fact]
    public void EmptySessionKey() => AssertError(SessionKeyValidationError.Empty, "");

    [Fact]
    public void WhitespaceOnlySessionKey() => AssertError(SessionKeyValidationError.Empty, "   ");

    [Fact]
    public void SessionKeyWithLeadingTrailingWhitespace() =>
        Assert.Equal(
            "sk-ant-sid01-abcdefghijklmnopqrstuvwxyz",
            SessionKeyValidator.Validate("  sk-ant-sid01-abcdefghijklmnopqrstuvwxyz  "));

    // Prefix

    [Fact]
    public void InvalidPrefix() =>
        AssertError(SessionKeyValidationError.InvalidPrefix, "invalid-prefix-abcdefghijklmnopqrstuvwxyz");

    [Fact]
    public void MissingPrefix() =>
        AssertError(SessionKeyValidationError.InvalidPrefix, "abcdefghijklmnopqrstuvwxyz1234567890");

    // Length

    [Fact]
    public void TooShort() => AssertError(SessionKeyValidationError.TooShort, "sk-ant-abc");

    [Fact]
    public void TooLong() => AssertError(SessionKeyValidationError.TooLong, "sk-ant-" + new string('a', 1000));

    // Character set

    [Fact]
    public void InvalidCharacters() =>
        AssertError(SessionKeyValidationError.InvalidCharacters, "sk-ant-sid01-hello@world!#$%");

    [Fact]
    public void InternalWhitespace() => AssertError(SessionKeyValidationError.ContainsWhitespace, "sk-ant-sid01 abcd efgh");

    // Security

    [Fact]
    public void NullBytes() => AssertError(SessionKeyValidationError.PotentiallyMalicious, "sk-ant-sid01-abc\0def");

    [Fact]
    public void PathTraversal() =>
        AssertError(SessionKeyValidationError.PotentiallyMalicious, "sk-ant-sid01-../etc/passwd");

    [Fact]
    public void ScriptInjection() =>
        Assert.Throws<SessionKeyValidationException>(
            () => SessionKeyValidator.Validate("sk-ant-sid01-<script>alert('xss')</script>"));

    // Format

    [Fact]
    public void ValidFormat() => SessionKeyValidator.Validate("sk-ant-sid01-abc-def-ghi-jkl");

    [Fact]
    public void InvalidFormatNoSeparators() =>
        AssertError(SessionKeyValidationError.InvalidFormat, "sk-ant-abcdefghijklmnopqrstuvwxyz");

    // Validation status

    [Fact]
    public void ValidationStatusValid()
    {
        var (isValid, errorMessage) = SessionKeyValidator.ValidationStatus("sk-ant-sid01-abcdefghijklmnopqrstuvwxyz");

        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidationStatusInvalid()
    {
        var (isValid, errorMessage) = SessionKeyValidator.ValidationStatus("invalid-key");

        Assert.False(isValid);
        Assert.NotNull(errorMessage);
    }

    // Edge cases

    [Fact]
    public void MixedCasePrefix() =>
        Assert.Throws<SessionKeyValidationException>(
            () => SessionKeyValidator.Validate("SK-ANT-sid01-abcdefghijklmnopqrstuvwxyz"));

    [Fact]
    public void UnicodeCharacters() =>
        Assert.Throws<SessionKeyValidationException>(() => SessionKeyValidator.Validate("sk-ant-sid01-héllo-wörld"));

    // Error messages

    [Theory]
    [InlineData("")]
    [InlineData("sk-ant-")]
    [InlineData("invalid")]
    public void ErrorMessages(string key) =>
        Assert.NotEmpty(Assert.Throws<SessionKeyValidationException>(() => SessionKeyValidator.Validate(key)).Message);

    [Fact]
    public void RecoverySuggestions() =>
        Assert.NotEmpty(
            Assert.Throws<SessionKeyValidationException>(() => SessionKeyValidator.Validate("")).RecoverySuggestion);

    // Real-world scenarios

    [Fact]
    public void TypicalUserInput() =>
        SessionKeyValidator.Validate("sk-ant-sid01-abcdefghijklmnopqrstuvwxyz1234567890-ABCDEFG\n");

    [Theory]
    [InlineData("sk-ant")] // too short
    [InlineData("sk ant sid01 abcd")] // spaces instead of hyphens
    [InlineData("sessionKey=sk-ant-sid01-abcd")] // copied the whole cookie line
    public void TypicalMistakes(string key)
    {
        Assert.Throws<SessionKeyValidationException>(() => SessionKeyValidator.Validate(key));
        Assert.False(SessionKeyValidator.IsValid(key));
    }

    private static void AssertError(SessionKeyValidationError expected, string key) =>
        Assert.Equal(
            expected, Assert.Throws<SessionKeyValidationException>(() => SessionKeyValidator.Validate(key)).Error);
}
