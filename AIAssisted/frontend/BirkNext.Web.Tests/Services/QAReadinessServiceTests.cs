using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public class QAReadinessServiceTests
{
    private readonly ConstitutionAnalysisService   _constitutionService = new();
    private readonly PlanAnalysisService            _planService         = new();
    private readonly ArtifactTraceabilityService    _traceability        = new();
    private readonly ConstitutionComplianceService  _compliance          = new();
    private readonly QAReadinessService             _sut;

    public QAReadinessServiceTests()
    {
        _sut = new QAReadinessService(_traceability, _compliance);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private const string FullSpec = """
        # Feature Spec

        ## Functional Requirements
        ### FR-001 Authentication
        PP-01: All users must authenticate.
        AC: Given a valid token, user is authenticated.
        SC: Authentication succeeds for valid credentials.

        ### FR-002 Logging
        PP-02: All operations must emit structured logs.
        AC: Given an action, a log entry is created.

        ### FR-003 Test Coverage
        PS-01: Minimum 80% test coverage on new modules.

        ## User Stories
        ### US-001 As a user, I want to log in securely.

        ## Assumptions
        - System runs on AWS.

        ## Edge Cases
        - Expired tokens return 401.
        """;

    private const string MinimalSpec = """
        # Minimal Spec

        ## Functional Requirements
        ### FR-001 Basic Feature
        A basic feature requirement.
        """;

    private const string EmptySpec = """
        # Empty Spec
        Just some text, no structured items.
        """;

    private const string FullPlan = """
        # Feature Plan

        ## Summary
        Implements authentication and logging.

        ## Architecture Decisions
        ### ADR-01 Use JWT
        Context: Need stateless auth.
        Decision: Use JWT tokens.
        Rationale: Standard, well-supported.

        ## Constitution Compliance
        | Principle | Requirement | Status |
        |-----------|-------------|--------|
        | PP-01     | FR-001      | Pass   |
        | PP-02     | FR-002      | Pass   |
        | PS-01     | FR-003      | Pass   |

        ## Risks
        - Risk: Token expiry edge cases. Severity: Medium.

        ## Dependencies
        - JWT library v4.0

        ## Implementation Phases
        ### Phase 1: Auth Core
        - Implement FR-001 authentication
        - FR-002: Add logging

        ## Testing
        - Unit tests for auth service
        - Integration tests for token validation
        """;

    private const string MinimalPlan = """
        # Minimal Plan

        ## Implementation Phases
        ### Phase 1
        - Do the work
        """;

    private const string FullTasks = """
        # Tasks

        ## Phase 1

        ### Implementation
        - [ ] T001 Implement auth (PP-01) [Status: Todo]
          References: FR-001
        - [ ] T002 Add logging (PP-02) [Status: Todo]
          References: FR-002
        - [ ] T003 PS-01 test suite setup [Status: Todo]
          References: FR-003

        ### Testing
        - [ ] T004 Write unit tests for auth [Status: Todo]
          References: FR-001
        - [ ] T005 Write integration tests [Status: Todo]
          References: FR-002
        """;

    private const string SimpleConstitution = """
        # Test Constitution

        ## Principles
        ### PP-01 Security First
        All systems must enforce authentication.

        ### PP-02 Observability
        All services must emit structured logs.

        ## Standards
        ### PS-01 Test Coverage
        Minimum 80% test coverage required.
        """;

    // ── Null / empty handling ─────────────────────────────────────────────────

    [Fact]
    public void Assess_AllNull_DoesNotThrow()
    {
        var act = () => _sut.Assess(null, null, null, null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Assess_AllNull_ReturnsZeroScore()
    {
        var report = _sut.Assess(null, null, null, null);

        report.OverallScore.Should().Be(0);
        report.OverallStatus.Should().Be(ReadinessStatus.NotReady);
    }

    [Fact]
    public void Assess_AllNull_HasFlagsAllFalse()
    {
        var report = _sut.Assess(null, null, null, null);

        report.HasConstitution.Should().BeFalse();
        report.HasSpecification.Should().BeFalse();
        report.HasPlan.Should().BeFalse();
        report.HasTasks.Should().BeFalse();
    }

    [Fact]
    public void Assess_AllNull_AlwaysHasFiveScores()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Scores.Should().HaveCount(5);
    }

    [Fact]
    public void Assess_AllNull_NoScoreIsAssessed()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Scores.Should().OnlyContain(s => !s.IsAssessed);
    }

    // ── Specification scoring ─────────────────────────────────────────────────

    [Fact]
    public void SpecScore_WithFullSpec_IsHigherThanEmpty()
    {
        var full    = _sut.Assess(null, SpecExplorerService.Parse(FullSpec),    null, null);
        var minimal = _sut.Assess(null, SpecExplorerService.Parse(MinimalSpec), null, null);

        var fullSpecScore    = full.Scores.First(s => s.Category == "Specification Quality");
        var minimalSpecScore = minimal.Scores.First(s => s.Category == "Specification Quality");

        fullSpecScore.Score.Should().BeGreaterThan(minimalSpecScore.Score);
    }

    [Fact]
    public void SpecScore_WithRequirements_IsAssessedAndPositive()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(FullSpec), null, null);
        var specScore = report.Scores.First(s => s.Category == "Specification Quality");

        specScore.IsAssessed.Should().BeTrue();
        specScore.Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SpecScore_EmptySpec_IsAssessedButLow()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(EmptySpec), null, null);
        var specScore = report.Scores.First(s => s.Category == "Specification Quality");

        specScore.IsAssessed.Should().BeTrue();
        specScore.Score.Should().BeLessThan(50);
    }

    [Fact]
    public void SpecScore_NoSpec_IsNotAssessed()
    {
        var report = _sut.Assess(null, null, null, null);
        var specScore = report.Scores.First(s => s.Category == "Specification Quality");

        specScore.IsAssessed.Should().BeFalse();
    }

    [Fact]
    public void SpecScore_FullSpec_HasSignals()
    {
        var report    = _sut.Assess(null, SpecExplorerService.Parse(FullSpec), null, null);
        var specScore = report.Scores.First(s => s.Category == "Specification Quality");

        specScore.Signals.Should().NotBeEmpty();
    }

    // ── Plan scoring ──────────────────────────────────────────────────────────

    [Fact]
    public void PlanScore_WithFullPlan_IsHigherThanMinimal()
    {
        var full    = _sut.Assess(null, null, _planService.Parse(FullPlan),    null);
        var minimal = _sut.Assess(null, null, _planService.Parse(MinimalPlan), null);

        var fullScore    = full.Scores.First(s => s.Category == "Plan Quality");
        var minimalScore = minimal.Scores.First(s => s.Category == "Plan Quality");

        fullScore.Score.Should().BeGreaterThan(minimalScore.Score);
    }

    [Fact]
    public void PlanScore_NoPlan_IsNotAssessed()
    {
        var report    = _sut.Assess(null, null, null, null);
        var planScore = report.Scores.First(s => s.Category == "Plan Quality");

        planScore.IsAssessed.Should().BeFalse();
    }

    [Fact]
    public void PlanScore_WithPhases_IsAssessedAndPositive()
    {
        var report    = _sut.Assess(null, null, _planService.Parse(FullPlan), null);
        var planScore = report.Scores.First(s => s.Category == "Plan Quality");

        planScore.IsAssessed.Should().BeTrue();
        planScore.Score.Should().BeGreaterThan(0);
    }

    // ── Task scoring ──────────────────────────────────────────────────────────

    [Fact]
    public void TaskScore_WithTasks_IsAssessedAndPositive()
    {
        var report    = _sut.Assess(null, null, null, TaskExplorerService.Parse(FullTasks));
        var taskScore = report.Scores.First(s => s.Category == "Task Readiness");

        taskScore.IsAssessed.Should().BeTrue();
        taskScore.Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TaskScore_NoTasks_IsNotAssessed()
    {
        var report    = _sut.Assess(null, null, null, null);
        var taskScore = report.Scores.First(s => s.Category == "Task Readiness");

        taskScore.IsAssessed.Should().BeFalse();
    }

    [Fact]
    public void TaskScore_WithAnyTasks_ScoresHigherThanNoTasks()
    {
        // Tasks present (score > 0) vs. no tasks at all (score = 0)
        var withTasks = _sut.Assess(null, null, null, TaskExplorerService.Parse(FullTasks));
        var noTasks   = _sut.Assess(null, null, null,
                            TaskExplorerService.Parse("# Empty\nNo structured tasks here."));

        var withScore = withTasks.Scores.First(s => s.Category == "Task Readiness").Score;
        var noScore   = noTasks.Scores.First(s => s.Category == "Task Readiness").Score;

        withScore.Should().BeGreaterThan(noScore);
    }

    // ── Traceability scoring ──────────────────────────────────────────────────

    [Fact]
    public void TraceScore_SingleArtifact_IsNotAssessedOrLow()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(FullSpec), null, null);
        var traceScore = report.Scores.First(s => s.Category == "Traceability");

        // Either not assessed (no chains possible) or a low score
        (traceScore.IsAssessed == false || traceScore.Score <= 50).Should().BeTrue();
    }

    [Fact]
    public void TraceScore_FullChain_IsAssessedAndPositive()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(FullSpec);
        var plan         = _planService.Parse(FullPlan);
        var tasks        = TaskExplorerService.Parse(FullTasks);

        var report     = _sut.Assess(constitution, spec, plan, tasks);
        var traceScore = report.Scores.First(s => s.Category == "Traceability");

        traceScore.IsAssessed.Should().BeTrue();
        traceScore.Score.Should().BeGreaterThan(0);
    }

    // ── Compliance scoring ────────────────────────────────────────────────────

    [Fact]
    public void ComplianceScore_NoConstitution_IsNotAssessed()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(FullSpec), null, null);
        var compScore = report.Scores.First(s => s.Category == "Compliance");

        compScore.IsAssessed.Should().BeFalse();
    }

    [Fact]
    public void ComplianceScore_WithConstitution_IsAssessedAndPositive()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(FullSpec);

        var report    = _sut.Assess(constitution, spec, null, null);
        var compScore = report.Scores.First(s => s.Category == "Compliance");

        compScore.IsAssessed.Should().BeTrue();
        compScore.Score.Should().BeGreaterThan(0);
    }

    // ── Overall score ─────────────────────────────────────────────────────────

    [Fact]
    public void OverallScore_AlwaysInRange_0To100()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(FullSpec);
        var plan         = _planService.Parse(FullPlan);
        var tasks        = TaskExplorerService.Parse(FullTasks);

        var report = _sut.Assess(constitution, spec, plan, tasks);

        report.OverallScore.Should().BeInRange(0, 100);
    }

    [Fact]
    public void OverallScore_MoreArtifacts_IsHigherOrEqual()
    {
        var spec = SpecExplorerService.Parse(FullSpec);
        var plan = _planService.Parse(FullPlan);

        var specOnly    = _sut.Assess(null, spec, null,  null);
        var specAndPlan = _sut.Assess(null, spec, plan,  null);

        specAndPlan.OverallScore.Should().BeGreaterThanOrEqualTo(specOnly.OverallScore);
    }

    [Fact]
    public void OverallScore_FullSetHighQuality_IsAbove50()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(FullSpec);
        var plan         = _planService.Parse(FullPlan);
        var tasks        = TaskExplorerService.Parse(FullTasks);

        var report = _sut.Assess(constitution, spec, plan, tasks);

        report.OverallScore.Should().BeGreaterThan(50);
    }

    // ── ReadinessStatus ───────────────────────────────────────────────────────

    [Fact]
    public void Status_NoArtifacts_IsNotReady()
    {
        var report = _sut.Assess(null, null, null, null);
        report.OverallStatus.Should().Be(ReadinessStatus.NotReady);
    }

    [Fact]
    public void Status_FullHighQualityArtifacts_IsNotReady_OrBetter()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(FullSpec);
        var plan         = _planService.Parse(FullPlan);
        var tasks        = TaskExplorerService.Parse(FullTasks);

        var report = _sut.Assess(constitution, spec, plan, tasks);

        // With a full quality set the status should be at least NeedsWork
        report.OverallStatus.Should().NotBe(ReadinessStatus.NotReady);
    }

    // ── Readiness gates ───────────────────────────────────────────────────────

    [Fact]
    public void Gates_AlwaysHasThree()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Gates.Should().HaveCount(3);
    }

    [Fact]
    public void Gate_Implementation_NotReady_WhenNoArtifacts()
    {
        var report = _sut.Assess(null, null, null, null);
        var gate   = report.Gates.First(g => g.Name == "Ready for Implementation");

        gate.IsReady.Should().BeFalse();
        gate.BlockReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Gate_Implementation_BlockReason_ClearedOrPresentBasedOnScore()
    {
        var spec = SpecExplorerService.Parse(FullSpec);
        var plan = _planService.Parse(FullPlan);

        var report = _sut.Assess(null, spec, plan, null);
        var gate   = report.Gates.First(g => g.Name == "Ready for Implementation");

        // When spec and plan are loaded, the gate either passes or has a reason it doesn't
        if (!gate.IsReady)
            gate.BlockReason.Should().NotBeNullOrEmpty(
                because: "a blocked gate must say why it is blocked");
        else
            gate.BlockReason.Should().BeNullOrEmpty(
                because: "a ready gate has no block reason");
    }

    [Fact]
    public void Gate_Implementation_Ready_WhenScoresAboveThreshold()
    {
        // Drive the gate directly by checking what score level triggers it.
        // A spec with all signals: requirements, AC, US, no clarifications, assumptions, edges = 100
        // A plan with all signals: phases, arch, checks, risks, summary, deps, testing = 100
        var spec = SpecExplorerService.Parse(FullSpec);
        var plan = _planService.Parse(FullPlan);

        var report    = _sut.Assess(null, spec, plan, null);
        var specScore = report.Scores.First(s => s.Category == "Specification Quality").Score;
        var planScore = report.Scores.First(s => s.Category == "Plan Quality").Score;

        // If both categories score above their respective gate thresholds (65 and 60),
        // the gate must be ready; otherwise it must not be (i.e. gate logic is consistent)
        var gate = report.Gates.First(g => g.Name == "Ready for Implementation");
        var expectedReady = specScore >= 65 && planScore >= 60;

        gate.IsReady.Should().Be(expectedReady,
            because: $"implementation gate should be ready iff spec ({specScore}) >= 65 AND plan ({planScore}) >= 60");
    }

    [Fact]
    public void Gate_Testing_NotReady_WhenNoTasks()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(FullSpec), null, null);
        var gate   = report.Gates.First(g => g.Name == "Ready for Testing");

        gate.IsReady.Should().BeFalse();
    }

    [Fact]
    public void Gate_Release_NotReady_WhenNoArtifacts()
    {
        var report = _sut.Assess(null, null, null, null);
        var gate   = report.Gates.First(g => g.Name == "Ready for Release");

        gate.IsReady.Should().BeFalse();
    }

    [Fact]
    public void Gates_HaveNamesAndQuestions()
    {
        var report = _sut.Assess(null, null, null, null);
        report.Gates.Should().OnlyContain(g =>
            !string.IsNullOrEmpty(g.Name) && !string.IsNullOrEmpty(g.Question));
    }

    // ── Gaps and recommendations ──────────────────────────────────────────────

    [Fact]
    public void Gaps_PoorQualitySpec_HasSpecGap()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(EmptySpec), null, null);

        report.Gaps.Should().Contain(g => g.Category == "Specification Quality");
    }

    [Fact]
    public void Recommendations_PoorQualitySpec_HasRecommendations()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(EmptySpec), null, null);

        report.Recommendations.Should().NotBeEmpty();
    }

    [Fact]
    public void Recommendations_HaveValidTargetArtifacts()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(EmptySpec), null, null);

        report.Recommendations.Should().OnlyContain(r =>
            r.TargetArtifact == ArtifactType.Specification ||
            r.TargetArtifact == ArtifactType.Plan          ||
            r.TargetArtifact == ArtifactType.Task          ||
            r.TargetArtifact == ArtifactType.Constitution);
    }

    [Fact]
    public void Recommendations_SortedByPriority()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(EmptySpec),
                                 _planService.Parse(MinimalPlan), null);

        // Critical should appear before Low
        var priorities = report.Recommendations.Select(r => (int)r.Priority).ToList();
        priorities.Should().BeInAscendingOrder();
    }

    // ── Health ────────────────────────────────────────────────────────────────

    [Fact]
    public void Health_ScoresMatchCategoryScores()
    {
        var spec = SpecExplorerService.Parse(FullSpec);
        var plan = _planService.Parse(FullPlan);

        var report = _sut.Assess(null, spec, plan, null);

        report.Health.SpecificationScore.Should().Be(
            report.Scores.First(s => s.Category == "Specification Quality").Score);
        report.Health.PlanScore.Should().Be(
            report.Scores.First(s => s.Category == "Plan Quality").Score);
    }

    [Fact]
    public void Health_OverallMatchesReport()
    {
        var spec = SpecExplorerService.Parse(FullSpec);
        var report = _sut.Assess(null, spec, null, null);

        report.Health.OverallScore.Should().Be(report.OverallScore);
        report.Health.OverallStatus.Should().Be(report.OverallStatus);
    }

    // ── Filter helpers ────────────────────────────────────────────────────────

    [Fact]
    public void FilterGapsBySeverity_ReturnsOnlyMatchingSeverity()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(EmptySpec), null, null);

        if (!report.Gaps.Any()) return; // nothing to filter

        var filtered = _sut.FilterGapsBySeverity(report.Gaps, ViolationSeverity.High).ToList();
        filtered.Should().OnlyContain(g => g.Severity == ViolationSeverity.High);
    }

    [Fact]
    public void FilterRecommendationsByArtifact_ReturnsOnlyMatching()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(EmptySpec), null, null);

        var filtered = _sut.FilterRecommendationsByArtifact(
            report.Recommendations, ArtifactType.Specification).ToList();

        filtered.Should().OnlyContain(r => r.TargetArtifact == ArtifactType.Specification);
    }

    [Fact]
    public void FilterRecommendationsByPriority_NullReturnsAll()
    {
        var report = _sut.Assess(null, SpecExplorerService.Parse(FullSpec), null, null);

        var filtered = _sut.FilterRecommendationsByPriority(report.Recommendations, null).ToList();
        filtered.Should().HaveCount(report.Recommendations.Count);
    }
}
