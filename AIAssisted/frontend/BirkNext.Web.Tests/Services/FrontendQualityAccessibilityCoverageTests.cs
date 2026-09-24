using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The dedicated Accessibility engine is one automated source; Browser Quality's browser evidence is another; the WCAG
/// profile is a ruleset and manual assessment is its requirement. Losing the engine limits automated coverage — it never
/// makes accessibility, or the profile, "unavailable".
/// </summary>
public sealed class FrontendQualityAccessibilityCoverageTests
{
    private static FrontendAnalysisContext Context(bool accessibilityEnabled = true)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://example.test" };
        profile.Features.EnableBrowserRuntimeEngine = false;
        profile.Features.EnableAccessibilityEngine = accessibilityEnabled;
        profile.Features.EnableLighthouseEngine = true;
        profile.Features.EnablePassiveSecurityEngine = true;
        profile.Features.EnableBrowserQualityEngine = true;
        profile.Features.EnablePerformanceQualityEngine = true;
        return new() { ActiveProfile = profile, TargetUrl = profile.TargetUrl, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection };
    }

    /// <param name="accessibilityAvailable">Whether the dedicated engine's runtime is available (Lighthouse and Passive Security never are here).</param>
    private static (FrontendAnalysisContext Context, IReadOnlyList<FrontendQualityCapabilityRow> Rows) Setup(
        bool accessibilityAvailable, BrowserCompanionState companion, bool accessibilityEnabled = true)
    {
        var context = Context(accessibilityEnabled);
        var status = new FrontendQualityEngineStatusReportDto
        {
            Engines = new[] { FrontendQualityEngineIdDto.Accessibility, FrontendQualityEngineIdDto.Lighthouse, FrontendQualityEngineIdDto.PassiveSecurity }
                .Select(id =>
                {
                    var up = id == FrontendQualityEngineIdDto.Accessibility && accessibilityAvailable;
                    return new FrontendQualityEngineStatusDto { EngineId = id, Layer1Allowed = true, Layer2Enabled = true, Available = up,
                        AuthModeSupported = true, Layer3Readiness = new() { IsAvailable = up } };
                }).ToList()
        };
        var rows = FrontendQualityLandingPresentation.Capabilities(context, FrontendQualityActiveEngines.Resolve(context), status,
            false, false, new FrontendQualityTargetAccessContext(), companion);
        return (context, rows);
    }

    private static FrontendQualityDimensionCard Accessibility(IReadOnlyList<FrontendQualityCapabilityRow> rows) =>
        FrontendQualityLandingPresentation.Dimensions(rows).Single(c => c.Category == FrontendQualityCategory.Accessibility);

    // 1, 2, 4, 5, 6, 7, 8. Engine unavailable, browser evidence still contributes.
    [Fact]
    public void EngineUnavailableWithBrowserEvidence_CoverageIsLimited()
    {
        var (context, rows) = Setup(accessibilityAvailable: false, BrowserCompanionState.Connected);

        FrontendQualityLandingPresentation.AutomatedAccessibilityLimitation(rows).Should().Be(FrontendQualityAutomatedAccessibilityCoverage.Limited);

        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Message.Should().Be("Automated accessibility coverage is limited. Lighthouse and Passive Security are currently unavailable. All required capabilities are available, so the review can run.");
        readiness.Details.Should().Contain("Automated accessibility coverage: Limited").And.NotContain("Accessibility: Unavailable");

        var card = Accessibility(rows);
        card.State.Should().Be(FrontendQualityDimensionState.Limited);
        card.Limitation.Should().Be("Automated accessibility coverage is limited.");
        card.ManualAssessmentRequired.Should().BeTrue("the profile requires manual assessment whatever the automation");
        card.ScopeNote.Should().Be(WcagProfiles.Norwegian.Label, "the profile is a ruleset and stays available");

        // The engine list keeps the engine's own technical state.
        rows.Single(r => r.EngineId == FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.Unavailable);

        var userFacing = string.Join(" ", new[] { readiness.Message, card.Limitation! }.Concat(readiness.Details));
        userFacing.Should().NotContainAny("Accessibility is unavailable", "Accessibility is currently unavailable", "Accessibility: Unavailable",
            "Full accessibility", "Full WCAG");
    }

    // 3. No automated accessibility source at all.
    [Fact]
    public void EngineUnavailableWithoutBrowserEvidence_CoverageIsUnavailable()
    {
        var (context, rows) = Setup(accessibilityAvailable: false, BrowserCompanionState.NotPaired);

        FrontendQualityLandingPresentation.AutomatedAccessibilityLimitation(rows).Should().Be(FrontendQualityAutomatedAccessibilityCoverage.Unavailable);
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Message.Should().StartWith("Automated accessibility coverage is unavailable.");
        readiness.Details.Should().Contain("Automated accessibility coverage: Unavailable");

        var card = Accessibility(rows);
        card.State.Should().Be(FrontendQualityDimensionState.PartialEvidence, "the domain stays in the review through its profile");
        card.Limitation.Should().Be("Automated accessibility coverage is unavailable. Browser Quality is not configured.");
        card.ManualAssessmentRequired.Should().BeTrue();
        card.ScopeNote.Should().Be(WcagProfiles.Norwegian.Label);
    }

    // The label is reserved for the dedicated engine being unavailable: a ready or switched-off engine is not a coverage limitation.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NoCoverageLimitationUnlessTheEnabledEngineIsUnavailable(bool accessibilityAvailable, bool accessibilityEnabled)
    {
        var (context, rows) = Setup(accessibilityAvailable, BrowserCompanionState.Connected, accessibilityEnabled);
        FrontendQualityLandingPresentation.AutomatedAccessibilityLimitation(rows).Should().BeNull();
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Message.Should().NotContain("Automated accessibility coverage");
        Accessibility(rows).ManualAssessmentRequired.Should().BeTrue();
    }
}
