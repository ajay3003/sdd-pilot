using BirkNext.Api.Services.FrontendBrowserRuntime;
using Xunit;

namespace BirkNext.Api.Tests.Unit.FrontendBrowserRuntime;


public sealed class BrowserEvidenceSanitizerTests
{
    private readonly BrowserEvidenceSanitizer _sanitizer = new();

    [Theory]
    [InlineData("https://example.com?token=secret123", "https://example.com?token=[REDACTED]")]
    [InlineData("https://example.com?access_token=abc", "https://example.com?access_token=[REDACTED]")]
    [InlineData("https://example.com?api_key=xyz", "https://example.com?api_key=[REDACTED]")]
    [InlineData("https://example.com?key=sensitive", "https://example.com?key=[REDACTED]")]
    public void SanitizeUrl_SensitiveQueryParams_Redacted(string url, string expected)
    {
        var result = _sanitizer.SanitizeUrl(url);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeUrl_UserInfo_Redacted()
    {
        var result = _sanitizer.SanitizeUrl("http://admin:password123@example.com");
        Assert.Contains("[REDACTED]@", result);
        Assert.DoesNotContain("password123", result);
    }

    [Fact]
    public void SanitizeUrl_MultipleParams_RedactsAll()
    {
        var url = "https://example.com?token=secret&api_key=key123&other=value";
        var result = _sanitizer.SanitizeUrl(url);

        Assert.Contains("token=[REDACTED]", result);
        Assert.Contains("api_key=[REDACTED]", result);
        Assert.Contains("other=value", result); // Not sensitive
    }

    [Theory]
    [InlineData("Error connecting with Bearer eyJ...", "Error connecting with Bearer [REDACTED]")]
    [InlineData("JWT token failed: eyJhbGciOiJIUzI1NiIs...", "JWT token failed: [REDACTED]")]
    public void SanitizeMessage_BearerTokens_Redacted(string message, string expectedPattern)
    {
        var result = _sanitizer.SanitizeMessage(message);
        Assert.DoesNotContain("eyJ", result);
    }

    [Fact]
    public void SanitizeMessage_ApiKeyLike_Redacted()
    {
        var message = "API key error: sk_live_51234567890abcdefghijk";
        var result = _sanitizer.SanitizeMessage(message);

        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("sk_live_", result);
    }

    [Fact]
    public void SanitizeMessage_NormalText_Unchanged()
    {
        var message = "This is a normal error message without secrets";
        var result = _sanitizer.SanitizeMessage(message);

        Assert.Equal(message, result);
    }

    [Fact]
    public void SanitizeConsoleEvents_RemovesSecrets()
    {
        var events = new List<BrowserConsoleEvent>
        {
            new("error", "Failed with token=secret123", "https://example.com?api_key=key", 1, 1)
        };

        var result = _sanitizer.SanitizeConsoleEvents(events);

        Assert.Single(result);
        Assert.Contains("[REDACTED]", result[0].Message);
        Assert.Contains("[REDACTED]", result[0].Location ?? "");
    }

    [Fact]
    public void SanitizePageErrors_RemovesSecrets()
    {
        var errors = new List<BrowserPageError>
        {
            new("Auth failed: Bearer abc123def", "https://api.example.com/auth?token=secret", null)
        };

        var result = _sanitizer.SanitizePageErrors(errors);

        Assert.Single(result);
        Assert.Contains("[REDACTED]", result[0].Message);
    }

    [Fact]
    public void SanitizeResourceFailures_SanitizesUrls()
    {
        var failures = new List<BrowserResourceFailure>
        {
            new("https://api.example.com?api_key=secret123", "fetch", "401 Unauthorized", 401)
        };

        var result = _sanitizer.SanitizeResourceFailures(failures);

        Assert.Single(result);
        Assert.Contains("[REDACTED]", result[0].Url);
    }

    [Fact]
    public void SanitizeFindings_RemovesSecretsFromAll()
    {
        var findings = new List<BrowserRuntimeFinding>
        {
            new(
                "test-finding",
                "API Key Exposed",
                BrowserRuntimeFindingSeverity.Critical,
                "Security",
                "Found API key: sk_live_123456789",
                "Remove the exposed key",
                new List<string> { "URL: https://example.com?key=secret" })
        };

        var result = _sanitizer.SanitizeFindings(findings);

        Assert.Single(result);
        Assert.Contains("[REDACTED]", result[0].Description);
        Assert.True(result[0].Evidence.Any(e => e.Contains("[REDACTED]")));
    }

    [Fact]
    public void SanitizeUrl_EmptyUrl_Unchanged()
    {
        var result = _sanitizer.SanitizeUrl("");
        Assert.Equal("", result);
    }

    [Fact]
    public void SanitizeMessage_NullOrEmpty_Unchanged()
    {
        Assert.Null(_sanitizer.SanitizeMessage(null));
        Assert.Equal("", _sanitizer.SanitizeMessage(""));
    }
}
