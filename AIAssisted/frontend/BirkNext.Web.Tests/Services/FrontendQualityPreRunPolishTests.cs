using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Final pre-run copy and the evidence source behind Ready. The reference state: Static Security and Passive Performance
/// required and available; Accessibility, Browser Quality and BirkNext Performance Quality ready; Lighthouse and
/// Passive Security unavailable; Browser Runtime disabled.
/// </summary>
public sealed class FrontendQualityPreRunPolishTests
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

    private static IReadOnlyList<FrontendQualityCapabilityRow> Rows(
        BrowserCompanionState? companion = BrowserCompanionState.Connected, BrowserEvidenceTotals? recorded = null)
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
            false, false, new FrontendQualityTargetAccessContext(), companion, recorded);
    }

    private static BrowserEvidenceTotals Captured(int pages, int performancePages) => new(pages, pages, pages, performancePages, DateTimeOffset.UnixEpoch);

    private static FrontendQualityCapabilityRow Engine(IReadOnlyList<FrontendQualityCapabilityRow> rows, FrontendQualityEngineId id) =>
        rows.Single(r => r.EngineId == id);

    private static FrontendQualityDimensionCard Card(IReadOnlyList<FrontendQualityCapabilityRow> rows, FrontendQualityCategory category) =>
        FrontendQualityLandingPresentation.Dimensions(rows).Single(c => c.Category == category);

    // ── UX polish: the reference Dev state ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AccessibilityEngineReady_ManualAssessmentStaysRequired_AndNothingSaysAccessibilityIsUnavailable()
    {
        var context = Context();
        var rows = Rows();
        Engine(rows, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.Ready);

        var card = Card(rows, FrontendQualityCategory.Accessibility);
        card.ManualAssessmentRequired.Should().BeTrue("the WCAG profile's manual obligation is independent of every engine");
        card.ScopeNote.Should().Be(WcagProfiles.Norwegian.Label);

        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Message.Should().NotContain("Accessibility is unavailable").And.NotContain("coverage is unavailable");
        readiness.Details.Should().Contain(FrontendQualityLandingPresentation.ManualAccessibilityDetail);
        readiness.Message.Should().NotContainAny("Passed", "Compliant", "Fully available", "All access available");
    }

    [Fact]
    public void BrowserRuntimeDisabledIsADecisionNotALimitation()
    {
        var context = Context();
        var rows = Rows();
        var runtime = Engine(rows, FrontendQualityEngineId.BrowserRuntime);
        runtime.State.Should().Be(FrontendQualityCapabilityState.Disabled);
        FrontendQualityCapabilityStates.Label(runtime.State).Should().Be("Disabled").And.NotBe("Unavailable");
        new FrontendQualityPreRunEngineSummary(rows).Limitations.Should().NotContain(r => r.EngineId == FrontendQualityEngineId.BrowserRuntime);

        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Message.Should().NotContain("Browser Runtime", "a switched-off engine is not a fault to fix");
        readiness.Details.Should().Contain("Browser Runtime: Disabled").And.NotContain("Browser Runtime: Unavailable");
    }

    [Fact]
    public void PerformanceNamesBrowserCompanionEvidenceOnlyWhenThoseEnginesAreReady()
    {
        Card(Rows(), FrontendQualityCategory.Performance).Limitation
            .Should().Be("Passive Performance and Browser Companion evidence are included. Lighthouse is unavailable.");
        // Not paired, nothing recorded: the engines are merely enabled inputs, so nothing is claimed for them.
        Card(Rows(BrowserCompanionState.NotPaired), FrontendQualityCategory.Performance).Limitation
            .Should().StartWith("Passive Performance is included.");
    }

    [Fact]
    public void BannerNamesTheUnavailableCapabilitiesAndSaysTheReviewCanRun()
    {
        var context = Context();
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), Rows(), false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
        readiness.Title.Should().Be("Review can run with limitations");
        readiness.Message.Should().Be("Lighthouse and Passive Security are currently unavailable. All required capabilities are available, so the review can run.");
    }

    [Fact]
    public void EngineStateSummaryIsUnchanged() =>
        new FrontendQualityPreRunEngineSummary(Rows()).Counts
            .Should().Be("2 required available · 3 optional ready · 2 optional unavailable · 1 optional disabled");

    [Fact]
    public void AccessibilityIsIncludedWithManualAssessmentAndNoCausalWording()
    {
        var card = Card(Rows(), FrontendQualityCategory.Accessibility);
        card.State.Should().Be(FrontendQualityDimensionState.Included);
        card.ManualAssessmentRequired.Should().BeTrue();
        card.Limitation.Should().Be("Automated accessibility checks are included.");
        card.Limitation.Should().NotContainAny("because", "Browser Quality", "missing");
    }

    [Theory]
    [InlineData(FrontendQualityCategory.Performance, "Passive Performance and Browser Companion evidence are included. Lighthouse is unavailable.")]
    [InlineData(FrontendQualityCategory.Security, "Static Security is included. Passive Security is unavailable.")]
    public void LimitedDomainsNameWhatRunsThenWhatIsUnavailable(FrontendQualityCategory category, string expected)
    {
        var card = Card(Rows(), category);
        card.State.Should().Be(FrontendQualityDimensionState.Limited);
        card.Limitation.Should().Be(expected);
    }

    [Theory]
    [InlineData(FrontendQualityCategory.Standards)]
    [InlineData(FrontendQualityCategory.BlazorWasm)]
    [InlineData(FrontendQualityCategory.Readiness)]
    public void OtherDomainsStayIncluded(FrontendQualityCategory category) =>
        Card(Rows(), category).State.Should().Be(FrontendQualityDimensionState.Included);

    [Fact]
    public void ConnectedIsReadyFromTheLiveSession()
    {
        var rows = Rows(BrowserCompanionState.Connected, Captured(3, 2));
        Engine(rows, FrontendQualityEngineId.BrowserQuality).State.Should().Be(FrontendQualityCapabilityState.Ready);
        // Connected, plus the captured evidence each engine will also read.
        Engine(rows, FrontendQualityEngineId.BrowserQuality).Summary.Should().Be("Browser Companion connected. 3 pages of captured evidence.");
        Engine(rows, FrontendQualityEngineId.PerformanceQuality).Summary.Should().Be("Browser Companion connected. Recorded performance evidence for 2 pages.");
    }

    [Fact]
    public void DisconnectedWithCapturedPagesIsReadyFromThatEvidenceAndNeverSaysConnected()
    {
        var rows = Rows(BrowserCompanionState.Disconnected, Captured(3, 2));
        var browser = Engine(rows, FrontendQualityEngineId.BrowserQuality);
        browser.State.Should().Be(FrontendQualityCapabilityState.Ready);
        browser.Summary.Should().Be("Uses captured browser evidence (3 pages). Browser Companion is not currently reporting.");
        var performance = Engine(rows, FrontendQualityEngineId.PerformanceQuality);
        performance.State.Should().Be(FrontendQualityCapabilityState.Ready);
        performance.Summary.Should().Be("Uses recorded performance evidence (2 pages). Browser Companion is not connected.");
        rows.Should().NotContain(r => r.Summary == "Browser Companion connected.");
        // Ready from evidence is not a limitation, so the banner stays as in the connected case.
        new FrontendQualityPreRunEngineSummary(rows).Counts.Should().Be("2 required available · 3 optional ready · 2 optional unavailable · 1 optional disabled");
    }

    [Fact]
    public void DisconnectedWithoutEvidenceStillRequiresABrowserSession()
    {
        var rows = Rows(BrowserCompanionState.Disconnected, Captured(0, 0));
        Engine(rows, FrontendQualityEngineId.BrowserQuality).State.Should().Be(FrontendQualityCapabilityState.RequiresBrowserSession);
        Engine(rows, FrontendQualityEngineId.PerformanceQuality).State.Should().Be(FrontendQualityCapabilityState.RequiresBrowserSession);
    }

    // Same rules as the run: Browser Quality is assessed from captured pages only while paired; Performance Quality
    // from recorded evidence in any session state.
    [Theory]
    [InlineData(BrowserCompanionState.Expired, FrontendQualityCapabilityState.RequiresBrowserSession)]
    [InlineData(BrowserCompanionState.NotPaired, FrontendQualityCapabilityState.NotConfigured)]
    [InlineData(BrowserCompanionState.PairingPending, FrontendQualityCapabilityState.NotConfigured)]
    public void ReadyFromEvidenceFollowsTheRunsAssessmentRules(BrowserCompanionState companion, FrontendQualityCapabilityState browserState)
    {
        var rows = Rows(companion, Captured(3, 2));
        Engine(rows, FrontendQualityEngineId.BrowserQuality).State.Should().Be(browserState);
        new BrowserQualityReviewResult { CompanionState = companion, PagesWithEvidence = 3 }.Assessed.Should().BeFalse();
        Engine(rows, FrontendQualityEngineId.PerformanceQuality).State.Should().Be(FrontendQualityCapabilityState.Ready);
    }

    [Fact]
    public void BrowserEvidenceWithoutPerformanceMetricsDoesNotMakePerformanceReady()
    {
        var rows = Rows(BrowserCompanionState.Disconnected, Captured(3, 0));
        Engine(rows, FrontendQualityEngineId.BrowserQuality).State.Should().Be(FrontendQualityCapabilityState.Ready);
        Engine(rows, FrontendQualityEngineId.PerformanceQuality).State.Should().Be(FrontendQualityCapabilityState.RequiresBrowserSession);
    }

    [Fact]
    public void UnknownCompanionStateIsNotPromotedByEvidence()
    {
        var rows = Rows(null, Captured(3, 2));
        Engine(rows, FrontendQualityEngineId.BrowserQuality).State.Should().Be(FrontendQualityCapabilityState.Enabled);
        Engine(rows, FrontendQualityEngineId.PerformanceQuality).State.Should().Be(FrontendQualityCapabilityState.Enabled);
    }
}
