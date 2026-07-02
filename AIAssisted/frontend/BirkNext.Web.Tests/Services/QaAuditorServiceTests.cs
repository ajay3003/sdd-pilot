using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public class QaAuditorServiceTests
{
    private readonly ConstitutionAnalysisService  _constitutionService = new();
    private readonly PlanAnalysisService           _planService         = new();
    private readonly ArtifactTraceabilityService   _traceability        = new();
    private readonly ConstitutionComplianceService _compliance          = new();
    private readonly QaAuditorService              _sut;

    public QaAuditorServiceTests()
    {
        _sut = new QaAuditorService(_traceability, _compliance);
    }

    // ── Helper to build ReviewContext for tests ────────────────────────────────

    private static ReviewContext BuildContext(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks)
    {
        var constModel = constitution is not null
            ? ConstitutionAnalysisService.BuildSemanticModel(constitution)
            : new ConstitutionSemanticModel();

        var specModel = spec is not null
            ? SpecExplorerService.BuildSemanticModel(spec, "")
            : new SpecificationSemanticModel();

        var planModel = plan is not null
            ? PlanAnalysisService.BuildSemanticModel(plan)
            : new PlanSemanticModel();

        var taskModel = tasks is not null
            ? TaskExplorerService.BuildSemanticModel(tasks)
            : new TaskSemanticModel();

        var dataModel = new DataModelSemanticModel();

        return ReviewContextFactory.Create(constModel, specModel, planModel, taskModel, dataModel);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private const string SimpleConstitution = """
        # Constitution

        ## Principles
        ### PP-01 Security First
        All systems must enforce authentication.

        ### PP-02 Observability
        All services must emit structured logs.

        ## Standards
        ### PS-01 Test Coverage
        Minimum 80% test coverage required.
        """;

    private const string FullSpec = """
        # Feature Spec

        ## Functional Requirements
        ### FR-001 Authentication
        PP-01: All users must authenticate.
        AC: Given a valid token, user is authenticated.

        ### FR-002 Logging
        PP-02: All operations must emit structured logs.
        AC: Given an action, a log entry is created.

        ## User Stories
        ### US-001 As a user, I want to log in securely.

        ## Edge Cases
        - Expired tokens return 401.
        """;

    private const string SpecNoAC = """
        # Spec Without Acceptance Criteria

        ## Functional Requirements
        ### FR-001 Authentication
        Users must authenticate before accessing the system.

        ### FR-002 Logging
        All operations must be logged.
        """;

    private const string FullPlan = """
        # Plan

        ## Summary
        Implements auth and logging.

        ## Architecture Decisions
        ### ADR-01 Use JWT
        Context: Need stateless auth.
        Decision: Use JWT tokens.
        Rationale: Industry standard, well-supported.

        ## Constitution Compliance
        | Principle | Requirement | Status |
        |-----------|-------------|--------|
        | PP-01     | FR-001      | Pass   |
        | PP-02     | FR-002      | Pass   |

        ## Risks
        - Risk: Token expiry edge cases. Severity: Medium.

        ## Dependencies
        - JWT library v4.0

        ## Implementation Phases
        ### Phase 1: Core Auth
        - Implement FR-001 authentication
        - FR-002: Add logging

        ## Testing
        - Unit tests for auth service
        - Integration tests for token validation
        """;

    private const string PlanNoPhases = """
        # Plan Without Phases

        ## Summary
        A basic plan.

        ## Architecture Decisions
        ### ADR-01 Use JWT
        Context: Need stateless auth.
        Decision: Use JWT tokens.
        """;

    private const string PlanWithViolation = """
        # Plan With Violation

        ## Constitution Compliance
        | Principle | Requirement | Status |
        |-----------|-------------|--------|
        | PP-01     | FR-001      | NonCompliant |
        """;

    private const string FullTasks = """
        # Tasks

        ## Phase 1
        ### Implementation
        - [ ] T001 Implement auth [Status: Todo]
        - [ ] T002 Add logging [Status: Todo]
        ### Testing
        - [ ] T003 Write unit tests [Status: Todo]
        - [ ] T004 Run integration tests [Status: Todo]
        """;

    // ── Null / empty handling ─────────────────────────────────────────────────

    [Fact]
    public void Audit_AllNull_DoesNotThrow()
    {
        var context = BuildContext(null, null, null, null);
        var act = () => _sut.Audit(null, null, null, null, context);
        act.Should().NotThrow();
    }

    [Fact]
    public void Audit_AllNull_EmptyFindings()
    {
        var context = BuildContext(null, null, null, null);
        var report = _sut.Audit(null, null, null, null, context);
        report.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Audit_AllNull_AllHasFlagsFalse()
    {
        var context = BuildContext(null, null, null, null);
        var report = _sut.Audit(null, null, null, null, context);
        report.HasConstitution.Should().BeFalse();
        report.HasSpecification.Should().BeFalse();
        report.HasPlan.Should().BeFalse();
        report.HasTasks.Should().BeFalse();
    }

    [Fact]
    public void Audit_AllNull_Score100_NoFindings()
    {
        var context = BuildContext(null, null, null, null);
        var report = _sut.Audit(null, null, null, null, context);
        report.Health.AuditScore.Should().Be(100);
    }

    // ── Constitution rules ────────────────────────────────────────────────────

    [Fact]
    public void ConstitutionRule_CONST001_WhenRuleNotCoveredByAnyArtifact()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);

        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        report.Findings.Should().Contain(f => f.RuleCode == "CONST-001");
    }

    [Fact]
    public void ConstitutionRule_CONST001_SeverityBasedOnRuleType()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        var principleFinding = report.Findings
            .FirstOrDefault(f => f.RuleCode == "CONST-001" && f.AffectedArtifact == "PP-01");

        principleFinding.Should().NotBeNull();
        principleFinding!.Severity.Should().Be(QaSeverity.Critical);
    }

    [Fact]
    public void ConstitutionRule_CONST003_WhenViolationInPlan()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var plan         = _planService.Parse(PlanWithViolation);

        var context = BuildContext(constitution, null, plan, null);
        var report = _sut.Audit(constitution, null, plan, null, context);

        report.Findings.Should().Contain(f => f.RuleCode == "CONST-003");
    }

    [Fact]
    public void ConstitutionRule_NoFindings_WhenConstitutionNotLoaded()
    {
        var spec = SpecExplorerService.Parse(FullSpec);

        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        // No CONST-* findings when no constitution loaded
        report.Findings.Should().NotContain(f => f.RuleCode.StartsWith("CONST-"));
    }

    // ── Specification rules ───────────────────────────────────────────────────

    [Fact]
    public void SpecRule_SPEC001_WhenRequirementsExistButNoAcceptanceCriteria()
    {
        var spec = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        report.Findings.Should().Contain(f => f.RuleCode == "SPEC-001");
    }

    [Fact]
    public void SpecRule_NoSPEC001_WhenSpecHasNoContent()
    {
        // A spec with only a title heading (TotalHeadings <= 1) should not fire SPEC-001
        const string minimalSpec = "# Spec Title";

        var spec   = SpecExplorerService.Parse(minimalSpec);
        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        report.Findings.Should().NotContain(f => f.RuleCode == "SPEC-001");
    }

    [Fact]
    public void SpecRule_SPEC001_SeverityIsHigh()
    {
        var spec = SpecExplorerService.Parse(SpecNoAC);
        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        var finding = report.Findings.FirstOrDefault(f => f.RuleCode == "SPEC-001");
        if (finding is not null)
            finding.Severity.Should().Be(QaSeverity.High);
    }

    [Fact]
    public void SpecRule_NoFinding_WhenSpecNotLoaded()
    {
        var context = BuildContext(null, null, null, null);
        var report = _sut.Audit(null, null, null, null, context);

        report.Findings.Should().NotContain(f => f.RuleCode.StartsWith("SPEC-"));
    }

    [Fact]
    public void SpecRule_SPEC005_WhenNoEdgeCases()
    {
        // SpecNoAC has requirements but no Edge Cases section
        var spec = SpecExplorerService.Parse(SpecNoAC);
        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        // SPEC-005 fires when requirements exist but no edge cases
        report.Findings.Should().Contain(f => f.RuleCode == "SPEC-005");
    }

    // ── Plan rules ────────────────────────────────────────────────────────────

    [Fact]
    public void PlanRule_PLAN001_WhenNoPhasesInPlan()
    {
        var plan = _planService.Parse(PlanNoPhases);

        var context = BuildContext(null, null, plan, null);
        var report = _sut.Audit(null, null, plan, null, context);

        report.Findings.Should().Contain(f => f.RuleCode == "PLAN-001");
    }

    [Fact]
    public void PlanRule_NoPLAN001_WhenPlanHasPhases()
    {
        var plan = _planService.Parse(FullPlan);

        var context = BuildContext(null, null, plan, null);
        var report = _sut.Audit(null, null, plan, null, context);

        report.Findings.Should().NotContain(f => f.RuleCode == "PLAN-001");
    }

    [Fact]
    public void PlanRule_PLAN003_WhenNoRisks()
    {
        const string planNoRisks = """
            # Plan Without Risks

            ## Implementation Phases
            ### Phase 1
            - Do something
            """;

        var plan = _planService.Parse(planNoRisks);
        var context = BuildContext(null, null, plan, null);
        var report = _sut.Audit(null, null, plan, null, context);

        report.Findings.Should().Contain(f => f.RuleCode == "PLAN-003");
    }

    [Fact]
    public void PlanRule_PLAN004_WhenNoTestingStrategy()
    {
        var plan = _planService.Parse(PlanNoPhases);
        var context = BuildContext(null, null, plan, null);
        var report = _sut.Audit(null, null, plan, null, context);

        report.Findings.Should().Contain(f => f.RuleCode == "PLAN-004");
    }

    [Fact]
    public void PlanRule_PLAN002_WhenADRMissingRationale()
    {
        const string planNoRationale = """
            # Plan

            ## Architecture Decisions
            ### ADR-01 Use JWT
            Context: Need stateless auth.
            Decision: Use JWT tokens.
            """;

        var plan = _planService.Parse(planNoRationale);
        var context = BuildContext(null, null, plan, null);
        var report = _sut.Audit(null, null, plan, null, context);

        report.Findings.Should().Contain(f => f.RuleCode == "PLAN-002");
    }

    [Fact]
    public void PlanRule_NoPLAN002_WhenADRHasRationale()
    {
        var plan = _planService.Parse(FullPlan);
        var context = BuildContext(null, null, plan, null);
        var report = _sut.Audit(null, null, plan, null, context);

        report.Findings.Should().NotContain(f => f.RuleCode == "PLAN-002");
    }

    // ── Task rules ────────────────────────────────────────────────────────────

    [Fact]
    public void TaskRule_NoFinding_WhenTasksNotLoaded()
    {
        var context = BuildContext(null, null, null, null);
        var report = _sut.Audit(null, null, null, null, context);

        report.Findings.Should().NotContain(f => f.RuleCode.StartsWith("TASK-"));
    }

    [Fact]
    public void TaskRule_GapAdded_WhenTasksNotLoaded()
    {
        var spec = SpecExplorerService.Parse(FullSpec);

        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        // A gap should appear when tasks are not loaded
        report.Gaps.Should().Contain(g => g.GapArea == "Missing Task Coverage");
    }

    // ── Audit score ───────────────────────────────────────────────────────────

    [Fact]
    public void AuditScore_NoFindings_Returns100()
    {
        var context = BuildContext(null, null, null, null);
        var report = _sut.Audit(null, null, null, null, context);
        report.Health.AuditScore.Should().Be(100);
    }

    [Fact]
    public void AuditScore_CriticalFindings_ReduceScore()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);

        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        // Critical findings for each uncovered principle
        report.Health.AuditScore.Should().BeLessThan(100);
    }

    [Fact]
    public void AuditScore_AlwaysInRange_0To100()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(SpecNoAC);
        var plan         = _planService.Parse(PlanNoPhases);

        var context = BuildContext(constitution, spec, plan, null);
        var report = _sut.Audit(constitution, spec, plan, null, context);

        report.Health.AuditScore.Should().BeInRange(0, 100);
    }

    // ── Health counts ─────────────────────────────────────────────────────────

    [Fact]
    public void Health_TotalFindingsMatchFindingsList()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        report.Health.TotalFindings.Should().Be(report.Findings.Count);
    }

    [Fact]
    public void Health_CriticalCount_MatchesCriticalFindings()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        int actual = report.Findings.Count(f => f.Severity == QaSeverity.Critical);
        report.Health.CriticalCount.Should().Be(actual);
    }

    [Fact]
    public void Health_HighCount_MatchesHighFindings()
    {
        var spec = SpecExplorerService.Parse(SpecNoAC);
        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        int actual = report.Findings.Count(f => f.Severity == QaSeverity.High);
        report.Health.HighCount.Should().Be(actual);
    }

    // ── Risks ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Risks_OnlyContainHighAndCriticalFindings()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Audit(constitution, spec, null, null, context);

        report.Risks.Should().OnlyContain(r =>
            r.Severity == QaSeverity.Critical || r.Severity == QaSeverity.High);
    }

    [Fact]
    public void Risks_CountMatchesCriticalPlusHigh()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        int expected = report.Findings.Count(f =>
            f.Severity == QaSeverity.Critical || f.Severity == QaSeverity.High);

        report.Risks.Should().HaveCount(expected);
    }

    // ── Coverage gaps ─────────────────────────────────────────────────────────

    [Fact]
    public void Gaps_MissingConstitutionCoverage_WhenRuleNotCovered()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);

        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        report.Gaps.Should().Contain(g => g.GapArea == "Missing Constitution Coverage");
    }

    [Fact]
    public void Gaps_MissingTestingCoverage_WhenPlanHasNoTestingSection()
    {
        var plan = _planService.Parse(PlanNoPhases);

        var context = BuildContext(null, null, plan, null);
        var report = _sut.Audit(null, null, plan, null, context);

        report.Gaps.Should().Contain(g => g.GapArea == "Missing Testing Coverage");
    }

    // ── Recommendations ───────────────────────────────────────────────────────

    [Fact]
    public void Recommendations_GeneratedForHighSeverityFindings()
    {
        var spec = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        report.Recommendations.Should().NotBeEmpty();
    }

    [Fact]
    public void Recommendations_SortedByPriority()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Audit(constitution, spec, null, null, context);

        var priorities = report.Recommendations.Select(r => (int)r.Priority).ToList();
        priorities.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Recommendations_NoDuplicateTexts()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Audit(constitution, spec, null, null, context);

        var texts = report.Recommendations.Select(r => r.Text).ToList();
        texts.Should().OnlyHaveUniqueItems();
    }

    // ── Search / filter ───────────────────────────────────────────────────────

    [Fact]
    public void SearchFindings_ByRuleCode_ReturnsMatch()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        var matches = _sut.SearchFindings(report.Findings, "CONST-001").ToList();

        matches.Should().OnlyContain(f => f.RuleCode == "CONST-001");
    }

    [Fact]
    public void FilterFindingsBySeverity_ReturnsOnlyMatching()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(constitution, spec, null, null);
        var report   = _sut.Audit(constitution, spec, null, null, context);
        var filtered = _sut.FilterFindingsBySeverity(report.Findings, QaSeverity.Critical).ToList();

        filtered.Should().OnlyContain(f => f.Severity == QaSeverity.Critical);
    }

    [Fact]
    public void FilterFindingsByCategory_ReturnsOnlyMatching()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(constitution, spec, null, null);
        var report   = _sut.Audit(constitution, spec, null, null, context);
        var filtered = _sut.FilterFindingsByCategory(report.Findings, QaCategory.Constitution).ToList();

        filtered.Should().OnlyContain(f => f.Category == QaCategory.Constitution);
    }

    [Fact]
    public void FilterFindingsBySeverity_NullReturnsAll()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        var filtered = _sut.FilterFindingsBySeverity(report.Findings, null).ToList();
        filtered.Should().HaveCount(report.Findings.Count);
    }

    [Fact]
    public void SearchGaps_ByGapArea_ReturnsMatch()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Audit(constitution, null, null, null, context);

        var matches = _sut.SearchGaps(report.Gaps, "Constitution").ToList();

        matches.Should().OnlyContain(g => g.GapArea.Contains("Constitution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SearchRecommendations_ByKeyword_ReturnsMatch()
    {
        var spec = SpecExplorerService.Parse(SpecNoAC);
        var context = BuildContext(null, spec, null, null);
        var report = _sut.Audit(null, spec, null, null, context);

        if (!report.Recommendations.Any()) return;

        var matches = _sut.SearchRecommendations(report.Recommendations, "acceptance").ToList();

        matches.Should().OnlyContain(r => r.Text.Contains("acceptance", StringComparison.OrdinalIgnoreCase) ||
                                          r.Category.ToString().Contains("acceptance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilterRecommendationsByCategory_ReturnsOnlyMatching()
    {
        var constitution = _constitutionService.Parse(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(SpecNoAC);

        var context = BuildContext(constitution, spec, null, null);
        var report   = _sut.Audit(constitution, spec, null, null, context);
        var filtered = _sut.FilterRecommendationsByCategory(report.Recommendations, QaCategory.Specification).ToList();

        filtered.Should().OnlyContain(r => r.Category == QaCategory.Specification);
    }
}
