using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Found by the Frontend Quality Review workflow test against a stand-in site (broken → fixed): a clean site has to read as "assessed,
/// nothing wrong", and an engine whose runtime has simply not been probed yet must not read as "not available in this environment".
/// </summary>
public sealed class FrontendQualityCleanSiteSemanticsTests
{
    private static FrontendQualityEngineRequirementPolicy Policy() => new FrontendQualityEngineRequirementSettings().ToPolicy();

    [Fact]
    public void A_clean_accessibility_run_has_evidence_its_passed_rules()
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.Accessibility("https://example.test", true, Policy(),
            new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed, AxeVersion: "4.13", ViolationCount: 0, IncompleteCount: 0, PassCount: 21,
                Findings: []), null, true);

        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        outcome.EvidenceCount.Should().Be(21, "axe evaluated 21 rules and they passed: that is evidence, not \"no evidence\"");
        FrontendQualityEngineOutcomePresentation.EvidenceLabel(outcome).Should().Be("21 axe rule results");
    }

    [Fact]
    public void An_accessibility_run_that_evaluated_nothing_still_has_no_evidence()
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.Accessibility("https://example.test", true, Policy(),
            new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed, AxeVersion: "4.13", Findings: []), null, true);

        outcome.EvidenceCount.Should().Be(0);
    }

    [Fact]
    public void A_site_with_every_expected_header_has_HTTP_Security_evidence()
    {
        var report = new WasmSecurityReviewReport
        {
            TargetUrl = "https://example.test/",
            Headers = new[] { "Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Permissions-Policy" }
                .Select(h => new SecurityHeaderResult { Header = h, Status = "Present" }).ToList(),
        };

        var outcome = FrontendQualityEngineOutcomeNormalizer.StaticSecurity("https://example.test/", true, Policy(), report, null);

        outcome.FindingCount.Should().Be(0);
        outcome.EvidenceCount.Should().Be(4, "four expected headers were checked and present");
    }

    [Theory]
    [InlineData(FrontendQualityEngineIdDto.BrowserRuntime, 15)]
    [InlineData(FrontendQualityEngineIdDto.Accessibility, 15)]
    [InlineData(FrontendQualityEngineIdDto.Lighthouse, 35)]
    [InlineData(FrontendQualityEngineIdDto.PassiveSecurity, 75)]
    public void A_run_waits_longer_for_readiness_than_the_server_probe_may_take(FrontendQualityEngineIdDto engine, int serverBudgetSeconds)
    {
        // The run used to give every engine 5 s while Lighthouse's own probe may take 35 s and a Chromium start 15 s: on a busy machine a
        // working engine was skipped as "Not ready".
        FrontendQualityReviewOrchestrator.ReadinessRevalidationBudget(engine).Should().BeGreaterThan(TimeSpan.FromSeconds(serverBudgetSeconds));
    }

    [Fact]
    public void Readiness_not_probed_yet_is_not_unavailable()
    {
        var notChecked = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Accessibility, Layer1Allowed = true, Layer2Enabled = true, AuthModeSupported = true,
            Layer3Readiness = new() { IsAvailable = false, StatusReason = "Readiness not checked: the engine is not active for this review." },
            Reasons = [FrontendQualityEngineUnavailableReasonDto.NotActiveForReview],
        };
        var missing = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.Accessibility, Layer1Allowed = true, Layer2Enabled = true, AuthModeSupported = true,
            Layer3Readiness = new() { IsAvailable = false, StatusReason = "Chromium is not installed." },
            Reasons = [FrontendQualityEngineUnavailableReasonDto.RuntimeUnavailable],
        };

        FrontendQualityLandingPresentation.ReadinessNotYetChecked(notChecked).Should().BeTrue();
        FrontendQualityLandingPresentation.ReadinessNotYetChecked(missing).Should().BeFalse();
        FrontendReviewEnginePresentation.PassiveWebSecurityUnavailable(notChecked).Should().BeNull();
    }

    [Theory]
    [InlineData(true, FrontendQualityCapabilityState.Enabled)]
    [InlineData(false, FrontendQualityCapabilityState.Unavailable)]
    public void The_capability_row_says_unavailable_only_for_a_real_runtime_problem(bool notYetChecked, FrontendQualityCapabilityState expected)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://example.test" };
        profile.Features.EnableAccessibilityEngine = true;
        var context = new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = profile.TargetUrl, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection };
        var status = new FrontendQualityEngineStatusReportDto
        {
            Engines = [new FrontendQualityEngineStatusDto
            {
                EngineId = FrontendQualityEngineIdDto.Accessibility, Layer1Allowed = true, Layer2Enabled = true, AuthModeSupported = true,
                Layer3Readiness = new() { IsAvailable = false, StatusReason = notYetChecked ? "Readiness not checked." : "Chromium is not installed." },
                Reasons = [notYetChecked ? FrontendQualityEngineUnavailableReasonDto.NotActiveForReview : FrontendQualityEngineUnavailableReasonDto.RuntimeUnavailable],
            }],
        };

        var rows = FrontendQualityLandingPresentation.Capabilities(context, FrontendQualityActiveEngines.Resolve(context), status,
            false, true, new FrontendQualityTargetAccessContext(), null);

        rows.Single(r => r.EngineId == FrontendQualityEngineId.Accessibility).State.Should().Be(expected);
    }
}
