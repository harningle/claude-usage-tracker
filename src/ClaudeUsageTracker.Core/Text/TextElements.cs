using System.Globalization;

namespace ClaudeUsageTracker.Core.Text;

internal static class TextElements
{
    /// <summary>The first <paramref name="count"/> user-perceived characters, never splitting one.</summary>
    public static string Prefix(string text, int count)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= count ? text : info.SubstringByTextElements(0, count);
    }
}
