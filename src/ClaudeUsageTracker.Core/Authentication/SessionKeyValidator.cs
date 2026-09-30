using System.Globalization;
using System.Text;

namespace ClaudeUsageTracker.Core.Authentication;

public enum SessionKeyValidationError
{
    Empty,
    TooShort,
    TooLong,
    InvalidPrefix,
    InvalidCharacters,
    InvalidFormat,
    ContainsWhitespace,
    PotentiallyMalicious,
}

/// <summary>A rejected session key. Messages never include the key itself.</summary>
public sealed class SessionKeyValidationException(SessionKeyValidationError error, string message)
    : Exception(message)
{
    public SessionKeyValidationError Error { get; } = error;

    public string RecoverySuggestion => Error switch
    {
        SessionKeyValidationError.Empty or SessionKeyValidationError.TooShort or SessionKeyValidationError.TooLong =>
            "Please copy the complete sessionKey cookie value from your browser's DevTools",
        SessionKeyValidationError.InvalidPrefix =>
            "Ensure you're copying the sessionKey cookie value, which should start with 'sk-ant-'",
        SessionKeyValidationError.InvalidCharacters or SessionKeyValidationError.InvalidFormat =>
            "The session key may be corrupted. Please copy it again from your browser",
        SessionKeyValidationError.ContainsWhitespace => "Remove any spaces or newlines from the session key",
        SessionKeyValidationError.PotentiallyMalicious => "Please verify the session key is from a legitimate source",
        _ => throw new InvalidOperationException($"Unhandled session key error: {Error}"),
    };
}

/// <summary>
/// Format and safety checks for a pasted claude.ai session key. Port of upstream
/// <c>SessionKeyValidator</c> with its default (strict) configuration, the only one upstream uses.
/// A key that passes may still be rejected by the server.
/// </summary>
public static class SessionKeyValidator
{
    private const string RequiredPrefix = "sk-ant-";
    private const int MinLength = 20;
    private const int MaxLength = 500;
    private const string AllowedCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";

    private static readonly string[] SuspiciousPatterns = ["<script", "javascript:", "data:", "vbscript:", "file:"];

    /// <summary>Validates a key and returns it trimmed.</summary>
    /// <exception cref="SessionKeyValidationException">The key is rejected.</exception>
    public static string Validate(string sessionKey)
    {
        var trimmed = sessionKey.Trim();
        if (trimmed.Length == 0)
        {
            throw Fail(SessionKeyValidationError.Empty, "Session key cannot be empty");
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw Fail(SessionKeyValidationError.ContainsWhitespace, "Session key cannot contain whitespace");
        }

        // Upstream counts user-perceived characters, i.e. text elements rather than UTF-16 units.
        var length = new StringInfo(trimmed).LengthInTextElements;
        if (length < MinLength)
        {
            throw Fail(
                SessionKeyValidationError.TooShort,
                $"Session key too short (minimum: {MinLength}, actual: {length})");
        }

        if (length > MaxLength)
        {
            throw Fail(
                SessionKeyValidationError.TooLong,
                $"Session key too long (maximum: {MaxLength}, actual: {length})");
        }

        if (!trimmed.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            throw Fail(SessionKeyValidationError.InvalidPrefix, $"Session key must start with '{RequiredPrefix}'");
        }

        // Security checks run before the character-set check so malicious input gets a specific error.
        PerformSecurityChecks(trimmed);

        var invalid = new StringBuilder();
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (!rune.IsAscii || !AllowedCharacters.Contains((char)rune.Value))
            {
                invalid.Append(rune.ToString());
            }
        }

        if (invalid.Length > 0)
        {
            throw Fail(
                SessionKeyValidationError.InvalidCharacters,
                $"Session key contains invalid characters: Found disallowed characters: '{invalid}'");
        }

        var afterPrefix = trimmed[RequiredPrefix.Length..];
        if (afterPrefix.Length == 0)
        {
            throw Fail(SessionKeyValidationError.InvalidFormat, "Invalid session key format: No content after prefix");
        }

        if (!afterPrefix.Contains('-') && !afterPrefix.Contains('_'))
        {
            throw Fail(
                SessionKeyValidationError.InvalidFormat, "Invalid session key format: Missing expected separators");
        }

        return trimmed;
    }

    public static bool IsValid(string sessionKey) => ValidationStatus(sessionKey).IsValid;

    /// <summary>Validation result with the error message, for inline setup feedback.</summary>
    public static (bool IsValid, string? ErrorMessage) ValidationStatus(string sessionKey)
    {
        try
        {
            Validate(sessionKey);
            return (true, null);
        }
        catch (SessionKeyValidationException e)
        {
            return (false, e.Message);
        }
    }

    private static void PerformSecurityChecks(string sessionKey)
    {
        if (sessionKey.Contains('\0'))
        {
            throw Malicious("Contains null bytes");
        }

        foreach (var rune in sessionKey.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                throw Malicious("Contains control characters");
            }
        }

        if (sessionKey.Contains("..", StringComparison.Ordinal) || sessionKey.Contains("//", StringComparison.Ordinal))
        {
            throw Malicious("Contains suspicious patterns");
        }

        var lowered = sessionKey.ToLowerInvariant();
        if (SuspiciousPatterns.Any(pattern => lowered.Contains(pattern, StringComparison.Ordinal)))
        {
            throw Malicious("Contains script injection pattern");
        }
    }

    private static SessionKeyValidationException Malicious(string reason) =>
        Fail(SessionKeyValidationError.PotentiallyMalicious, $"Session key rejected for security: {reason}");

    private static SessionKeyValidationException Fail(SessionKeyValidationError error, string message) =>
        new(error, message);
}
