using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public class ConstitutionComplianceServiceTests
{
    private readonly ConstitutionAnalysisService  _constitutionService = new();
    private readonly PlanAnalysisService          _planService         = new();
    private readonly ConstitutionComplianceService _sut                = new();

    // ── Helper to build ReviewContext for tests ──────────────────────────

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

    // ── Fixtures ─────────────────────────────────────────────────────────

    private ConstitutionDocument ParseConstitution(string md) =>
        _constitutionService.Parse(md);

    private PlanDocument ParsePlan(string md) =>
        _planService.Parse(md);

    private const string SimpleConstitution = """
        # Test Constitution

        ## Principles
        ### PP-01 Security First
        All systems must enforce authentication at every boundary.

        ### PP-02 Observability
        All services must emit structured logs and metrics.

        ## Standards
        ### PS-01 Test Coverage
        Minimum 80% test coverage required for all modules.

        ## Guidelines
        ### GL-01 Documentation
        All public APIs should include inline documentation.
        """;

    private const string AllCoveredSpec = """
        # Test Spec

        ## Functional Requirements
        ### FR-001 Authentication Gate
        PP-01: Enforces authentication at all service boundaries.

        ### FR-002 Logging Service
        PP-02: Implements structured logging across the platform.

        ### FR-003 Test Suite
        PS-01: Requires 80% test coverage on all new modules.
        """;

    private const string PartialSpec = """
        # Test Spec

        ## Functional Requirements
        ### FR-001 Authentication Gate
        PP-01: Enforces authentication at all service boundaries.
        """;

    private const string AllCoveredPlan = """
        # Test Plan

        ## Constitution Compliance
        | Principle | Requirement | Status |
        |-----------|-------------|--------|
        | PP-01     | FR-001      | Pass   |
        | PP-02     | FR-002      | Pass   |
        | PS-01     | FR-003      | Pass   |
        | GL-01     | —           | Pass   |

        ## Implementation Phases
        ### Phase 1: Core Auth
        - Implement PP-01 authentication
        - FR-001: Build auth middleware
        - FR-002: Add PP-02 structured logging
        - FR-003: Add PS-01 test suite setup
        """;

    private const string PlanWithViolation = """
        # Test Plan

        ## Constitution Compliance
        | Principle | Requirement | Status |
        |-----------|-------------|--------|
        | PP-01     | FR-001      | NonCompliant |
        | PP-02     | FR-002      | Pass   |
        """;

    private const string AllCoveredTasks = """
        # Tasks

        ## Phase 1

        ### Task 1.1 Implement auth (PP-01) [Status: Todo]
        References: FR-001
        Implements authentication boundary enforcement.

        ### Task 1.2 Add logging (PP-02) [Status: Todo]
        References: FR-002
        Sets up structured logging pipeline.

        ### Task 1.3 Write tests (PS-01) [Status: Todo]
        References: FR-003
        Adds unit + integration test coverage.
        """;

    // ── Null / empty handling ─────────────────────────────────────────────

    [Fact]
    public void Analyze_WithNullConstitution_ReturnsEmptyReport()
    {
        var context = BuildContext(null, null, null, null);
        var report = _sut.Analyze(null, null, null, null, context);

        report.Results.Should().BeEmpty();
        report.Violations.Should().BeEmpty();
        report.Gaps.Should().BeEmpty();
        report.Recommendations.Should().BeEmpty();
        report.HasConstitution.Should().BeFalse();
    }

    [Fact]
    public void Analyze_WithEmptyConstitution_ReturnsEmptyReport()
    {
        var constitution = ParseConstitution("# Empty Constitution");
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Results.Should().BeEmpty();
        report.HasConstitution.Should().BeTrue();
    }

    [Fact]
    public void Analyze_WithConstitutionOnly_ReturnsMissingForAll()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.HasConstitution.Should().BeTrue();
        report.HasSpecification.Should().BeFalse();
        report.HasPlan.Should().BeFalse();
        report.HasTasks.Should().BeFalse();

        report.Results.Should().NotBeEmpty();
        report.Results.Should().OnlyContain(r => r.Status == ComplianceStatus.Missing);
    }

    [Fact]
    public void Analyze_NullAllArtifacts_DoesNotThrow()
    {
        var context = BuildContext(null, null, null, null);
        var act = () => _sut.Analyze(null, null, null, null, context);
        act.Should().NotThrow();
    }

    // ── Coverage calculation ──────────────────────────────────────────────

    [Fact]
    public void Analyze_AllArtifactsLoaded_SetsHasFlags()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(AllCoveredSpec);
        var plan         = ParsePlan(AllCoveredPlan);
        var tasks        = TaskExplorerService.Parse(AllCoveredTasks);

        var context = BuildContext(constitution, spec, plan, tasks);
        var report = _sut.Analyze(constitution, spec, plan, tasks, context);

        report.HasConstitution.Should().BeTrue();
        report.HasSpecification.Should().BeTrue();
        report.HasPlan.Should().BeTrue();
        report.HasTasks.Should().BeTrue();
    }

    [Fact]
    public void Analyze_SpecReferencesRule_ResultHasSpecCoverage()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(AllCoveredSpec);

        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Analyze(constitution, spec, null, null, context);

        var pp01 = report.Results.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull();
        pp01!.HasSpecCoverage.Should().BeTrue();
    }

    [Fact]
    public void Analyze_SpecDoesNotReferenceRule_ResultMissingSpecCoverage()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(PartialSpec);

        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Analyze(constitution, spec, null, null, context);

        var pp02 = report.Results.FirstOrDefault(r => r.RuleId == "PP-02");
        pp02.Should().NotBeNull();
        pp02!.HasSpecCoverage.Should().BeFalse();
    }

    [Fact]
    public void Analyze_PlanReferencesRule_ResultHasPlanCoverage()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var plan         = ParsePlan(AllCoveredPlan);

        var context = BuildContext(constitution, null, plan, null);
        var report = _sut.Analyze(constitution, null, plan, null, context);

        var pp01 = report.Results.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull();
        pp01!.HasPlanCoverage.Should().BeTrue();
    }

    [Fact]
    public void Analyze_TasksReferenceRule_ResultHasTaskCoverage()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var tasks        = TaskExplorerService.Parse(AllCoveredTasks);

        var context = BuildContext(constitution, null, null, tasks);
        var report = _sut.Analyze(constitution, null, null, tasks, context);

        var pp01 = report.Results.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull();
        pp01!.HasTaskCoverage.Should().BeTrue();
    }

    // ── Status determination ──────────────────────────────────────────────

    [Fact]
    public void Analyze_RuleWithAllCoverage_IsCompliant()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(AllCoveredSpec);
        var plan         = ParsePlan(AllCoveredPlan);
        var tasks        = TaskExplorerService.Parse(AllCoveredTasks);

        var context = BuildContext(constitution, spec, plan, tasks);
        var report = _sut.Analyze(constitution, spec, plan, tasks, context);

        var pp01 = report.Results.FirstOrDefault(r => r.RuleId == "PP-01");
        pp01.Should().NotBeNull();
        pp01!.Status.Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public void Analyze_RuleWithNoArtifactCoverage_IsMissing()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Results.Should().OnlyContain(r => r.Status == ComplianceStatus.Missing);
    }

    // ── Violation detection ───────────────────────────────────────────────

    [Fact]
    public void Analyze_NonCompliantPlanGate_ProducesViolation()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var plan         = ParsePlan(PlanWithViolation);

        var context = BuildContext(constitution, null, plan, null);
        var report = _sut.Analyze(constitution, null, plan, null, context);

        report.Violations.Should().Contain(v => v.RuleId == "PP-01");
    }

    [Fact]
    public void Analyze_AllPassGates_ProducesNoViolations()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var plan         = ParsePlan(AllCoveredPlan);

        var context = BuildContext(constitution, null, plan, null);
        var report = _sut.Analyze(constitution, null, plan, null, context);

        report.Violations.Should().BeEmpty();
    }

    // ── Gap detection ─────────────────────────────────────────────────────

    [Fact]
    public void Analyze_MissingRule_AppearsInGaps()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(PartialSpec);

        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Analyze(constitution, spec, null, null, context);

        var pp02Gap = report.Gaps.FirstOrDefault(g => g.RuleId == "PP-02");
        pp02Gap.Should().NotBeNull();
        pp02Gap!.MissingInSpec.Should().BeTrue();
    }

    [Fact]
    public void Analyze_AllRulesCovered_ProducesNoGaps()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(AllCoveredSpec);
        var plan         = ParsePlan(AllCoveredPlan);
        var tasks        = TaskExplorerService.Parse(AllCoveredTasks);

        var context = BuildContext(constitution, spec, plan, tasks);
        var report = _sut.Analyze(constitution, spec, plan, tasks, context);

        report.Gaps.Should().BeEmpty();
    }

    // ── Coverage stats ────────────────────────────────────────────────────

    [Fact]
    public void Coverage_TotalItems_MatchesRuleCount()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Coverage.TotalItems.Should().Be(report.Results.Count);
    }

    [Fact]
    public void Coverage_AllMissing_ZeroCompliancePercentage()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Coverage.CompliancePercentage.Should().Be(0);
    }

    [Fact]
    public void Coverage_SomeCompliant_PositiveCompliancePercentage()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(PartialSpec);

        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Analyze(constitution, spec, null, null, context);

        report.Coverage.CompliancePercentage.Should().BeGreaterThan(0);
    }

    // ── Recommendations ───────────────────────────────────────────────────

    [Fact]
    public void Analyze_MissingRules_ProducesRecommendations()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Recommendations.Should().NotBeEmpty();
    }

    [Fact]
    public void Recommendations_HaveTargetArtifact()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Recommendations.Should().OnlyContain(r =>
            r.TargetArtifact == ArtifactType.Specification ||
            r.TargetArtifact == ArtifactType.Plan ||
            r.TargetArtifact == ArtifactType.Task);
    }

    // ── Health indicators ─────────────────────────────────────────────────

    [Fact]
    public void Health_TotalRules_MatchesConstitutionRuleCount()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Health.TotalRules.Should().Be(report.Results.Count);
    }

    [Fact]
    public void Health_ArtifactNotLoaded_ProducesWarningIndicator()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        report.Health.Indicators.Should().Contain(i => i.Level == ComplianceHealthLevel.Warning);
    }

    [Fact]
    public void Health_NoViolations_NoErrorIndicator()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var plan         = ParsePlan(AllCoveredPlan);

        var context = BuildContext(constitution, null, plan, null);
        var report = _sut.Analyze(constitution, null, plan, null, context);

        var errorIndicators = report.Health.Indicators
            .Where(i => i.Level == ComplianceHealthLevel.Error)
            .ToList();

        errorIndicators.Should().BeEmpty();
    }

    // ── Filter / Search ───────────────────────────────────────────────────

    [Fact]
    public void SearchResults_ByRuleId_ReturnsMatch()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        var matches = _sut.SearchResults(report.Results, "PP-01").ToList();

        matches.Should().Contain(r => r.RuleId == "PP-01");
    }

    [Fact]
    public void FilterResultsByStatus_ReturnsOnlyMatching()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        var filtered = _sut.FilterResultsByStatus(report.Results, ComplianceStatus.Missing).ToList();

        filtered.Should().OnlyContain(r => r.Status == ComplianceStatus.Missing);
    }

    [Fact]
    public void FilterResultsByRuleType_ReturnsOnlyMatchingType()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var context = BuildContext(constitution, null, null, null);
        var report = _sut.Analyze(constitution, null, null, null, context);

        var filtered = _sut.FilterResultsByRuleType(report.Results, ConstitutionRuleType.Principle).ToList();

        filtered.Should().OnlyContain(r => r.RuleType == ConstitutionRuleType.Principle);
    }

    [Fact]
    public void SearchGaps_ByRuleId_ReturnsMatch()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var spec         = SpecExplorerService.Parse(PartialSpec);
        var context = BuildContext(constitution, spec, null, null);
        var report = _sut.Analyze(constitution, spec, null, null, context);

        var matches = _sut.SearchGaps(report.Gaps, "PP-02").ToList();

        matches.Should().Contain(g => g.RuleId == "PP-02");
    }

    [Fact]
    public void FilterViolationsBySeverity_ReturnsOnlyMatching()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var plan         = ParsePlan(PlanWithViolation);
        var context = BuildContext(constitution, null, plan, null);
        var report = _sut.Analyze(constitution, null, plan, null, context);

        if (!report.Violations.Any()) return; // no violations produced — skip assertion

        var filtered = _sut.FilterViolationsBySeverity(report.Violations, ViolationSeverity.Critical).ToList();

        filtered.Should().OnlyContain(v => v.Severity == ViolationSeverity.Critical);
    }

    [Fact]
    public void FilterViolationsByArtifact_ReturnsOnlyPlanViolations()
    {
        var constitution = ParseConstitution(SimpleConstitution);
        var plan         = ParsePlan(PlanWithViolation);
        var context = BuildContext(constitution, null, plan, null);
        var report = _sut.Analyze(constitution, null, plan, null, context);

        if (!report.Violations.Any()) return;

        var filtered = _sut.FilterViolationsByArtifact(report.Violations, ArtifactType.Plan).ToList();

        filtered.Should().OnlyContain(v => v.Artifact == ArtifactType.Plan);
    }
}
