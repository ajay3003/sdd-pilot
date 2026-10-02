using BirkNext.Applicability;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Frontend Quality Review on the shared scoring semantics: no category scored without its evidence; N/A and unavailable never 0.</summary>
public sealed class FrontendQualityScoringTests
{
    private static readonly FrontendQualityReviewService Service = new();

    [Fact]
    public void SecurityScanAlone_DoesNotScorePerformance()
    {
        var report = Service.BuildReport("https://site.example.test/", new WasmSecurityReviewReport { IsBlazorWasm = false, Health = new WasmSecurityHealth { Score = 90 } }, null);
        report.PerformanceScore.Should().BeNull("no performance engine ran (previously a default 100)");
        report.SecurityScore.Should().Be(90);
        report.WasmScore.Should().BeNull("WASM checks are not applicable to a non-WASM site");
        report.AccessibilityScore.Should().BeNull();
        report.OverallScore.Should().Be(90, "only assessed categories are averaged");
        report.CategoryScores.Single(c => c.Category == FrontendQualityCategory.BlazorWasm).Assessed.Should().BeFalse();
    }

    [Fact]
    public void UnreachableSecurityScan_AndNothingElse_HasNoScore()
    {
        var report = Service.BuildReport("https://site.example.test/", new WasmSecurityReviewReport { Health = new WasmSecurityHealth { Score = null } }, null);
        report.SecurityScore.Should().BeNull();
        report.OverallScore.Should().BeNull("nothing was assessed — no score, never 0");
        Service.BuildReport("https://site.example.test/", null, null).OverallScore.Should().BeNull();
    }

    [Fact]
    public void EngineAvailability_IsToolCoverage_NotQuality()
    {
        FrontendQualityEngineOutcome O(FrontendQualityEngineId id, FrontendQualityEngineExecutionState state) => new() { EngineId = id, ExecutionState = state };
        var coverage = FrontendQualityScoring.EngineCoverage(
        [
            O(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineExecutionState.Assessed),
            O(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineExecutionState.Unavailable),
            O(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineExecutionState.EngineError),
            O(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineExecutionState.NotApplicable),
        ]);
        coverage.ApplicableChecks.Should().Be(3);
        coverage.ExecutedChecks.Should().Be(1);
        coverage.NotAssessedChecks.Should().Be(2);
        FrontendQualityScoring.EngineOutcome(FrontendQualityEngineExecutionState.EngineError).Should().Be(CheckOutcome.Unavailable, "an engine error is not a project failure");
        ScoreSemantics.IsAssessed(FrontendQualityScoring.EngineOutcome(FrontendQualityEngineExecutionState.Assessed)).Should().BeFalse("engine coverage never enters quality");
    }
}
