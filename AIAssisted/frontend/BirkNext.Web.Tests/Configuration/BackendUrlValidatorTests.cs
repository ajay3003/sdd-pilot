using BirkNext.Web.Configuration;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Configuration;

public sealed class BackendUrlValidatorTests
{
    // ────────────────────────────────────────
    // Production Tests
    // ────────────────────────────────────────

    [Fact]
    public void ProductionHttps_Accepted()
    {
        // Act & Assert — should not throw
        BackendUrlValidator.Validate("https://api.example.com", "Production");
    }

    [Fact]
    public void ProductionHttpLocalhost_Rejected()
    {
        // Act & Assert
        var action = () => BackendUrlValidator.Validate("http://localhost:5000", "Production");
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*HTTP is only allowed for loopback*");
    }

    [Fact]
    public void ProductionHttpNonLoopback_Rejected()
    {
        // Act & Assert
        var action = () => BackendUrlValidator.Validate("http://10.0.0.1:5000", "Production");
        action.Should().Throw<InvalidOperationException>();
    }

    // ────────────────────────────────────────
    // Development Loopback Tests
    // ────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://127.1.1.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("http://::1:5000")]
    public void DevelopmentHttpLoopback_Accepted(string url)
    {
        // Act & Assert — should not throw
        BackendUrlValidator.Validate(url, "Development");
    }

    // ────────────────────────────────────────
    // Development Non-Loopback Tests
    // ────────────────────────────────────────

    [Theory]
    [InlineData("http://10.0.0.1:5000")]
    [InlineData("http://192.168.1.1:5000")]
    [InlineData("http://m2lbdev.bufetat.no")]
    public void DevelopmentHttpNonLoopback_Rejected(string url)
    {
        // Act & Assert
        var action = () => BackendUrlValidator.Validate(url, "Development");
        action.Should().Throw<InvalidOperationException>();
    }

    // ────────────────────────────────────────
    // Development HTTPS Tests
    // ────────────────────────────────────────

    [Theory]
    [InlineData("https://localhost:5000")]
    [InlineData("https://m2lbdev.bufetat.no")]
    [InlineData("https://api.example.com")]
    public void DevelopmentHttps_Accepted(string url)
    {
        // Act & Assert — should not throw
        BackendUrlValidator.Validate(url, "Development");
    }

    // ────────────────────────────────────────
    // Case Insensitivity Tests
    // ────────────────────────────────────────

    [Fact]
    public void ProtocolCaseInsensitive()
    {
        // Act & Assert — uppercase HTTPS should be accepted
        BackendUrlValidator.Validate("HTTPS://localhost:5000", "Development");
    }

    [Fact]
    public void LocalhostCaseInsensitive()
    {
        // Act & Assert — uppercase LOCALHOST should be treated as loopback
        BackendUrlValidator.Validate("http://LOCALHOST:5000", "Development");
    }
}
