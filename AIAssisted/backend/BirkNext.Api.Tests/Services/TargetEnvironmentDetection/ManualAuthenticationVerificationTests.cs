using System.Text.Json;
using BirkNext.Api.Configuration;
using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class ManualAuthenticationVerificationTests
{
    [Fact]
    public async Task ConfiguredEnterpriseHost_PreflightAndContinuationDoNotInvokeBrowser()
    {
        var dns = new FakeDnsResolver();
        dns.Add("m2lbdev.example.org", "203.0.113.2");
        var service = new TargetEnvironmentDetectionService(new BrowserTargetValidator(),
            new HttpClient(DetectionFixtures.BlazorWasmTarget()), dns, new ClientFrameworkDetector(),
            NullLogger<TargetEnvironmentDetectionService>.Instance,
            Options.Create(new TargetDetectionOptions { ManualManagedEdgeHosts = ["m2lbdev.example.org"] }));
        var strategy = new Mock<ITargetDetectionAuthenticationStrategy>(MockBehavior.Strict);
        var result = await service.DetectWithStrategyAsync("https://m2lbdev.example.org", "session", "profile", strategy.Object);
        Assert.Equal(TargetDetectionState.ManualAuthenticationVerificationRequired, result.State);
        Assert.True(result.DetectionResponse.Success);
        Assert.Equal(TargetReachability.Reachable, result.DetectionResponse.Reachability);
        Assert.NotNull(result.DetectionResponse.DetectedClientFramework);
        Assert.False(result.IsActivationReady);
        Assert.False(result.DetectionResponse.BrowserRuntimeInspectionRequired);
        Assert.NotEqual(true, result.BrowserRuntimeInspectionRequired);
        strategy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExplicitManualContinuation_NeverResolvesBrowserManager()
    {
        var service = new Mock<ITargetEnvironmentDetectionService>();
        service.Setup(x => x.DetectFromUrlAsync("https://example.com", default))
            .ReturnsAsync(new TargetEnvironmentDetectionResponse { Success = true, Reachability = TargetReachability.Reachable });
        var controller = new TargetEnvironmentDetectionController(service.Object, NullLogger<TargetEnvironmentDetectionController>.Instance);
        var action = await controller.ContinueDetectionInBrowser(new BrowserDetectionRequest
        {
            TargetUrl = "https://example.com", ProfileId = "profile", ReviewSessionId = "session",
            AuthenticationVerificationMode = AuthenticationVerificationMode.ManualManagedEdge
        }, default);
        var outcome = Assert.IsType<TargetDetectionOutcome>(Assert.IsType<OkObjectResult>(action).Value);
        Assert.True(outcome.ManualAuthenticationVerificationRequired);
        Assert.Null(outcome.AuthenticationFailureReason);
    }

    [Fact]
    public void JsonContract_UsesExactTypedStringsAndSeparateFlags()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = true, Reachability = TargetReachability.Reachable };
        ManualAuthenticationVerification.Apply(response);
        var outcome = new DetectionStateComputer().CreateOutcome(response, "https://example.com", "https://example.com");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(outcome));
        Assert.Equal("ManualAuthenticationVerificationRequired", json.RootElement.GetProperty("state").GetString());
        Assert.Equal("Required", json.RootElement.GetProperty("manualAuthenticationVerificationStatus").GetString());
        Assert.True(json.RootElement.GetProperty("manualAuthenticationVerificationRequired").GetBoolean());
        Assert.False(json.RootElement.GetProperty("isActivationReady").GetBoolean());
    }

    [Fact]
    public void UrlChange_InvalidatesManualOutcome()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = true, Reachability = TargetReachability.Reachable };
        ManualAuthenticationVerification.Apply(response);
        var outcome = new DetectionStateComputer().CreateOutcome(response, "https://example.com", "https://other.example.com");
        Assert.Equal(TargetDetectionState.Stale, outcome.State);
        Assert.Equal(ManualAuthenticationVerificationStatus.Stale, outcome.ManualAuthenticationVerificationStatus);
        Assert.False(outcome.IsActivationReady);
    }

    [Fact]
    public void ManualPolicy_DoesNotHidePreflightFailure()
    {
        var response = new TargetEnvironmentDetectionResponse { Success = false, State = TargetDetectionState.Failed };
        ManualAuthenticationVerification.Apply(response);
        Assert.Equal(TargetDetectionState.Failed, response.State);
        Assert.False(response.ManualAuthenticationVerificationRequired);
    }
}
