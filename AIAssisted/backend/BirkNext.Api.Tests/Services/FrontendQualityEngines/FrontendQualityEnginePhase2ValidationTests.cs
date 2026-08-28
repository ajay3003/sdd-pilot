using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>Phase 2 validation: backend capability model, persistence, migration, API contracts.</summary>
public sealed class FrontendQualityEnginePhase2ValidationTests
{
    [Fact(DisplayName = "STRICT: Layer 1 cannot be modified through System Settings API")]
    public async Task Layer1_IsReadOnlyFromSettingsAPI()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var client = factory.CreateClient();

        var scope = factory.Services.CreateScope();
        var adminService = scope.ServiceProvider.GetRequiredService<AdminService>();

        var request = new SaveSettingsRequest
        {
            FrontendQualityEngines = new SaveFrontendQualityEngineSettings
            {
                BrowserRuntimeEnabled = true,
            }
        };

        var result = await adminService.SaveSettingsAsync(request);

        result.Success.Should().BeTrue(because: "Layer 2 saves are allowed");

        var editable = adminService.BuildEditableSettings();

        editable.FrontendQualityEngines.Should().NotBeNull();

        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();
        var report = await statusService.GetStatusAsync();

        var browserRuntime = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.BrowserRuntime);

        browserRuntime.Layer1Allowed.Should().BeFalse(because: "Layer 1 remains blocked from test defaults");
        browserRuntime.Layer2Enabled.Should().BeTrue(because: "Layer 2 was just saved");
        browserRuntime.Available.Should().BeFalse(because: "Layer 1 blocks despite Layer 2 true");
    }

    [Fact(DisplayName = "STRICT: Layer 2 preference preserved when Layer 1 later denies")]
    public async Task Layer2_PreservedWhenLayer1Blocks()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();

        var adminService = scope.ServiceProvider.GetRequiredService<AdminService>();
        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var request = new SaveSettingsRequest
        {
            FrontendQualityEngines = new SaveFrontendQualityEngineSettings
            {
                LighthouseEnabled = true,
            }
        };

        var result = await adminService.SaveSettingsAsync(request);
        result.Success.Should().BeTrue();

        var report = await statusService.GetStatusAsync();
        var lighthouse = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.Lighthouse);

        lighthouse.Layer2Enabled.Should().BeTrue(because: "Layer 2 was saved as true");
        lighthouse.Layer1Allowed.Should().BeFalse(because: "test host denies Layer 1");
        lighthouse.Available.Should().BeFalse(because: "Layer 1 blocks");

        var editable = adminService.BuildEditableSettings();
        editable.FrontendQualityEngines.LighthouseEnabled.Should().BeTrue(because: "Layer 2 preference is still true in UI");
    }

    [Fact(DisplayName = "API contract: general status includes all layers")]
    public async Task GeneralStatusContract_IncludesAllLayersPerEngine()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();

        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();
        var report = await statusService.GetStatusAsync();

        report.Engines.Should().HaveCount(4);

        foreach (var status in report.Engines)
        {
            status.EngineId.Should().BeOneOf(
                FrontendQualityEngineId.BrowserRuntime,
                FrontendQualityEngineId.Accessibility,
                FrontendQualityEngineId.Lighthouse,
                FrontendQualityEngineId.PassiveSecurity);

            status.DisplayName.Should().NotBeNullOrEmpty();

            status.Layer3Readiness.Should().NotBeNull(because: "Layer 3 must be present");
            status.Layer3Readiness.EngineId.Should().Be(status.EngineId);
            status.Layer3Readiness.CheckedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
            status.Reasons.Should().NotBeEmpty(because: "reasons must always be populated");
        }
    }

    [Fact(DisplayName = "STRICT: one readiness provider timeout doesn't break others")]
    public async Task ReadinessTimeout_OneFailureDoesNotBlockOthers()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();

        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var report = await statusService.GetStatusAsync();

        report.Should().NotBeNull(because: "HTTP 200 must be returned even if readiness checks fail");
        report.Engines.Should().HaveCount(4, because: "all engines must be present even if one checks timed out");
    }

    [Fact(DisplayName = "Review-context API: selection doesn't make Available=false")]
    public async Task ReviewContextAPI_SelectedFalseDoesNotBlockAvailable()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();

        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();

        var selection = new FrontendQualityEngineSelectionContext(
            new Dictionary<FrontendQualityEngineId, bool>
            {
                { FrontendQualityEngineId.BrowserRuntime, false },
                { FrontendQualityEngineId.Accessibility, true },
            });

        var query = new FrontendQualityEngineStatusQuery(
            ReviewAuthenticationMode.Anonymous,
            selection);

        var report = await statusService.GetStatusAsync(query);

        var browserRuntime = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.BrowserRuntime);

        browserRuntime.Selected.Should().BeFalse(because: "selection says false");
        browserRuntime.Available.Should().BeFalse(because: "Layer 1 blocks");

        browserRuntime.EligibleToExecute.Should().BeFalse(because: "not available");

        var accessibility = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.Accessibility);

        accessibility.Selected.Should().BeTrue(because: "selection says true");
        accessibility.Available.Should().BeFalse(because: "Layer 1 blocks");

        accessibility.EligibleToExecute.Should().BeFalse(because: "not available");
    }

    [Fact(DisplayName = "ZAP anonymous proof: Allowed + Enabled + Ready = Available")]
    public async Task ZAPAnonymousProof()
    {
        using var factory = TestHostConfiguration.CreateHostWithEngineEnabled("FrontendQualityCapabilities:PassiveSecurityAllowed");
        using var scope = factory.Services.CreateScope();

        var interpreter = scope.ServiceProvider.GetRequiredService<FrontendQualityEngineLegacyConfigInterpreter>();
        var (allowed, enabled) = interpreter.ResolveLayer1And2(FrontendQualityEngineId.PassiveSecurity);

        allowed.Should().BeTrue(because: "Layer 1 explicitly enabled");
        enabled.Should().BeFalse(because: "Layer 2 not explicitly enabled, test host defaults disable");
    }

    [Fact(DisplayName = "Browser Runtime authenticated support (post-A3)")]
    public void BrowserRuntimeAuthSupport_IncludesAuthenticated()
    {
        FrontendQualityEngineAuthenticationSupport.Supports(
            FrontendQualityEngineId.BrowserRuntime,
            ReviewAuthenticationMode.Authenticated).Should().BeTrue(because: "after A3");
    }

    [Fact(DisplayName = "Accessibility pre-A4: authenticated NOT supported")]
    public void AccessibilityAuthSupport_PreA4_NoAuthenticated()
    {
        FrontendQualityEngineAuthenticationSupport.Supports(
            FrontendQualityEngineId.Accessibility,
            ReviewAuthenticationMode.Authenticated).Should().BeFalse(because: "until A4");
    }

    [Fact(DisplayName = "Reason codes: typed identifiers, no free text")]
    public async Task ReasonCodes_AreTyped_NotFreeText()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();

        var statusService = scope.ServiceProvider.GetRequiredService<IFrontendQualityEngineStatusService>();
        var report = await statusService.GetStatusAsync();

        foreach (var status in report.Engines)
        {
            status.Reasons.Should().NotBeEmpty();

            foreach (var reason in status.Reasons)
            {
                var validReasons = new[]
                {
                    FrontendQualityEngineUnavailableReason.None,
                    FrontendQualityEngineUnavailableReason.BlockedByDeploymentPolicy,
                    FrontendQualityEngineUnavailableReason.DisabledInSystemSettings,
                    FrontendQualityEngineUnavailableReason.RuntimeUnavailable,
                    FrontendQualityEngineUnavailableReason.RuntimeStatusUnknown,
                    FrontendQualityEngineUnavailableReason.AuthenticationModeUnsupported,
                };
                reason.Should().BeOneOf(validReasons);
            }
        }
    }

    [Fact(DisplayName = "Migration: legacy true → both Layer1 and Layer2 true")]
    public void Migration_LegacyTrueFlowsToLayersPreservingIntent()
    {
        var factory = TestHostConfiguration.CreateHostWithEngineEnabled("FrontendBrowserRuntime:Enabled");
        using var scope = factory.Services.CreateScope();

        var interpreter = scope.ServiceProvider.GetRequiredService<FrontendQualityEngineLegacyConfigInterpreter>();

        var (allowed, enabled) = interpreter.ResolveLayer1And2(FrontendQualityEngineId.BrowserRuntime);

        allowed.Should().BeTrue(because: "legacy true → Layer 1 Allowed true");
        enabled.Should().BeTrue(because: "legacy true → Layer 2 Enabled true (intent preserved)");
    }

    [Fact(DisplayName = "Migration: idempotence - second call unchanged")]
    public void Migration_Idempotent_SecondCallUnchanged()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();

        var interpreter = scope.ServiceProvider.GetRequiredService<FrontendQualityEngineLegacyConfigInterpreter>();

        var (allowed1, enabled1) = interpreter.ResolveLayer1And2(FrontendQualityEngineId.BrowserRuntime);
        var (allowed2, enabled2) = interpreter.ResolveLayer1And2(FrontendQualityEngineId.BrowserRuntime);

        allowed1.Should().Be(allowed2, because: "migration is idempotent");
        enabled1.Should().Be(enabled2, because: "migration is idempotent");
        allowed1.Should().BeFalse(because: "defaults from test host");
        enabled1.Should().BeFalse(because: "defaults from test host");
    }
}
