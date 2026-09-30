using ClaudeUsageTracker.Core.Localisation;

namespace ClaudeUsageTracker.Tests.Localisation;

/// <summary>Port of upstream <c>LanguageManagerTests</c>, plus the explicit Chinese culture mapping.</summary>
public class SupportedLanguagesTests
{
    [Fact]
    public void VietnameseCaseExists() => Assert.NotNull(SupportedLanguages.FromCode("vi"));

    [Fact]
    public void VietnameseProperties()
    {
        var vi = SupportedLanguages.FromCode("vi");

        Assert.NotNull(vi);
        Assert.Equal("vi", vi.Code);
        Assert.Equal("Tiếng Việt", vi.DisplayName);
        Assert.Equal("Vietnamese", vi.EnglishName);
        Assert.Equal("🇻🇳", vi.Flag);
    }

    [Fact]
    public void SupportedLanguageCountIs14() => Assert.Equal(14, SupportedLanguages.All.Count);

    [Theory]
    [InlineData("zh-cn", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hant")]
    public void ChineseCodesMapToExplicitCultures(string code, string cultureName) =>
        Assert.Equal(cultureName, SupportedLanguages.FromCode(code)?.CultureName);
}
