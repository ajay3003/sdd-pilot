using BirkNext.Api.Configuration;
using BirkNext.Api.Filters;
using BirkNext.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Tests verifying HTTPS transport policy enforcement on Target Environment Detection API.
///
/// REQUIREMENT:
/// - Production: HTTPS required, HTTP rejected (426 Upgrade Required)
/// - Development: HTTPS required, but HTTP allowed from loopback only
/// - Non-loopback HTTP always rejected
/// </summary>
public sealed class TargetEnvironmentDetectionControllerHttpsTests
{
    private readonly RequireTargetDetectionHttpsFilter _filter;
    private readonly Mock<ILogger<RequireTargetDetectionHttpsFilter>> _loggerMock;
    private readonly Mock<IWebHostEnvironment> _environmentMock;

    public TargetEnvironmentDetectionControllerHttpsTests()
    {
        _loggerMock = new Mock<ILogger<RequireTargetDetectionHttpsFilter>>();
        _environmentMock = new Mock<IWebHostEnvironment>();
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns("Production");

        var options = new TargetDetectionOptions { RequireHttps = true };
        _filter = new RequireTargetDetectionHttpsFilter(options, _loggerMock.Object, _environmentMock.Object);
    }

    private void SetEnvironment(string environmentName)
    {
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns(environmentName);
    }

    // ────────────────────────────────────────────────────────────
    // CASE A: Production HTTPS — accepted
    // ────────────────────────────────────────────────────────────
    [Fact]
    public void ProductionHttps_Request_Accepted()
    {
        // Arrange
        var context = CreateActionExecutingContext(isHttps: true, host: "api.example.com");

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeNull("HTTPS request should be accepted");
    }

    // ────────────────────────────────────────────────────────────
    // CASE B: Production HTTP non-loopback — rejected (426)
    // ────────────────────────────────────────────────────────────
    [Fact]
    public void ProductionHttpNonLoopback_Request_Rejected()
    {
        // Arrange
        var context = CreateActionExecutingContext(isHttps: false, host: "api.example.com");

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(426, "HTTP to non-loopback should return 426 Upgrade Required");
    }

    // ────────────────────────────────────────────────────────────
    // CASE C: Production HTTP loopback — rejected (loopback exception only in Development)
    // ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public void ProductionHttpLoopback_Request_Rejected(string loopbackHost)
    {
        // Arrange - Production environment
        SetEnvironment("Production");
        var context = CreateActionExecutingContext(isHttps: false, host: loopbackHost);

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(426, "Production should reject all HTTP, including loopback");
    }

    // ────────────────────────────────────────────────────────────
    // CASE D: Development HTTPS — accepted
    // ────────────────────────────────────────────────────────────
    [Fact]
    public void DevelopmentHttps_Request_Accepted()
    {
        // Arrange
        SetEnvironment("Development");
        var context = CreateActionExecutingContext(isHttps: true, host: "localhost");

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeNull("HTTPS request should be accepted in Development");
    }

    // ────────────────────────────────────────────────────────────
    // CASE E: Development HTTP loopback — accepted
    // ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.1.1")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    public void DevelopmentHttpLoopback_Request_Accepted(string loopbackHost)
    {
        // Arrange
        SetEnvironment("Development");
        var context = CreateActionExecutingContext(isHttps: false, host: loopbackHost);

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeNull("HTTP loopback should be accepted in Development");
    }

    // ────────────────────────────────────────────────────────────
    // CASE F: Development HTTP non-loopback — rejected
    // ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("api.example.com")]
    public void DevelopmentHttpNonLoopback_Request_Rejected(string nonLoopbackHost)
    {
        // Arrange
        SetEnvironment("Development");
        var context = CreateActionExecutingContext(isHttps: false, host: nonLoopbackHost);

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(426, "Even in Development, HTTP non-loopback should be rejected");
    }

    // ────────────────────────────────────────────────────────────
    // CASE G: RequireHttps disabled — all requests accepted
    // ────────────────────────────────────────────────────────────
    [Fact]
    public void RequireHttpsDisabled_HttpRequest_Accepted()
    {
        // Arrange
        var options = new TargetDetectionOptions { RequireHttps = false };
        var filter = new RequireTargetDetectionHttpsFilter(options, _loggerMock.Object, _environmentMock.Object);
        var context = CreateActionExecutingContext(isHttps: false, host: "example.com");

        // Act
        filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeNull("HTTP should be accepted when RequireHttps is false");
    }

    // ────────────────────────────────────────────────────────────
    // IPv4 loopback boundary tests
    // ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("127.0.0.0")]
    [InlineData("127.255.255.255")]
    [InlineData("126.0.0.1")]      // Should be rejected - not loopback
    [InlineData("128.0.0.1")]      // Should be rejected - not loopback
    public void Ipv4LoopbackBoundary_DevelopmentHttp(string host)
    {
        // Arrange
        SetEnvironment("Development");
        var context = CreateActionExecutingContext(isHttps: false, host: host);
        var isLoopback = host.StartsWith("127.");

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        if (isLoopback)
        {
            context.Result.Should().BeNull($"{host} should be treated as loopback");
        }
        else
        {
            context.Result.Should().BeOfType<StatusCodeResult>()
                .Which.StatusCode.Should().Be(426, $"{host} should be rejected as non-loopback");
        }
    }

    // ────────────────────────────────────────────────────────────
    // Case sensitivity
    // ────────────────────────────────────────────────────────────
    [Fact]
    public void LocalhostCaseInsensitive_Treated_As_Loopback()
    {
        // Arrange
        SetEnvironment("Development");
        var context = CreateActionExecutingContext(isHttps: false, host: "LOCALHOST");

        // Act
        _filter.OnActionExecuting(context);

        // Assert
        context.Result.Should().BeNull("localhost should be case-insensitive loopback");
    }

    // ────────────────────────────────────────────────────────────
    // Helper: Create test context with configurable isHttps and host
    // ────────────────────────────────────────────────────────────
    private static ActionExecutingContext CreateActionExecutingContext(bool isHttps, string host)
    {
        var httpContextMock = new Mock<HttpContext>();
        var requestMock = new Mock<HttpRequest>();

        requestMock.SetupGet(x => x.IsHttps).Returns(isHttps);
        requestMock.SetupGet(x => x.Host).Returns(new HostString(host));

        httpContextMock.SetupGet(x => x.Request).Returns(requestMock.Object);

        var actionDescriptor = new Mock<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor>();
        var filters = new Mock<IFilterMetadata>();

        return new ActionExecutingContext(
            new ActionContext(httpContextMock.Object, new Microsoft.AspNetCore.Routing.RouteData(), actionDescriptor.Object),
            new List<IFilterMetadata> { filters.Object },
            new Dictionary<string, object>(),
            controller: new object());
    }
}
