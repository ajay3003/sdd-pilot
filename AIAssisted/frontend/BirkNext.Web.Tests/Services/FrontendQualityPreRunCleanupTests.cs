using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityPreRunCleanupTests
{
    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://example.test" };
        profile.Features.EnableBrowserRuntimeEngine = false;
        profile.Features.EnableAccessibilityEngine = true;
        profile.Features.EnableLighthouseEngine = true;
        profile.Features.EnablePassiveSecurityEngine = true;
        profile.Features.EnableBrowserQualityEngine = true;
        profile.Features.EnablePerformanceQualityEngine = true;
        return new() { ActiveProfile = profile, TargetUrl = profile.TargetUrl, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection };
    }

    private static IReadOnlyList<FrontendQualityCapabilityRow> Rows(BrowserCompanionState companion = BrowserCompanionState.NotPaired)
    {
        var context = Context();
        var status = new FrontendQualityEngineStatusReportDto
        {
            Engines = new[] { FrontendQualityEngineIdDto.Accessibility, FrontendQualityEngineIdDto.Lighthouse, FrontendQualityEngineIdDto.PassiveSecurity }
                .Select(id => new FrontendQualityEngineStatusDto { EngineId = id, Layer1Allowed = true, Layer2Enabled = true,
                    Available = id == FrontendQualityEngineIdDto.Accessibility, AuthModeSupported = true,
                    Layer3Readiness = new() { IsAvailable = id == FrontendQualityEngineIdDto.Accessibility } }).ToList()
        };
        return FrontendQualityLandingPresentation.Capabilities(context, FrontendQualityActiveEngines.Resolve(context), status,
            false, false, new FrontendQualityTargetAccessContext(), companion);
    }

    [Fact]
    public void MixedStatesAgreeAcrossBannerAndEngineSummary()
    {
        var context = Context();
        var rows = Rows();
        var summary = new FrontendQualityPreRunEngineSummary(rows);
        summary.AllRequiredAvailable.Should().BeTrue();
        summary.Limitations.Should().HaveCount(4).And.NotContain(r => r.EngineId == FrontendQualityEngineId.BrowserRuntime);
        summary.Counts.Should().Be("2 required available · 1 optional ready · 2 optional unavailable · 2 optional not configured · 1 optional disabled");
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Message.Should().Be("4 optional capability limitations: 2 unavailable: Lighthouse and Passive Security; 2 not configured: Browser Quality and BirkNext Performance Quality. All required capabilities are available; the review can run.");
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
    }

    [Theory]
    [InlineData(FrontendQualityCategory.Performance, "Passive Performance is included. Lighthouse is unavailable. Browser Quality and BirkNext Performance Quality are not configured.")]
    [InlineData(FrontendQualityCategory.Security, "Static Security is included. Passive Security is unavailable.")]
    [InlineData(FrontendQualityCategory.Accessibility, "The dedicated Accessibility engine is ready. Browser Quality is not configured.")]
    [InlineData(FrontendQualityCategory.BlazorWasm, "Browser Quality and BirkNext Performance Quality are not configured.")]
    public void DomainsUseExactContributorStates(FrontendQualityCategory category, string expected)
    {
        var card = FrontendQualityLandingPresentation.Dimensions(Rows()).Single(c => c.Category == category);
        card.State.Should().Be(FrontendQualityDimensionState.Limited);
        card.Limitation.Should().Be(expected);
    }

    [Theory]
    [InlineData(BrowserCompanionState.NotPaired, FrontendQualityCapabilityState.NotConfigured)]
    [InlineData(BrowserCompanionState.PairingPending, FrontendQualityCapabilityState.NotConfigured)]
    [InlineData(BrowserCompanionState.Connected, FrontendQualityCapabilityState.Ready)]
    [InlineData(BrowserCompanionState.Disconnected, FrontendQualityCapabilityState.RequiresBrowserSession)]
    [InlineData(BrowserCompanionState.Expired, FrontendQualityCapabilityState.RequiresBrowserSession)]
    public void CompanionSetupStateDoesNotChangeManualAssessment(BrowserCompanionState companion, FrontendQualityCapabilityState expected)
    {
        var rows = Rows(companion);
        rows.Single(r => r.EngineId == FrontendQualityEngineId.BrowserQuality).State.Should().Be(expected);
        FrontendQualityLandingPresentation.Dimensions(rows).Single(c => c.Category == FrontendQualityCategory.Accessibility)
            .ManualAssessmentRequired.Should().BeTrue();
        FrontendQualityLandingPresentation.Dimensions(rows).Where(c => c.Category is FrontendQualityCategory.Standards or FrontendQualityCategory.Readiness)
            .Should().OnlyContain(c => c.State == FrontendQualityDimensionState.Included);
    }

    [Fact]
    public void AvailableAndReadyRetainTheirDifferentEvidence()
    {
        var rows = Rows();
        rows.Single(r => r.EngineId == FrontendQualityEngineId.StaticSecurity).State.Should().Be(FrontendQualityCapabilityState.Enabled);
        rows.Single(r => r.EngineId == FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.Ready);
        rows.Single(r => r.EngineId == FrontendQualityEngineId.Accessibility).SelectableEngineId.Should().Be(FrontendQualityEngineIdDto.Accessibility);
        rows.Where(r => r.State == FrontendQualityCapabilityState.Unavailable).Should().OnlyContain(r => r.SelectableEngineId == null);
    }

    [Fact]
    public void HistoricalExecutionRulesAreDifferentFromCurrentSetup()
    {
        new BrowserQualityReviewResult { CompanionState = BrowserCompanionState.NotPaired, PagesWithEvidence = 3 }.Assessed.Should().BeFalse();
        new BrowserQualityReviewResult { CompanionState = BrowserCompanionState.Disconnected, PagesWithEvidence = 3 }.Assessed.Should().BeTrue();
        Rows().Where(r => r.EngineId is FrontendQualityEngineId.BrowserQuality or FrontendQualityEngineId.PerformanceQuality)
            .Should().OnlyContain(r => r.State == FrontendQualityCapabilityState.NotConfigured);
        Rows().Single(r => r.EngineId == FrontendQualityEngineId.PerformanceQuality).Summary.Should().Contain("Recorded performance evidence can still contribute");
    }
}
