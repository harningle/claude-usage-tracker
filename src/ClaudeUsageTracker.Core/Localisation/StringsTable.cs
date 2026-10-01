using System.Globalization;
using System.Text;

namespace ClaudeUsageTracker.Core.Localisation;

/// <summary>
/// A parsed upstream <c>Localizable.strings</c> file. The port ships upstream's files unchanged, so
/// lookups use upstream keys and translations can be added without rewriting the UI.
/// </summary>
public sealed class StringsTable
{
    private readonly Dictionary<string, string> _entries;

    private StringsTable(Dictionary<string, string> entries) => _entries = entries;

    public int Count => _entries.Count;

    public bool TryGet(string key, out string value) => _entries.TryGetValue(key, out value!);

    /// <summary>
    /// Parses the <c>"key" = "value";</c> format with <c>/* */</c> and <c>//</c> comments and the
    /// escapes <c>\" \\ \n \t \r</c>. Throws <see cref="FormatException"/> on malformed input or a
    /// duplicate key rather than guessing which entry was meant.
    /// </summary>
    public static StringsTable Parse(string text)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        while (true)
        {
            SkipTrivia(text, ref position);
            if (position >= text.Length)
            {
                return new StringsTable(entries);
            }

            var key = ReadQuoted(text, ref position);
            SkipTrivia(text, ref position);
            Expect(text, ref position, '=');
            SkipTrivia(text, ref position);
            var value = ReadQuoted(text, ref position);
            SkipTrivia(text, ref position);
            Expect(text, ref position, ';');
            entries[key] = value;
        }
    }

    /// <summary>
    /// Formats an upstream printf-style template: <c>%@</c>, <c>%d</c>, <c>%ld</c>, <c>%i</c>,
    /// <c>%u</c>, <c>%f</c> and <c>%.Nf</c>, optionally positional (<c>%1$@</c>), and <c>%%</c>.
    /// Upstream only formats when arguments are supplied, so templates used without arguments
    /// keep a literal <c>%</c> and must not be passed here.
    /// </summary>
    public static string Format(string template, IFormatProvider culture, params object?[] args)
    {
        var result = new StringBuilder(template.Length + 16);
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '%')
            {
                result.Append(c);
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '%')
            {
                result.Append('%');
                i++;
                continue;
            }

            var j = i + 1;
            int? position = null;
            var digits = ReadDigits(template, ref j);
            if (digits is not null && j < template.Length && template[j] == '$')
            {
                position = digits - 1;
                j++;
            }
            else
            {
                j = i + 1;
            }

            int? precision = null;
            if (j < template.Length && template[j] == '.')
            {
                j++;
                precision = ReadDigits(template, ref j)
                    ?? throw new FormatException($"Missing precision in \"{template}\".");
            }

            while (j < template.Length && template[j] == 'l')
            {
                j++;
            }

            if (j >= template.Length)
            {
                throw new FormatException($"Unterminated format specifier in \"{template}\".");
            }

            var index = position ?? next++;
            if (index < 0 || index >= args.Length)
            {
                throw new FormatException($"Template \"{template}\" needs more than {args.Length} arguments.");
            }

            var arg = args[index];
            result.Append(template[j] switch
            {
                '@' => Convert.ToString(arg, culture),
                'd' or 'i' or 'u' => Convert.ToInt64(arg, culture).ToString(culture),
                'f' => Convert.ToDouble(arg, culture).ToString("F" + (precision ?? 6), culture),
                var other => throw new FormatException($"Unsupported format specifier %{other} in \"{template}\"."),
            });
            i = j;
        }

        return result.ToString();
    }

    private static int? ReadDigits(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
        }

        return position > start
            ? int.Parse(text.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }

    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]) || text[position] == '﻿')
            {
                position++;
            }
            else if (string.CompareOrdinal(text, position, "/*", 0, 2) == 0)
            {
                var end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                position = end < 0 ? throw new FormatException("Unterminated comment.") : end + 2;
            }
            else if (string.CompareOrdinal(text, position, "//", 0, 2) == 0)
            {
                var end = text.IndexOf('\n', position);
                position = end < 0 ? text.Length : end + 1;
            }
            else
            {
                return;
            }
        }
    }

    private static void Expect(string text, ref int position, char expected)
    {
        if (position >= text.Length || text[position] != expected)
        {
            throw new FormatException($"Expected '{expected}' at offset {position}.");
        }

        position++;
    }

    private static string ReadQuoted(string text, ref int position)
    {
        Expect(text, ref position, '"');
        var value = new StringBuilder();
        while (position < text.Length)
        {
            var c = text[position++];
            if (c == '"')
            {
                return value.ToString();
            }

            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            if (position >= text.Length)
            {
                break;
            }

            var escaped = text[position++];
            value.Append(escaped switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '"' or '\\' or '\'' => escaped,
                _ => throw new FormatException($"Unsupported escape \\{escaped} at offset {position - 1}."),
            });
        }

        throw new FormatException("Unterminated string.");
    }
}
