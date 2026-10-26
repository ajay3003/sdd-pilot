using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The pack-aware evidence foundation: one canonical requirement → plan → task relationship (explicit IDs only) consumed by the evidence graph,
/// Implementation Evidence Review and DQR; requirement scope on every pack; source snapshot linkage from Project Import; source-test states;
/// constitution rules without IDs never "missing"; artifact history per logical artifact; clarification states that never auto-resolve.
/// </summary>
public sealed class EvidenceFoundationTests
{
    private const string Spec = """
        # Feature Specification: Person

        ## Requirements

        ### Functional Requirements

        - **FR-001**: System MUST search children by name.
        - **FR-002**: System MUST exclude Kode 6/7 children from search.
        - **FR-003**: System MUST record status history.

        ## Clarifications

        - Q: Who may see Kode 6 children? → A: Only callers with an explicit grant.
        - Q: Is history ever deleted?
        """;

    // FR-001 is named in a phase section (no ADR), FR-2 is written without padding; FR-003 is only described in similar words.
    private const string Plan = """
        # Implementation Plan: Person

        ## Summary

        Search and history for children.

        ## Phase 2: Search

        Implements FR-001 (name search) and FR-2 (classified children are excluded).

        ## Phase 3: History

        Status changes are recorded for every child.
        """;

    private const string Tasks = """
        # Tasks: Person

        ## Phase 2: Search

        - [x] T010 [US1] Implement name search (FR-01)
        - [ ] T011 [US1] Write PersonSearchServiceTests for FR-002
        - [ ] T012 Record status history
        """;

    private static ReviewContext Context(string spec = Spec, string plan = Plan, string tasks = Tasks) =>
        ReviewContextFactory.Create(new ConstitutionSemanticModel(),
            SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(spec), spec),
            PlanAnalysisService.BuildSemanticModel(new PlanAnalysisService().Parse(plan)),
            TaskExplorerService.BuildSemanticModel(TaskExplorerService.Parse(tasks)),
            new DataModelSemanticModel());

    // ── Canonical plan/task links ──────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_links_are_explicit_ids_anywhere_in_the_plan_and_never_similar_wording()
    {
        var context = Context();
        context.GetLinkedPlans("FR-001").Should().Contain("§ Phase 2: Search", "an ID written in a phase section is an explicit reference");
        context.GetLinkedPlans("FR-002").Should().Contain("§ Phase 2: Search", "FR-2 and FR-002 are the same id in one spelling");
        context.GetLinkedPlans("FR-003").Should().BeEmpty("'status changes are recorded' is similar wording, not a link");
        context.GetLinkedTasks("FR-001").Should().Contain("T010", "FR-01 in a task is FR-001");
        context.GetLinkedTasks("FR-003").Should().BeEmpty();
    }

    [Fact]
    public void The_evidence_graph_reads_the_same_canonical_links_without_a_recorded_copy()
    {
        var workspace = new WorkspaceArtifactRepository();
        var provider = new Mock<IReviewContextProvider>();
        var context = Context();
        provider.Setup(p => p.GetCurrent()).Returns(context);
        var graph = new SddEvidenceGraphService(provider.Object);

        var rows = graph.Project(workspace).Rows;   // Active workspace; nothing synchronized or recorded
        workspace.SddLifecycle.Links.Should().BeEmpty();
        rows.Single(r => r.Requirement.Id == "FR-001").PlanReferences.Should().NotBeEmpty();
        rows.Count(r => r.PlanReferences.Count > 0).Should().Be(2);
        foreach (var row in rows)
            row.TaskReferences.Should().BeEquivalentTo(context.GetLinkedTasks(row.Requirement.Id), "IER/Traceability and Task Explorer read one relationship");

        var baseline = graph.Project(workspace, LifecycleProjectionMode.AuthoritativeBaseline).Rows;
        baseline.Select(r => r.TaskReferences.Count).Should().Equal(rows.Select(r => r.TaskReferences.Count), "the no-baseline fallback agrees with the active view");
    }

    [Fact]
    public void Duplicate_requirement_ids_of_two_packs_never_share_evidence()
    {
        var workspace = new WorkspaceArtifactRepository();
        workspace.SddLifecycle.ImplementationEvidence.Add(new SddImplementationEvidence { RequirementId = "FR-001", Currentness = "Current", Confidence = "Confirmed", RequirementScope = "import:a|Person|001-person-module" });
        var provider = new Mock<IReviewContextProvider>();
        provider.Setup(p => p.GetCurrent()).Returns(Context());
        var graph = new SddEvidenceGraphService(provider.Object);

        workspace.SddLifecycle.ActiveRequirementScope = "import:a|Person|001-person-module";
        graph.Project(workspace).Rows.Single(r => r.Requirement.Id == "FR-001").Implementation.Should().ContainSingle();
        workspace.SddLifecycle.ActiveRequirementScope = "import:a|Person|002-barn-parent-feilkode";
        graph.Project(workspace).Rows.Single(r => r.Requirement.Id == "FR-001").Implementation.Should().BeEmpty("pack 002's FR-001 is another requirement");
        workspace.SddLifecycle.ActiveRequirementScope = "import:a|Frontend|005-access-admin-panel";
        graph.Project(workspace).Rows.Single(r => r.Requirement.Id == "FR-001").Implementation.Should().BeEmpty("another module's FR-001 is another requirement");
    }

    // ── DQR QA Auditor consumes the canonical relationship ─────────────────────────────────────

    [Fact]
    public void Plan_coverage_finding_lists_only_requirements_without_an_explicit_plan_reference()
    {
        var spec = SpecExplorerService.Parse(Spec);
        var plan = new PlanAnalysisService().Parse(Plan);
        var report = new QaAuditorService(new ArtifactTraceabilityService(), new ConstitutionComplianceService()).Audit(null, spec, plan, null, Context());

        var spec002 = report.Findings.Single(f => f.RuleCode == "SPEC-002");
        spec002.Title.Should().Be("Plan coverage gap — 1 requirement(s) have no explicit Plan link");
        spec002.Description.Should().Contain("FR-003").And.NotContain("FR-001").And.NotContain("FR-002");
        spec002.Severity.Should().NotBe(QaSeverity.High).And.NotBe(QaSeverity.Critical);
    }

    // ── Constitution: placeholder IDs are not "missing" references ─────────────────────────────

    private const string Constitution = """
        # Constitution

        ## Core Principles

        ### I. Code Quality

        Code is reviewed.

        ### PP-001 Security First

        All systems enforce authentication.
        """;

    [Fact]
    public void Rules_without_an_id_have_no_deterministic_mapping_and_ids_match_in_one_spelling()
    {
        var constitutionService = new ConstitutionAnalysisService();
        var constitution = constitutionService.Parse(Constitution);
        constitution.RuleCatalog.Should().Contain(r => ConstitutionRuleIds.HasNoCitableId(r), "'I. Code Quality' has no rule ID");
        ConstitutionRuleIds.Comparer.Equals("PP-01", "PP-001").Should().BeTrue();

        const string spec = "# Spec\n\n## Requirements\n\n- **FR-001**: System MUST authenticate (PP-01).\n";
        var specTree = SpecExplorerService.Parse(spec);
        var compliance = new ConstitutionComplianceService().Analyze(constitution, specTree, null, null);
        compliance.Results.Single(r => r.RuleId == "PP-001").Status.Should().NotBe(ComplianceStatus.Missing, "PP-01 cites PP-001");
        compliance.Results.Where(r => ConstitutionRuleIds.IsPlaceholder(r.RuleId)).Should().OnlyContain(r => r.Status == ComplianceStatus.Unknown);
        compliance.Gaps.Should().NotContain(g => ConstitutionRuleIds.IsPlaceholder(g.RuleId));

        var context = ReviewContextFactory.Create(ConstitutionAnalysisService.BuildSemanticModel(constitution), SpecExplorerService.BuildSemanticModel(specTree, spec),
            new PlanSemanticModel(), new TaskSemanticModel(), new DataModelSemanticModel());
        var report = new QaAuditorService(new ArtifactTraceabilityService(), new ConstitutionComplianceService()).Audit(constitution, specTree, null, null, context);
        report.Findings.Should().NotContain(f => f.RuleCode == "CONST-001" && f.AffectedArtifact != null && ConstitutionRuleIds.IsPlaceholder(f.AffectedArtifact));
        report.Findings.Should().Contain(f => f.RuleCode == "CONST-004" && f.Severity == QaSeverity.Info);
        report.Findings.Where(f => f.RuleCode is "CONST-001" or "TRACE-001").Should().NotContain(f => f.Severity == QaSeverity.High || f.Severity == QaSeverity.Critical,
            "a missing reference is a documentation gap, not a product failure");
    }

    // ── Source Analysis linkage and source tests ───────────────────────────────────────────────

    private static SddLifecycleState ImportedWithSnapshot()
    {
        var lifecycle = new SddLifecycleState { CurrentProjectImportId = "imp" };
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "imp", ProjectName = "M2LB", ArchiveFileName = "M2LB.zip", SourceState = "Created", SourceSnapshotId = Guid.NewGuid() });
        return lifecycle;
    }

    [Fact]
    public void A_project_import_snapshot_is_used_and_an_unmapped_requirement_is_not_NoSourceAvailable()
    {
        var lifecycle = ImportedWithSnapshot();
        var linkage = SddSourceAssessment.Linkage(lifecycle);
        linkage.State.Should().Be(SourceLinkageState.ImportedProject);
        linkage.Available.Should().BeTrue();
        SddSourceAssessment.RequirementImplementation(linkage, [], packMapped: false)
            .Should().Be("Source snapshot available — Artifact Pack not mapped to source; implementation not assessed");
        SddSourceAssessment.RequirementImplementation(linkage, [], packMapped: true)
            .Should().Be("Source snapshot available — no explicit implementation link for this requirement");
        SddSourceAssessment.RequirementImplementation(SddSourceAssessment.Linkage(new SddLifecycleState()), [], false)
            .Should().StartWith("Source snapshot unavailable");
    }

    [Fact]
    public void Several_current_snapshots_are_ambiguous_and_none_is_chosen()
    {
        var lifecycle = ImportedWithSnapshot();
        lifecycle.SourceSnapshots.Add(new SddSourceSnapshotReference { SnapshotId = "a", Currentness = "Current", AnalysisStatus = "Ready" });
        lifecycle.SourceSnapshots.Add(new SddSourceSnapshotReference { SnapshotId = "b", Currentness = "Current", AnalysisStatus = "Ready" });
        var linkage = SddSourceAssessment.Linkage(lifecycle);
        (linkage.State, linkage.SnapshotId).Should().Be((SourceLinkageState.Ambiguous, (string?)null));
    }

    [Fact]
    public void Source_tests_states_keep_designed_source_and_executed_apart()
    {
        SddSourceAssessment.SourceTests(new SddLifecycleState()).Should().StartWith("Source tests not assessed — no Source Analysis snapshot");
        SddSourceAssessment.SourceTests(ImportedWithSnapshot()).Should().StartWith("Source tests not assessed — the Project Import source snapshot is available")
            .And.EndWith("Executed evidence not imported.");
        var discovered = ImportedWithSnapshot();
        discovered.TestDefinitions.Add(new SddTestDefinitionEvidence { Definition = new() { FullyQualifiedName = "A.B" } });
        SddSourceAssessment.SourceTests(discovered).Should().StartWith("Tests discovered; no explicit requirement link (1 source test)").And.NotContain("fail");
    }

    // ── Artifact history per logical artifact ─────────────────────────────────────────────────

    [Fact]
    public void History_is_per_logical_artifact_across_imports_and_baselines_never_demote_siblings()
    {
        var repository = new WorkspaceArtifactRepository();
        var state = repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "r1", ProjectName = "M2LB", ProjectNameBasis = "ArchiveRoot" });
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "r2", ProjectName = "M2LB", ProjectNameBasis = "ArchiveRoot" });
        SddArtifactRevision Add(string scope, string path, string content, string role = "Tasks") =>
            repository.AddArtifactRevision(Enum.Parse<WorkspaceArtifactType>(role), content, path, path, scope, "Import", select: false, projectImportId: ProjectImportScope.ImportIdOf(scope))!;

        var personA = Add("import:r1", "M2LB/Person/specs/001-person-module/tasks.md", "- [ ] T001 a");
        var frontend = Add("import:r1", "M2LB/Frontend/specs/005-access-admin-panel/tasks.md", "- [ ] T001 f");
        var personB = Add("import:r1", "M2LB/Person/specs/002-barn-parent-feilkode/tasks.md", "- [ ] T001 b");
        var personA2 = Add("import:r2", "M2LB/Person/specs/001-person-module/tasks.md", "- [x] T001 a");   // same artifact, changed content
        Add("import:r2", "M2LB/Frontend/specs/005-access-admin-panel/tasks.md", "- [ ] T001 f");          // same artifact, same content
        Add("import:r1", "M2LB/Person/.specify/memory/constitution.md", "# Constitution", "Constitution");

        var histories = ArtifactLogicalIdentity.Histories(state);
        histories.Where(h => h.Role == "Tasks").Should().HaveCount(3, "three different tasks.md files are three artifacts, not revisions of one");
        var a = histories.Single(h => h.Path == "Person/specs/001-person-module/tasks.md");
        a.Versions.Should().HaveCount(2, "a different fingerprint in a later import is a new content version");
        a.Current.Fingerprint.Should().Be(personA2.Fingerprint);
        var f = histories.Single(h => h.Path == "Frontend/specs/005-access-admin-panel/tasks.md");
        f.Versions.Should().ContainSingle("the same fingerprint in a later import is unchanged content").Which.Revisions.Should().HaveCount(2);
        histories.Single(h => h.Role == "Constitution").Scope.Should().Be("Shared (module or project)");
        a.Scope.Should().Be("Artifact Pack 001-person-module");

        repository.SetArtifactAuthority(frontend.RevisionId, "Baseline");
        repository.SetArtifactAuthority(personB.RevisionId, "Baseline");
        repository.SetArtifactAuthority(personA.RevisionId, "Baseline");
        (frontend.Authority, personB.Authority, personA.Authority).Should().Be(("Baseline", "Baseline", "Baseline"), "a baseline never demotes another artifact's baseline");
        repository.SetArtifactAuthority(personA2.RevisionId, "Baseline");
        personA.Authority.Should().Be("Historical", "only an earlier version of the same artifact is demoted");
    }

    // ── Clarifications ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_documented_answer_needs_confirmation_and_is_never_resolved_automatically()
    {
        var answered = new SemanticClarification { Id = "Q1", Question = "Who?", Answer = "Explicit grant." };
        ClarificationStates.Of(answered, null).Should().Be(ClarificationState.DocumentedAnswerFound);
        ClarificationStates.Label(ClarificationState.DocumentedAnswerFound).Should().Be("Documented answer found — needs confirmation");
        ClarificationStates.Help(ClarificationState.DocumentedAnswerFound).Should().Be("A documented answer was found, but no authoritative resolution has been recorded.");
        ClarificationStates.Of(new SemanticClarification { Question = "Deleted?" }, null).Should().Be(ClarificationState.Unanswered);
        ClarificationStates.Of(answered, new SddClarification { Status = "Resolved" }).Should().Be(ClarificationState.Resolved);
    }
}
