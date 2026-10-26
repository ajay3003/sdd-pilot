using System.Net;
using System.Text;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Engine;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One canonical Artifact Pack inventory (discover once → classify once → keep provenance → consume differently): primary, shared, supporting
/// and generated documents with inclusion reasons; siblings and history excluded; the Plan as a document set; Document Quality Review reading
/// the full relevant set per Review Pack contract with exact provenance; advisory documentation gates without a release policy.
/// </summary>
public sealed class ArtifactScopeEvidenceTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    public ArtifactScopeEvidenceTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private const string ConstitutionText = """
        # Person Constitution

        ## Core Principles

        ### PP-01: Privacy by default
        Personal data MUST be minimised.

        ### PP-02: Validate every input
        Every external input MUST be validated server-side.
        """;

    private const string SpecText = """
        # Feature Specification: Person

        **Privacy**: see [privacy notes](../../docs/privacy.md).

        ## User Scenarios & Testing

        ### User Story 1 - Find a person (Priority: P1)

        **Acceptance Scenarios**:

        1. **Given** a registered person, **When** a caseworker searches by name, **Then** the person is listed.

        ## Requirements

        ### Functional Requirements

        - **FR-001**: System MUST search persons by name (PP-01).
        - **FR-002**: System MUST hide classified persons.
        - **FR-003**: System MUST publish PersonCreated events.
        """;

    internal const string PlanText = """
        # Implementation Plan: Person

        **Spec**: [spec.md](spec.md)

        ## Summary

        Person search and events.

        ## Technical Context

        **Testing**: xUnit

        ## Project Structure

        ```text
        specs/001-person/
        ├── plan.md
        ├── research.md
        ├── quickstart.md
        └── contracts/
            └── events.md
        ```

        ## Phase 1: Search

        Implements FR-001 (PP-01).
        """;

    private const string TasksText = "# Tasks: Person\n\n## Phase 1: Search\n\n- [x] T001 [US1] Implement name search (FR-001)\n";

    private const string DataModelText = "# Data Model: Person\n\n## Entities\n\n### Person\n\n| Field | Type | Nullable |\n|---|---|---|\n| Id | Guid | No |\n| Name | string | No |\n";

    private const string ResearchText = """
        # Research: Person

        ### Decision: Testing Stack
        Every FR is verified by an integration test with xUnit and Testcontainers. FR-002 is covered by a classified-person test.

        ### Decision: Accessibility
        Keyboard navigation and tab order are documented for the admin screens.
        """;

    private const string QuickstartText = "# Quickstart\n\nRun the service locally and open the search page.\n";
    private const string SecurityText = "# Security notes\n\nPP-02 applies: requests are validated before use.\n";
    private const string EventsText = "# Events\n\nPersonCreated is published for FR-003.\n";
    private const string DecisionText = "# Decision 001: Drop uniqueness on external id\n\n## Context\n\nDuplicates exist upstream.\n\n## Decision\n\nDrop the unique index.\n";
    private const string PrivacyText = "# Privacy\n\nRetention is 5 years; personal data is minimised.\n";
    private const string UnrelatedText = "# Team notes\n\nKeyboard navigation is great.\n";
    private const string SiblingSpec = "# Feature Specification: Billing\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST bill. Keyboard navigation and tab order are supported.\n";
    private const string SiblingPlan = "# Implementation Plan: Billing\n\n## Summary\n\nBilling.\n\n## Phase 1: Bill\n\nImplements FR-001.\n";
    // The autodoc format: "### X (table: X)" with Column | Type | Constraints; a column without NOT NULL/nullable leaves nullability unstated.
    private const string GeneratedDataModel = "# Data Model\n\n## Entities\n\n### Person (table: Person)\n\n| Column | Type | Constraints |\n|---|---|---|\n| PersonId | Guid | PK |\n| Navn | string | MaxLength(500) |\n| Kilde | string | MaxLength(50) |\n";

    private void ImportFixture(params (string Path, string Content)[] extra) => ImportFixture(_repository, extra);

    /// <summary>The Person-like fixture as a Project Import: primary, supporting, shared, sibling-pack and generated documents.</summary>
    internal static void ImportFixture(WorkspaceArtifactRepository repository, params (string Path, string Content)[] extra)
    {
        (string, string)[] documents =
        [
            (".specify/memory/constitution.md", ConstitutionText),
            ("specs/001-person/spec.md", SpecText), ("specs/001-person/plan.md", PlanText), ("specs/001-person/tasks.md", TasksText),
            ("specs/001-person/data-model.md", DataModelText), ("specs/001-person/research.md", ResearchText),
            ("specs/001-person/quickstart.md", QuickstartText), ("specs/001-person/security.md", SecurityText),
            ("specs/001-person/contracts/events.md", EventsText), ("specs/001-person/decisions/001-drop-uniqueness.md", DecisionText),
            ("docs/privacy.md", PrivacyText), ("docs/unrelated-notes.md", UnrelatedText),
            ("specs/002-billing/spec.md", SiblingSpec), ("specs/002-billing/plan.md", SiblingPlan),
            ("autodoc/data-model.md", GeneratedDataModel),
            .. extra,
        ];
        var preview = Preview(new string('a', 64), source: false, documents);
        new ProjectImportActivation(repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
    }

    private async Task<ArtifactPackInventory> PersonInventory()
    {
        ImportFixture();
        await _explorers.SelectPackAsync("001-person");
        return await _explorers.GetPackInventoryAsync();
    }

    // ── Canonical inventory ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_classifies_primary_supporting_shared_and_generated_documents_with_reasons()
    {
        var inventory = await PersonInventory();

        inventory.Pack.Should().Be("001-person");
        inventory.PrimaryOf(WorkspaceArtifactType.Specification)!.Path.Should().Be("specs/001-person/spec.md");
        inventory.PrimaryOf(WorkspaceArtifactType.Plan)!.Class.Should().Be(PackDocumentClass.PrimaryPackLocal);
        inventory.PrimaryOf(WorkspaceArtifactType.Constitution)!.Class.Should().Be(PackDocumentClass.SharedPrimary, "the project Constitution applies to the pack");

        var supporting = inventory.Supporting.ToDictionary(d => d.Path);
        supporting.Keys.Should().Contain(["specs/001-person/research.md", "specs/001-person/quickstart.md", "specs/001-person/security.md",
            "specs/001-person/contracts/events.md", "specs/001-person/decisions/001-drop-uniqueness.md", "docs/privacy.md"]);
        supporting["specs/001-person/contracts/events.md"].Reasons.Should().Contain(["Same Artifact Pack", "Explicitly referenced by Plan"]);
        supporting["specs/001-person/contracts/events.md"].Family.Should().Be(WorkspaceArtifactType.Plan);
        supporting["docs/privacy.md"].Class.Should().Be(PackDocumentClass.SupportingShared);
        supporting["docs/privacy.md"].Reasons.Should().Contain("Explicitly referenced by Specification", "a shared document is included only by explicit reference");
        supporting["specs/001-person/decisions/001-drop-uniqueness.md"].Reasons.Should().Contain(r => r.Contains("by kind (not referenced)"),
            "a kind-based family is a stated fallback, never presented as a reference");
        supporting.Values.Should().OnlyContain(d => d.Fingerprint != null && d.Origin == PackDocumentOrigin.Authored);

        inventory.Documents.Should().NotContain(d => d.Path == "docs/unrelated-notes.md", "an unreferenced repository document is never read");
        inventory.Generated.Select(d => d.Path).Should().Contain("autodoc/data-model.md");
        inventory.Generated.Should().OnlyContain(d => d.Origin == PackDocumentOrigin.Generated && !d.IsAuthored);
        inventory.AuthoredForReview.Should().NotContain(d => d.Path.StartsWith("autodoc/"), "generated documentation is not read by default");
    }

    [Fact]
    public async Task Sibling_packs_are_listed_as_excluded_and_never_merged()
    {
        var inventory = await PersonInventory();

        inventory.Documents.Should().NotContain(d => d.Path.Contains("002-billing"));
        inventory.Excluded.Should().Contain(e => e.Path == "specs/002-billing/spec.md" && e.Reason.StartsWith("Sibling Artifact Pack 002-billing"));
    }

    [Fact]
    public async Task Inventory_is_deterministic_and_reading_it_changes_nothing()
    {
        var first = await PersonInventory();
        var revisions = _repository.SddLifecycle.Revisions.Select(r => (r.RevisionId, r.Authority, r.IsCurrentSelection)).ToList();
        var second = await _explorers.GetPackInventoryAsync();

        second.Documents.Select(d => (d.Path, d.Reason, d.Class)).Should().Equal(first.Documents.Select(d => (d.Path, d.Reason, d.Class)));
        _repository.SddLifecycle.Revisions.Select(r => (r.RevisionId, r.Authority, r.IsCurrentSelection)).Should().Equal(revisions, "viewing never saves, approves or selects");
    }

    [Fact]
    public void Project_import_keeps_supporting_documents_as_revisions_without_selection_or_authority()
    {
        ImportFixture();

        var supporting = _repository.SddLifecycle.Revisions.Where(r => r.Role == nameof(WorkspaceArtifactType.SupportingDocument)).ToList();
        supporting.Select(r => r.SourceReference).Should().Contain(["specs/001-person/quickstart.md", "specs/001-person/security.md", "docs/privacy.md"])
            .And.NotContain("docs/unrelated-notes.md");
        supporting.Should().OnlyContain(r => !r.IsCurrentSelection && r.Authority == "Unknown" && r.Origin == ProjectImportActivation.Origin);
    }

    [Fact]
    public void Historical_versions_and_non_current_documents_are_excluded_from_review_by_default()
    {
        ExplorerArtifact Artifact(string path, ExplorerArtifactCurrentness currentness, int count) => new("workspace:" + path, WorkspaceArtifactType.Plan,
            path, path, path, ExplorerArtifactSource.Workspace, "File", 1, count, "Unknown", currentness, null, Feature: ArtifactModules.FeatureOf(path));
        var inventory = ArtifactPackInventoryBuilder.Build("s", ArtifactModules.ProjectLevel, "001-x",
            [new PackInventoryCandidate(Artifact("specs/001-x/plan.md", ExplorerArtifactCurrentness.Current, 3), () => "# Plan", HistoricalVersions: 2),
             new PackInventoryCandidate(Artifact("specs/001-x/plan-old.md", ExplorerArtifactCurrentness.Superseded, 1), () => "# Old plan")],
            new Dictionary<WorkspaceArtifactType, string> { [WorkspaceArtifactType.Plan] = "workspace:specs/001-x/plan.md" });

        inventory.Documents.Should().ContainSingle().Which.Path.Should().Be("specs/001-x/plan.md");
        inventory.Excluded.Should().Contain(e => e.Path == "specs/001-x/plan-old.md" && e.Reason.StartsWith("Superseded content version"));
        inventory.Excluded.Should().Contain(e => e.Path == "specs/001-x/plan.md" && e.Reason.StartsWith("2 earlier content versions"));
    }

    [Fact]
    public void Several_plan_documents_form_one_family_and_shared_requirement_ids_stay_out_of_pack_links()
    {
        ExplorerArtifact Artifact(string path) => new("workspace:" + path, WorkspaceArtifactType.Plan, path, path, path, ExplorerArtifactSource.Workspace,
            "File", 1, 1, "Unknown", ExplorerArtifactCurrentness.Current, null, Feature: ArtifactModules.FeatureOf(path));
        var inventory = ArtifactPackInventoryBuilder.Build("s", ArtifactModules.ProjectLevel, "001-x",
            [new(Artifact("specs/001-x/plan.md"), () => "# Plan\n\n## Phase 1\n\nFR-001."),
             new(Artifact("specs/001-x/plan-rollout.md"), () => "# Rollout plan\n\n## Phase 9\n\nFR-002 rollout."),
             new(Artifact("docs/plans/2026-health.md"), () => "# Health plan\n\nFR-003 of another feature.")],
            new Dictionary<WorkspaceArtifactType, string> { [WorkspaceArtifactType.Plan] = "workspace:specs/001-x/plan.md" });

        inventory.CountFor(WorkspaceArtifactType.Plan).Should().Be(3, "the primary plan, a pack-local plan and a shared plan are one family");
        inventory.Family(WorkspaceArtifactType.Plan)[0].IsPrimary.Should().BeTrue("the primary document comes first, never the first enumerated file");
        inventory.PackScopedFamily(WorkspaceArtifactType.Plan).Select(d => d.Path).Should().Equal("specs/001-x/plan.md", "specs/001-x/plan-rollout.md");

        var plan = new PlanAnalysisService().Parse("# Plan\n\n## Phase 1\n\nFR-001.");
        PlanDocumentSet.AddPackScopedMentions(plan, inventory);
        plan.RequirementMentions.Keys.Should().Contain(["FR-001", "FR-002"]).And.NotContain("FR-003", "a shared document's FR-003 is not this pack's FR-003");
        plan.RequirementMentions["FR-002"].Should().ContainSingle().Which.Should().StartWith("plan-rollout.md", "each link keeps its document");
    }

    [Fact]
    public void In_a_module_project_level_documents_apply_only_by_explicit_reference()
    {
        ExplorerArtifact Artifact(string path, WorkspaceArtifactType role, string module) => new("workspace:" + path, role, path, path, path,
            ExplorerArtifactSource.Workspace, "File", 1, 1, "Unknown", ExplorerArtifactCurrentness.Current, null, Module: module, Feature: ArtifactModules.FeatureOf(path));
        var inventory = ArtifactPackInventoryBuilder.Build("s", "Person", "001-x",
            [new(Artifact("Person/specs/001-x/spec.md", WorkspaceArtifactType.Specification, "Person"), () => "# Spec\n\nSee docs/architecture-overview.md."),
             new(Artifact("Person/docs/person-domain.md", WorkspaceArtifactType.DataModel, "Person"), () => "# Domain"),
             new(Artifact("docs/analysis/other-service.md", WorkspaceArtifactType.Specification, ""), () => "# Other service"),
             new(Artifact("docs/architecture-overview.md", WorkspaceArtifactType.SupportingDocument, ""), () => "# Architecture overview")],
            new Dictionary<WorkspaceArtifactType, string> { [WorkspaceArtifactType.Specification] = "workspace:Person/specs/001-x/spec.md" });

        inventory.Supporting.Select(d => d.Path).Should().BeEquivalentTo("Person/docs/person-domain.md", "docs/architecture-overview.md");
        inventory.Excluded.Should().Contain(e => e.Path == "docs/analysis/other-service.md" && e.Reason == "Project-level document not referenced by this Artifact Pack");
    }

    [Fact]
    public void Decision_documents_of_the_pack_feed_plan_architecture_whichever_document_references_them()
    {
        ExplorerArtifact Artifact(string path, WorkspaceArtifactType role) => new("workspace:" + path, role, path, path, path, ExplorerArtifactSource.Workspace,
            "File", 1, 1, "Unknown", ExplorerArtifactCurrentness.Current, null, Feature: ArtifactModules.FeatureOf(path));
        var inventory = ArtifactPackInventoryBuilder.Build("s", ArtifactModules.ProjectLevel, "001-x",
            [new(Artifact("specs/001-x/plan.md", WorkspaceArtifactType.Plan), () => "# Plan\n\n## Summary\n\nPlan."),
             new(Artifact("specs/001-x/tasks.md", WorkspaceArtifactType.Tasks), () => "# Tasks\n\n- [ ] T001 Apply decisions/001-drop.md"),
             new(Artifact("specs/001-x/decisions/001-drop.md", WorkspaceArtifactType.SupportingDocument), () => DecisionText)],
            new Dictionary<WorkspaceArtifactType, string> { [WorkspaceArtifactType.Plan] = "workspace:specs/001-x/plan.md", [WorkspaceArtifactType.Tasks] = "workspace:specs/001-x/tasks.md" });
        var parser = new PlanAnalysisService();

        inventory.Supporting.Single().Family.Should().Be(WorkspaceArtifactType.Tasks, "the reference decides the family");
        PlanDocumentSet.From(parser.Parse("# Plan\n\n## Summary\n\nPlan."), inventory, parser).Decisions
            .Should().ContainSingle(d => d.Path == "specs/001-x/decisions/001-drop.md");
    }

    // ── Plan document set ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_architecture_and_testing_use_the_supporting_plan_documents_with_provenance()
    {
        var inventory = await PersonInventory();
        var parser = new PlanAnalysisService();
        var set = PlanDocumentSet.From(parser.Parse(PlanText), inventory, parser, PlanText);

        set.PrimaryDecisionCount.Should().Be(0, "the primary plan states no decision");
        set.Decisions.Should().Contain(d => d.Decision.Title == "Testing Stack" && d.Path == "specs/001-person/research.md");
        set.Decisions.Should().Contain(d => d.Path == "specs/001-person/decisions/001-drop-uniqueness.md" && d.Decision.Id == "001");
        set.TestingStatements.Should().Contain(s => s.Path == "specs/001-person/research.md" && s.Text.StartsWith("Testing Stack:"));
        set.TestingTools.Select(t => t.Tool).Should().Contain("xUnit");
        set.TestingGaps.Should().NotContain(g => g.Contains("states what to test"), "a testing statement exists in a supporting plan document");
    }

    [Fact]
    public void Plan_without_testing_statements_reports_the_gap_and_a_tool_name_is_not_a_strategy()
    {
        var parser = new PlanAnalysisService();
        var plan = parser.Parse(PlanText);
        var set = PlanDocumentSet.Single(plan, text: PlanText);

        set.TestingStatements.Should().BeEmpty();
        set.TestingGaps.Should().Contain(g => g.StartsWith("Testing tools are named, but none of the 1 inspected Plan document"));
        set.Decisions.Should().BeEmpty("absence stays absence when the documents truly state none");
    }

    [Fact]
    public async Task Requirement_links_from_the_pack_supporting_plan_documents_are_explicit_and_keep_their_document()
    {
        var inventory = await PersonInventory();
        var plan = new PlanAnalysisService().Parse(PlanText);
        PlanDocumentSet.AddPackScopedMentions(plan, inventory);
        var context = ReviewContextFactory.Create(new ConstitutionSemanticModel(),
            SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(SpecText), SpecText),
            PlanAnalysisService.BuildSemanticModel(plan), TaskExplorerService.BuildSemanticModel(TaskExplorerService.Parse(TasksText)), new DataModelSemanticModel());

        context.GetLinkedPlans("FR-001").Should().Contain("§ Phase 1: Search");
        context.GetLinkedPlans("FR-002").Should().ContainSingle().Which.Should().StartWith("research.md", "FR-002 is written in research.md");
        context.GetLinkedPlans("FR-003").Should().ContainSingle().Which.Should().StartWith("events.md");
        context.GetLinkedTasks("FR-002").Should().BeEmpty("a plan link is never a task link");
    }

    // ── Document Quality Review ─────────────────────────────────────────────────────────────

    internal static QualityReviewService ReviewService()
    {
        var traceability = new ArtifactTraceabilityService();
        var compliance = new ConstitutionComplianceService();
        var readiness = new QAReadinessService(traceability, compliance);
        var auditor = new QaAuditorService(traceability, compliance);
        var standards = new StandardsComplianceService(new HttpClient(new WwwrootHandler()) { BaseAddress = new Uri("http://localhost/") });
        var service = new QualityReviewService(new ArtifactParserService(new ConstitutionAnalysisService(), new PlanAnalysisService()), auditor, compliance, standards,
            readiness, new DeliveryReadinessService(traceability, compliance, readiness, auditor), new DataModelAnalysisService());
        service.InitializeAsync().GetAwaiter().GetResult();
        return service;
    }

    private static readonly string[] Packs = ["qa-auditor", "constitution-compliance", "qa-readiness", "delivery-readiness", "data-model-quality", "WCAG22", "OWASP"];

    private static QualityReviewRunInput Input(ArtifactPackInventory? inventory, DocumentationGatePolicy? policy = null) => new()
    {
        ConstitutionText = ConstitutionText, SpecText = SpecText, PlanText = PlanText, TaskText = TasksText, DataModelText = DataModelText,
        Inventory = inventory, Policy = policy ?? DocumentationGatePolicy.None,
    };

    [Fact]
    public async Task Review_reads_the_relevant_supporting_documents_and_lists_exactly_what_it_read()
    {
        var report = await ReviewService().RunAsync(Input(await PersonInventory()), Packs);

        report.HasDocumentScope.Should().BeTrue();
        var read = report.DocumentsRead.ToDictionary(d => d.Path);
        read.Keys.Should().Contain(["specs/001-person/spec.md", "specs/001-person/research.md", "specs/001-person/security.md", "docs/privacy.md"]);
        read.Keys.Should().NotContain(k => k.Contains("002-billing") || k == "docs/unrelated-notes.md");
        read["specs/001-person/research.md"].Category.Should().Be("Supporting authored documentation");
        read["specs/001-person/research.md"].ConsumedBy.Should().Contain(["WCAG 2.2 — Documentation", "QA Auditor"]);
        read["specs/001-person/security.md"].ConsumedBy.Should().NotContain("QA Auditor", "QA Auditor reads only the Plan's supporting documents");
        read["docs/privacy.md"].Category.Should().Be("Shared documentation");
        read.Values.Should().OnlyContain(d => !string.IsNullOrEmpty(d.Fingerprint) && d.Reason.Length > 0);
        read["autodoc/data-model.md"].ConsumedBy.Should().Equal(["Data Model Quality"], "generated documentation is read only by the pack that requests it");
        report.PackResults.Single(r => r.PackId == "data-model-quality").ContextDocuments.Should().Equal("autodoc/data-model.md");
        report.DocumentsExcluded.Should().Contain(e => e.Path == "specs/002-billing/spec.md");
    }

    [Fact]
    public async Task Documentation_rules_search_every_relevant_document_and_name_the_match()
    {
        var withScope = await ReviewService().RunAsync(Input(await PersonInventory()), ["WCAG22"]);
        var keyboard = withScope.PackResults.Single().Standards!.Results.Single(r => r.RuleId == "wcag-keyboard");
        keyboard.Status.Should().Be(CheckStatus.Passed, "keyboard navigation is documented in research.md, a valid supporting document");
        keyboard.MatchedDocument.Should().Be("specs/001-person/research.md");

        var screenReader = withScope.PackResults.Single().Standards!.Results.Single(r => r.RuleId == "wcag-screen-reader");
        screenReader.Status.Should().NotBe(CheckStatus.Passed, "no relevant document states it");
        screenReader.SearchedDocuments.Should().Contain(["specs/001-person/spec.md", "specs/001-person/research.md", "docs/privacy.md"])
            .And.NotContain(p => p.Contains("002-billing") || p.Contains("unrelated") || p.StartsWith("autodoc/"));

        var primaryOnly = await ReviewService().RunAsync(Input(null), ["WCAG22"]);
        primaryOnly.PackResults.Single().Standards!.Results.Single(r => r.RuleId == "wcag-keyboard").Status.Should().NotBe(CheckStatus.Passed,
            "without the supporting documents the statement is not found — the input scope changes the result");
        primaryOnly.PackResults.Single().Score.Should().BeLessThan(withScope.PackResults.Single().Score, "scores recalculate from the actual scope");
    }

    [Fact]
    public async Task Constitution_rules_cited_only_in_a_supporting_document_are_documented_not_missing()
    {
        var report = await ReviewService().RunAsync(Input(await PersonInventory()), ["constitution-compliance"]);
        var compliance = report.PackResults.Single().Compliance!;

        var pp02 = compliance.Results.Single(r => r.RuleId == "PP-02");
        pp02.SupportingReferences.Should().Contain("specs/001-person/security.md");
        pp02.Status.Should().NotBe(ComplianceStatus.Missing);
        compliance.Gaps.Should().NotContain(g => g.RuleId == "PP-02", "the rule is documented in the pack's reviewed documents");
    }

    [Fact]
    public async Task Without_a_release_policy_delivery_readiness_is_advisory_and_never_a_release_block()
    {
        var report = await ReviewService().RunAsync(Input(await PersonInventory()), ["delivery-readiness"]);
        var delivery = report.PackResults.Single();

        delivery.DeliveryReadiness!.ReleaseSignificant.Should().BeFalse();
        (delivery.Critical + delivery.High + delivery.Medium + delivery.Low).Should().Be(0, "advisory documentation readiness adds no Critical/High findings");
        delivery.Info.Should().Be(delivery.DeliveryReadiness.Blockers.Count);
        DocumentationGate.Heading(false).Should().Be("Documentation gate");
        DocumentationGate.StateLabel(ReadinessState.Blocked, false).Should().Be("Documentation gate not met").And.NotContain("Release");
    }

    [Fact]
    public async Task Release_gate_wording_appears_only_when_a_policy_names_it()
    {
        var policy = new DocumentationGatePolicy(true, "release-policy.md", 80);
        var report = await ReviewService().RunAsync(Input(await PersonInventory(), policy), ["delivery-readiness"]);
        var delivery = report.PackResults.Single().DeliveryReadiness!;

        delivery.ReleaseSignificant.Should().BeTrue();
        delivery.PolicySource.Should().Be("release-policy.md");
        report.Policy.Description.Should().Contain("release-policy.md").And.Contain("80%");
        DocumentationGate.StateLabel(ReadinessState.Blocked, true).Should().Be("Blocked");
        QualityReviewInputContracts.Describe("delivery-readiness").Should().Contain("advisory unless a project policy");
    }

    // ── Data model: generated documentation ─────────────────────────────────────────────────

    [Fact]
    public void Generated_model_omissions_are_unknown_evidence_not_schema_findings()
    {
        var service = new DataModelAnalysisService();
        var generated = DataModelGeneratedSemantics.Apply(service.Parse(GeneratedDataModel));

        generated.IsGeneratedDocumentation.Should().BeTrue();
        generated.Findings.Should().NotContain(f => f.Description.Contains("nullable/not-null"));
        generated.NotStatedInGeneratedDocumentation.Should().Contain(f => f.Description.Contains("nullable/not-null"));
        generated.EvidenceGaps.Should().Contain(g => g.Category == "Nullability" && g.Description.StartsWith(DataModelGeneratedSemantics.NotStated));
        generated.EvidenceGaps.Should().NotContain(g => g.Category == "Traceability", "generated documentation is not expected to cite requirements");
        generated.EvidenceGaps.Where(g => g.Category == "Relationships").Should().OnlyContain(g => g.Description.Contains("does not mean the implementation has none"));
        new[] { generated.RelationshipEvidence, generated.IndexEvidence, generated.ConstraintEvidence }.Should().NotContain(DataModelEvidenceState.NotApplicable);

        var authored = service.Parse(DataModelText);
        authored.IsGeneratedDocumentation.Should().BeFalse();
        authored.Entities.Single().Columns.Should().OnlyContain(c => c.Nullable == false, "explicit nullability in an authored model is parsed normally");
        authored.Entities.Single().Columns.Should().NotBeEmpty();
        DataModelGeneratedSemantics.NullabilityState(authored.Entities.Single().Columns[^1], generated: false).Should().Be("Explicitly required (not null)");
        DataModelGeneratedSemantics.NullabilityState(generated.Entities.Single().Columns.First(c => !c.IsPrimaryKey), generated: true)
            .Should().Be(DataModelGeneratedSemantics.NotStated);
    }

    // ── Implementation evidence assistance ──────────────────────────────────────────────────

    [Fact]
    public void Suggested_source_bindings_are_candidates_with_reasons_and_never_bindings()
    {
        var snapshot = new BirkNext.Integrations.IqrSourceSnapshot
        {
            Projects =
            [
                new("Person.Api", "Person/src/Person.Api/Person.Api.csproj", false, "Web", BirkNext.Integrations.SourceConfidence.Confirmed),
                new("Person.Tests", "Tests/Person.Tests/Person.Tests.csproj", true, "Test", BirkNext.Integrations.SourceConfidence.Confirmed),
                new("Frontend.Web", "Frontend/src/Frontend.Web.csproj", false, "Web", BirkNext.Integrations.SourceConfidence.Confirmed),
            ],
        };
        var lifecycle = new SddLifecycleState();

        var suggestions = SourceBindingSuggestions.For(SourceBindingSuggestions.ModuleOf("import:x|Person|001-person-module"), snapshot);

        suggestions.Select(s => s.ProjectName).Should().Equal("Person.Api", "Person.Tests");
        suggestions[0].Reason.Should().Contain("inside module folder Person/");
        suggestions[1].Reason.Should().Contain("weaker: name only");
        lifecycle.ImplementationEvidence.Should().BeEmpty("a suggestion is never saved as evidence");
    }

    [Fact]
    public void Artifact_history_defaults_to_the_viewed_pack_and_its_shared_documents()
    {
        var lifecycle = new SddLifecycleState();
        void Add(string role, string path) => lifecycle.Revisions.Add(new SddArtifactRevision
        {
            Role = role, FileName = path, SourceReference = path, Content = path, Fingerprint = path, WorkspaceScope = "shop", Revision = 1,
        });
        Add("Plan", "Person/specs/001-person/plan.md");
        Add("Plan", "Person/specs/002-billing/plan.md");
        Add("Constitution", "Person/.specify/memory/constitution.md");
        Add("Plan", "Frontend/specs/005-admin/plan.md");
        var histories = ArtifactLogicalIdentity.Histories(lifecycle);

        string[] Paths(ArtifactLogicalIdentity.HistoryScope scope) =>
            [.. histories.Where(h => ArtifactLogicalIdentity.InScope(h, "shop", "Person", "001-person", scope)).Select(h => h.Path)];
        Paths(ArtifactLogicalIdentity.HistoryScope.ArtifactPack).Should().BeEquivalentTo("Person/specs/001-person/plan.md", "Person/.specify/memory/constitution.md");
        Paths(ArtifactLogicalIdentity.HistoryScope.Module).Should().HaveCount(3).And.NotContain("Frontend/specs/005-admin/plan.md");
        Paths(ArtifactLogicalIdentity.HistoryScope.All).Should().HaveCount(4);
    }

    /// <summary>Serves the real <c>wwwroot/standards</c> rule packs from disk: no network.</summary>
    private sealed class WwwrootHandler : HttpMessageHandler
    {
        private static readonly string Root = FindWwwroot();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Path.Combine(Root, request.RequestUri!.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(path), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string FindWwwroot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Web", "wwwroot", "standards"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "BirkNext.Web", "wwwroot");
        }
    }
}
