using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

public sealed class FrontendQualityEngineStatusServiceTests
{
    [Fact]
    public async Task GetStatus_WithDefaultQuery_ReturnsAllFourEngines()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var client = factory.CreateClient();

        var scope = factory.Services.CreateScope();
        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var report = await statusService.GetStatusAsync();

        report.Engines.Should().HaveCount(4);
        report.Engines.Should().Contain(e => e.EngineId == FrontendQualityEngineId.BrowserRuntime);
        report.Engines.Should().Contain(e => e.EngineId == FrontendQualityEngineId.Accessibility);
        report.Engines.Should().Contain(e => e.EngineId == FrontendQualityEngineId.Lighthouse);
        report.Engines.Should().Contain(e => e.EngineId == FrontendQualityEngineId.PassiveSecurity);
    }

    [Fact]
    public async Task GetStatus_WithDisabledLayers_ReturnsUnavailableWithCorrectReasons()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var client = factory.CreateClient();

        var scope = factory.Services.CreateScope();
        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var report = await statusService.GetStatusAsync();

        var browserRuntime = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.BrowserRuntime);
        browserRuntime.Available.Should().BeFalse();
        browserRuntime.Layer1Allowed.Should().BeFalse();
        browserRuntime.Layer2Enabled.Should().BeFalse();
        browserRuntime.Reasons.Should().Contain(FrontendQualityEngineUnavailableReason.BlockedByDeploymentPolicy);
        browserRuntime.Reasons.Should().Contain(FrontendQualityEngineUnavailableReason.DisabledInSystemSettings);
    }

    [Fact]
    public async Task GetStatus_WithAnonymousMode_ReturnsAuthSupportedTrueForAllEngines()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var client = factory.CreateClient();

        var scope = factory.Services.CreateScope();
        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var query = new FrontendQualityEngineStatusQuery(ReviewAuthenticationMode.Anonymous);
        var report = await statusService.GetStatusAsync(query);

        report.Engines.Should().AllSatisfy(e => e.AuthModeSupported.Should().BeTrue(because: "all engines support anonymous mode"));
    }

    [Fact]
    public async Task GetStatus_WithAuthenticatedMode_ReturnsMixedAuthSupport()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var client = factory.CreateClient();

        var scope = factory.Services.CreateScope();
        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var query = new FrontendQualityEngineStatusQuery(ReviewAuthenticationMode.Authenticated);
        var report = await statusService.GetStatusAsync(query);

        var browserRuntime = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.BrowserRuntime);
        var accessibility = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.Accessibility);
        var lighthouse = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.Lighthouse);
        var passiveSecurity = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.PassiveSecurity);

        browserRuntime.AuthModeSupported.Should().BeTrue(because: "BrowserRuntime supports authenticated mode");
        accessibility.AuthModeSupported.Should().BeTrue(because: "Accessibility supports authenticated mode after A4");
        lighthouse.AuthModeSupported.Should().BeFalse(because: "Lighthouse does not support authenticated mode");
        passiveSecurity.AuthModeSupported.Should().BeFalse(because: "PassiveSecurity does not support authenticated mode");
    }

    [Fact]
    public void CaptureSnapshot_IncludesAuthModeAndAllLayers()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();

        var scope = factory.Services.CreateScope();
        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var snapshot = statusService.CaptureSnapshot(ReviewAuthenticationMode.Anonymous);

        snapshot.AuthMode.Should().Be(ReviewAuthenticationMode.Anonymous);
        snapshot.Allowed.Should().HaveCount(4);
        snapshot.Enabled.Should().HaveCount(4);
        snapshot.AuthSupported.Should().HaveCount(4);
        snapshot.Allowed.Values.Should().AllSatisfy(v => v.Should().BeFalse(because: "all engines are Layer-1 blocked in test host"));
        snapshot.Enabled.Values.Should().AllSatisfy(v => v.Should().BeFalse(because: "all engines are Layer-2 disabled in test host"));
        snapshot.AuthSupported.Values.Should().AllSatisfy(v => v.Should().BeTrue(because: "all support anonymous mode"));
    }
}
