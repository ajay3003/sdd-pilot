using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services;

/// <summary>Deterministic tests proving external engines are disabled by default
/// and fail-closed before launching external processes.</summary>
[Trait("Category", "ExternalEngineHardening")]
public sealed class ExternalEngineHardeningTests
{
    [Fact]
    public async Task BrowserRuntime_DisabledByDefault_RejectsExecution()
    {
        using var factory = Utilities.TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFrontendBrowserRuntimeReviewService>();

        var result = await service.ReviewAsync("http://example.com");

        result.Status.Should().Be(BrowserRuntimeEngineStatus.Skipped);
        result.OutcomeReason.Should().Be(BrowserRuntimeOutcomeReason.SessionUnavailable);
        result.EngineError.Should().Contain("disabled");
    }

    [Fact]
    public async Task Accessibility_DisabledByDefault_SkipsExecution()
    {
        using var factory = Utilities.TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFrontendAccessibilityReviewService>();

        var result = await service.ReviewAsync("http://example.com");

        result.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.Skipped);
        result.EngineError.Should().Contain("disabled");
    }

    [Fact]
    public async Task Lighthouse_DisabledByDefault_SkipsExecution()
    {
        using var factory = Utilities.TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFrontendLighthouseReviewService>();

        var result = await service.ReviewAsync("http://example.com");

        result.ExecutionStatus.Should().Be(LighthouseExecutionStatus.Skipped);
        result.EngineError.Should().Contain("disabled");
    }

    [Fact]
    public async Task BrowserRuntime_CheckReadiness_DisabledByDefault_ReturnsFalseAvailable()
    {
        using var factory = Utilities.TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFrontendBrowserRuntimeReviewService>();

        var readiness = await service.CheckReadinessAsync();

        readiness.IsAvailable.Should().BeFalse();
        readiness.ErrorMessage.Should().Contain("disabled");
    }

    [Fact]
    public async Task Accessibility_CheckReadiness_DisabledByDefault_ReturnsFalseAvailable()
    {
        using var factory = Utilities.TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFrontendAccessibilityReviewService>();

        var readiness = await service.CheckReadinessAsync();

        readiness.Available.Should().BeFalse();
        readiness.Error.Should().Contain("disabled");
    }

    [Fact]
    public async Task Lighthouse_CheckReadiness_DisabledByDefault_ReturnsFalseAvailable()
    {
        using var factory = Utilities.TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFrontendLighthouseReviewService>();

        var readiness = await service.CheckReadinessAsync();

        readiness.Available.Should().BeFalse();
        readiness.Error.Should().Contain("disabled");
    }
}
