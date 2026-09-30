namespace ClaudeUsageTracker.Core.Localisation;

/// <param name="Code">Upstream language code, as persisted by upstream settings.</param>
/// <param name="CultureName">The .NET culture used for resources, mapped explicitly rather than inferred.</param>
/// <param name="DisplayName">Native name.</param>
/// <param name="EnglishName">English name.</param>
/// <param name="Flag">Upstream's flag emoji.</param>
public sealed record SupportedLanguage(
    string Code, string CultureName, string DisplayName, string EnglishName, string Flag);

/// <summary>Supported UI languages. Port of upstream <c>LanguageManager.SupportedLanguage</c>.</summary>
public static class SupportedLanguages
{
    public static readonly IReadOnlyList<SupportedLanguage> All =
    [
        new("en", "en", "English", "English", "🇬🇧"),
        new("es", "es", "Español", "Spanish", "🇪🇸"),
        new("fr", "fr", "Français", "French", "🇫🇷"),
        new("de", "de", "Deutsch", "German", "🇩🇪"),
        new("it", "it", "Italiano", "Italian", "🇮🇹"),
        new("pt", "pt", "Português", "Portuguese", "🇵🇹"),
        new("pt-BR", "pt-BR", "Português (Brasil)", "Brazilian Portuguese", "🇧🇷"),
        new("ja", "ja", "日本語", "Japanese", "🇯🇵"),
        new("ko", "ko", "한국어", "Korean", "🇰🇷"),
        // Upstream code "zh-cn" (resources in zh-ch.lproj) is Simplified Chinese.
        new("zh-cn", "zh-Hans", "简体中文", "Simplified Chinese", "🇨🇳"),
        new("zh-Hant", "zh-Hant", "繁體中文", "Traditional Chinese", "🇹🇼"),
        new("uk", "uk", "Українська", "Ukrainian", "🇺🇦"),
        new("tr", "tr", "Türkçe", "Turkish", "🇹🇷"),
        new("vi", "vi", "Tiếng Việt", "Vietnamese", "🇻🇳"),
    ];

    public static SupportedLanguage? FromCode(string code) => All.FirstOrDefault(l => l.Code == code);
}
