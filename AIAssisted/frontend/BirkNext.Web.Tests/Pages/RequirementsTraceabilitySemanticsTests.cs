using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Services;
using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Requirements Traceability reads as explicit authored-document reference evidence across the whole page, the export and the Dashboard:
/// absence of an explicit reference is an informational observation (never "missing", a gap or a defect), Plan → Tasks fails safe as
/// Not assessed, there is no combined traceability score, and only broken, ambiguous or policy-required references are issues.
/// </summary>
public sealed class RequirementsTraceabilitySemanticsTests : BunitContext
{
    private const string Constitution = "# Constitution\n\n## Principles\n\n### PP-01 — Self-access prohibition\n\nUsers MUST NOT grant themselves access.\n\n### PP-02 — Centralised Access Decision\n\nAccess decisions are central.\n";
    private const string Spec = "# Feature Specification: Access\n\n## Requirements\n\n- **FR-001**: The system MUST support child search.\n- **FR-033**: The system MUST reject self-assignment (PP-01).\n";
    private const string Plan = "# Implementation Plan\n\n## Phase D — Access Management (US3)\n\n- Implement grant service for FR-033.\n\n## Constitution Check\n\n- FR-001 is reviewed against the principles.\n";
    private const string Tasks = "# Tasks\n\n## Phase 1\n\n- [ ] T001 Create solution\n- [ ] T002 Implement search for FR-001\n- [ ] T003 Implement export for FR-999\n";

    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();

    public RequirementsTraceabilitySemanticsTests()
    {
        _samples.Repository = _workspace;
        Services.AddSingleton<IArtifactExplorerContext>(new ArtifactExplorerContext(_workspace, _samples, _samples));
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IArtifactTraceabilityService, ArtifactTraceabilityService>();
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
    }

    private static ArtifactTraceabilityReport Analyze(TraceabilityPolicy? policy = null, string spec = Spec, string tasksText = Tasks)
    {
        var constitution = new ConstitutionAnalysisService().Parse(Constitution);
        var specTree = SpecExplorerService.Parse(spec);
        var plan = new PlanAnalysisService().Parse(Plan);
        var tasks = TaskExplorerService.Parse(tasksText);
        var context = ReviewContextFactory.Create(ConstitutionAnalysisService.BuildSemanticModel(constitution), SpecExplorerService.BuildSemanticModel(specTree, spec),
            PlanAnalysisService.BuildSemanticModel(plan), TaskExplorerService.BuildSemanticModel(tasks), new DataModelSemanticModel());
        return new ArtifactTraceabilityService().Analyze(constitution, specTree, plan, tasks, context, policy ?? TraceabilityPolicy.None);
    }

    private IRenderedComponent<ArtifactTraceability> RenderReport()
    {
        _samples.SetProjectConstitution("access", Constitution);
        _samples.SetProjectSpecification("access", Spec);
        _samples.SetProjectPlan("access", Plan);
        _samples.SetProjectTasks("access", Tasks);
        _workspace.CurrentProject = "access";
        var cut = Render<ArtifactTraceability>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='at-semantics']"));
        return cut;
    }

    private static void Tab(IRenderedComponent<ArtifactTraceability> cut, string label) =>
        cut.FindAll(".at-tab-btn").First(b => b.TextContent.Contains(label)).Click();

    // ── Overview ──────────────────────────────────────────────────────────

    [Fact]
    public void Overview_uses_neutral_cards_and_reports_transition_assessment_without_a_global_score()
    {
        var cut = RenderReport();
        var markup = cut.Markup;

        markup.Should().NotContainEquivalentOf("Missing explicit references").And.NotContainEquivalentOf("Required traceability gaps")
            .And.NotContainEquivalentOf("Reference gaps").And.NotContain("Missing references are not automatically");
        markup.Should().Contain("No explicit document reference").And.Contain("Informational reference observations across assessed transitions.")
            .And.Contain("Policy-required trace issues").And.Contain("No explicit traceability policy configured.");
        cut.Find("[data-testid='at-semantics']").TextContent.Should().Contain(TraceabilityLabels.Semantics);

        cut.Find("[data-testid='at-assessment']").TextContent.Should().Contain("Assessment coverage: 3 of 4 transitions assessed");
        var transitions = cut.FindAll("[data-testid='at-transition']");
        transitions.Single(t => t.GetAttribute("data-key") == "plan-task").GetAttribute("data-assessed").Should().Be("false");
        transitions.Count(t => t.GetAttribute("data-assessed") == "true").Should().Be(3);
        cut.Find("[data-testid='at-plan-task-overview']").TextContent.Should().Contain("Not assessed").And.NotContain("%");
        cut.FindAll(".at-coverage-bar-fill.is-error, .at-coverage-bar-fill.is-warning").Should().BeEmpty("a low explicit-reference percentage is never red or amber");
    }

    [Fact]
    public void Constitution_tab_shows_implicit_applicability_and_neutral_no_reference_badges()
    {
        var cut = RenderReport();
        Tab(cut, "Constitution → Spec");

        cut.Find("[data-testid='at-implicit-applicability']").TextContent.Should().Be(TraceabilityLabels.ImplicitApplicability);
        cut.Markup.Should().Contain("No explicit Specification reference").And.NotContainEquivalentOf("missing Constitution");
        cut.FindAll(".at-status-badge.at-status-missing").Should().BeEmpty();
        cut.FindAll("[data-testid='at-evidence-filters'] button").Select(b => b.TextContent).Should().Equal("All", "Explicitly linked", "No explicit reference");
    }

    [Fact]
    public void Plan_to_tasks_shows_only_not_assessed_without_filters_percentage_or_gap_count()
    {
        var cut = RenderReport();
        Tab(cut, "Plan → Tasks");

        cut.Find("[data-testid='at-plan-task-not-assessed']").TextContent.Should().Contain("Not assessed").And.Contain(TraceabilityLabels.PlanToTaskNotAssessed);
        cut.FindAll("[data-testid='at-evidence-filters']").Should().BeEmpty("linked / no-reference filters imply assessed data");
        cut.Find(".at-tab-body").TextContent.Should().NotContain("%").And.NotContainEquivalentOf("gap");
    }

    [Fact]
    public void Reference_observations_are_categorised_neutral_and_never_prefixed_with_problem()
    {
        var cut = RenderReport();
        var tab = cut.Find("[data-testid='at-tab-observations']");
        tab.TextContent.Should().Contain("Reference observations");
        tab.GetAttribute("title").Should().Contain("informational or policy-evaluated reference observation").And.Contain("Most are simply items with no explicit document reference");
        tab.Click();

        cut.Markup.Should().NotContain("Problem:");
        cut.FindAll("[data-testid='at-observation-filters'] button").Select(b => b.TextContent.Trim())
            .Should().Equal("All", "No explicit reference", "Broken reference", "Ambiguous reference", "Policy-required only");
        var pp02 = cut.FindAll("[data-testid='at-observation']").Single(o => o.TextContent.Contains("PP-02"));
        pp02.ClassList.Should().Contain("at-obs-info").And.NotContain("at-obs-warning").And.NotContain("at-obs-error");
        pp02.QuerySelector("[data-testid='at-observation-category']")!.TextContent.Should().Be("No explicit Specification reference");
        pp02.QuerySelector("[data-testid='at-observation-state']")!.TextContent.Should().Be("Informational");
        pp02.TextContent.Should().Contain("Rule: Centralised Access Decision").And.Contain("does not establish that the principle is absent or inapplicable");

        var broken = cut.FindAll("[data-testid='at-observation']").Single(o => o.GetAttribute("data-category") == nameof(TraceabilityObservationCategory.BrokenReference));
        broken.ClassList.Should().Contain("at-obs-error");
        broken.TextContent.Should().Contain("Broken reference").And.Contain("FR-999");
    }

    [Fact]
    public void Matrix_uses_authored_plan_location_and_readable_edge_evidence()
    {
        var cut = RenderReport();
        Tab(cut, "Matrix");

        cut.Find("[data-testid='at-matrix-semantics']").TextContent.Should().Contain(TraceabilityLabels.MatrixScope);
        cut.FindAll(".at-matrix-table th").Select(t => t.TextContent).Should().Contain("Authored plan location").And.NotContain("Plan (typed authored node)");
        var edges = cut.FindAll("[data-testid='at-edge']").Select(e => e.TextContent).ToList();
        edges.Should().Contain(e => e.StartsWith("Constitution → Spec: ")).And.Contain(e => e == "Plan → Tasks: Not assessed");
        edges.Should().NotContain(e => e.StartsWith("C→S") || e.StartsWith("S→P") || e.StartsWith("P→T"));
        cut.FindAll(".at-matrix-table .at-matrix-blank").Should().OnlyContain(b => b.GetAttribute("title") == TraceabilityLabels.NoReferenceCell);
        cut.FindAll(".at-edge-notassessed").Should().NotBeEmpty();
        cut.Markup.Should().NotContain("Source Code").And.NotContain("repository root");
    }

    // ── Service semantics ────────────────────────────────────────────────

    [Fact]
    public void Broken_and_ambiguous_references_are_issues_while_no_reference_stays_informational()
    {
        var report = Analyze();
        var broken = report.Gaps.Single(g => TraceabilityLabels.CategoryOf(g) == TraceabilityObservationCategory.BrokenReference);
        broken.ItemId.Should().Be("T003");
        TraceabilityLabels.Tone(broken).Should().Be("error");
        TraceabilityLabels.Significance(broken).Should().Be("Invalid reference target");
        report.TaskCoverage.CoveredItems.Should().Be(2, "explicit reference counts are unchanged");
        report.TaskCoverage.OrphanedItems.Should().Be(1);
        report.ReferenceIssueCount.Should().Be(1);
        report.PolicyRequiredGapCount.Should().Be(0);
        report.Gaps.Where(g => TraceabilityLabels.CategoryOf(g) == TraceabilityObservationCategory.NoExplicitReference)
            .Should().OnlyContain(g => TraceabilityLabels.Tone(g) == "info" && TraceabilityLabels.Significance(g) == "Informational");

        var ambiguous = new TraceabilityGap { GapIn = ArtifactType.Task, Category = TraceabilityObservationCategory.AmbiguousReference };
        TraceabilityLabels.Category(ambiguous).Should().Be("Ambiguous reference");
        TraceabilityLabels.Significance(ambiguous).Should().Be("Review required");
        TraceabilityLabels.Tone(ambiguous).Should().Be("warning");
    }

    [Fact]
    public void Task_story_references_use_the_shared_work_item_interpretation()
    {
        const string spec = "# Feature Specification: Access\n\n## User Scenarios\n\n### User Story 1 - Child search (Priority: P1)\n\nSearch.\n\n" +
            "### User Story 2 - Export (Removed)\n\nGone.\n\n## Requirements\n\n- **FR-001**: The system MUST support child search.\n";
        const string tasks = "# Tasks\n\n## Phase 1: Setup\n\n- [ ] T001 Create solution\n\n## Phase 2: User Story 1\n\n- [ ] T002 [US1] Implement search for FR-001\n" +
            "- [ ] T003 [US2] Implement export\n- [ ] T004 [US6] Implement audit\n";
        var report = Analyze(spec: spec, tasksText: tasks);

        var setup = report.Gaps.Single(g => g.ItemId == "T001" && g.Status == TraceabilityStatus.Orphaned);
        setup.WorkItem!.Relationship.Should().Be(DeliveryTaskRelationship.SharedFoundational);
        setup.Description.Should().Contain("shared & foundational work");
        TraceabilityLabels.Significance(setup).Should().Be("Informational");

        var removed = report.Gaps.Single(g => g.ItemId == "T003" && g.Category == TraceabilityObservationCategory.BrokenReference);
        removed.WorkItem!.RuleId.Should().Be(WorkItemInterpreter.RemovedRule);
        var unresolved = report.Gaps.Single(g => g.ItemId == "T004" && g.Category == TraceabilityObservationCategory.BrokenReference);
        unresolved.WorkItem!.RuleId.Should().Be(WorkItemInterpreter.UnresolvedRule);
        unresolved.Description.Should().Contain("never re-pointed");
        report.Gaps.Should().NotContain(g => g.ItemId == "T002" && g.WorkItem != null && g.WorkItem.NeedsReview);
    }

    [Fact]
    public void An_id_mentioned_anywhere_in_the_specification_is_never_a_broken_reference()
    {
        var report = Analyze(spec: Spec + "\n## Notes\n\nFR-999 is reserved for the export story.\n");
        report.Gaps.Should().NotContain(g => TraceabilityLabels.CategoryOf(g) == TraceabilityObservationCategory.BrokenReference);
    }

    [Fact]
    public void Legacy_observations_without_a_category_keep_their_meaning()
    {
        TraceabilityLabels.CategoryOf(new TraceabilityGap { GapIn = ArtifactType.Specification }).Should().Be(TraceabilityObservationCategory.NoExplicitReference);
        TraceabilityLabels.CategoryOf(new TraceabilityGap { PolicyRequired = true }).Should().Be(TraceabilityObservationCategory.PolicyRequiredTraceNotEstablished);
        TraceabilityLabels.Category(new TraceabilityGap { GapIn = ArtifactType.Specification }).Should().Be("No explicit Plan reference");
    }

    [Fact]
    public void Tasks_without_reference_are_never_orphaned_and_visual_tokens_follow_the_semantics()
    {
        TraceabilityLabels.Evidence(TraceabilityStatus.Orphaned, ArtifactType.Task).Should().Be("No explicit Requirement/SC reference");
        TraceabilityLabels.Evidence(TraceabilityStatus.Covered, ArtifactType.Task).Should().Be("Explicitly referenced");
        TraceabilityLabels.EdgeTone("Explicit").Should().Be("explicit");
        TraceabilityLabels.EdgeTone("No explicit reference").Should().Be("info");
        TraceabilityLabels.EdgeTone(null).Should().Be("info");
        TraceabilityLabels.EdgeTone("Not assessed").Should().Be("notassessed");
    }

    // ── Export ────────────────────────────────────────────────────────────

    [Fact]
    public void Export_html_matches_the_page_semantics()
    {
        var html = new ReportExportService().ExportArtifactTraceability(Analyze(), "Access");

        html.Should().NotContainEquivalentOf("Missing explicit references").And.NotContainEquivalentOf("Reference gaps")
            .And.NotContainEquivalentOf("Required traceability gaps").And.NotContain(">Orphaned<").And.NotContain(">Missing<")
            .And.NotContainEquivalentOf("Uncovered").And.NotContainEquivalentOf("Failed traceability");
        html.Should().Contain("Explicit reference coverage").And.Contain("No explicit document reference").And.Contain("Not assessed")
            .And.Contain("Policy-required trace issues").And.Contain("Reference observations").And.Contain("Authored plan location")
            .And.Contain("Assessment coverage: 3 of 4 transitions assessed");
        html.Should().Contain("badge-trace-info").And.Contain("badge-trace-explicit").And.Contain("badge-trace-notassessed").And.Contain("badge-trace-error");
        html.Should().NotContain("badge-missing\"").And.NotContain("badge badge-orphaned");
    }

    // ── Downstream ────────────────────────────────────────────────────────

    [Fact]
    public void Informational_observations_never_make_the_dashboard_traceability_area_need_attention()
    {
        var workspace = WorkspaceSnapshots.AllRoles();
        var informational = new DashboardReviewFacts { TraceabilityRun = true, TraceabilityTransitionsAssessed = 3, TraceabilityTransitionsTotal = 4, TraceabilityReferenceIssues = 0 };

        var area = DashboardPresentation.Health(workspace, null, informational).Single(a => a.Key == "traceability");
        area.Status.Should().Be(DashboardStatus.Passed);
        area.Result.Should().Be("3 of 4 transitions assessed").And.NotContain("%");
        area.Explanation.Should().Contain("Absence of an explicit reference is informational");

        var broken = informational with { TraceabilityReferenceIssues = 1 };
        DashboardPresentation.Health(workspace, null, broken).Single(a => a.Key == "traceability").Status.Should().Be(DashboardStatus.AttentionNeeded);
    }

    [Fact]
    public void Plan_to_tasks_not_assessed_is_never_zero_percent()
    {
        var report = Analyze();
        var planTask = report.Transitions.Single(t => t.Key == "plan-task");
        planTask.Assessed.Should().BeFalse();
        planTask.Detail.Should().Be("No stable Plan→Task identity available").And.NotContain("0");
        report.AssessedTransitionCount.Should().Be(3);
    }

    // ── Guided steps, observation breakdown, no combined score in the dashboard export ─────────────────────────────────────

    [Fact]
    public void Guided_steps_follow_the_transitions_and_plan_to_tasks_stays_not_assessed_with_the_reason()
    {
        var cut = RenderReport();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=at-steps] [data-testid=gsl-step]").Should().HaveCount(6));
        cut.FindAll("[data-testid=at-steps] [data-testid=gsl-step]").Select(s => s.GetAttribute("data-step")).Should().Equal("scope", "const-spec", "spec-plan", "plan-task", "task-ref", "gaps");
        var plan = cut.Find("[data-testid=at-steps] [data-step=plan-task]");
        plan.GetAttribute("data-state").Should().Be("NotApplicable");
        plan.TextContent.Should().Contain("Not assessed").And.Contain("stable task identifiers").And.NotContain("0%");
        cut.Find("[data-testid=at-steps] [data-step=const-spec]").TextContent.Should().Contain("not proof that work was implemented or tested");
        cut.Find("[data-testid=at-steps] [data-step=gaps]").TextContent.Should().Contain("not a failure").And.Contain("Links are never created to raise coverage");
        cut.Find("[data-testid=at-steps] [data-step=plan-task] [data-testid=gsl-action]").Click();
        cut.Find(".at-tab-btn.is-active").TextContent.Should().Contain("Plan");
    }

    [Fact]
    public void Informational_observations_are_broken_down_by_population_and_never_one_rate()
    {
        var report = Analyze();
        report.NoExplicitReferenceBreakdown.Sum(b => b.Count).Should().Be(report.NoExplicitReferenceCount);
        var cut = RenderReport();
        if (report.NoExplicitReferenceCount > 0)
            cut.Find("[data-testid=at-observation-breakdown]").TextContent.Should().Contain("different denominators, never one rate");
    }

    [Fact]
    public void The_dashboard_export_states_transitions_and_never_a_combined_percentage()
    {
        var report = Analyze();
        var html = new ReportExportService().ExportDashboardSummary("Access", report, null, null, null, null, null, null);
        html.Should().NotContain("% across assessed transitions");
        html.Should().Contain($"{report.AssessedTransitionCount} of {report.Transitions.Count} transitions assessed").And.Contain("no combined score");
        html.Should().Contain("Plan → Tasks: Not assessed");
    }

    // ── URL state: tab, search and status filter survive reloads and deep links ──────────────────────────────────────

    [Fact]
    public void A_deep_link_opens_the_tab_search_and_filter_it_names()
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("artifact-traceability?tab=const-spec&q=GOV&status=missing");
        var cut = RenderReport();
        cut.Find(".at-tab-btn.is-active").TextContent.Should().Contain("Constitution");
        cut.Find("input.at-search").GetAttribute("value").Should().Be("GOV");
        cut.FindAll(".at-filter-chip.is-active").Should().ContainSingle(c => c.TextContent.Contains("No explicit reference"));
    }

    [Fact]
    public void Changing_tab_writes_the_url_and_an_unknown_tab_falls_back_to_overview()
    {
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("artifact-traceability?tab=nonsense");
        var cut = RenderReport();
        cut.Find(".at-tab-btn.is-active").TextContent.Should().Contain("Overview");
        Tab(cut, "Matrix");
        cut.WaitForAssertion(() => navigation.Uri.Should().Contain("tab=matrix"));
        Tab(cut, "Overview");
        cut.WaitForAssertion(() => navigation.Uri.Should().NotContain("tab="));
    }
}
