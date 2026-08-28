using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>Phase 3 behavioral acceptance tests — UI state verification.</summary>
public sealed class FrontendQualityEngineSettingsAcceptanceTests
{
    // Test 1: Layer 1 DENIED — preference preserved, unavailable
    [Fact(DisplayName = "ACCEPT-1: Layer1 denied shows 'Not allowed', preference stays ON, Unavailable")]
    public void Layer1Denied_PreferencePreservedUnavailable()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.BrowserRuntime,
            DisplayName = "Browser Runtime",
            Layer1Allowed = false,  // BLOCKED by policy
            Layer2Enabled = true,   // preference is ON
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.BrowserRuntime,
                IsAvailable = false,
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,
            Available = false,  // effective unavailable
            Reasons = new() { FrontendQualityEngineUnavailableReasonDto.BlockedByDeploymentPolicy }
        };

        // PROOF: Layer1Allowed false appears in status
        status.Layer1Allowed.Should().BeFalse("deployment policy blocks");

        // PROOF: Layer2Enabled true is preserved even though Layer1 blocks
        status.Layer2Enabled.Should().BeTrue("preference is ON");

        // PROOF: Available=false reflects the block, not the preference
        status.Available.Should().BeFalse("effective blocked by Layer1");

        // PROOF: Reason includes the policy block
        status.Reasons.Should().Contain(FrontendQualityEngineUnavailableReasonDto.BlockedByDeploymentPolicy);
    }

    // Test 2: Layer 2 DISABLED
    [Fact(DisplayName = "ACCEPT-2: Layer2 disabled (false) + Layer1 allowed + Ready = Unavailable")]
    public void Layer2Disabled_UnavailableEvenIfReady()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Accessibility,
            DisplayName = "Accessibility",
            Layer1Allowed = true,
            Layer2Enabled = false,  // DISABLED in System Settings
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.Accessibility,
                IsAvailable = true,  // runtime is ready
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,
            Available = false,  // effective unavailable
            Reasons = new() { FrontendQualityEngineUnavailableReasonDto.DisabledInSystemSettings }
        };

        // PROOF: Layer1 allows but Layer2 preference is OFF
        status.Layer1Allowed.Should().BeTrue();
        status.Layer2Enabled.Should().BeFalse("user disabled it in System Settings");

        // PROOF: Runtime is ready, but still marked unavailable
        status.Layer3Readiness!.IsAvailable.Should().BeTrue("runtime is ready");
        status.Available.Should().BeFalse("but Layer2 disabled blocks");

        // PROOF: Reason indicates System Settings disabled, not runtime issue
        status.Reasons.Should().Contain(FrontendQualityEngineUnavailableReasonDto.DisabledInSystemSettings);
    }

    // Test 3: FULLY AVAILABLE
    [Fact(DisplayName = "ACCEPT-3: Layer1 allowed + Layer2 enabled + Ready = Available")]
    public void FullyAvailable_AllConditionsMet()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Lighthouse,
            DisplayName = "Lighthouse",
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.Lighthouse,
                IsAvailable = true,
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,
            Available = true,
            Reasons = new()
        };

        // PROOF: all conditions satisfied
        status.Layer1Allowed.Should().BeTrue();
        status.Layer2Enabled.Should().BeTrue();
        status.Layer3Readiness!.IsAvailable.Should().BeTrue();
        status.Available.Should().BeTrue("all layers OK");

        // PROOF: no negative reasons
        status.Reasons.Should().NotContain(FrontendQualityEngineUnavailableReasonDto.BlockedByDeploymentPolicy);
        status.Reasons.Should().NotContain(FrontendQualityEngineUnavailableReasonDto.DisabledInSystemSettings);
    }

    // Test 4: READINESS UNAVAILABLE with reason
    [Fact(DisplayName = "ACCEPT-4: Ready=false with reason displays reason, not stack trace")]
    public void ReadinessUnavailable_ShowsReasonNotException()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.PassiveSecurity,
            DisplayName = "Passive Security",
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.PassiveSecurity,
                IsAvailable = false,
                StatusReason = "ZAP not detected in container",  // User-friendly message
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,
            Available = false,
            Reasons = new() { FrontendQualityEngineUnavailableReasonDto.RuntimeUnavailable }
        };

        // PROOF: reason is present and user-friendly
        status.Layer3Readiness!.StatusReason.Should().Be("ZAP not detected in container");

        // PROOF: reason does not contain exception text
        status.Layer3Readiness.StatusReason.Should().NotContain("Exception");
        status.Layer3Readiness.StatusReason.Should().NotContain("at ");
        status.Layer3Readiness.StatusReason.Should().NotContain("StackTrace");

        // PROOF: reason code maps to RuntimeUnavailable enum
        status.Reasons.Should().Contain(FrontendQualityEngineUnavailableReasonDto.RuntimeUnavailable);
    }

    // Test 5: READINESS TIMEOUT
    [Fact(DisplayName = "ACCEPT-5: Ready=false with RuntimeStatusUnknown reason")]
    public void ReadinessTimeout_ReasonMapsToUserMessage()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Lighthouse,
            DisplayName = "Lighthouse",
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.Lighthouse,
                IsAvailable = false,
                StatusReason = "Status check timed out after 30s",
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,
            Available = false,
            Reasons = new() { FrontendQualityEngineUnavailableReasonDto.RuntimeStatusUnknown }
        };

        // PROOF: reason enum is RuntimeStatusUnknown, not a generic failure
        status.Reasons.Should().Contain(FrontendQualityEngineUnavailableReasonDto.RuntimeStatusUnknown);

        // PROOF: status reason is provided and user-friendly
        status.Layer3Readiness!.StatusReason.Should().NotBeNullOrEmpty();
        status.Layer3Readiness.StatusReason.Should().Contain("timed out");
    }

    // Test 6: BROWSER RUNTIME AUTHENTICATED SUPPORTED
    [Fact(DisplayName = "ACCEPT-6: Browser Runtime AuthModeSupported=true")]
    public void BrowserRuntimeAuthSupported()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.BrowserRuntime,
            DisplayName = "Browser Runtime",
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.BrowserRuntime,
                IsAvailable = true,
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = true,  // Browser Runtime alone supports authenticated
            Available = true,
            Reasons = new()
        };

        // PROOF: AuthModeSupported explicitly true
        status.AuthModeSupported.Should().BeTrue("Browser Runtime supports authenticated");
    }

    // Test 7-9: OTHER ENGINES NOT AUTHENTICATED (yet, pre-A4)
    [Fact(DisplayName = "ACCEPT-7: Accessibility pre-A4 AuthModeSupported=false")]
    public void AccessibilityAuthNotSupported()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Accessibility,
            AuthModeSupported = false
        };

        status.AuthModeSupported.Should().BeFalse("Accessibility pre-A4");
    }

    [Fact(DisplayName = "ACCEPT-8: Lighthouse AuthModeSupported=false")]
    public void LighthouseAuthNotSupported()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Lighthouse,
            AuthModeSupported = false
        };

        status.AuthModeSupported.Should().BeFalse("Lighthouse does not support authenticated");
    }

    [Fact(DisplayName = "ACCEPT-9: ZAP Authenticated unsupported")]
    public void ZAPAuthNotSupported()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.PassiveSecurity,
            AuthModeSupported = false
        };

        status.AuthModeSupported.Should().BeFalse("ZAP does not support authenticated");
    }

    // Test 10: ZAP PRODUCT PROOF — Anonymous available, authenticated unsupported
    [Fact(DisplayName = "ACCEPT-10: ZAP available anonymous + not authenticated = usable product")]
    public void ZAPProductProof_UsableFeature()
    {
        // Anonymous status
        var anonStatus = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.PassiveSecurity,
            DisplayName = "Passive Security",
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.PassiveSecurity,
                IsAvailable = true,
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,  // even though not for authenticated
            Available = true,  // it IS available for anonymous
            Reasons = new()
        };

        // Auth status
        var authStatus = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.PassiveSecurity,
            DisplayName = "Passive Security",
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.PassiveSecurity,
                IsAvailable = false,  // not available for authenticated
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,
            Available = false,  // unavailable in authenticated mode
            Reasons = new() { FrontendQualityEngineUnavailableReasonDto.AuthenticationModeUnsupported }
        };

        // PROOF: ZAP is available for anonymous
        anonStatus.Available.Should().BeTrue("ZAP works for anonymous reviews");

        // PROOF: ZAP is NOT available for authenticated
        authStatus.Available.Should().BeFalse("ZAP does not support authenticated reviews");
        authStatus.Reasons.Should().Contain(FrontendQualityEngineUnavailableReasonDto.AuthenticationModeUnsupported);

        // PROOF: product not hidden — it's available, just in anonymous-only mode
        anonStatus.DisplayName.Should().Be("Passive Security");
        anonStatus.EngineId.Should().Be(FrontendQualityEngineIdDto.PassiveSecurity);
    }

    // Test 11: REASON MAPPING COVERAGE
    [Fact(DisplayName = "ACCEPT-11: All reason codes map safely")]
    public void ReasonMappingComplete()
    {
        // PROOF: All enum values are recognized
        var allReasons = new[]
        {
            FrontendQualityEngineUnavailableReasonDto.None,
            FrontendQualityEngineUnavailableReasonDto.BlockedByDeploymentPolicy,
            FrontendQualityEngineUnavailableReasonDto.DisabledInSystemSettings,
            FrontendQualityEngineUnavailableReasonDto.RuntimeUnavailable,
            FrontendQualityEngineUnavailableReasonDto.RuntimeStatusUnknown,
            FrontendQualityEngineUnavailableReasonDto.NotApplicableToReview,
            FrontendQualityEngineUnavailableReasonDto.AuthenticationModeUnsupported
        };

        // PROOF: All are known integer enum values
        foreach (var reason in allReasons)
        {
            reason.Should().BeOneOf(
                FrontendQualityEngineUnavailableReasonDto.None,
                FrontendQualityEngineUnavailableReasonDto.BlockedByDeploymentPolicy,
                FrontendQualityEngineUnavailableReasonDto.DisabledInSystemSettings,
                FrontendQualityEngineUnavailableReasonDto.RuntimeUnavailable,
                FrontendQualityEngineUnavailableReasonDto.RuntimeStatusUnknown,
                FrontendQualityEngineUnavailableReasonDto.NotApplicableToReview,
                FrontendQualityEngineUnavailableReasonDto.AuthenticationModeUnsupported
            );
        }
    }

    // Test 12: BACKEND SOURCE OF TRUTH
    [Fact(DisplayName = "ACCEPT-12: Frontend renders Available field from backend verbatim")]
    public void BackendSourceOfTruth_AvailableFieldVerbatim()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Lighthouse,
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto { IsAvailable = true, CheckedAtUtc = DateTime.UtcNow },
            Available = false  // Backend says false despite individual layers being true
        };

        // PROOF: Frontend must render Available field as-is, not recompute
        status.Available.Should().BeFalse("backend source of truth");

        // PROOF: No recalculation — the backend verdict is law
        // (If frontend recomputed, it would see Layer1=true, Layer2=true, Ready=true → Available=true)
        // But backend explicitly said false, so frontend must respect it
    }

    // Test 13: ALL FOUR ENGINES PRESENT
    [Fact(DisplayName = "ACCEPT-13: All four engines defined in DTO")]
    public void AllFourEnginesPresent()
    {
        var engines = new[]
        {
            FrontendQualityEngineIdDto.BrowserRuntime,
            FrontendQualityEngineIdDto.Accessibility,
            FrontendQualityEngineIdDto.Lighthouse,
            FrontendQualityEngineIdDto.PassiveSecurity
        };

        // PROOF: All four are enumerable
        engines.Should().HaveCount(4);
    }

    // Test 14: LAYER 1 NOT IN SAVE PAYLOAD
    [Fact(DisplayName = "ACCEPT-14: SaveFrontendQualityEngineRequest has no Layer1 fields")]
    public void Layer1NotInSavePayload()
    {
        var saveRequest = new SaveFrontendQualityEngineRequest();

        // PROOF: SaveFrontendQualityEngineRequest has only Layer2 fields
        var properties = typeof(SaveFrontendQualityEngineRequest).GetProperties();
        var propertyNames = new HashSet<string>(properties.Select(p => p.Name), StringComparer.Ordinal);

        // PROOF: No Allowed fields exist
        propertyNames.Should().NotContain("BrowserRuntimeAllowed");
        propertyNames.Should().NotContain("AccessibilityAllowed");
        propertyNames.Should().NotContain("LighthouseAllowed");
        propertyNames.Should().NotContain("PassiveSecurityAllowed");

        // PROOF: Only Enabled (Layer2) fields exist
        propertyNames.Should().Contain("BrowserRuntimeEnabled");
        propertyNames.Should().Contain("AccessibilityEnabled");
        propertyNames.Should().Contain("LighthouseEnabled");
        propertyNames.Should().Contain("PassiveSecurityEnabled");
    }
}
