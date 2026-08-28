using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>Phase 2 blocker fixes: Layer 2 persistence, Layer 1 immutability, test configuration.</summary>
public sealed class FrontendQualityEnginePhase2BlockerFixTests
{
    [Fact(DisplayName = "Layer 2 saves to appsettings.Local.json")]
    public async Task Layer2Persistence_WritesToFile()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();

        var adminService = scope.ServiceProvider.GetRequiredService<AdminService>();

        var saveRequest = new SaveSettingsRequest
        {
            FrontendQualityEngines = new SaveFrontendQualityEngineSettings
            {
                BrowserRuntimeEnabled = true,
                AccessibilityEnabled = false,
                LighthouseEnabled = true,
                PassiveSecurityEnabled = false,
            }
        };

        var (success, message) = await adminService.SaveSettingsAsync(saveRequest);
        success.Should().BeTrue($"save should succeed: {message}");

        var env = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var localSettingsPath = System.IO.Path.Combine(env.ContentRootPath, "appsettings.Local.json");

        System.IO.File.Exists(localSettingsPath).Should().BeTrue(because: "appsettings.Local.json should be created");

        var content = await System.IO.File.ReadAllTextAsync(localSettingsPath);
        content.Should().Contain("FrontendQualityEnginePreferences", because: "section should be saved");
        content.Should().Contain("BrowserRuntimeEnabled", because: "preference property should be saved");
    }

    [Fact(DisplayName = "STRICT: Layer 2 request DTO cannot set Layer 1 Allowed fields")]
    public void Layer1_NotInWritableDTO_ImmutableByContract()
    {
        var request = new SaveFrontendQualityEngineSettings();

        typeof(SaveFrontendQualityEngineSettings).GetProperties()
            .Select(p => p.Name)
            .Should()
            .NotContain("BrowserRuntimeAllowed",
                because: "Layer 1 Allowed fields must not be in the writable request DTO")
            .And
            .NotContain("AccessibilityAllowed",
                because: "Layer 1 is deployment policy, not user-modifiable")
            .And
            .NotContain("LighthouseAllowed",
                because: "Layer 1 is immutable through System Settings API")
            .And
            .NotContain("PassiveSecurityAllowed",
                because: "deployment policy cannot change at runtime through admin API");
    }

    [Fact(DisplayName = "ZAP with Layer1 enabled, Layer2 enabled, ready")]
    public async Task ZAP_FullyEnabledReadyState()
    {
        using var factory = TestHostConfiguration.CreateHostWithEngineEnabled("FrontendQualityCapabilities:PassiveSecurityAllowed");
        using var scope = factory.Services.CreateScope();

        var interpreter = scope.ServiceProvider.GetRequiredService<FrontendQualityEngineLegacyConfigInterpreter>();
        var (allowed, enabled) = interpreter.ResolveLayer1And2(FrontendQualityEngineId.PassiveSecurity);

        allowed.Should().BeTrue(because: "Layer 1 explicitly enabled via test override");

        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();
        var report = await statusService.GetStatusAsync();

        var zap = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.PassiveSecurity);

        zap.Layer1Allowed.Should().BeTrue();
        zap.Layer2Enabled.Should().BeFalse(because: "Layer 2 defaults false in test host");

        var query = new FrontendQualityEngineStatusQuery(ReviewAuthenticationMode.Anonymous);
        var reportWithAuth = await statusService.GetStatusAsync(query);
        var zapAuth = reportWithAuth.Engines.Single(e => e.EngineId == FrontendQualityEngineId.PassiveSecurity);

        zapAuth.AuthModeSupported.Should().BeTrue(because: "ZAP supports anonymous");
    }

    [Fact(DisplayName = "Layer 2 true + Layer 1 later false = Layer 2 preserved, Available false")]
    public async Task Layer2Preserved_WhenLayer1Blocks()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled(removeLocalJson: false);
        using var scope = factory.Services.CreateScope();

        var adminService = scope.ServiceProvider.GetRequiredService<AdminService>();

        var saveRequest = new SaveSettingsRequest
        {
            FrontendQualityEngines = new SaveFrontendQualityEngineSettings
            {
                LighthouseEnabled = true,
            }
        };

        var (success, _) = await adminService.SaveSettingsAsync(saveRequest);
        success.Should().BeTrue();

        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();
        var report = await statusService.GetStatusAsync();

        var lighthouse = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.Lighthouse);

        lighthouse.Layer1Allowed.Should().BeFalse(because: "test host blocks Layer 1");
        lighthouse.Layer2Enabled.Should().BeTrue(because: "Layer 2 was saved as true, must be preserved");
        lighthouse.Available.Should().BeFalse(because: "Layer 1 blocks, so overall availability is false");

        var editable = adminService.BuildEditableSettings();
        editable.FrontendQualityEngines.LighthouseEnabled.Should().BeTrue(
            because: "UI must show that preference is true, even though Layer 1 currently blocks");
    }
}
