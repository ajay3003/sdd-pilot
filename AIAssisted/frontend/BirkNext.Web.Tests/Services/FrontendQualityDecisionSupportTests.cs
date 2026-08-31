using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityDecisionSupportTests
{
    [Theory]
    [InlineData(FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment, FrontendQualityReleaseDisposition.Blocked)]
    [InlineData(FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed, FrontendQualityReleaseDisposition.ReviewRequired)]
    [InlineData(FrontendQualityRequiredCoverageState.AllRequiredAssessed, FrontendQualityReleaseDisposition.NoAutomatedBlockDetected)]
    public void CoverageStates_DriveExplicitBaselineDisposition(
        FrontendQualityRequiredCoverageState coverageState,
        FrontendQualityReleaseDisposition expected)
    {
        Evaluate(coverageState, AllAssessed(), [], [], new()).Should().Be(expected);
    }

    [Theory]
    [InlineData(FrontendQualityEngineExecutionState.EngineError)]
    [InlineData(FrontendQualityEngineExecutionState.TimedOut)]
    [InlineData(FrontendQualityEngineExecutionState.SafetyBlocked)]
    [InlineData(FrontendQualityEngineExecutionState.Unavailable)]
    [InlineData(FrontendQualityEngineExecutionState.Cancelled)]
    [InlineData(FrontendQualityEngineExecutionState.Disabled)]
    public void RequiredUnassessedEngine_RequiresReviewWhenOtherRequiredEvidenceExists(FrontendQualityEngineExecutionState state)
    {
        var outcomes = AllAssessed();
        outcomes[0] = Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, state, state != FrontendQualityEngineExecutionState.Disabled);

        Evaluate(FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed, outcomes, [], [], new())
            .Should().Be(FrontendQualityReleaseDisposition.ReviewRequired);
    }

    [Fact]
    public void OptionalDisabled_DoesNotAlterRequiredCoverageOrDisposition()
    {
        var outcomes = AllAssessed();
        outcomes[2] = Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.Disabled, false);

        Evaluate(FrontendQualityRequiredCoverageState.AllRequiredAssessed, outcomes, [], [], new())
            .Should().Be(FrontendQualityReleaseDisposition.NoAutomatedBlockDetected);
    }

    [Fact]
    public void OptionalEngineError_ReviewBehaviorIsExplicitlyConfigurable()
    {
        var outcomes = AllAssessed();
        outcomes[2] = Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.EngineError);

        Evaluate(FrontendQualityRequiredCoverageState.AllRequiredAssessed, outcomes, [], [], new())
            .Should().Be(FrontendQualityReleaseDisposition.ReviewRequired);
        Evaluate(FrontendQualityRequiredCoverageState.AllRequiredAssessed, outcomes, [], [], new() { ReviewOptionalEngineFailures = false })
            .Should().Be(FrontendQualityReleaseDisposition.NoAutomatedBlockDetected);
    }

    [Fact]
    public void ConfiguredBlockingLogicalIssue_BlocksButHighUnconfiguredIssueDoesNot()
    {
        var issue = Issue("headers:csp:missing", FrontendQualitySeverity.High);
        Evaluate(FrontendQualityRequiredCoverageState.AllRequiredAssessed, AllAssessed(), [issue], [],
                new() { BlockingLogicalIssueIds = [issue.LogicalId] })
            .Should().Be(FrontendQualityReleaseDisposition.Blocked);
        Evaluate(FrontendQualityRequiredCoverageState.AllRequiredAssessed, AllAssessed(), [issue], [], new())
            .Should().Be(FrontendQualityReleaseDisposition.NoAutomatedBlockDetected);
    }

    [Fact]
    public void ExplicitManualItem_RequiresReviewWithoutInventingFailure()
    {
        var manual = new FrontendQualityManualReviewItem { Title = "Keyboard navigation", Reason = "Requires human interaction", Source = "Accessibility" };
        Evaluate(FrontendQualityRequiredCoverageState.AllRequiredAssessed, AllAssessed(), [], [manual], new())
            .Should().Be(FrontendQualityReleaseDisposition.ReviewRequired);
    }

    [Fact]
    public void ManualItems_OnlyUseExplicitLogicalDispositionAndEngineObligations()
    {
        var issue = Issue("finding:Accessibility:manual:0", FrontendQualitySeverity.Medium) with
        {
            ReviewDisposition = FrontendQualityReviewDisposition.ManualVerificationRequired,
            ManualVerificationRequired = true,
        };
        var outcomes = AllAssessed();
        outcomes[3] = outcomes[3] with { ManualTestingObligations = ["Manual accessibility testing remains required."] };

        var items = FrontendQualityDecisionSupportService.BuildManualReviewItems([issue], outcomes);

        items.Should().HaveCount(2);
        items.Should().Contain(item => item.RelatedLogicalId == issue.LogicalId);
        items.Should().Contain(item => item.Reason.Contains("Manual accessibility"));
    }

    [Theory]
    [InlineData(FrontendQualityEngineExecutionState.Assessed, "Assessed")]
    [InlineData(FrontendQualityEngineExecutionState.Disabled, "Disabled")]
    [InlineData(FrontendQualityEngineExecutionState.Unavailable, "Unavailable")]
    [InlineData(FrontendQualityEngineExecutionState.SafetyBlocked, "Safety blocked")]
    [InlineData(FrontendQualityEngineExecutionState.TimedOut, "Timed out")]
    [InlineData(FrontendQualityEngineExecutionState.Cancelled, "Cancelled")]
    [InlineData(FrontendQualityEngineExecutionState.EngineError, "Engine error")]
    [InlineData(FrontendQualityEngineExecutionState.NotApplicable, "Not applicable")]
    public void EveryTypedExecutionState_HasDistinctPresentation(FrontendQualityEngineExecutionState state, string label) =>
        FrontendQualityDecisionSupportService.ExecutionStateLabel(state).Should().Be(label);

    [Fact]
    public void DiagnosticJson_PreservesDecisionSupportAndSourceScores()
    {
        var report = Report();
        var json = JsonSerializer.Serialize(report);
        var roundTrip = JsonSerializer.Deserialize<FrontendQualityReviewReport>(json)!;

        roundTrip.ReleaseDisposition.Should().Be(report.ReleaseDisposition);
        roundTrip.Coverage!.RequiredCoverageState.Should().Be(report.Coverage!.RequiredCoverageState);
        roundTrip.EngineOutcomes.Should().HaveCount(6);
        roundTrip.LogicalIssues.Should().HaveCount(1);
        roundTrip.ManualReviewItems.Should().HaveCount(1);
        roundTrip.SecurityScore.Should().Be(81);
        roundTrip.PerformanceScore.Should().Be(72);
        roundTrip.OverallScore.Should().Be(76);
    }

    internal static FrontendQualityReviewReport Report()
    {
        var issue = Issue("headers:csp:missing", FrontendQualitySeverity.High);
        return new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            GeneratedAt = DateTime.UtcNow,
            OverallScore = 76,
            SecurityScore = 81,
            PerformanceScore = 72,
            Coverage = new() { RequiredCoverageState = FrontendQualityRequiredCoverageState.AllRequiredAssessed },
            ReleaseDisposition = FrontendQualityReleaseDisposition.ReviewRequired,
            EngineOutcomes = AllAssessed(),
            Findings = [Finding("static-csp", FrontendQualityEngineId.StaticSecurity), Finding("zap-csp", FrontendQualityEngineId.PassiveSecurity)],
            LogicalIssues = [issue],
            ManualReviewItems = [new() { Title = "Keyboard navigation", Reason = "Automation cannot complete interaction testing.", Source = "Accessibility" }],
            BrowserRuntimeReport = new(BrowserRuntimeEngineStatusDto.Assessed, BrowserName: "Chromium", BrowserVersion: "130", DurationMs: 250,
                ConsoleErrorCount: 1, PageErrorCount: 1, CriticalResourceFailureCount: 1,
                Findings: [new("runtime-wasm", "WASM bootstrap failure", BrowserRuntimeFindingSeverityDto.High, "PageError", "Runtime failed", "Inspect startup", ["sanitized evidence"])],
                Limitations: ["Single startup observation"]),
            LighthouseReport = new(LighthouseExecutionStatusDto.Assessed, PerformanceScore: 88),
        };
    }

    internal static List<FrontendQualityEngineOutcome> AllAssessed() =>
    [
        Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required),
        Outcome(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineRequirement.Required),
        Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineRequirement.Optional),
        Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineRequirement.Optional),
        Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional),
        Outcome(FrontendQualityEngineId.PassiveSecurity, FrontendQualityEngineRequirement.Optional),
    ];

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityEngineId id, FrontendQualityEngineRequirement requirement,
        FrontendQualityEngineExecutionState state = FrontendQualityEngineExecutionState.Assessed, bool enabled = true) => new()
        { EngineId = id, DisplayName = FrontendQualityDecisionSupportService.SourceLabel(id), Requirement = requirement, Enabled = enabled, ExecutionState = state, FindingCount = 1, EvidenceCount = 1 };

    private static FrontendQualityLogicalIssue Issue(string id, FrontendQualitySeverity severity) => new()
    {
        LogicalId = id, CanonicalTitle = "Content Security Policy header missing", PrimarySeverity = severity,
        Sources = [FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassiveSecurity],
        FindingInstances =
        [
            Instance("static-csp", FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY"),
            Instance("zap-csp", FrontendQualityEngineId.PassiveSecurity, "10038"),
        ],
        EvidenceStrength = FrontendQualityEvidenceStrength.ToolDiagnostic,
        Confidence = FrontendQualityEvidenceConfidence.High,
        ReviewDisposition = FrontendQualityReviewDisposition.AutomatedFinding,
        Category = FrontendQualityCategory.Security,
        Recommendation = "Configure CSP.",
    };

    private static FrontendQualityFindingInstance Instance(string id, FrontendQualityEngineId engine, string rule) => new()
    {
        EngineId = engine, SourceSystem = engine.ToString(), SourceFindingId = id, SourceRuleId = rule, Title = id,
        Severity = FrontendQualitySeverity.High, Category = FrontendQualityCategory.Security, Description = "description",
        Recommendation = "recommendation", SanitizedEvidence = ["sanitized evidence"], ExecutionState = CheckExecutionStatus.Failed,
        EvidenceStrength = engine == FrontendQualityEngineId.PassiveSecurity ? FrontendQualityEvidenceStrength.ToolDiagnostic : FrontendQualityEvidenceStrength.StaticIndicator,
        ReviewDisposition = FrontendQualityReviewDisposition.AutomatedFinding,
    };

    private static FrontendQualityFinding Finding(string id, FrontendQualityEngineId engine) => new()
    { Id = id, EngineId = engine, SourceRuleId = id, SourceSystem = engine.ToString(), Title = id, Severity = FrontendQualitySeverity.High, Category = FrontendQualityCategory.Security };

    private static FrontendQualityReleaseDisposition Evaluate(FrontendQualityRequiredCoverageState state,
        IReadOnlyList<FrontendQualityEngineOutcome> outcomes, IReadOnlyList<FrontendQualityLogicalIssue> issues,
        IReadOnlyList<FrontendQualityManualReviewItem> manual, FrontendQualityReleasePolicySettings policy) =>
        FrontendQualityDecisionSupportService.EvaluateReleaseDisposition(new() { RequiredCoverageState = state }, outcomes, issues, manual, policy);
}
