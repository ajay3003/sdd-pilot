using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Moq;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Pack-aware evidence (PACK-EVID/RESOLVE/REQID/DQR/READY/STATE/SHARED-01): one resolver (PackLocal → ModuleShared → ProjectShared → missing,
/// never a sibling pack), requirement identity = workspace + module + pack + local id, lifecycle evidence isolated per pack, review provenance
/// kept as read, readiness per pack. Without several packs nothing changes (scope null). Generic fixtures only.
/// </summary>
public sealed class PackEvidenceTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    private const string SpecB = "# Feature Specification: Refunds\n\n## User Scenarios & Testing\n\n### User Story 1 - Refund (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST refund payments.\n";

    public PackEvidenceTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private void Import(params (string Path, string Content)[] documents)
    {
        var preview = Preview(new string('e', 64), source: false, documents);
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
    }

    private Task<ArtifactExplorerState> State(WorkspaceArtifactType role) => _explorers.GetStateAsync(role);

    // ── Resolver ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pack_B_without_a_plan_is_missing_its_plan_and_never_receives_pack_A_s_plan()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan), ("specs/002-refund/spec.md", SpecB));

        await _explorers.SelectPackAsync("001-cart");
        (await State(WorkspaceArtifactType.Plan)).Resolution.Should().Be("Exact pack-local match");

        await _explorers.SelectPackAsync("002-refund");
        var plan = await State(WorkspaceArtifactType.Plan);
        plan.Status.Should().Be(ExplorerArtifactStatus.Empty);
        plan.Selected.Should().BeNull();
        plan.Resolution.Should().Be("No PackLocal, ModuleShared or ProjectShared Plan applies.");
    }

    [Fact]
    public async Task A_module_shared_constitution_applies_to_every_pack_of_its_module_with_its_reason()
    {
        Import(("shop/.specify/memory/constitution.md", Constitution), ("shop/specs/001-cart/spec.md", Spec), ("shop/specs/002-refund/spec.md", SpecB));

        foreach (var pack in new[] { "001-cart", "002-refund" })
        {
            await _explorers.SelectPackAsync(pack);
            var constitution = await State(WorkspaceArtifactType.Constitution);
            constitution.Reason.Should().Be(ExplorerSelectionReason.SharedArtifact);
            constitution.Resolution.Should().Be("Module-shared artifact");
            ArtifactPacks.ScopeOf(constitution.Selected!).Should().Be(ArtifactScope.Module);
        }
    }

    [Fact]
    public async Task A_project_shared_constitution_applies_to_a_module_pack_when_the_module_has_none()
    {
        Import(("docs/constitution.md", Constitution),
            ("shop/specs/001-cart/spec.md", Spec), ("shop/specs/002-refund/spec.md", SpecB), ("billing/specs/001-pay/spec.md", SpecB.Replace("Refunds", "Pay")));

        await _explorers.SelectModuleAsync("shop");
        await _explorers.SelectPackAsync("002-refund");
        var constitution = await State(WorkspaceArtifactType.Constitution);
        constitution.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        constitution.Resolution.Should().Be("Project-shared artifact");
        constitution.Selected!.SourcePath.Should().Be("docs/constitution.md");
    }

    [Fact]
    public async Task Two_module_shared_plans_need_review_rather_than_a_silent_pick()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/002-refund/spec.md", SpecB), ("docs/plan-a.md", Plan), ("docs/plan-b.md", Plan.Replace("Cart", "Other")));

        await _explorers.SelectPackAsync("002-refund");
        var plan = await State(WorkspaceArtifactType.Plan);
        plan.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        plan.AmbiguousInPack.Should().BeTrue();
        plan.Selected.Should().BeNull();
    }

    // ── Active requirement scope ─────────────────────────────────────────────

    [Fact]
    public async Task The_active_requirement_scope_follows_the_viewed_pack_and_is_null_without_packs()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/002-refund/spec.md", SpecB));
        var scope = _explorers.CurrentScope;
        await State(WorkspaceArtifactType.Specification);
        _repository.SddLifecycle.ActiveRequirementScope.Should().BeNull("no pack is viewed yet");

        await _explorers.SelectPackAsync("002-refund");
        await State(WorkspaceArtifactType.Specification);
        _repository.SddLifecycle.ActiveRequirementScope.Should().Be($"{scope}||002-refund");
        RequirementKey.Of(_repository.SddLifecycle.ActiveRequirementScope, "FR-001").Should().Match<RequirementKey>(k =>
            k.Workspace == scope && k.Module == "" && k.Pack == "002-refund" && k.Display == "Pack 002-refund · FR-001");
    }

    [Fact]
    public async Task A_single_pack_is_still_scoped_so_its_requirements_never_collide_with_another_module_s()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan));
        await State(WorkspaceArtifactType.Specification);
        _repository.SddLifecycle.ActiveRequirementScope.Should().EndWith("|001-cart", "FR-001 of the only pack is still that pack's FR-001");
        RequirementKey.Of(null, "FR-001").Display.Should().Be("FR-001", "legacy unscoped identity remains readable");
    }

    [Fact]
    public async Task A_selection_saved_before_packs_restores_its_pack_as_last_viewed_and_scope()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/002-refund/spec.md", SpecB));
        var scope = _explorers.CurrentScope;
        _repository.SddLifecycle.ExplorerSelections.Add(new SddExplorerSelection { WorkspaceScope = scope, Role = "Specification", ArtifactId = "workspace:specs/002-refund/spec.md", Module = "" });

        await State(WorkspaceArtifactType.Specification);

        _repository.SddLifecycle.PackFocus.Should().ContainSingle(f => f.Pack == "002-refund", "the old selection becomes the pack last viewed");
        _repository.SddLifecycle.ActiveRequirementScope.Should().EndWith("|002-refund");
    }

    // ── Requirement identity and lifecycle evidence ──────────────────────────

    private static SemanticRequirement Requirement(string id, string text) => new() { Id = id, Text = text };

    [Fact]
    public void FR_001_in_two_packs_are_two_requirements_with_their_own_snapshots_and_evidence()
    {
        var state = new SddLifecycleState { ActiveRequirementScope = "w||001-cart" };
        SddLifecycleReviewService.ReconcileRequirements(state, [Requirement("FR-001", "store carts"), Requirement("FR-002", "list carts")]);
        state.ImplementationEvidence.Add(new SddImplementationEvidence { RequirementId = "FR-001", RequirementScope = "w||001-cart", Reference = "Cart.cs" });
        state.TestEvidence.Add(new SddTestEvidence { RequirementId = "FR-001", RequirementScope = "w||001-cart", TestReference = "CartTests" });

        state.ActiveRequirementScope = "w||002-refund";
        SddLifecycleReviewService.ReconcileRequirements(state, [Requirement("FR-001", "refund payments")]);

        state.RequirementChanges.Should().NotContain(c => c.Change == "Modified" || c.Change == "Removed",
            "pack 002's FR-001 is not a revision of pack 001's FR-001, and pack 001's FR-002 was not removed");
        state.RequirementSnapshots.Where(s => s.IsCurrent).Select(s => (s.RequirementScope, s.RequirementId)).Should()
            .BeEquivalentTo([("w||001-cart", "FR-001"), ("w||001-cart", "FR-002"), ("w||002-refund", "FR-001")]);
        state.ImplementationEvidence.Single().Currentness.Should().Be("Current", "pack 002 never stales pack 001's evidence");
        state.TestEvidence.Single().Currentness.Should().Be("Current");

        state.ActiveRequirementScope = "w||001-cart";
        SddLifecycleReviewService.ReconcileRequirements(state, [Requirement("FR-001", "store carts and totals"), Requirement("FR-002", "list carts")]);
        state.RequirementChanges.Should().ContainSingle(c => c.Change == "Modified" && c.RequirementScope == "w||001-cart" && c.RequirementId == "FR-001");
        state.ImplementationEvidence.Single().Currentness.Should().Be("PotentiallyStale", "a change within the same pack still stales its evidence");
    }

    [Fact]
    public void Without_packs_reconciliation_behaves_exactly_as_before()
    {
        var state = new SddLifecycleState();
        SddLifecycleReviewService.ReconcileRequirements(state, [Requirement("FR-001", "a")]);
        SddLifecycleReviewService.ReconcileRequirements(state, [Requirement("FR-001", "b")]);
        state.RequirementChanges.Select(c => c.Change).Should().Equal("Added", "Modified");
        state.RequirementSnapshots.Should().OnlyContain(s => s.RequirementScope == null);
    }

    [Fact]
    public void Clarifications_and_links_are_isolated_by_pack()
    {
        var state = new SddLifecycleState { ActiveRequirementScope = "w||001-cart" };
        SddLifecycleReviewService.ResolveQuestion(state, "Q-1", "Which currency?", "NOK", "DEC-1", ["FR-001"]);
        SddLifecycleReviewService.AddOrRefreshLink(state, "FR-001", "T001", "RequirementDecomposedInto", "StronglySupported");

        state.ActiveRequirementScope = "w||002-refund";
        SddLifecycleReviewService.ResolveQuestion(state, "Q-1", "Refund window?", "30 days", "DEC-2", ["FR-001"]);
        SddLifecycleReviewService.AddOrRefreshLink(state, "FR-001", "T001", "RequirementDecomposedInto", "StronglySupported");

        state.Questions.Select(q => (q.RequirementScope, q.Resolution)).Should().BeEquivalentTo([("w||001-cart", "NOK"), ("w||002-refund", "30 days")]);
        state.Links.Select(l => l.RequirementScope).Should().BeEquivalentTo(["w||001-cart", "w||002-refund"]);
    }

    private static (WorkspaceArtifactRepository Repository, SddEvidenceGraphService Graph) Graph(params string[] requirementIds)
    {
        var context = new ReviewContext();
        foreach (var id in requirementIds) context.Specification.Requirements.Add(new SemanticRequirement { Id = id, Text = id });
        var provider = new Mock<IReviewContextProvider>();
        provider.Setup(x => x.GetCurrent()).Returns(context);
        return (new WorkspaceArtifactRepository(), new SddEvidenceGraphService(provider.Object));
    }

    [Fact]
    public void The_evidence_graph_applies_only_the_active_pack_s_evidence_and_names_legacy_unscoped_evidence()
    {
        var (repository, graph) = Graph("FR-001");
        var state = repository.SddLifecycle;
        state.ImplementationEvidence.Add(new SddImplementationEvidence { RequirementId = "FR-001", RequirementScope = "w||001-cart", Reference = "Cart.cs" });
        state.ImplementationEvidence.Add(new SddImplementationEvidence { RequirementId = "FR-001", RequirementScope = null, Reference = "Legacy.cs" });
        state.TestExecutions.Add(new SddTestExecutionEvidence { TestId = "RefundTests", RequirementReferences = ["FR-001"], RequirementScope = "w||002-refund", Result = "Passed" });

        state.ActiveRequirementScope = "w||001-cart";
        var cart = graph.Project(repository);
        cart.Rows.Single().Implementation.Select(e => e.Reference).Should().Equal("Cart.cs");
        cart.Rows.Single().Executions.Should().BeEmpty("pack 002's test result never covers pack 001's FR-001");
        cart.Metadata.Limitations.Should().Contain("1 legacy unscoped evidence item is not applied to Pack 001-cart: they were recorded before artifact packs and name no pack.");

        state.ActiveRequirementScope = "w||002-refund";
        var refund = graph.Project(repository);
        refund.Rows.Single().Implementation.Should().BeEmpty();
        refund.Rows.Single().Executions.Select(e => e.TestId).Should().Equal("RefundTests");

        state.ActiveRequirementScope = null;
        graph.Project(repository).Rows.Single().Implementation.Select(e => e.Reference).Should().Equal("Legacy.cs");
    }

    [Fact]
    public void Imported_executions_and_designed_tests_carry_the_active_scope()
    {
        var (repository, graph) = Graph();
        var state = repository.SddLifecycle;
        state.ActiveRequirementScope = "w||002-refund";
        SddEvidenceGraphService.ImportExecutions(state, [new SddTestExecutionImportRecord { TestId = "T1", TestName = "T1", ResultSource = "r.json", Result = "Passed", RequirementReferences = ["FR-001"] }]);
        state.TestExecutions.Single().RequirementScope.Should().Be("w||002-refund");
        state.Links.Should().OnlyContain(l => l.RequirementScope == "w||002-refund");
    }

    // ── Rule re-evaluation per pack (TASK-002, SPEC-002) ─────────────────────

    // Same shapes as QaAuditorServiceTests' FullSpec/FullPlan/FullTasks (no TASK-002/SPEC-002 on their own).
    private const string SpecWithTwo = """
        # Feature Spec

        ## Functional Requirements
        ### FR-001 Authentication
        AC: Given a valid token, user is authenticated.

        ### FR-002 Logging
        AC: Given an action, a log entry is created.

        ## Edge Cases
        - Expired tokens return 401.
        """;

    private const string PlanCoveringBoth = """
        # Plan

        ## Summary
        Implements auth and logging.

        ## Risks
        - Risk: Token expiry edge cases. Severity: Medium.

        ## Implementation Phases
        ### Phase 1: Core Auth
        - Implement FR-001 authentication
        - FR-002: Add logging

        ## Testing
        - Unit tests for auth service
        """;

    private const string TasksWithTests = """
        # Tasks

        ## Phase 1
        ### Implementation
        - [ ] T001 Implement auth [Status: Todo]
        - [ ] T002 Add logging [Status: Todo]
        ### Testing
        - [ ] T003 Write unit tests [Status: Todo]
        """;

    private const string TasksWithoutTests = """
        # Tasks

        ## Phase 1
        ### Implementation
        - [ ] T001 Implement refunds [Status: Todo]
        """;

    private async Task<QaAuditReport> AuditPackAsync(string pack)
    {
        await _explorers.SelectPackAsync(pack);
        string? Text(ArtifactExplorerState s) => s.Status == ExplorerArtifactStatus.Loaded ? s.Content : null;
        var spec = Text(await State(WorkspaceArtifactType.Specification));
        var plan = Text(await State(WorkspaceArtifactType.Plan));
        var tasks = Text(await State(WorkspaceArtifactType.Tasks));
        var auditor = new QaAuditorService(new ArtifactTraceabilityService(), new ConstitutionComplianceService());
        return auditor.Audit(null, spec is null ? null : SpecExplorerService.Parse(spec), plan is null ? null : new PlanAnalysisService().Parse(plan),
            tasks is null ? null : TaskExplorerService.Parse(tasks));
    }

    [Fact]
    public async Task Task_and_plan_coverage_rules_are_evaluated_per_pack_and_never_against_a_sibling_pack()
    {
        Import(("specs/001-cart/spec.md", SpecWithTwo), ("specs/001-cart/plan.md", PlanCoveringBoth), ("specs/001-cart/tasks.md", TasksWithTests),
            ("specs/002-refund/spec.md", SpecB), ("specs/002-refund/tasks.md", TasksWithoutTests));

        var cart = await AuditPackAsync("001-cart");
        var refund = await AuditPackAsync("002-refund");

        cart.Findings.Should().NotContain(f => f.RuleCode == "TASK-002", "pack 001 has its own testing task");
        refund.Findings.Should().Contain(f => f.RuleCode == "TASK-002", "pack 002 has no testing task — pack 001's test task never covers it");
        refund.Findings.Should().NotContain(f => f.RuleCode == "SPEC-002",
            "pack 002 has no plan: plan coverage is not assessed, and pack 001's plan is never compared with pack 002's requirements");
    }

    // ── Review provenance, readiness, dashboard ──────────────────────────────

    [Fact]
    public async Task Review_inputs_record_pack_scope_and_exact_paths_and_survive_a_later_selection()
    {
        Import((".specify/memory/constitution.md", Constitution), ("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan), ("specs/002-refund/spec.md", SpecB));
        await _explorers.SelectPackAsync("001-cart");
        var states = new List<ArtifactExplorerState>();
        foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles) states.Add(await State(role));

        var inputs = ArtifactPackReview.InputsOf(states);
        inputs.Select(i => (i.Role, i.Path, i.Pack, i.Scope)).Should().BeEquivalentTo([
            ("Constitution", ".specify/memory/constitution.md", "001-cart", "ProjectShared"),
            ("Specification", "specs/001-cart/spec.md", "001-cart", "PackLocal"),
            ("Plan", "specs/001-cart/plan.md", "001-cart", "PackLocal")]);
        inputs.Should().OnlyContain(i => i.Fingerprint.Length > 0 && i.ArtifactId.StartsWith("workspace:"));

        var session = new QualityReviewSessionService();
        session.SaveResult(new QualityReviewReport(), ["qa-auditor"], "shop", new Dictionary<WorkspaceArtifactKind, string>(), inputs);
        await _explorers.SelectPackAsync("002-refund");
        session.ReviewInputs.Should().Contain(i => i.Path == "specs/001-cart/plan.md", "a later pack selection never rewrites what a result read");
    }

    [Fact]
    public async Task A_saved_result_is_restored_only_for_the_inputs_it_read()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/002-refund/spec.md", SpecB));
        async Task<List<SddReviewInput>> InputsOf(string pack)
        {
            await _explorers.SelectPackAsync(pack);
            var states = new List<ArtifactExplorerState>();
            foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles) states.Add(await State(role));
            return ArtifactPackReview.InputsOf(states);
        }
        var cart = await InputsOf("001-cart");
        var refund = await InputsOf("002-refund");

        QualityReviewSessionService.InputsMatch(cart, cart).Should().BeTrue();
        QualityReviewSessionService.InputsMatch(cart, refund).Should().BeFalse("pack 001's result is never shown as pack 002's");
        QualityReviewSessionService.InputsMatch([], refund).Should().BeFalse("a result without input records is never restored for a known Artifact Pack");
        QualityReviewSessionService.InputsMatch([], []).Should().BeTrue("outside Artifact Packs a legacy result is still shown");
    }

    [Fact]
    public void Readiness_says_a_missing_role_does_not_apply_to_the_pack()
    {
        var pack = new QualityReviewPackDescriptor("delivery-readiness", "Quality", "Delivery Readiness", "", true);
        var readiness = QualityReviewPackReadiness.Evaluate(pack, new ReviewArtifactPresence(true, true, false, true, false, "002-refund"));
        readiness.Limitations.Should().Contain(l => l.StartsWith("No Plan artifact applies to this artifact pack (002-refund):"));
        QualityReviewPackReadiness.Evaluate(pack, new ReviewArtifactPresence(true, true, false, true, false)).Limitations
            .Should().Contain(l => l.StartsWith("No Plan:"), "without packs the wording is unchanged");
    }

    [Fact]
    public async Task Dashboard_counts_packs_and_the_viewed_pack_s_applicability()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan), ("specs/001-cart/tasks.md", Tasks),
            ("specs/002-refund/spec.md", SpecB), ("specs/002-refund/plan.md", Plan.Replace("Cart", "Refund")), ("specs/003-invoice/spec.md", SpecB.Replace("Refunds", "Invoices")));
        await _explorers.SelectPackAsync("003-invoice");
        var roles = new List<ArtifactRoleStatus>();
        foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles)
        {
            var s = await State(role);
            roles.Add(new ArtifactRoleStatus(role, s.Artifacts.Count > 0 ? ArtifactRoleAvailability.Available : ArtifactRoleAvailability.Missing, s.Artifacts.Count,
                s.Selected is null ? ArtifactRoleSelection.None : ArtifactRoleSelection.Selected, s.Selected, null, null, s.Modules, s.Module, s.Packs, s.Pack, s.PackRequired,
                s.Artifacts.Any(a => a.Feature is null), s.Resolution));
        }
        var snapshot = WorkspaceSnapshots.Loaded("Auto_shop", "import:x", "shop") with { Roles = roles };

        snapshot.PackOverview.Should().Be("3 artifact packs · 3 Specifications · 2 Plans · 1 Task · 1 pack missing Plan · 2 packs missing Task");
        snapshot.Role(WorkspaceArtifactType.Specification).PackApplicability.Should().Be("Specification applicable to pack 003-invoice: Ready · PackLocal · specs/003-invoice/spec.md");
        snapshot.Role(WorkspaceArtifactType.Plan).PackApplicability.Should().Be("Plan applicable to pack 003-invoice: Missing — No PackLocal, ModuleShared or ProjectShared Plan applies.");
    }
}
