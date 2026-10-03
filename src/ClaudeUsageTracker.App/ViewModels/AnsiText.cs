using System.Globalization;
using System.Windows.Media;

namespace ClaudeUsageTracker.App.ViewModels;

/// <summary>A stretch of status line text in one colour; no colour means the terminal's own text colour.</summary>
public sealed record AnsiRun(string Text, Color? Colour);

/// <summary>
/// Reads the colour codes the status line renderer emits, so the settings preview shows the very text a
/// terminal would get: reset, the basic foreground colours, the 256-colour palette and 24-bit colours.
/// </summary>
public static class AnsiText
{
    // The basic colours as upstream's preview draws them (its TerminalColors).
    private static readonly Dictionary<int, Color> Basic = new()
    {
        [34] = Color.FromRgb(0, 0, 238), [32] = Color.FromRgb(0, 187, 0), [33] = Color.FromRgb(187, 187, 0),
        [35] = Color.FromRgb(187, 0, 187), [36] = Color.FromRgb(0, 187, 187), [90] = Color.FromRgb(128, 128, 128),
    };

    private static readonly byte[] CubeLevels = [0, 95, 135, 175, 215, 255];

    public static IReadOnlyList<AnsiRun> Parse(string text)
    {
        var runs = new List<AnsiRun>();
        Color? colour = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\e' || i + 1 >= text.Length || text[i + 1] != '[')
            {
                continue;
            }

            var end = text.IndexOf('m', i);
            if (end < 0)
            {
                break;
            }

            Add(runs, text[start..i], colour);
            colour = Apply(colour, text[(i + 2)..end]);
            start = end + 1;
            i = end;
        }

        Add(runs, text[start..], colour);
        return runs;
    }

    /// <summary>A colour of the xterm 256-colour palette: the colour cube and the grey ramp; the first 16 are not used by the renderer.</summary>
    public static Color? Indexed(int index) => index switch
    {
        >= 16 and <= 231 => Color.FromRgb(CubeLevels[(index - 16) / 36], CubeLevels[(index - 16) / 6 % 6], CubeLevels[(index - 16) % 6]),
        >= 232 and <= 255 => Color.FromRgb((byte)(8 + (index - 232) * 10), (byte)(8 + (index - 232) * 10), (byte)(8 + (index - 232) * 10)),
        _ => null,
    };

    private static Color? Apply(Color? current, string parameters)
    {
        var codes = parameters.Split(';').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 0).ToArray();
        for (var i = 0; i < codes.Length; i++)
        {
            switch (codes[i])
            {
                case 0:
                    current = null;
                    break;
                case 38 when i + 2 < codes.Length && codes[i + 1] == 5:
                    current = Indexed(codes[i + 2]);
                    i += 2;
                    break;
                case 38 when i + 4 < codes.Length && codes[i + 1] == 2:
                    current = Color.FromRgb((byte)codes[i + 2], (byte)codes[i + 3], (byte)codes[i + 4]);
                    i += 4;
                    break;
                case var code when Basic.TryGetValue(code, out var basic):
                    current = basic;
                    break;
            }
        }

        return current;
    }

    private static void Add(List<AnsiRun> runs, string text, Color? colour)
    {
        if (text.Length > 0)
        {
            runs.Add(new AnsiRun(text, colour));
        }
    }
}
