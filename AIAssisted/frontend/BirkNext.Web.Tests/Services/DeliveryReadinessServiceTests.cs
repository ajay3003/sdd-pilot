using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

public sealed class DeliveryReadinessServiceTests
{
    // ── Infrastructure ─────────────────────────────────────────────────────────

    private readonly ConstitutionAnalysisService  _constitutionSvc = new();
    private readonly PlanAnalysisService           _planSvc         = new();
    private readonly ArtifactTraceabilityService   _traceability    = new();
    private readonly ConstitutionComplianceService _compliance      = new();
    private readonly DeliveryReadinessService      _sut;

    public DeliveryReadinessServiceTests()
    {
        _sut = new DeliveryReadinessService(
            _traceability,
            _compliance,
            new QAReadinessService(_traceability, _compliance),
            new QaAuditorService(_traceability, _compliance));
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private const string SimpleConstitution = """
        # Project Constitution

        ## Rule Catalog

        ### PP-01 — Code Review Required
        Type: Process
        All code must be peer-reviewed before merge.

        ### PP-02 — Testing Required
        Type: Process
        All features must have automated tests.
        """;

    private const string FullSpec = """
        # Software Design Document

        ## Functional Requirements

        ### FR-001 User Authentication
        Users must be able to authenticate.

        #### Acceptance Criteria
        - AC: User can log in with valid credentials
        - AC: Invalid credentials show error

        ### FR-002 Dashboard
        Users must see a dashboard after login.

        #### Acceptance Criteria
        - AC: Dashboard shows user summary
        """;

    private const string FullPlan = """
        # Implementation Plan

        ## Summary
        Implement authentication and dashboard features.

        ## Architecture Decisions

        ### ADR-01 — Use JWT
        #### Status: Accepted
        #### Decision
        Use JWT tokens for authentication (FR-001). Implements PP-01.
        #### Rationale
        Stateless, scalable, industry standard.

        ## Implementation Phases

        ### Phase 1: Authentication
        Implement FR-001 authentication flows.

        #### Tasks
        - [ ] Implement login endpoint
        - [ ] Add JWT generation

        ## Testing Strategy
        All phases include unit and integration tests.

        ## Gates

        ### Pre-Merge Gate
        - Runs PP-01 code review check
        - Runs PP-02 testing check
        """;

    private const string FullTasks = """
        # Task List

        ## Sprint 1

        ### TASK-001 Implement Login
        References: FR-001
        Implement the login endpoint.

        ### TASK-002 Add Unit Tests
        References: FR-001, FR-002
        Add unit tests for authentication and dashboard.
        Type: Testing

        ### TASK-003 Dashboard View
        References: FR-002
        Implement dashboard view.
        """;

    // ── Null / empty handling ──────────────────────────────────────────────────

    [Fact]
    public void Assess_AllNull_DoesNotThrow()
    {
        var act = () => _sut.Assess(null, null, null, null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Assess_AllNull_ReturnsReport()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Should().NotBeNull();
        report.DevelopmentGate.Should().NotBeNull();
        report.TestingGate.Should().NotBeNull();
        report.ReleaseGate.Should().NotBeNull();
    }

    [Fact]
    public void Assess_AllNull_NoArtifactFlagsSet()
    {
        var report = _sut.Assess(null, null, null, null);
        report.HasConstitution.Should().BeFalse();
        report.HasSpecification.Should().BeFalse();
        report.HasPlan.Should().BeFalse();
        report.HasTasks.Should().BeFalse();
    }

    [Fact]
    public void Assess_AllNull_GatesHaveChecks()
    {
        var report = _sut.Assess(null, null, null, null);
        var devTotal  = report.DevelopmentGate.PassedChecks.Count + report.DevelopmentGate.FailedChecks.Count;
        var testTotal = report.TestingGate.PassedChecks.Count + report.TestingGate.FailedChecks.Count;
        devTotal.Should().BeGreaterThan(0);
        testTotal.Should().BeGreaterThan(0);
    }

    // ── Artifact flags ─────────────────────────────────────────────────────────

    [Fact]
    public void Assess_WithAllArtifacts_HasFlagsSetCorrectly()
    {
        var constitution = _constitutionSvc.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(FullSpec);
        var plan         = _planSvc.Parse(FullPlan);
        var tasks        = TaskExplorerService.Parse(FullTasks);

        var report = _sut.Assess(constitution, spec, plan, tasks);

        report.HasConstitution.Should().BeTrue();
        report.HasSpecification.Should().BeTrue();
        report.HasPlan.Should().BeTrue();
        report.HasTasks.Should().BeTrue();
    }

    [Fact]
    public void Assess_ConstitutionOnly_HasConstitutionTrue()
    {
        var constitution = _constitutionSvc.Parse(SimpleConstitution);
        var report = _sut.Assess(constitution, null, null, null);
        report.HasConstitution.Should().BeTrue();
        report.HasSpecification.Should().BeFalse();
    }

    // ── Development gate ───────────────────────────────────────────────────────

    [Fact]
    public void DevGate_Score_InRange0To100()
    {
        var report = _sut.Assess(null, null, null, null);
        report.DevelopmentGate.Score.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void DevGate_HasChecks()
    {
        var report = _sut.Assess(null, null, null, null);
        var total = report.DevelopmentGate.PassedChecks.Count + report.DevelopmentGate.FailedChecks.Count;
        total.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DevGate_WithNoSpecOrPlan_HasBlockersForMissingArtifacts()
    {
        var report = _sut.Assess(null, null, null, null);
        report.DevelopmentGate.Blockers.Should()
            .Contain(b => b.Category == "Specification" || b.Category == "Plan");
    }

    [Fact]
    public void DevGate_WithSpecAndPlan_ScoresHigherThanNoArtifacts()
    {
        var spec = SpecExplorerService.Parse(FullSpec);
        var plan = _planSvc.Parse(FullPlan);
        var withArtifacts = _sut.Assess(null, spec, plan, null);
        var noArtifacts   = _sut.Assess(null, null, null, null);
        withArtifacts.DevelopmentGate.Score.Should()
            .BeGreaterThan(noArtifacts.DevelopmentGate.Score);
    }

    [Fact]
    public void DevGate_WithConstitutionOnly_FailsComplianceCheck()
    {
        var constitution = _constitutionSvc.Parse(SimpleConstitution);
        var report = _sut.Assess(constitution, null, null, null);
        report.DevelopmentGate.FailedChecks.Should()
            .Contain(c => c.Contains("compliance", StringComparison.OrdinalIgnoreCase) ||
                          c.Contains("loaded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DevGate_StateEnum_IsValid()
    {
        var report = _sut.Assess(null, null, null, null);
        Enum.IsDefined(report.DevelopmentGate.State).Should().BeTrue();
    }

    // ── Testing gate ───────────────────────────────────────────────────────────

    [Fact]
    public void TestingGate_Score_InRange0To100()
    {
        var report = _sut.Assess(null, null, null, null);
        report.TestingGate.Score.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void TestingGate_HasChecks()
    {
        var report = _sut.Assess(null, null, null, null);
        var total = report.TestingGate.PassedChecks.Count + report.TestingGate.FailedChecks.Count;
        total.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TestingGate_WithSpecAndTasks_ScoresHigherThanNoArtifacts()
    {
        var spec  = SpecExplorerService.Parse(FullSpec);
        var tasks = TaskExplorerService.Parse(FullTasks);
        var withArtifacts = _sut.Assess(null, spec, null, tasks);
        var noArtifacts   = _sut.Assess(null, null, null, null);
        withArtifacts.TestingGate.Score.Should()
            .BeGreaterThan(noArtifacts.TestingGate.Score);
    }

    [Fact]
    public void TestingGate_StateEnum_IsValid()
    {
        var report = _sut.Assess(null, null, null, null);
        Enum.IsDefined(report.TestingGate.State).Should().BeTrue();
    }

    // ── Release gate ───────────────────────────────────────────────────────────

    [Fact]
    public void ReleaseGate_Score_InRange0To100()
    {
        var report = _sut.Assess(null, null, null, null);
        report.ReleaseGate.Score.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void ReleaseGate_HasChecks()
    {
        var report = _sut.Assess(null, null, null, null);
        var total = report.ReleaseGate.PassedChecks.Count + report.ReleaseGate.FailedChecks.Count;
        total.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ReleaseGate_WhenDevAndTestingNotCleared_HasBlockers()
    {
        var report = _sut.Assess(null, null, null, null);
        bool devFailed  = report.DevelopmentGate.State is ReadinessState.NotReady or ReadinessState.Blocked;
        bool testFailed = report.TestingGate.State    is ReadinessState.NotReady or ReadinessState.Blocked;
        if (devFailed && testFailed)
        {
            report.ReleaseGate.Blockers.Should()
                .Contain(b => b.Category == "Development" || b.Category == "Testing");
        }
    }

    [Fact]
    public void ReleaseGate_WithAllArtifacts_HasMorePassedChecks()
    {
        var constitution = _constitutionSvc.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(FullSpec);
        var plan         = _planSvc.Parse(FullPlan);
        var tasks        = TaskExplorerService.Parse(FullTasks);
        var withAll    = _sut.Assess(constitution, spec, plan, tasks);
        var withNone   = _sut.Assess(null, null, null, null);
        withAll.ReleaseGate.PassedChecks.Count.Should()
            .BeGreaterThanOrEqualTo(withNone.ReleaseGate.PassedChecks.Count);
    }

    // ── Health ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Health_AllScoresInRange()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Health.DevelopmentScore.Should().BeInRange(0, 100);
        report.Health.TestingScore.Should().BeInRange(0, 100);
        report.Health.ReleaseScore.Should().BeInRange(0, 100);
        report.Health.OverallReadinessScore.Should().BeInRange(0, 100);
    }

    [Fact]
    public void Health_OverallScore_IsAverageOfGateScores()
    {
        var report = _sut.Assess(null, null, null, null);
        double expected = Math.Round(
            (report.DevelopmentGate.Score + report.TestingGate.Score + report.ReleaseGate.Score) / 3.0, 1);
        report.Health.OverallReadinessScore.Should().Be(expected);
    }

    [Fact]
    public void Health_GateScoresMatchGates()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Health.DevelopmentScore.Should().Be(report.DevelopmentGate.Score);
        report.Health.TestingScore.Should().Be(report.TestingGate.Score);
        report.Health.ReleaseScore.Should().Be(report.ReleaseGate.Score);
    }

    // ── Blockers ───────────────────────────────────────────────────────────────

    [Fact]
    public void Blockers_WhenNoArtifacts_NotEmpty()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Blockers.Should().NotBeEmpty();
    }

    [Fact]
    public void Blockers_SortedBySeverity()
    {
        var report   = _sut.Assess(null, null, null, null);
        var sevOrder = report.Blockers.Select(b => (int)b.Severity).ToList();
        sevOrder.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Blockers_NoDuplicateTitles()
    {
        var constitution = _constitutionSvc.Parse(SimpleConstitution);
        var report       = _sut.Assess(constitution, null, null, null);
        var titles       = report.Blockers.Select(b => b.Title).ToList();
        titles.Should().OnlyHaveUniqueItems();
    }

    // ── Recommendations ────────────────────────────────────────────────────────

    [Fact]
    public void Recommendations_GeneratedWhenBlockersExist()
    {
        var report = _sut.Assess(null, null, null, null);
        if (report.Blockers.Count > 0)
            report.Recommendations.Should().NotBeEmpty();
    }

    [Fact]
    public void Recommendations_NoDuplicateTexts()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Recommendations.Select(r => r.Text).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Recommendations_SortedByPriority()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Recommendations.Select(r => (int)r.Priority).Should().BeInAscendingOrder();
    }

    // ── Decisions ─────────────────────────────────────────────────────────────

    [Fact]
    public void Decisions_StateMatchesGateState()
    {
        var spec   = SpecExplorerService.Parse(FullSpec);
        var plan   = _planSvc.Parse(FullPlan);
        var report = _sut.Assess(null, spec, plan, null);
        report.DevelopmentDecision.State.Should().Be(report.DevelopmentGate.State);
        report.TestingDecision.State.Should().Be(report.TestingGate.State);
        report.ReleaseDecision.State.Should().Be(report.ReleaseGate.State);
    }

    [Fact]
    public void Decisions_ScoreMatchesGateScore()
    {
        var report = _sut.Assess(null, null, null, null);
        report.DevelopmentDecision.Score.Should().Be(report.DevelopmentGate.Score);
        report.TestingDecision.Score.Should().Be(report.TestingGate.Score);
        report.ReleaseDecision.Score.Should().Be(report.ReleaseGate.Score);
    }

    [Fact]
    public void Decisions_SummaryNotNullWhenStateSet()
    {
        var report = _sut.Assess(null, null, null, null);
        report.DevelopmentDecision.Summary.Should().NotBeNullOrEmpty();
        report.TestingDecision.Summary.Should().NotBeNullOrEmpty();
        report.ReleaseDecision.Summary.Should().NotBeNullOrEmpty();
    }

    // ── Filter / search helpers ────────────────────────────────────────────────

    [Fact]
    public void FilterBlockersBySeverity_NullFilter_ReturnsAll()
    {
        var report = _sut.Assess(null, null, null, null);
        var result = _sut.FilterBlockersBySeverity(report.Blockers, null).ToList();
        result.Should().HaveCount(report.Blockers.Count);
    }

    [Fact]
    public void FilterBlockersBySeverity_CriticalFilter_ReturnsCriticalOnly()
    {
        var report = _sut.Assess(null, null, null, null);
        var result = _sut.FilterBlockersBySeverity(report.Blockers, GateSeverity.Critical).ToList();
        result.Should().AllSatisfy(b => b.Severity.Should().Be(GateSeverity.Critical));
    }

    [Fact]
    public void FilterBlockersByPhase_DevelopmentFilter_IncludesNullPhase()
    {
        var report = _sut.Assess(null, null, null, null);
        var result = _sut.FilterBlockersByPhase(report.Blockers, "Development").ToList();
        result.Should().AllSatisfy(b =>
            (b.Phase == null || b.Phase == "Development").Should().BeTrue());
    }

    [Fact]
    public void FilterRecommendationsByPhase_NullFilter_ReturnsAll()
    {
        var report = _sut.Assess(null, null, null, null);
        var result = _sut.FilterRecommendationsByPhase(report.Recommendations, null).ToList();
        result.Should().HaveCount(report.Recommendations.Count);
    }

    [Fact]
    public void SearchRecommendations_EmptyQuery_ReturnsAll()
    {
        var report = _sut.Assess(null, null, null, null);
        var result = _sut.SearchRecommendations(report.Recommendations, "").ToList();
        result.Should().HaveCount(report.Recommendations.Count);
    }

    [Fact]
    public void SearchRecommendations_NoMatchQuery_ReturnsEmpty()
    {
        var report = _sut.Assess(null, null, null, null);
        var result = _sut.SearchRecommendations(report.Recommendations, "xyzzy_no_match_12345").ToList();
        result.Should().BeEmpty();
    }
}
