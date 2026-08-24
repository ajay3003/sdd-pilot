using BirkNext.Api.Services.FrontendBrowserRuntime;
using Xunit;

namespace BirkNext.Api.Tests.Unit.FrontendBrowserRuntime;


public sealed class BrowserTargetValidatorTests
{
    private readonly BrowserTargetValidator _validator = new();

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://api.github.com")]
    [InlineData("http://localhost:5000")]
    public void ValidateTarget_ValidPublicUrls_ReturnsValid(string url)
    {
        var result = _validator.ValidateTarget(url, "Public");
        Assert.True(result.IsValid);
        Assert.Equal("Public", result.ClassifiedType);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://example.com")]
    public void ValidateTarget_BlockedSchemes_ReturnsInvalid(string url)
    {
        var result = _validator.ValidateTarget(url);
        Assert.False(result.IsValid);
        Assert.Contains("not allowed", result.BlockReason ?? "");
    }

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://metadata.google.internal")]
    public void ValidateTarget_MetadataEndpoints_ReturnsBlocked(string url)
    {
        var result = _validator.ValidateTarget(url);
        Assert.False(result.IsValid);
        Assert.Contains("Metadata", result.BlockReason ?? "");
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://::1")]
    public void ValidateTarget_LoopbackAddresses_ReturnsBlocked(string url)
    {
        var result = _validator.ValidateTarget(url);
        Assert.False(result.IsValid);
        Assert.Contains("Loopback", result.BlockReason ?? "");
    }

    [Theory]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://192.168.1.1")]
    public void ValidateTarget_PrivateNetworks_RequiresInternalContext(string url)
    {
        var resultPublic = _validator.ValidateTarget(url, "Public");
        Assert.False(resultPublic.IsValid);
        Assert.Contains("Private", resultPublic.BlockReason ?? "");

        var resultInternal = _validator.ValidateTarget(url, "Internal");
        Assert.True(resultInternal.IsValid);
    }

    [Fact]
    public void ValidateTarget_UserInfoInUrl_ReturnsBlocked()
    {
        var result = _validator.ValidateTarget("http://user:password@example.com");
        Assert.False(result.IsValid);
        Assert.Contains("userinfo", result.BlockReason ?? "");
    }

    [Fact]
    public void ValidateTarget_EmptyUrl_ReturnsInvalid()
    {
        var result = _validator.ValidateTarget("");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateTarget_InvalidUrl_ReturnsInvalid()
    {
        var result = _validator.ValidateTarget("not a url");
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("http://example.com/path", "https://example.com/different")]
    [InlineData("http://example.com/path", "http://example.com/different")]
    public void ValidateRedirectTarget_SameHostRedirect_ReturnsValid(string original, string redirect)
    {
        var result = _validator.ValidateRedirectTarget(redirect, "example.com", "Public");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateRedirectTarget_RedirectToBlockedHost_ReturnsBlocked()
    {
        var result = _validator.ValidateRedirectTarget(
            "http://169.254.169.254/",
            "example.com",
            "Public");

        Assert.False(result.IsValid);
        Assert.Contains("blocked", result.BlockReason ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
