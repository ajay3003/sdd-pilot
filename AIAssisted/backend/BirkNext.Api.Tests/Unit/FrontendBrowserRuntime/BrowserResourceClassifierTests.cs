using BirkNext.Api.Services.FrontendBrowserRuntime;
using Xunit;

namespace BirkNext.Api.Tests.Unit.FrontendBrowserRuntime;


public sealed class BrowserResourceClassifierTests
{
    private readonly BrowserResourceClassifier _classifier = new();

    [Theory]
    [InlineData("https://example.com/_framework/dotnet.wasm")]
    [InlineData("https://example.com/blazor.boot.json")]
    [InlineData("https://example.com/_framework/MyApp.WebAssembly.dll")]
    [InlineData("https://example.com/blazor.webassembly.js")]
    [InlineData("https://example.com/app.js")]
    [InlineData("https://example.com/app.css")]
    public void Classify_CriticalResources_ReturnsCritical(string url)
    {
        var result = _classifier.Classify(url, "script");
        Assert.True(result.IsCritical);
    }

    [Theory]
    [InlineData("https://example.com/favicon.ico")]
    [InlineData("https://example.com/image.png")]
    [InlineData("https://example.com/image.jpg")]
    [InlineData("https://example.com/font.woff2")]
    [InlineData("https://example.com/analytics.js")]
    [InlineData("https://example.com/google-analytics.js")]
    [InlineData("https://example.com/hotjar.js")]
    public void Classify_NonCriticalResources_ReturnsNonCritical(string url)
    {
        var result = _classifier.Classify(url, "image");
        Assert.False(result.IsCritical);
        Assert.Equal("NonCritical", result.Category);
    }

    [Theory]
    [InlineData("https://example.com/api/data")]
    [InlineData("https://example.com/important-config.json")]
    public void Classify_OtherResources_ReturnsImportant(string url)
    {
        var result = _classifier.Classify(url, "fetch");
        Assert.False(result.IsCritical);
        Assert.Equal("Important", result.Category);
    }

    [Fact]
    public void Classify_EmptyUrl_ReturnsUnknown()
    {
        var result = _classifier.Classify("", "unknown");
        Assert.False(result.IsCritical);
        Assert.Equal("Unknown", result.Category);
    }

    [Fact]
    public void Classify_WasmFile_ReturnsCritical()
    {
        var result = _classifier.Classify("https://example.com/runtime.wasm", "script");
        Assert.True(result.IsCritical);
    }

    [Theory]
    [InlineData("HTTPS://EXAMPLE.COM/_FRAMEWORK/DOTNET.WASM")]
    [InlineData("https://example.com/_Framework/dotnet.wasm")]
    public void Classify_CaseInsensitive_CorrectlyClassifies(string url)
    {
        var result = _classifier.Classify(url, "script");
        Assert.True(result.IsCritical);
    }
}
