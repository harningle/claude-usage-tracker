using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Markup;
using ClaudeUsageTracker.Core.Localisation;

namespace ClaudeUsageTracker.App.Localisation;

/// <summary>
/// UI text by upstream localisation key, or by a <c>windows.</c> key for text only the port has.
/// Milestone 2 ships upstream's English table only; the other upstream languages and the explicit
/// zh-Hans/zh-Hant mapping are added in Milestone 6.
/// </summary>
public static class Strings
{
    private static readonly Lazy<StringsTable> Upstream = new(() => Load("en.lproj", "Localizable.strings"));
    private static readonly Lazy<StringsTable> Windows = new(() => Load("en.lproj", "Windows.strings"));

    /// <summary>The text for <paramref name="key"/>. A missing key is a programming error and throws.</summary>
    public static string Get(string key)
    {
        var table = key.StartsWith("windows.", StringComparison.Ordinal) ? Windows.Value : Upstream.Value;
        return table.TryGet(key, out var value)
            ? value
            : throw new KeyNotFoundException($"No localised string \"{key}\".");
    }

    /// <summary>
    /// The culture of the UI language, for month names, AM/PM designators and numbers inside UI text, so
    /// they match the language of the surrounding words. English until Milestone 6 adds languages; the
    /// 12/24-hour choice still follows the user's regional format.
    /// </summary>
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Formats an upstream printf-style template, e.g. <c>%@</c> or <c>%d</c>.</summary>
    public static string Format(string key, params object?[] args) =>
        StringsTable.Format(Get(key), Culture, args);

    private static StringsTable Load(string folder, string file)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n =>
            n.StartsWith("Localisation.", StringComparison.Ordinal)
            && n.Contains(folder, StringComparison.Ordinal)
            && n.EndsWith(file, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return StringsTable.Parse(reader.ReadToEnd());
    }
}

/// <summary>XAML access to <see cref="Strings"/>: <c>Text="{l:Loc common.refresh}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
