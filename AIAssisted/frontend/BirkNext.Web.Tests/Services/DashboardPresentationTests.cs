using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The tester-first Dashboard projection: pack-aware counts from the real explorer context (Module → Artifact Packs → roles), genuine
/// same-pack ambiguity only, shared artifacts satisfying every pack, and status semantics that never present import metadata as a review.
/// </summary>
public class DashboardPresentationTests
{
    private static SampleFileDto File(string path, string kind) => new(path, true, kind, "", "", true, false);

    /// <summary>The current workspace as the production projection builds it for a Sample Project with these files.</summary>
    private static async Task<CurrentWorkspaceSnapshot> SnapshotAsync(params SampleFileDto[] files)
    {
        var handler = new DashboardPageTests.SampleProjectsHttpHandler();
        handler.SetProjects(new SampleProjectDto("proj", "Project", "PROJ", "", "C:\\SampleData\\proj", true, files));
        var workspace = new WorkspaceArtifactRepository { CurrentProject = "proj" };
        var services = new ServiceCollection();
        DashboardPageTests.AddCurrentWorkspace(services, workspace, handler);
        return await services.BuildServiceProvider().GetRequiredService<ICurrentWorkspaceProjection>().GetAsync();
    }

    private static readonly DashboardReviewFacts NoReviews = new();

    private static WorkflowReadiness Readiness(CurrentWorkspaceSnapshot workspace, WorkflowStepViewModel? next = null,
        List<WorkflowStepViewModel>? steps = null, FrontendAnalysisProfile? environment = null) =>
        new(workspace, null, null, null, null, next, new WorkflowReadinessBreakdown(), 0, 0, steps ?? [], false, "")
        {
            Inputs = ProjectInputPresentation.Build(workspace, environment, null),
        };

    // ── Pack awareness ────────────────────────────────────────────────────────

    [Fact]
    public async Task ModuleWithTwoPacks_OneWithoutPlan_ReportsThatPack_NotTheModule()
    {
        var snapshot = await SnapshotAsync(
            File("ModuleA/specs/001-alpha/spec.md", "Specification"), File("ModuleA/specs/001-alpha/plan.md", "Plan"), File("ModuleA/specs/001-alpha/tasks.md", "Tasks"),
            File("ModuleA/specs/002-beta/spec.md", "Specification"), File("ModuleA/specs/002-beta/tasks.md", "Tasks"));

        snapshot.Overview.Should().NotBeNull();
        snapshot.Overview!.PackCount.Should().Be(2);
        var plan = snapshot.Overview.Role(WorkspaceArtifactType.Plan);
        plan.PacksWithRole.Should().Be(1);
        plan.PacksMissing.Select(p => p.Pack).Should().Equal("002-beta");

        DashboardPresentation.Found(snapshot, null).Select(f => f.Text).Should().Contain("2 feature packs");
        var missing = DashboardPresentation.NeedsAttention(snapshot, null, NoReviews).Single(i => i.Key == "missing-Plan");
        missing.Text.Should().Be("1 feature pack is missing a plan");
        missing.Items.Should().ContainSingle().Which.Should().Contain("002-beta");

        var card = DashboardPresentation.Card(WorkspaceArtifactType.Plan, snapshot, null, false);
        card.Summary.Should().Be("1 plan");
        card.Meta.Should().Contain("In 1 of 2 feature packs");
        card.Attention.Should().Equal("1 pack missing a plan");
        card.Status.Should().NotBe(DashboardStatus.AttentionNeeded, "a missing plan in one pack is not an ambiguity and not a review result");
    }

    [Fact]
    public async Task ModuleSharedConstitution_SatisfiesEveryPack()
    {
        var snapshot = await SnapshotAsync(
            File("ModuleA/.specify/memory/constitution.md", "Constitution"),
            File("ModuleA/specs/001-alpha/spec.md", "Specification"), File("ModuleA/specs/001-alpha/plan.md", "Plan"), File("ModuleA/specs/001-alpha/tasks.md", "Tasks"),
            File("ModuleA/specs/002-beta/spec.md", "Specification"), File("ModuleA/specs/002-beta/plan.md", "Plan"), File("ModuleA/specs/002-beta/tasks.md", "Tasks"));

        var constitution = snapshot.Overview!.Role(WorkspaceArtifactType.Constitution);
        constitution.PacksMissing.Should().BeEmpty("one module-shared constitution applies to both packs");
        constitution.PacksWithRole.Should().Be(2);
        DashboardPresentation.NeedsAttention(snapshot, null, NoReviews).Should().NotContain(i => i.Key.StartsWith("missing-"));
        DashboardPresentation.Card(WorkspaceArtifactType.Constitution, snapshot, null, false).Meta.Should().Contain("Shared · applies to all 2 feature packs");
        DashboardPresentation.ReviewReadiness(snapshot, null, NoReviews).Single(r => r.Key == "Constitution").StatusLabel.Should().Be("Ready");
    }

    [Fact]
    public async Task ThreeSpecificationsInThreePacks_AreNotAnAmbiguity()
    {
        var snapshot = await SnapshotAsync(
            File("specs/001-a/spec.md", "Specification"), File("specs/002-b/spec.md", "Specification"), File("specs/003-c/spec.md", "Specification"));

        snapshot.Overview!.Ambiguities.Should().BeEmpty();
        DashboardPresentation.Ambiguities(snapshot).Should().BeEmpty();
        var card = DashboardPresentation.Card(WorkspaceArtifactType.Specification, snapshot, null, false);
        card.Summary.Should().Be("3 specifications");
        card.Meta.Should().Contain("In 3 of 3 feature packs");
        card.Status.Should().Be(DashboardStatus.ReadyToReview);
        (card.StatusLabel + card.Meta + string.Concat(card.Attention)).Should().NotContainAny("candidates", "needs selection", "Needs selection");
        DashboardPresentation.NeedsAttention(snapshot, null, NoReviews).Should().NotContain(i => i.Key.StartsWith("ambiguous-"));
    }

    [Fact]
    public async Task TwoSpecificationsInTheSamePack_NeedAttention_WithAResolveAction()
    {
        var snapshot = await SnapshotAsync(
            File("specs/001-a/specification.md", "Specification"), File("specs/001-a/feature-spec.md", "Specification"),
            File("specs/002-b/spec.md", "Specification"));

        var ambiguity = snapshot.Overview!.Ambiguities.Should().ContainSingle().Subject;
        ambiguity.Pack.Should().Be("001-a");
        ambiguity.Candidates.Should().HaveCount(2);

        var item = DashboardPresentation.NeedsAttention(snapshot, null, NoReviews).Single(i => i.Key == "ambiguous-Specification");
        item.Text.Should().EndWith("has 2 possible specification files");
        item.ActionLabel.Should().Be("Resolve");
        item.Informational.Should().BeFalse();

        var card = DashboardPresentation.Card(WorkspaceArtifactType.Specification, snapshot, null, false);
        card.Status.Should().Be(DashboardStatus.AttentionNeeded);
        card.ActionLabel.Should().Be("Resolve");
    }

    [Fact]
    public async Task SeveralModules_CountEveryModulesPacks_AndModuleChoiceIsNotAmbiguity()
    {
        var snapshot = await SnapshotAsync(
            File("Person/specs/001-person/spec.md", "Specification"), File("Person/specs/002-barn/spec.md", "Specification"),
            File("Adapter/specs/001-adapter/spec.md", "Specification"), File("Adapter/specs/002-wolverine/spec.md", "Specification"),
            File("Adapter/specs/003-entity/spec.md", "Specification"));

        snapshot.ModuleRequired.Should().BeTrue();
        snapshot.Overview!.Modules.Should().Be(2);
        snapshot.Overview.PackCount.Should().Be(5, "every module's packs count, not only a focused module's");
        DashboardPresentation.WorkspaceSummary(snapshot).Should().Be("2 modules · 5 feature packs · 1 document type");
        DashboardPresentation.Ambiguities(snapshot).Should().BeEmpty("a module to choose is navigation, never an ambiguity");
        DashboardPresentation.Card(WorkspaceArtifactType.Specification, snapshot, null, false).Summary.Should().Be("5 specifications across 2 modules");
    }

    // ── Initial import, reviews and risks ─────────────────────────────────────

    [Fact]
    public void InitialImport_IsUsefulBeforeAnyReview_AndClaimsNoResult()
    {
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:abc", "M2LB", [.. CurrentWorkspaceSnapshot.WorkflowRoles]);
        var readiness = Readiness(workspace);

        workspace.IsImportedProject.Should().BeTrue();
        DashboardPresentation.Found(workspace, readiness).Should().NotBeEmpty();
        DashboardPresentation.NeedsAttention(workspace, readiness, NoReviews).Select(i => i.Key).Should().Contain(["dqr-not-run", "target-missing"]);
        var health = DashboardPresentation.Health(workspace, readiness, NoReviews);
        health.Should().NotContain(a => a.Status == DashboardStatus.Passed, "import is never a review result");
        health.Single(a => a.Key == "qa").Status.Should().Be(DashboardStatus.ReadyToReview);
        health.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Explanation) && a.Explanation != "—");
        DashboardPresentation.ReviewReadiness(workspace, readiness, NoReviews).Single(r => r.Key == "dqr").StatusLabel.Should().Be("Not run");
    }

    [Fact]
    public void DocumentReviewResult_ShowsLatestReviewScopeAndFindings_AndDropsNotRun()
    {
        var workspace = WorkspaceSnapshots.AllRoles();
        var reviews = new DashboardReviewFacts
        {
            DocumentReview = AnalysisSessionStatus.Completed, DocumentReviewAt = DateTimeOffset.UtcNow, DocumentReviewFindings = 7, DocumentReviewSevere = 2,
            DocumentReviewScope = "pack Revision System (001-revisjon)", QaAssessed = true, QaScore = 64,
        };

        var attention = DashboardPresentation.NeedsAttention(workspace, Readiness(workspace), reviews);
        attention.Should().NotContain(i => i.Key == "dqr-not-run");
        attention.Single(i => i.Key == "severe-findings").Text.Should().Be("2 critical or high findings in the latest Document Quality Review");
        var qa = DashboardPresentation.Health(workspace, Readiness(workspace), reviews).Single(a => a.Key == "qa");
        qa.Status.Should().Be(DashboardStatus.AttentionNeeded);
        qa.Result.Should().Be("64/100 in the latest review");
        var row = DashboardPresentation.ReviewReadiness(workspace, Readiness(workspace), reviews).Single(r => r.Key == "dqr");
        row.StatusLabel.Should().Be("Latest review");
        row.Text.Should().Contain("pack Revision System (001-revisjon)").And.Contain("7 findings");
    }

    [Fact]
    public void Risks_NotAssessed_IsNeverNoRisksDetected()
    {
        DashboardPresentation.Risks(NoReviews).Should().Be(new DashboardRiskState(false, 0, "Risks have not been assessed yet."));
        DashboardPresentation.Risks(new DashboardReviewFacts { RisksAssessed = true }).Message.Should().Be("No significant risks found in the latest review.");
        DashboardPresentation.Risks(new DashboardReviewFacts { RisksAssessed = true, RiskCount = 3 }).Message.Should().Be("3 risks found in the latest review.");
    }

    [Fact]
    public void NotAssessed_IsDistinctFromNotApplicable()
    {
        var withoutConstitution = WorkspaceSnapshots.Loaded("W", "p", "P", WorkspaceArtifactType.Specification);
        var health = DashboardPresentation.Health(withoutConstitution, Readiness(withoutConstitution), NoReviews);
        health.Single(a => a.Key == "compliance").Status.Should().Be(DashboardStatus.NotApplicable);
        health.Single(a => a.Key == "delivery").Status.Should().Be(DashboardStatus.NotApplicable, "no plan or tasks");

        var all = WorkspaceSnapshots.AllRoles();
        var full = DashboardPresentation.Health(all, Readiness(all), NoReviews);
        full.Single(a => a.Key == "compliance").Status.Should().Be(DashboardStatus.ReadyToReview);
        full.Single(a => a.Key == "delivery").Status.Should().Be(DashboardStatus.NotAssessed);
        full.Single(a => a.Key == "performance").Status.Should().Be(DashboardStatus.NeedsSetup, "no test environment yet");
        full.Single(a => a.Key == "delivery").Prominent.Should().BeFalse("unrun delivery is a compact row, not a large empty card");
        full.Single(a => a.Key == "performance").Prominent.Should().BeFalse();
    }

    // ── Target Environment ────────────────────────────────────────────────────

    [Fact]
    public void SelectedDevelopmentEnvironment_IsShown_WithoutImplyingRuntimeVerification()
    {
        var workspace = WorkspaceSnapshots.AllRoles();
        var readiness = Readiness(workspace, environment: new FrontendAnalysisProfile { Name = "Development", TargetUrl = "https://dev.birk.example.no" });

        DashboardPresentation.TestEnvironment(readiness).Should().Be((DashboardStatus.Available, "Test environment: Development"));
        var rows = DashboardPresentation.ReviewReadiness(workspace, readiness, NoReviews);
        rows.Single(r => r.Key == "target").Text.Should().Contain("configured, not yet tested");
        rows.Single(r => r.Key == "runtime").Should().Match<DashboardReadinessRow>(r => r.Status == DashboardStatus.NotAssessed && r.StatusLabel == "Not assessed");
        DashboardPresentation.NeedsAttention(workspace, readiness, NoReviews).Should().NotContain(i => i.Key.StartsWith("target"));
        DashboardPresentation.Health(workspace, readiness, NoReviews).Single(a => a.Key == "performance").Status.Should().Be(DashboardStatus.NotAssessed);
    }

    [Fact]
    public void NoEnvironment_SaysNotSelected()
    {
        var workspace = WorkspaceSnapshots.AllRoles();
        DashboardPresentation.TestEnvironment(Readiness(workspace)).Should().Be((DashboardStatus.NeedsSetup, "Test environment: Not selected"));
    }

    // ── Recommended next step ─────────────────────────────────────────────────

    [Fact]
    public async Task RecommendedNextStep_FollowsTheProjectState_NotAFixedAction()
    {
        // Several modules, none chosen: choosing where to start comes first (Document Quality Review reads one module).
        var monorepo = await SnapshotAsync(File("Person/specs/001-person/spec.md", "Specification"), File("Adapter/specs/001-adapter/spec.md", "Specification"));
        var chooseModule = new WorkflowStepViewModel { Key = WorkflowReadinessService.ChooseArtifactsKey, Title = "Choose a module to review", Route = "specification-explorer" };
        var moduleStep = DashboardPresentation.NextStep(monorepo, Readiness(monorepo, chooseModule))!;
        moduleStep.Title.Should().Be("Choose a module to review first");
        moduleStep.Reason.Should().Contain("2 modules").And.Contain("2 feature packs");
        moduleStep.ActionLabel.Should().Be("Choose a module");

        // An open document review gate: review the imported documents.
        var workspace = WorkspaceSnapshots.AllRoles();
        var gate = ReviewDecisionSemanticsTests.Gate("ConstitutionExplorer", "Constitution Explorer", "Constitution", WorkflowStepStatus.Available);
        var docStep = DashboardPresentation.NextStep(workspace, Readiness(workspace, gate, [gate]))!;
        docStep.Title.Should().Be("Review imported documents");
        docStep.ActionLabel.Should().Be("Review project rules");
        docStep.Reason.Should().Contain("has not been reviewed yet");

        // Every gate decided, Document Quality Review applicable: start it.
        var dqr = new WorkflowStepViewModel { Key = WorkflowReadinessService.OpenReviewKeyPrefix + "quality-review", Title = "Open Document Quality Review", Route = "quality/document" };
        var dqrStep = DashboardPresentation.NextStep(workspace, Readiness(workspace, dqr))!;
        dqrStep.Title.Should().Be("Review documentation quality");
        dqrStep.ActionLabel.Should().Be("Start Document Quality Review");
        dqrStep.Achieves.Should().Contain("within each feature pack");

        new[] { moduleStep.Title, docStep.Title, dqrStep.Title }.Should().OnlyHaveUniqueItems();
    }
}
