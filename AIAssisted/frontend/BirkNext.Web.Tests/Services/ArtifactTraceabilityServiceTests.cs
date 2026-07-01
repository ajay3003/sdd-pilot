using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ArtifactTraceabilityServiceTests
{
    private readonly ArtifactTraceabilityService _svc = new();

    // ── Test fixtures ─────────────────────────────────────────────────────────

    private static ConstitutionDocument Constitution() =>
        new ConstitutionAnalysisService().Parse("""
            # Test Constitution

            ## Core Principles

            ### PP-01 Zero-Trust Security
            All access requires explicit authorization. No implicit trust.

            ### PP-02 Least Privilege
            Grant the minimum permissions required.

            ### PP-03 Separation of Concerns
            Authorization logic must not leak into business logic.
            """);

    private static SpecTree Spec() =>
        SpecExplorerService.Parse("""
            # Feature Specification

            ## Requirements

            ### FR-001 Authentication Gate
            All users must authenticate before accessing any protected resource. PP-01 compliance required.

            ### FR-002 Role-Based Access
            The system shall enforce PP-02 by granting only required roles.

            ### FR-003 UI Polish
            The landing page shall have consistent typography.
            """);

    private static PlanDocument Plan() =>
        new PlanAnalysisService().Parse("""
            # Implementation Plan

            ## Architecture

            ### Authorization Middleware
            Implements FR-001 requirements. All requests validated against auth service.

            ## Implementation

            ### Phase 1: Core Auth
            - Implement token validation covering FR-001
            - Add role enforcement per FR-002
            """);

    private static TaskTree Tasks() =>
        TaskExplorerService.Parse("""
            # Implementation Tasks

            ## Phase 1

            - [ ] T001 Add JWT validation middleware (FR-001)
            - [ ] T002 Add role enforcement service (FR-001, FR-002)
            - [ ] T003 Write unit tests for auth service
            - [ ] T004 Fix button spacing on dashboard
            """);

    private static SpecTree SpecWithNoRuleRefs() =>
        SpecExplorerService.Parse("""
            # Spec Without Rule Refs

            ## Requirements

            ### FR-001 Some Feature
            This requirement mentions nothing about constitution rules.
            """);

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

    // ── 1: Constitution → Spec coverage ──────────────────────────────────────

    [Fact]
    public void ConstitutionCoverage_CoveredWhenSpecReferencesRule()
    {
        var context = BuildContext(Constitution(), Spec(), null, null);
        var report = _svc.Analyze(Constitution(), Spec(), null, null, context);

        var pp01Entry = report.ConstitutionToSpec.FirstOrDefault(c => c.ItemId == "PP-01");
        pp01Entry.Should().NotBeNull("PP-01 is mentioned in FR-001 content");
        pp01Entry!.Status.Should().Be(TraceabilityStatus.Covered,
            "FR-001 is a Requirement node that mentions PP-01");
    }

    [Fact]
    public void ConstitutionCoverage_MissingWhenNoSpecReference()
    {
        var context = BuildContext(Constitution(), SpecWithNoRuleRefs(), null, null);
        var report = _svc.Analyze(Constitution(), SpecWithNoRuleRefs(), null, null, context);

        report.ConstitutionToSpec.Should().NotBeEmpty();
        var missing = report.ConstitutionToSpec.Where(c => c.Status == TraceabilityStatus.Missing).ToList();
        missing.Should().NotBeEmpty("most rules are not mentioned in the spec with no rule refs");
    }

    [Fact]
    public void ConstitutionCoverage_ReturnsOneEntryPerRule()
    {
        var context = BuildContext(Constitution(), Spec(), null, null);
        var report = _svc.Analyze(Constitution(), Spec(), null, null, context);

        report.ConstitutionToSpec.Should()
            .HaveCount(Constitution().RuleCatalog.Count,
                "one ChainCoverage entry per constitution rule");
    }

    [Fact]
    public void ConstitutionCoverage_StatsMatchChainEntries()
    {
        var context = BuildContext(Constitution(), Spec(), null, null);
        var report = _svc.Analyze(Constitution(), Spec(), null, null, context);

        report.ConstitutionCoverage.TotalItems.Should().Be(report.ConstitutionToSpec.Count);
        report.ConstitutionCoverage.CoveredItems.Should().Be(
            report.ConstitutionToSpec.Count(c => c.Status == TraceabilityStatus.Covered));
        report.ConstitutionCoverage.MissingItems.Should().Be(
            report.ConstitutionToSpec.Count(c => c.Status == TraceabilityStatus.Missing));
    }

    // ── 2: Spec → Plan coverage ───────────────────────────────────────────────

    [Fact]
    public void SpecCoverage_CoveredWhenPlanMentionsFR()
    {
        var context = BuildContext(null, Spec(), Plan(), null);
        var report = _svc.Analyze(null, Spec(), Plan(), null, context);

        var fr001 = report.SpecToPlan.FirstOrDefault(s => s.ItemId.Equals("FR-001", StringComparison.OrdinalIgnoreCase));
        fr001.Should().NotBeNull("FR-001 is mentioned in the plan architecture section");
        fr001!.Status.Should().Be(TraceabilityStatus.Covered);
    }

    [Fact]
    public void SpecCoverage_MissingWhenPlanOmitsFR()
    {
        var context = BuildContext(null, Spec(), Plan(), null);
        var report = _svc.Analyze(null, Spec(), Plan(), null, context);

        // FR-003 is not mentioned in the plan
        var fr003 = report.SpecToPlan.FirstOrDefault(s => s.ItemId.Equals("FR-003", StringComparison.OrdinalIgnoreCase));
        fr003.Should().NotBeNull("FR-003 exists in spec");
        fr003!.Status.Should().Be(TraceabilityStatus.Missing,
            "FR-003 is a UI polish requirement not mentioned in the plan");
    }

    [Fact]
    public void SpecCoverage_StatsMatchChainEntries()
    {
        var context = BuildContext(null, Spec(), Plan(), null);
        var report = _svc.Analyze(null, Spec(), Plan(), null, context);

        report.SpecificationCoverage.TotalItems.Should().Be(report.SpecToPlan.Count);
    }

    // ── 3: Plan → Task coverage ───────────────────────────────────────────────

    [Fact]
    public void PlanCoverage_CoveredWhenTaskReferencesFR()
    {
        var context = BuildContext(null, null, Plan(), Tasks());
        var report = _svc.Analyze(null, null, Plan(), Tasks(), context);

        var covered = report.PlanToTask.Where(p => p.Status == TraceabilityStatus.Covered).ToList();
        covered.Should().NotBeEmpty("tasks T001 and T002 reference FR-001 which is in the plan");
    }

    [Fact]
    public void PlanCoverage_PlanToTaskEntriesCreated()
    {
        var context = BuildContext(null, null, Plan(), Tasks());
        var report = _svc.Analyze(null, null, Plan(), Tasks(), context);

        report.PlanToTask.Should().NotBeEmpty("plan has ADRs and phases");
    }

    // ── 4: Orphan task detection ──────────────────────────────────────────────

    [Fact]
    public void OrphanTask_DetectedWhenNoFrOrScRefs()
    {
        var context = BuildContext(null, null, null, Tasks());
        var report = _svc.Analyze(null, null, null, Tasks(), context);

        // T004 "Fix button spacing on dashboard" has no FR/SC refs
        var orphanGaps = report.Gaps
            .Where(g => g.GapIn == ArtifactType.Task && g.Status == TraceabilityStatus.Orphaned)
            .ToList();
        orphanGaps.Should().NotBeEmpty("T004 has no FR references");
    }

    [Fact]
    public void OrphanTask_TaskCoverageStatsCountsOrphans()
    {
        var context = BuildContext(null, null, null, Tasks());
        var report = _svc.Analyze(null, null, null, Tasks(), context);

        report.TaskCoverage.OrphanedItems.Should().BeGreaterThan(0,
            "at least one orphan task expected");
        report.TaskCoverage.TotalItems.Should().BeGreaterThan(0);
    }

    // ── 5: Gap detection ──────────────────────────────────────────────────────

    [Fact]
    public void GapDetection_IncludesMissingRules()
    {
        var context = BuildContext(Constitution(), SpecWithNoRuleRefs(), null, null);
        var report = _svc.Analyze(Constitution(), SpecWithNoRuleRefs(), null, null, context);

        var constitutionGaps = report.Gaps
            .Where(g => g.GapIn == ArtifactType.Constitution && g.Status == TraceabilityStatus.Missing)
            .ToList();
        constitutionGaps.Should().NotBeEmpty("rules not covered by spec generate gaps");
    }

    [Fact]
    public void GapDetection_IncludesOrphanTasks()
    {
        var context = BuildContext(null, null, null, Tasks());
        var report = _svc.Analyze(null, null, null, Tasks(), context);

        var taskGaps = report.Gaps
            .Where(g => g.GapIn == ArtifactType.Task && g.Status == TraceabilityStatus.Orphaned)
            .ToList();
        taskGaps.Should().NotBeEmpty();
    }

    [Fact]
    public void GapDetection_IncludesMissingSpecRequirements()
    {
        var context = BuildContext(null, Spec(), Plan(), null);
        var report = _svc.Analyze(null, Spec(), Plan(), null, context);

        var specGaps = report.Gaps
            .Where(g => g.GapIn == ArtifactType.Specification && g.Status == TraceabilityStatus.Missing)
            .ToList();
        // FR-003 not in plan → should generate a spec gap
        specGaps.Should().NotBeEmpty("FR-003 is not referenced in the plan");
    }

    // ── 6: Matrix generation ──────────────────────────────────────────────────

    [Fact]
    public void Matrix_ContainsRowsWhenConstitutionLoaded()
    {
        var context = BuildContext(Constitution(), Spec(), null, null);
        var report = _svc.Analyze(Constitution(), Spec(), null, null, context);

        report.Matrix.Should().NotBeEmpty("constitution rules generate matrix rows");
    }

    [Fact]
    public void Matrix_ContainsMissingRowForUncoveredRule()
    {
        var context = BuildContext(Constitution(), SpecWithNoRuleRefs(), null, null);
        var report = _svc.Analyze(Constitution(), SpecWithNoRuleRefs(), null, null, context);

        var missingRows = report.Matrix.Where(r => r.Status == TraceabilityStatus.Missing).ToList();
        missingRows.Should().NotBeEmpty("rules without spec coverage → Missing rows");
        missingRows.All(r => string.IsNullOrEmpty(r.SpecRequirementId)).Should().BeTrue(
            "missing rows should have no spec requirement filled");
    }

    [Fact]
    public void Matrix_ContainsFullChainRowWhenAllArtifactsLoaded()
    {
        var context = BuildContext(Constitution(), Spec(), Plan(), Tasks());
        var report = _svc.Analyze(Constitution(), Spec(), Plan(), Tasks(), context);

        // A fully covered row has all four columns filled
        var coveredRows = report.Matrix.Where(r => r.Status == TraceabilityStatus.Covered).ToList();
        coveredRows.Should().NotBeEmpty("PP-01 → FR-001 → plan → T001/T002 forms a full chain");
        var fullRow = coveredRows.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.ConstitutionRuleId) &&
            !string.IsNullOrEmpty(r.SpecRequirementId) &&
            !string.IsNullOrEmpty(r.PlanItemId) &&
            !string.IsNullOrEmpty(r.TaskId));
        fullRow.Should().NotBeNull("at least one full 4-column covered row expected");
    }

    // ── 7: Search and filter ──────────────────────────────────────────────────

    [Fact]
    public void FilterGapsByArtifact_ReturnsOnlyMatching()
    {
        var context = BuildContext(Constitution(), SpecWithNoRuleRefs(), null, Tasks());
        var report = _svc.Analyze(Constitution(), SpecWithNoRuleRefs(), null, Tasks(), context);

        var constGaps = _svc.FilterGapsByArtifact(report.Gaps, ArtifactType.Constitution).ToList();
        constGaps.Should().NotBeEmpty();
        constGaps.Should().OnlyContain(g => g.GapIn == ArtifactType.Constitution);
    }

    [Fact]
    public void SearchMatrix_FindsByRuleId()
    {
        var context = BuildContext(Constitution(), Spec(), null, null);
        var report = _svc.Analyze(Constitution(), Spec(), null, null, context);

        var results = _svc.SearchMatrix(report.Matrix, "PP-01").ToList();
        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => r.ConstitutionRuleId.Contains("PP-01"));
    }

    [Fact]
    public void FilterMatrixByStatus_ReturnsOnlyMatching()
    {
        var context = BuildContext(Constitution(), Spec(), null, null);
        var report = _svc.Analyze(Constitution(), Spec(), null, null, context);

        var covered = _svc.FilterMatrixByStatus(report.Matrix, TraceabilityStatus.Covered).ToList();
        covered.Should().OnlyContain(r => r.Status == TraceabilityStatus.Covered);
    }

    // ── 8: Health and totals ──────────────────────────────────────────────────

    [Fact]
    public void Health_TotalCountsMatchInputs()
    {
        var constitution = Constitution();
        var context = BuildContext(constitution, Spec(), Plan(), Tasks());
        var report = _svc.Analyze(constitution, Spec(), Plan(), Tasks(), context);

        report.Health.TotalRules.Should().Be(constitution.RuleCatalog.Count);
        report.Health.TotalRules.Should().BeGreaterThan(0);
    }

    // ── 9: Null artifact handling ─────────────────────────────────────────────

    [Fact]
    public void NullArtifacts_ProduceEmptyReportWithoutCrash()
    {
        var context = BuildContext(null, null, null, null);
        var report = _svc.Analyze(null, null, null, null, context);

        report.Should().NotBeNull();
        report.ConstitutionToSpec.Should().BeEmpty();
        report.SpecToPlan.Should().BeEmpty();
        report.PlanToTask.Should().BeEmpty();
        report.Gaps.Should().BeEmpty();
        report.Matrix.Should().BeEmpty();
    }

    [Fact]
    public void PartialAnalysis_ConstitutionOnlyDoesNotCrash()
    {
        var context = BuildContext(Constitution(), null, null, null);
        var report = _svc.Analyze(Constitution(), null, null, null, context);

        report.ConstitutionToSpec.Should().BeEmpty("no spec to analyze against");
        report.Matrix.Should().NotBeEmpty("constitution rules generate matrix rows even without spec");
    }
}
