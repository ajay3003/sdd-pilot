using BirkNext.Api.Configuration;
using BirkNext.Api.Filters;
using BirkNext.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private RequireTargetDetectionHttpsFilter _filter;
    private readonly Mock<ILogger<RequireTargetDetectionHttpsFilter>> _loggerMock;
    private readonly Mock<IWebHostEnvironment> _environmentMock;

    public TargetEnvironmentDetectionControllerHttpsTests()
    {
        _loggerMock = new Mock<ILogger<RequireTargetDetectionHttpsFilter>>();
        _environmentMock = new Mock<IWebHostEnvironment>();
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns("Production");

        var options = new TargetDetectionOptions { RequireHttps = true };
        var optionsWrapper = Options.Create(options);
        _filter = new RequireTargetDetectionHttpsFilter(optionsWrapper, _loggerMock.Object, Local());
    }

    /// <summary>The Local Tester Mode gate the filter consults (the hosting environment is only reported, never decisive).</summary>
    private BirkNext.Api.Services.ActiveEventTesting.LocalTrustedExecution Local(string? mode = "true") =>
        new(_environmentMock.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BirkNext.Api.Services.ActiveEventTesting.LocalTrustedExecution.ModeSetting] = mode }).Build());

    private void SetEnvironment(string environmentName, string? mode = "true", params string[] listeners)
    {
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns(environmentName);
        var local = Local(mode);
        local.ObserveListeners(listeners);
        _filter = new RequireTargetDetectionHttpsFilter(Options.Create(new TargetDetectionOptions { RequireHttps = true }), _loggerMock.Object, local);
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
    // CASE C: HTTP loopback in Local Tester Mode — accepted in every hosting environment (2026-11-18: a local Release run and the Windows
    // Tester Package run as Production; their target checks were refused with 426). Rejected when the mode is off or a listener is not loopback.
    // ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Production", "localhost")]
    [InlineData("Production", "127.0.0.1")]
    [InlineData("Production", "[::1]")]
    [InlineData("Staging", "localhost")]
    public void LocalTesterModeHttpLoopback_Request_Accepted_InAnyEnvironment(string environment, string loopbackHost)
    {
        SetEnvironment(environment, mode: "true", "http://localhost:5480");
        var context = CreateActionExecutingContext(isHttps: false, host: loopbackHost);

        _filter.OnActionExecuting(context);

        context.Result.Should().BeNull("the local BirkNext's own loopback request is accepted whatever the hosting environment");
    }

    [Theory]
    [InlineData("false", new string[0])]
    [InlineData(null, new string[0])]
    [InlineData("true", new[] { "http://0.0.0.0:5000" })]
    [InlineData("true", new[] { "http://localhost:5000", "http://192.168.1.20:5000" })]
    public void HttpLoopback_Request_Rejected_WithoutAnActiveLocalTesterMode(string? mode, string[] listeners)
    {
        SetEnvironment("Production", mode, listeners);
        var context = CreateActionExecutingContext(isHttps: false, host: "localhost");

        _filter.OnActionExecuting(context);

        context.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(426, "mode off, or BirkNext also reachable from other machines: plain HTTP is refused");
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
        var optionsWrapper = Options.Create(options);
        var filter = new RequireTargetDetectionHttpsFilter(optionsWrapper, _loggerMock.Object, Local());
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
