using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.SampleProjects;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Plan Explorer keeps plan structure, findings (risks, complexity), declared dependencies, the testing plan and provenance
/// apart: each number says what it counts, nothing declared in the plan is shown as verified, and the tabs are the one
/// navigation.
/// </summary>
public sealed class PlanExplorerSemanticsTests : BunitContext
{
    private readonly PlanAnalysisService _service = new();

    public PlanExplorerSemanticsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IPlanAnalysisService>(_service);
        Services.AddSingleton<MarkdownRenderingService>();
    }

    /// <summary>A generic plan with every kind of content and no product-specific names.</summary>
    private const string GenericPlan = """
        # Implementation Plan: Order Service

        **Branch**: `042-orders` | **Date**: 2026-05-01 | **Spec**: [spec.md](spec.md)
        **Input**: Feature specification from `specs/042-orders/spec.md`

        ## Summary

        Accept and track customer orders.

        ## Technical Context

        **Language/Version**: Python 3.12
        **Primary Dependencies**:
        - `fastapi` — HTTP API
        - `sqlalchemy` — persistence
        - `structlog (json, console)` — structured logging
        **Testing**: pytest, hypothesis
        **Performance Goals**: p95 latency under 200 ms
        **Scale/Scope**: millions of orders per day

        ## Risks

        ### Critical Risk: Payment double-charge
        Retries may charge twice.

        ### High Risk: Schema migration downtime
        Large table rewrite.

        ### Low Risk: Log volume
        More logs than expected.

        ## Open Items

        | ID | Item | Blocking? |
        |----|------|-----------|
        | O-01 | Confirm the tax rules | No |
        | ~~O-02~~ | ~~Choose a queue~~ — Resolved | Closed |

        ## Constitution Check

        - [x] **P-01**: Orders are idempotent
        - [ ] **P-02**: Personal data minimised
        """;

    private IRenderedComponent<PlanExplorerPanel> RenderPlan(string markdown) =>
        Render<PlanExplorerPanel>(p => p.Add(c => c.ParsedPlan, _service.Parse(markdown)));

    private static string Tab(IRenderedComponent<PlanExplorerPanel> cut, string key) =>
        cut.Find($"[data-testid=pe-tab][data-tab={key}]").TextContent.Trim();

    // ── Model: what each number counts ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Risks_CountOnlyCurrentItems_WithTheSeverityThePlanStates()
    {
        var plan = _service.Parse(GenericPlan);

        plan.Risks.Select(r => r.Severity).Should().BeEquivalentTo(
            [RiskSeverity.Critical, RiskSeverity.High, RiskSeverity.Low, RiskSeverity.Unrated]);
        plan.Risks.Should().NotContain(r => r.Title.Contains("queue"), "a struck-through, closed open item is not a current risk");
        plan.Risks.Single(r => r.Severity == RiskSeverity.Unrated).Title.Should().Be("O-01: Confirm the tax rules");
        plan.Health.UnratedRisks.Should().Be(1);
        plan.Health.MediumRisks.Should().Be(0, "no severity is invented");
    }

    [Fact]
    public void Dependencies_AreDeclaredEntries_OnePerBullet_WithPurposeSeparated()
    {
        var plan = _service.Parse(GenericPlan);

        plan.Dependencies.Select(d => d.Name).Should().Equal("fastapi", "sqlalchemy", "structlog (json, console)");
        plan.Dependencies.Select(d => d.Description).Should().Equal("HTTP API", "persistence", "structured logging");
        plan.Dependencies.Should().OnlyContain(d => !d.ScopeStated && d.DeclaredIn == "Technical Context",
            "the plan does not say internal or external here");
    }

    [Fact]
    public void Dependencies_InlineCommaList_DoesNotSplitInsideParentheses()
    {
        var plan = _service.Parse("# Plan\n\n## Technical Context\n\n**Primary Dependencies**: Gin 1.9, OpenTelemetry (traces, metrics), lodash\n");

        plan.Dependencies.Select(d => d.Name).Should().Equal("Gin", "OpenTelemetry (traces, metrics)", "lodash");
    }

    [Fact]
    public void DependenciesSection_StatesScope_OnlyUnderAnExternalOrInternalHeading()
    {
        var plan = _service.Parse("# Plan\n\n## Dependencies\n\n### External\n- left-pad: padding\n\n### Internal\n- billing-core: shared domain\n");

        plan.Dependencies.Should().HaveCount(2).And.OnlyContain(d => d.ScopeStated);
        plan.Dependencies.Single(d => d.Name == "left-pad").IsExternal.Should().BeTrue();
        plan.Dependencies.Single(d => d.Name == "billing-core").IsExternal.Should().BeFalse();
    }

    [Fact]
    public void ConstitutionChecklist_IsParsed_AsTheStatusThePlanStates()
    {
        var plan = _service.Parse(GenericPlan);

        plan.ConstitutionCheckItems.Should().HaveCount(2);
        plan.ConstitutionCheckItems.Select(c => c.Status).Should().Equal(ConstitutionCheckStatus.Compliant, ConstitutionCheckStatus.NeedsReview);
    }

    [Fact]
    public void ExplicitComplexityItems_AreNotMarkedDerived_AnEmptyComplexitySectionIs()
    {
        var explicitPlan = _service.Parse("# Plan\n\n## Complexity\n\n| Area | Complexity |\n|---|---|\n| Sync engine | High |\n");
        explicitPlan.ComplexityDerived.Should().BeFalse();
        explicitPlan.ComplexityItems.Should().ContainSingle();

        var derived = _service.Parse(GenericPlan.Replace("## Constitution Check", "## Complexity Tracking\n\n> No violations to justify.\n\n## Constitution Check"));
        derived.ComplexityDerived.Should().BeTrue("the section lists no items, so every item comes from plan analysis");
    }

    [Fact]
    public void SamplePlan_PersonAdapter_CountsWhatThePlanSays()
    {
        var plan = _service.Parse(File.ReadAllText(TestDataHelper.ResolveFixturePath("person-adapter", "plan.md")));

        plan.Risks.Should().OnlyContain(r => r.Severity == RiskSeverity.Unrated);
        plan.Risks.Should().OnlyContain(r => r.Severity == RiskSeverity.Unrated);
        plan.Dependencies.Should().OnlyHaveUniqueItems(d => d.Name);
        plan.ComplexityItems.Should().NotBeEmpty();
        plan.ConstitutionCheckItems.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Overview ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Overview_RiskSummary_ShowsTheDistribution_NotAVerdict()
    {
        var cut = RenderPlan(GenericPlan);

        var risk = cut.Find("[data-testid=pe-risk-summary]");
        risk.QuerySelector(".pe-ov-value")!.TextContent.Should().Be("4");
        cut.Find("[data-testid=pe-risk-distribution]").TextContent.Should().Be("1 critical · 1 high · 1 low · 1 severity not stated");
        cut.Markup.Should().NotContain("Approved").And.NotContain("Passed").And.NotContain("Healthy");
    }

    [Fact]
    public void Overview_NoCriticalOrHighRisk_IsAFact_NotAPass()
    {
        var cut = RenderPlan("# Plan\n\n## Risks\n\n### Low Risk: Log volume\nMore logs.\n");

        cut.Find("[data-testid=pe-risk-distribution]").TextContent.Should().Be("0 critical · 0 high · 1 low");
        cut.Find("[data-testid=pe-risk-summary]").ClassList.Should().NotContain("pe-ov-ok");
        cut.Markup.Should().NotContain("✓ 1 risk");
    }

    [Fact]
    public void Overview_ComplexityHighIsShownAsASubsetOfTheTotal()
    {
        var plan = _service.Parse(File.ReadAllText(TestDataHelper.ResolveFixturePath("person-adapter", "plan.md")));
        var cut = Render<PlanExplorerPanel>(p => p.Add(c => c.ParsedPlan, plan));

        var complexity = cut.Find("[data-testid=pe-complexity-summary]");
        complexity.QuerySelector(".pe-ov-value")!.TextContent.Should().Be(plan.ComplexityItems.Count.ToString());
        complexity.QuerySelector(".pe-ov-label")!.TextContent.Should().Be("Complexity items");
        cut.Find("[data-testid=pe-complexity-high]").TextContent.Should().Contain($"{plan.Health.HighComplexityItems} of {plan.ComplexityItems.Count}");
        complexity.TextContent.Should().Contain("Complexity items");
        cut.Markup.Should().NotContain("high-complexity areas identified");
    }

    [Fact]
    public void Overview_GroupsAnalysis_SeparatelyFromDeclaredDependencies()
    {
        var cut = RenderPlan(GenericPlan);

        cut.Find("[data-testid=pe-analysis]").TextContent.Should().NotContain("Dependencies");
        var deps = cut.Find("[data-testid=pe-dependencies]");
        deps.QuerySelector("h3")!.TextContent.Should().Be("Declared dependencies");
        cut.Find("[data-testid=pe-dependency-count]").TextContent.Should().Be("3");
        cut.FindAll("[data-testid=pe-dependency]").Should().HaveCount(3);
        cut.Find("[data-testid=pe-dependency-help]").TextContent.Should()
            .Contain("3 declared dependency entries in the plan (Technical Context)")
            .And.Contain("multiple names on one line count as one entry")
            .And.Contain("not verified here")
            .And.Contain("does not say which are internal or external");
    }

    [Fact]
    public void Dependencies_ShowAClassificationOnlyWhenThePlanStatesIt()
    {
        var unstated = RenderPlan(GenericPlan);
        unstated.FindAll("[data-testid=pe-dependency-scope]").Should().BeEmpty();
        unstated.Markup.Should().NotContain(">External<");

        var stated = RenderPlan("# Plan\n\n## Dependencies\n\n### External\n- left-pad: padding\n\n### Internal\n- billing-core: shared domain\n");
        stated.FindAll("[data-testid=pe-dependency-scope]").Select(b => b.TextContent).Should().Equal("External to project", "Internal to project");
    }

    [Fact]
    public void Dependencies_PreviewSix_AndShowAllOnRequest()
    {
        var many = "# Plan\n\n## Dependencies\n\n" + string.Join("\n", Enumerable.Range(1, 9).Select(i => $"- lib-{i}: purpose {i}")) + "\n";
        var cut = RenderPlan(many);

        cut.FindAll("[data-testid=pe-dependency]").Should().HaveCount(6);
        cut.Find("[data-testid=pe-dependency-visible-count]").TextContent.Should().Be("Showing 6 of 9 declared dependency entries");
        cut.Find("[data-testid=pe-dependency-more]").TextContent.Trim().Should().Be("Show all 9 declared dependency entries");
        cut.Find("[data-testid=pe-dependency-more]").Click();
        cut.FindAll("[data-testid=pe-dependency]").Should().HaveCount(9);
        cut.Find("[data-testid=pe-dependency-visible-count]").TextContent.Should().Be("Showing 9 of 9 declared dependency entries");
    }

    [Fact]
    public void TestingPlan_IsNamedAsPlanContent_NeverAsAResult()
    {
        var cut = RenderPlan(GenericPlan);

        Tab(cut, "testing").Should().Be("Testing (in plan)");
        cut.Markup.Should().NotContain("(✓)");
        cut.Find("[data-testid=pe-testing-summary]").TextContent.Should().Contain("In plan").And.Contain("pytest").And.Contain("No tests are run or read here");

        cut.Find("[data-testid=pe-tab][data-tab=testing]").Click();
        cut.Find("[data-testid=pe-testing-note]").TextContent.Should().Contain("declared in the Plan artifact");

        var none = RenderPlan("# Plan\n\n## Summary\n\nNothing about tests.\n");
        Tab(none, "testing").Should().Be("Testing");
        none.Find("[data-testid=pe-testing-summary]").TextContent.Should().Contain("Not in plan");
    }

    [Fact]
    public void Provenance_RendersTheSpecificationOnce_WithItsPath()
    {
        var cut = RenderPlan(GenericPlan);

        var provenance = cut.Find("[data-testid=pe-provenance]");
        provenance.QuerySelector(".pe-meta-label")!.TextContent.Should().Be("Source specification");
        provenance.QuerySelector(".pe-meta-value")!.TextContent.Should().Be("spec.md");
        cut.Find("[data-testid=pe-provenance-detail]").TextContent.Should().Be("specs/042-orders/spec.md");
        provenance.TextContent.Should().Contain("No current Specification artifact state is available");
        cut.Find("[data-testid=pe-metadata]").TextContent.Should().NotContain("Input Source");
    }

    [Fact]
    public void Tabs_AreTheOnlyNavigation_WithCountsThatMatchTheirContent()
    {
        var plan = _service.Parse(GenericPlan);
        var cut = RenderPlan(GenericPlan);

        Tab(cut, "risks").Should().Be($"Risks ({plan.Risks.Count}) & Constraints ({plan.Constraints.Count})");
        Tab(cut, "complexity").Should().Be($"Complexity ({plan.ComplexityItems.Count})");
        Tab(cut, "constitution").Should().Be("Constitution (2)");
        cut.Find("[role=tablist]").QuerySelectorAll("[role=tab]").Should().HaveCount(9);
        cut.Find("[data-testid=pe-tab][data-tab=overview]").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll(".pe-nav-card").Should().BeEmpty("the overview no longer repeats the tabs as shortcut cards");
        cut.FindAll(".pe-overview button").Should().OnlyContain(b => b.GetAttribute("data-testid") == "pe-dependency-more");

        cut.Find("[data-testid=pe-tab][data-tab=risks]").Click();
        cut.FindAll(".pe-risk-card").Should().HaveCount(plan.Risks.Count);
        cut.FindAll(".pe-constraint-card").Should().HaveCount(plan.Constraints.Count);
        cut.Markup.Should().Contain("Severity not stated (1)");
        cut.Find("[data-testid=pe-tab][data-tab=risks]").GetAttribute("aria-selected").Should().Be("true");

        cut.Find("[data-testid=pe-tab][data-tab=complexity]").Click();
        cut.FindAll(".pe-complexity-card").Should().HaveCount(plan.ComplexityItems.Count);
    }

    [Fact]
    public void ConstitutionTab_SaysItShowsThePlansOwnStatuses()
    {
        var cut = RenderPlan(GenericPlan);

        cut.Find("[data-testid=pe-constitution-summary]").TextContent.Should().Contain("1 marked met").And.Contain("1 need review").And.Contain("BirkNext assessment: Not performed");
        cut.Find("[data-testid=pe-tab][data-tab=constitution]").Click();
        cut.Find("[data-testid=pe-constitution-note]").TextContent.Should().Contain("plan's own statements").And.Contain("does not independently assess");
        cut.Markup.Should().NotContain("PASS");
    }

    [Fact]
    public void GeneratedConstitutionSummary_ReportsPlanStatusesWithoutAssessingThem()
    {
        var cut = RenderPlan(File.ReadAllText(TestDataHelper.ResolveFixturePath("person-adapter", "plan.md")));

        cut.Find("[data-testid=pe-constitution-summary]").TextContent.Should()
            .Contain("marked met").And.Contain("BirkNext assessment: Not performed");
        cut.Find("[data-testid=pe-constitution-summary-card]").ClassList.Should().NotContain("pe-ov-ok");
    }

    [Fact]
    public void StructuralContent_IsNotRenderedAsAFinding()
    {
        var cut = RenderPlan("# Plan\n\n## Implementation\n\n### Phase 1: Setup\n- Create project\n\n### Phase 2: Build\n- Write code\n");

        cut.Find("[data-testid=pe-structure-summary]").TextContent.Should().Contain("Implementation phases").And.Contain("not delivery progress");
        cut.Find("[data-testid=pe-analysis]").TextContent.Should().NotContain("phase");
    }

    [Fact]
    public void ComplexityTab_DerivedNotice_FollowsTheModel_NotTheSectionHeading()
    {
        var withEmptySection = RenderPlan(GenericPlan.Replace("## Constitution Check", "## Complexity Tracking\n\n> No violations to justify.\n\n## Constitution Check"));
        withEmptySection.Find("[data-testid=pe-tab][data-tab=complexity]").Click();
        withEmptySection.FindAll("[data-testid=pe-complexity-derived]").Should().ContainSingle();

        var explicitItems = RenderPlan("# Plan\n\n## Complexity\n\n| Area | Complexity |\n|---|---|\n| Sync engine | High |\n");
        explicitItems.Find("[data-testid=pe-tab][data-tab=complexity]").Click();
        explicitItems.FindAll("[data-testid=pe-complexity-derived]").Should().BeEmpty();
    }
}

/// <summary>Plan Explorer reads the Plan role from the shared repository: any file name, no Sample Project needed, a new revision re-analysed.</summary>
public sealed class PlanExplorerArtifactSourceTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();

    public PlanExplorerArtifactSourceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_samples);
        Services.AddSingleton<ISampleProjectArtifactDiscovery>(_samples);
        Services.AddSingleton<IArtifactExplorerContext>(new ArtifactExplorerContext(_workspace, _samples, _samples));
    }

    private const string RoadmapV1 = "# Delivery Roadmap\n\n## Technical Context\n\n**Primary Dependencies**: spring-boot, jackson\n\n## Risks\n\n### High Risk: Data loss\nBackups.\n";

    [Fact]
    public void ImportedPlan_WithACustomFileName_WorksWithoutASampleProject()
    {
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Plan, RoadmapV1, "delivery-roadmap.md", null, null, "File", select: true);

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=pe-dependency-count]").TextContent.Should().Be("2");
            cut.Find("[data-testid=pe-risk-distribution]").TextContent.Should().Be("0 critical · 1 high");
        });
    }

    [Fact]
    public void NewPlanRevision_IsAnalysedAgain_NoEarlierCountsRemain()
    {
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Plan, RoadmapV1, "delivery-roadmap.md", null, null, "File", select: true);
        var cut = Render<PlanExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pe-dependency-count]").TextContent.Should().Be("2"));

        _workspace.AddArtifactRevision(WorkspaceArtifactType.Plan, RoadmapV1.Replace("spring-boot, jackson", "spring-boot, jackson, flyway"),
            "delivery-roadmap.md", null, null, "File", select: true);

        cut.WaitForAssertion(() => cut.Find("[data-testid=pe-dependency-count]").TextContent.Should().Be("3"));
    }

    [Fact]
    public void PlanSourceReference_ResolvesToCurrentSpecificationByExactPath()
    {
        const string project = "resolution-demo";
        const string path = "specs/001-demo/custom-requirements.md";
        _samples.SetSelectedProject(project);
        _samples.AddDocument(project, WorkspaceArtifactType.Plan, "specs/001-demo/plan.md",
            $"# Implementation Plan: Demo\n\n**Spec**: custom-requirements.md\n**Input**: {path}\n\n## Testing\n\nxUnit.");
        _samples.AddDocument(project, WorkspaceArtifactType.Specification, path, "# Feature Specification: Demo\n");

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=pe-source-spec-resolution]").TextContent.Should().Contain("resolved to the current Specification artifact").And.Contain("does not establish traceability");
            cut.Find("[data-testid=pe-source-spec-identity]").TextContent.Should().Contain(path).And.Contain("exact source path");
        });
    }
}

public sealed class PlanSpecificationReferenceResolverTests
{
    [Fact]
    public void UniqueFilename_ResolvesCustomSpecificationName()
    {
        var artifact = Artifact("spec-a", "custom-requirements.md", "specs/feature/custom-requirements.md");
        var result = PlanSpecificationReferenceResolver.Resolve(
            new PlanDocument { SpecLink = "custom-requirements.md" },
            State(artifact));

        result.Status.Should().Be(PlanSpecificationReferenceStatus.Resolved);
        result.Match.Should().Be(PlanSpecificationReferenceMatch.UniqueFileName);
        result.Artifact.Should().Be(artifact);
    }

    [Fact]
    public void DuplicateFilename_IsAmbiguousAndDoesNotChooseFirst()
    {
        var first = Artifact("spec-a", "requirements.md", "specs/a/requirements.md");
        var second = Artifact("spec-b", "requirements.md", "specs/b/requirements.md");
        var result = PlanSpecificationReferenceResolver.Resolve(
            new PlanDocument { SpecLink = "requirements.md" },
            new(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Loaded, "demo", "demo", [first, second], first, ExplorerSelectionReason.Explicit, "content"));

        result.Status.Should().Be(PlanSpecificationReferenceStatus.Ambiguous);
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public void ExactPathMatchingAnotherArtifact_DoesNotOverrideCurrentSelection()
    {
        var declared = Artifact("spec-a", "requirements.md", "specs/a/requirements.md");
        var selected = Artifact("spec-b", "requirements.md", "specs/b/requirements.md");
        var result = PlanSpecificationReferenceResolver.Resolve(
            new PlanDocument { InputSource = "specs/a/requirements.md" },
            new(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Loaded, "demo", "demo", [declared, selected], selected, ExplorerSelectionReason.Explicit, "content"));

        result.Status.Should().Be(PlanSpecificationReferenceStatus.NamedOnly);
        result.Detail.Should().Contain("not the current Specification selection");
    }

    [Fact]
    public void MissingArtifact_RemainsMissingAndNamedOnlyHasNoTraceabilitySemantics()
    {
        var missing = PlanSpecificationReferenceResolver.Resolve(
            new PlanDocument { SpecLink = "spec.md" },
            new(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Empty, null, null, []));
        var namedOnly = PlanSpecificationReferenceResolver.Resolve(new PlanDocument { SpecLink = "spec.md" }, null);

        missing.Status.Should().Be(PlanSpecificationReferenceStatus.Missing);
        namedOnly.Status.Should().Be(PlanSpecificationReferenceStatus.NamedOnly);
    }

    private static ArtifactExplorerState State(ExplorerArtifact artifact) =>
        new(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Loaded, "demo", "demo", [artifact], artifact, ExplorerSelectionReason.OnlyArtifact, "spec content");

    private static ExplorerArtifact Artifact(string id, string fileName, string path) =>
        new(id, WorkspaceArtifactType.Specification, $"Feature Specification: {fileName}", fileName, path,
            ExplorerArtifactSource.SampleProject, null, null, 0, "Unknown", ExplorerArtifactCurrentness.Current, null);
}
