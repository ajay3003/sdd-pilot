using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Step 1 ("Load project artifacts") is complete only when the roles the required review steps need are available — never just
/// because a workspace or project exists. Until then it is the one current step with the one primary action, and steps whose
/// artifacts are missing are locked with the reason, without review or approval controls.
/// </summary>
public sealed class RecommendedWorkflowStepOneTests : BunitContext
{
    // The backend's workflow (WorkflowDefinitions): requirements per step; a step is locked while any is missing.
    private static readonly (string Key, string Title, string[] Requires, bool Optional, bool Approval)[] Definitions =
    [
        ("LoadSampleProject", "Load project artifacts", [], false, false),
        ("ConstitutionExplorer", "Constitution Explorer", ["Constitution"], false, true),
        ("SpecificationExplorer", "Specification Explorer", ["Specification"], false, true),
        ("PlanExplorer", "Plan Explorer", ["Plan"], false, true),
        ("TaskExplorer", "Task Explorer", ["Tasks"], false, true),
        ("DataModelExplorer", "Data Model Explorer", ["DataModel"], true, true),
        ("ArtifactTraceability", "Artifact Traceability", ["Constitution", "Specification", "Plan", "Tasks"], false, true),
        ("ImplementationReview", "Implementation Review", ["Specification", "Tasks"], false, true),
    ];

    private static List<WorkflowStepViewModel> BackendSteps(bool c, bool s, bool p, bool t, bool d)
    {
        var available = new Dictionary<string, bool> { ["Constitution"] = c, ["Specification"] = s, ["Plan"] = p, ["Tasks"] = t, ["DataModel"] = d };
        var current = false;
        return Definitions.Where(x => x.Key != "DataModelExplorer" || d).Select((x, i) =>
        {
            var unlocked = x.Requires.All(r => available[r]);
            var isCurrent = !current && unlocked && x.Approval;
            current |= isCurrent;
            return new WorkflowStepViewModel
            {
                Number = i + 1, Key = x.Key, Title = x.Title, Description = x.Title, Route = x.Key.ToLowerInvariant(), ActionLabel = $"Open {x.Title}",
                Color = "#2563eb", Status = unlocked ? WorkflowStepStatus.Available : WorkflowStepStatus.Locked, CanOpen = unlocked, IsCurrent = isCurrent,
                IsFuture = !unlocked, IsOptional = x.Optional, RequiresApproval = x.Approval, RequiresManualReview = x.Approval,
                ApprovalState = ApprovalState.Pending, ReviewState = ReviewState.NotStarted,
                Prerequisites = unlocked ? PrerequisiteState.Available : PrerequisiteState.Missing,
                DisabledReason = unlocked ? "" : "Load required artifacts first", RequiredArtifacts = [.. x.Requires],
            };
        }).ToList();
    }

    private static async Task<WorkflowReadiness> ReadinessAsync(CurrentWorkspaceSnapshot workspace)
    {
        var projection = WorkspaceSnapshots.Projection(workspace);
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(x => x.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" });
        var applicability = new ProjectApplicabilityState(Mock.Of<ITechnologyCoverageApiService>(), contexts.Object, projection.Object, Mock.Of<IFrontendAnalysisSettingsService>());
        var api = new Mock<IRecommendedWorkflowApiService>();
        api.Setup(a => a.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, bool c, bool s, bool p, bool t, bool d) => BackendSteps(c, s, p, t, d));
        return await new WorkflowReadinessService(projection.Object, applicability, api.Object, NullLogger<WorkflowReadinessService>.Instance).GetReadinessAsync();
    }

    private static CurrentWorkspaceSnapshot PersonModule(params WorkspaceArtifactType[] roles) =>
        WorkspaceSnapshots.Loaded("person-module", "person-module", "Person Module", roles);

    private static readonly WorkspaceArtifactType[] Required =
        [WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks];

    private static WorkflowStepViewModel Step(WorkflowReadiness r, string key) => r.Steps.Single(s => s.Key == key);

    // ── Service: state model ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_NoWorkspace_StepOneIsTheCurrentAction()
    {
        var r = await ReadinessAsync(CurrentWorkspaceSnapshot.None());

        r.ArtifactLoad.Should().Be(ArtifactLoadState.Required);
        r.NextRecommendedAction!.Title.Should().Be("Load project artifacts");
        r.NextRecommendedAction.Description.Should().StartWith("Required project artifacts are missing.");
        r.Steps.Should().ContainSingle().Which.Should().BeSameAs(r.NextRecommendedAction);
        r.NextRecommendedAction.IsCurrent.Should().BeTrue();
        r.NextRecommendedAction.CanOpen.Should().BeTrue();
        r.NextRecommendedAction.ActionLabel.Should().Be("Load artifacts");
        r.AlternativeActions.Select(a => a.ActionLabel).Should().Contain("Import documents instead");
    }

    [Fact]
    public async Task B_WorkspaceWithProjectButNoArtifacts_StepOneIsNotCompleted_AndNothingElseIsCurrent()
    {
        var r = await ReadinessAsync(PersonModule());

        r.WorkspaceLoaded.Should().BeTrue("the workspace exists");
        r.ArtifactLoad.Should().Be(ArtifactLoadState.Required, "a workspace alone does not load artifacts");
        r.RequiredRoles.Should().Equal(Required, "Data Model is optional");
        r.MissingRoles.Should().Equal(Required);
        var load = Step(r, "LoadSampleProject");
        r.NextRecommendedAction.Should().BeSameAs(load, "the recommendation is the actionable step, not Constitution Explorer");
        load.Title.Should().Be("Load project artifacts");
        load.IsCurrent.Should().BeTrue();
        load.Status.Should().NotBe(WorkflowStepStatus.Approved);
        r.Steps.Count(s => s.IsCurrent).Should().Be(1);
        Step(r, "ConstitutionExplorer").Status.Should().Be(WorkflowStepStatus.Locked);
        Step(r, "ConstitutionExplorer").DisabledReason.Should().Be("Requires Constitution artifact");
        Step(r, "ArtifactTraceability").DisabledReason.Should().Be("Requires Constitution, Specification, Plan and Tasks artifacts");
        r.ReleaseReadinessPercent.Should().BeNull();
    }

    [Fact]
    public async Task C_ImportedWorkspaceWithoutSampleProject_StepOneDone_NextStepActive()
    {
        var r = await ReadinessAsync(WorkspaceSnapshots.Loaded("Unsaved workspace", null, null, Required));

        r.ArtifactLoad.Should().Be(ArtifactLoadState.Done);
        r.ProjectName.Should().Be("Not assigned");
        Step(r, "LoadSampleProject").IsCurrent.Should().BeFalse();
        r.NextRecommendedAction!.Key.Should().Be("ConstitutionExplorer");
        r.Steps.Count(s => s.IsCurrent).Should().Be(1);
    }

    [Fact]
    public async Task D_SampleProjectWithAllRoles_StepOneDone_NextStepActive()
    {
        var r = await ReadinessAsync(PersonModule([.. CurrentWorkspaceSnapshot.WorkflowRoles]));

        r.ArtifactLoad.Should().Be(ArtifactLoadState.Done);
        r.MissingRoles.Should().BeEmpty();
        r.NextRecommendedAction!.Key.Should().Be("ConstitutionExplorer");
        r.AlternativeActions.Should().BeEmpty();
    }

    [Fact]
    public async Task E_PartialArtifacts_AddMissingArtifacts_DependentStepsLockedWithReasons()
    {
        var r = await ReadinessAsync(PersonModule(WorkspaceArtifactType.Specification, WorkspaceArtifactType.Tasks));

        r.ArtifactLoad.Should().Be(ArtifactLoadState.Partial);
        r.MissingRoles.Should().Equal(WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Plan);
        r.NextRecommendedAction!.Title.Should().Be("Add missing artifacts");
        r.NextRecommendedAction.Description.Should().Contain("Required project artifacts are missing: Constitution, Plan");
        Step(r, "ArtifactTraceability").DisabledReason.Should().Be("Requires Constitution and Plan artifacts");
        Step(r, "PlanExplorer").DisabledReason.Should().Be("Requires Plan artifact");
        Step(r, "SpecificationExplorer").Status.Should().Be(WorkflowStepStatus.Available, "its artifact exists");
        r.Steps.Single(s => s.IsCurrent).Key.Should().Be("LoadSampleProject");
        r.AlternativeActions.Single().Route.Should().Be("constitution-explorer", "import the first missing role in its explorer");
    }

    [Fact]
    public async Task ApprovalDependencyLock_SaysCompleteThePreviousStep()
    {
        var workspace = PersonModule([.. CurrentWorkspaceSnapshot.WorkflowRoles]);
        var projection = WorkspaceSnapshots.Projection(workspace);
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(x => x.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveTargetError = "none" });
        var api = new Mock<IRecommendedWorkflowApiService>();
        var steps = BackendSteps(true, true, true, true, true);
        var implementation = steps.Single(s => s.Key == "ImplementationReview");
        implementation.Status = WorkflowStepStatus.Locked;
        implementation.DisabledReason = "Complete prerequisite approvals first";
        api.Setup(a => a.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>())).ReturnsAsync(steps);
        var r = await new WorkflowReadinessService(projection.Object,
            new ProjectApplicabilityState(Mock.Of<ITechnologyCoverageApiService>(), contexts.Object, projection.Object, Mock.Of<IFrontendAnalysisSettingsService>()),
            api.Object, NullLogger<WorkflowReadinessService>.Instance).GetReadinessAsync();

        Step(r, "ImplementationReview").DisabledReason.Should().Be("Complete the previous step first");
    }

    // ── Page: hierarchy and controls ──────────────────────────────────────────────────────────────────────────────────────

    private IRenderedComponent<RecommendedWorkflow> RenderPage(WorkflowReadiness readiness)
    {
        var service = new Mock<IWorkflowReadinessService>();
        service.Setup(s => s.GetReadinessAsync()).ReturnsAsync(readiness);
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.StartMonitoringAsync()).Returns(Task.CompletedTask);
        Services.AddSingleton(service.Object);
        Services.AddSingleton(Mock.Of<ICurrentWorkspaceProjection>());
        Services.AddSingleton(Mock.Of<IWorkspacePersistenceApiService>());
        Services.AddSingleton(Mock.Of<IWorkspaceSessionRestoreService>());
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(Mock.Of<IRecommendedWorkflowApiService>());
        Services.AddSingleton(NullLogger<RecommendedWorkflow>.Instance);
        return Render<RecommendedWorkflow>();
    }

    [Fact]
    public async Task Page_ScreenshotState_StepOneIsTheOnlyPrimaryAction_AndLockedStepsHaveNoApprovalControls()
    {
        var cut = RenderPage(await ReadinessAsync(PersonModule()));

        var stepOne = cut.Find("[data-testid=rw-step][data-step=LoadSampleProject]");
        stepOne.GetAttribute("data-state").Should().Be("Action required");
        stepOne.GetAttribute("aria-current").Should().Be("step");
        stepOne.QuerySelector("[data-testid=rw-step-state]")!.TextContent.Should().Be("Action required").And.NotBe("Reviewed");
        var load = stepOne.QuerySelector("[data-testid=rw-load-artifacts]")!;
        load.TagName.Should().Be("A");
        load.GetAttribute("href").Should().Be("sample-projects");
        load.TextContent.Trim().Should().Be("Load artifacts");
        stepOne.QuerySelector("[data-testid=rw-alternative-action]")!.TextContent.Should().Be("Import documents instead");

        cut.FindAll("[data-testid=rw-step][aria-current=step]").Should().ContainSingle();
        cut.FindAll("a.rw-cta:not(.rw-cta-secondary), a.rw-next-action-btn").Should().ContainSingle("only one primary call to action");

        var locked = cut.FindAll("[data-testid=rw-step][data-state=Locked]");
        locked.Should().NotBeEmpty();
        foreach (var step in locked)
        {
            step.QuerySelectorAll("button").Should().BeEmpty("a locked step offers no Mark Reviewed, Approve or Needs Changes");
            step.QuerySelector("[data-testid=rw-step-locked-reason]")!.TextContent.Should().StartWith("Requires ");
        }
        cut.Markup.Should().NotContain("Load required artifacts first");

        var card = cut.Find("[data-testid=rw-next-action]");
        card.QuerySelector(".rw-next-action-title")!.TextContent.Should().Be("Load project artifacts");
        card.TextContent.Should().Contain("Required project artifacts are missing.").And.NotContain("Constitution Explorer");
        card.QuerySelector("[data-testid=rw-next-action-step]")!.TextContent.Should().Be("Step 1 below.");
    }

    [Fact]
    public async Task Page_StepOneDone_SaysDone_NotReviewed_AndHasNoAction()
    {
        var cut = RenderPage(await ReadinessAsync(PersonModule([.. CurrentWorkspaceSnapshot.WorkflowRoles])));

        var stepOne = cut.Find("[data-testid=rw-step][data-step=LoadSampleProject]");
        stepOne.GetAttribute("data-state").Should().Be("Done");
        stepOne.QuerySelectorAll("a, button").Should().BeEmpty();
        cut.Find("[data-testid=rw-step][aria-current=step]").GetAttribute("data-step").Should().Be("ConstitutionExplorer");
        cut.Find("[data-testid=rw-step][data-step=ConstitutionExplorer] [data-testid=rw-step-state]").TextContent.Should().Be("Ready");
    }

    [Fact]
    public async Task Page_WorkspaceCard_ShowsOnlyWorkspaceProjectRolesAndSaved()
    {
        var cut = RenderPage(await ReadinessAsync(PersonModule()));

        cut.FindAll(".rw-workspace-details dt").Select(dt => dt.TextContent).Should().Equal("Workspace", "Project", "Artifact roles", "Saved");
        cut.Find("[data-testid=rw-artifact-roles]").TextContent.Should().Be("None available");
        cut.FindAll(".rw-summary .metric-card").Should().HaveCount(2, "Manual Review and Release Readiness only");
        cut.Markup.Should().NotContain("Automatic Setup");
    }
}
