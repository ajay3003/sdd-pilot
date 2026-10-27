using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Requirements Traceability reports authored reference evidence; it does not treat absence of an explicit reference as proof that a
/// requirement or principle is uncovered. Policy significance is separate from evidence state, and Plan nodes are typed authored nodes.
/// </summary>
public sealed class RequirementsTraceabilityEvidenceTests
{
    private const string Constitution = "# Constitution\n\n## Principles\n\n### PP-01 — Self-access prohibition\n\nUsers MUST NOT grant themselves access.\n\n### PP-02 — Centralised Access Decision\n\nAccess decisions are central.\n";
    private const string Spec = "# Feature Specification: Access\n\n## Requirements\n\n- **FR-001**: The system MUST support child search.\n- **FR-033**: The system MUST reject self-assignment (PP-01).\n";
    private const string Plan = "# Implementation Plan\n\n## Phase D — Access Management (US3)\n\n- Implement grant service for FR-033.\n\n## Project Structure\n\n### Source Code (repository root)\n\n```text\nsrc/GradertBarntilgangService.cs   # FR-033, FR-001\n```\n";
    private const string Tasks = "# Tasks\n\n## Phase 1\n\n- [ ] T001 Create solution\n- [ ] T002 Implement search for FR-001\n";

    private static ArtifactTraceabilityReport Analyze(TraceabilityPolicy? policy = null)
    {
        var constitution = new ConstitutionAnalysisService().Parse(Constitution);
        var spec = SpecExplorerService.Parse(Spec);
        var plan = new PlanAnalysisService().Parse(Plan);
        var tasks = TaskExplorerService.Parse(Tasks);
        var context = ReviewContextFactory.Create(ConstitutionAnalysisService.BuildSemanticModel(constitution), SpecExplorerService.BuildSemanticModel(spec, Spec),
            PlanAnalysisService.BuildSemanticModel(plan), TaskExplorerService.BuildSemanticModel(tasks), new DataModelSemanticModel());
        return new ArtifactTraceabilityService().Analyze(constitution, spec, plan, tasks, context, policy ?? TraceabilityPolicy.None);
    }

    [Fact]
    public void Missing_constitution_link_is_no_explicit_link_and_informational()
    {
        var report = Analyze();
        var pp02 = report.ConstitutionToSpec.Single(r => r.ItemId == "PP-02");
        TraceabilityLabels.Evidence(pp02.Status, ArtifactType.Constitution).Should().Be("No explicit link");
        var gap = report.Gaps.Single(g => g.ItemId == "PP-02");
        gap.Severity.Should().Be(GapSeverity.Informational, "no policy requires the link");
        gap.PolicyRequired.Should().BeFalse();
        gap.Description.Should().StartWith("PP-02 has no explicit reference in the selected Specification").And.NotContain("no coverage");
        report.PolicyRequiredGapCount.Should().Be(0);
        report.Health.Indicators.Should().OnlyContain(i => i.Level == TraceabilityHealthLevel.Info || i.Level == TraceabilityHealthLevel.Good);
    }

    [Fact]
    public void Explicit_constitution_link_keeps_exact_ids()
    {
        var pp01 = Analyze().ConstitutionToSpec.Single(r => r.ItemId == "PP-01");
        TraceabilityLabels.Evidence(pp01.Status, ArtifactType.Constitution).Should().Be("Explicitly linked");
        pp01.Links.Select(l => l.TargetId).Should().Equal("FR-033");
    }

    [Fact]
    public void Spec_to_plan_is_explicit_or_no_explicit_plan_reference_never_no_strategy()
    {
        var report = Analyze();
        var fr033 = report.SpecToPlan.Single(r => r.ItemId == "FR-033");
        TraceabilityLabels.Evidence(fr033.Status, ArtifactType.Specification).Should().Be("Explicitly referenced");
        fr033.Links.Select(l => PlanTraceabilityNodes.KindOf(l.TargetId)).Should().Contain("Plan phase");
        var fr001 = report.SpecToPlan.Single(r => r.ItemId == "FR-001");
        TraceabilityLabels.Evidence(fr001.Status, ArtifactType.Specification).Should().Be("No explicit plan reference",
            "its only mention is in the Plan's source-code file tree, which is not a Plan traceability node");
        report.Gaps.Single(g => g.ItemId == "FR-001").Description.Should().StartWith("No explicit Plan reference to FR-001 was found").And.NotContain("implementation strategy");
    }

    [Fact]
    public void Source_structure_listings_never_become_plan_nodes()
    {
        var report = Analyze();
        report.SpecToPlan.SelectMany(r => r.Links).Should().NotContain(l => l.TargetTitle!.Contains("Source Code") || l.TargetTitle!.Contains("repository root"));
        report.Matrix.Should().NotContain(r => (r.PlanItemTitle ?? "").Contains("Source Code") || (r.PlanItemId ?? "").Contains("repository root"));
        report.Matrix.Where(r => r.PlanItemId is not null).Should().OnlyContain(r => new[] { "Plan phase", "Plan section", "Architecture decision", "Supporting plan document" }.Contains(r.PlanItemKind));
        PlanTraceabilityNodes.IsStructureListing("§ Source Code (repository root)").Should().BeTrue();
        PlanTraceabilityNodes.IsStructureListing("§ Constitution Check").Should().BeFalse("an authored Plan section, kept with its type");
    }

    [Fact]
    public void Plan_to_task_stays_not_assessed_and_never_contributes_gaps_or_partial_rows()
    {
        var report = Analyze();
        report.PlanToTask.Should().BeEmpty();
        report.PlanCoverage.HasApplicablePopulation.Should().BeFalse();
        report.Gaps.Should().NotContain(g => g.GapIn == ArtifactType.Plan);
        report.Matrix.Should().OnlyContain(r => r.Status != TraceabilityStatus.Partial && r.TaskEdge == "Not assessed");
    }

    [Fact]
    public void Tasks_without_references_are_never_orphaned_in_wording()
    {
        var report = Analyze();
        var t001 = report.Gaps.Single(g => g.ItemId == "T001");
        TraceabilityLabels.Evidence(t001.Status, ArtifactType.Task).Should().Be("No explicit Requirement/SC reference");
        t001.Description.Should().Contain("Task Alignment Review").And.NotContainEquivalentOf("orphan");
    }

    [Fact]
    public void A_policy_turns_missing_links_into_required_gaps_without_changing_evidence()
    {
        var report = Analyze(new TraceabilityPolicy(RequireConstitutionToSpec: true));
        var gap = report.Gaps.Single(g => g.ItemId == "PP-02");
        gap.PolicyRequired.Should().BeTrue();
        TraceabilityLabels.Significance(gap).Should().Be("Required link missing (policy)");
        TraceabilityLabels.Evidence(gap.Status, ArtifactType.Constitution).Should().Be("No explicit link", "evidence state is unchanged by policy");
        report.Gaps.Single(g => g.ItemId == "FR-001").PolicyRequired.Should().BeFalse("only the configured relation is required");
        report.PolicyRequiredGapCount.Should().Be(1);
    }

    [Fact]
    public void Markdown_and_duplicate_ids_are_cleaned_for_display()
    {
        BirkNext.Web.Pages.ArtifactTraceability.CleanTitle("FR-001", "**FR-001**: The system MUST support child search.").Should().Be("The system MUST support child search.");
        BirkNext.Web.Pages.ArtifactTraceability.CleanTitle("PP-02", "II. Centralised Access Decision (PP-02)").Should().Be("Centralised Access Decision");
    }
}
