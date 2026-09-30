using ClaudeUsageTracker.Core.Networking;

namespace ClaudeUsageTracker.Tests.Networking;

/// <summary>Port of upstream <c>URLBuilderTests</c>. Upstream "contains" checks are exact here.</summary>
public class UrlBuilderTests
{
    // Initialisation

    [Fact]
    public void ValidBaseUrl() =>
        Assert.Equal("https://api.example.com", new UrlBuilder("https://api.example.com").Build().OriginalString);

    [Fact]
    public void InvalidBaseUrl() => AssertError(UrlBuilderError.InvalidBaseUrl, () => new UrlBuilder("not a url"));

    [Fact]
    public void MissingScheme() => AssertError(UrlBuilderError.InvalidBaseUrl, () => new UrlBuilder("api.example.com"));

    // Paths

    [Fact]
    public void AppendSinglePath() =>
        Assert.Equal(
            "https://api.example.com/users",
            new UrlBuilder("https://api.example.com").AppendingPath("/users").Build().OriginalString);

    [Fact]
    public void AppendMultiplePaths()
    {
        var url = new UrlBuilder("https://api.example.com")
            .AppendingPath("/users")
            .AppendingPath("/123")
            .AppendingPath("/profile")
            .Build();

        Assert.Equal("https://api.example.com/users/123/profile", url.OriginalString);
    }

    [Fact]
    public void AppendPathComponents()
    {
        var url = new UrlBuilder("https://api.example.com")
            .AppendingPathComponents(["/users", "123", "/profile"])
            .Build();

        Assert.Equal("https://api.example.com/users/123/profile", url.OriginalString);
    }

    [Fact]
    public void PathTraversalPrevention() =>
        AssertError(
            UrlBuilderError.InvalidPath, () => new UrlBuilder("https://api.example.com").AppendingPath("../etc/passwd"));

    // Query parameters

    [Fact]
    public void AddSingleQueryParameter()
    {
        var url = new UrlBuilder("https://api.example.com")
            .AppendingPath("/search")
            .AddingQueryParameter("q", "test")
            .Build();

        Assert.Equal("https://api.example.com/search?q=test", url.OriginalString);
    }

    [Fact]
    public void AddMultipleQueryParameters()
    {
        var url = new UrlBuilder("https://api.example.com")
            .AppendingPath("/search")
            .AddingQueryParameter("q", "test")
            .AddingQueryParameter("limit", "10")
            .Build();

        Assert.Equal("https://api.example.com/search?q=test&limit=10", url.OriginalString);
    }

    [Fact]
    public void EmptyParameterName() =>
        AssertError(
            UrlBuilderError.InvalidQueryParameter,
            () => new UrlBuilder("https://api.example.com").AddingQueryParameter("", "test"));

    // Convenience builders

    [Fact]
    public void ClaudeApiBuilder() =>
        Assert.Equal("https://claude.ai/api/organizations", UrlBuilder.ClaudeApi("/organizations").Build().OriginalString);

    [Fact]
    public void ConsoleApiBuilder() =>
        Assert.Equal(
            "https://console.anthropic.com/api/organizations",
            UrlBuilder.ConsoleApi("/organizations").Build().OriginalString);

    [Fact]
    public void ClaudeStatusBuilder() =>
        Assert.Equal(
            "https://status.claude.com/api/v2/status.json", UrlBuilder.ClaudeStatus("/status.json").Build().OriginalString);

    [Fact]
    public void ComplexClaudeUrl()
    {
        var url = UrlBuilder.ClaudeApi()
            .AppendingPathComponents(["/organizations", "org_123", "/chat_conversations", "conv_456", "/completion"])
            .Build();

        Assert.Equal(
            "https://claude.ai/api/organizations/org_123/chat_conversations/conv_456/completion", url.OriginalString);
    }

    /// <summary>Upstream's case asserts only that appending an embedded ".." throws; the Result wrapper is not ported.</summary>
    [Fact]
    public void BuildResultFailure() =>
        AssertError(UrlBuilderError.InvalidPath, () => new UrlBuilder("https://api.example.com").AppendingPath("..invalid.."));

    // Edge cases

    [Fact]
    public void UrlWithSpecialCharacters()
    {
        var url = new UrlBuilder("https://api.example.com")
            .AppendingPath("/search")
            .AddingQueryParameter("q", "hello world")
            .Build();

        Assert.Equal("https://api.example.com/search?q=hello%20world", url.OriginalString);
    }

    [Fact]
    public void UrlWithMultipleSlashes()
    {
        var url = new UrlBuilder("https://api.example.com")
            .AppendingPath("/users/")
            .AppendingPath("/123/")
            .Build();

        Assert.Equal("https://api.example.com/users/123", url.OriginalString);
    }

    // Path escaping (no upstream case; upstream escapes through URLComponents.path)

    [Theory]
    [InlineData("item#detail", "/item%23detail")]
    [InlineData("a?b", "/a%3Fb")]
    [InlineData("hello world", "/hello%20world")]
    [InlineData("100%", "/100%25")]
    [InlineData("café", "/caf%C3%A9")]
    [InlineData("a:b@c!$&'()*+,;=-._~", "/a:b@c!$&'()*+,;=-._~")]
    public void AppendedPathEscapesReservedCharacters(string path, string expectedPath) =>
        Assert.Equal(expectedPath, new UrlBuilder("https://api.example.com").AppendingPath(path).Build().AbsolutePath);

    [Fact]
    public void FragmentCharacterInPathDoesNotSwallowQuery()
    {
        var url = new UrlBuilder("https://api.example.com")
            .AppendingPath("item#detail")
            .AddingQueryParameter("q", "x")
            .Build();

        Assert.Equal("https://api.example.com/item%23detail?q=x", url.AbsoluteUri);
        Assert.Equal("?q=x", url.Query);
        Assert.Empty(url.Fragment);
    }

    [Theory]
    [InlineData("%2e%2e/other", "/api/%252e%252e/other")]
    [InlineData("%2E%2E/other", "/api/%252E%252E/other")]
    public void EncodedTraversalStaysInsideBasePath(string path, string expectedPath) =>
        Assert.Equal(expectedPath, UrlBuilder.ClaudeApi().AppendingPath(path).Build().AbsolutePath);

    private static void AssertError(UrlBuilderError expected, Func<object> action) =>
        Assert.Equal(expected, Assert.Throws<UrlBuilderException>(action).Error);
}
