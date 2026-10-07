using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Workflow readiness reads workspace and artifact presence only from the current-workspace projection. It adds review steps,
/// the next action and release readiness, and never re-decides whether a workspace exists.
/// </summary>
public sealed class WorkflowReadinessServiceTests
{
    private static readonly WorkspaceArtifactType[] AllRoles = [.. CurrentWorkspaceSnapshot.WorkflowRoles];

    [Fact]
    public async Task NoWorkspace_RecommendsLoadingArtifacts_WithoutBackendWorkflowState()
    {
        var fixture = new Fixture(CurrentWorkspaceSnapshot.None());

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceLoaded.Should().BeFalse();
        readiness.WorkspaceName.Should().Be("No workspace loaded");
        readiness.Workspace.AvailableRoleCount.Should().Be(0);
        readiness.NextRecommendedAction!.Title.Should().Be("Load project artifacts");
        readiness.NextRecommendedAction.Key.Should().Be(WorkflowReadinessService.LoadWorkspaceKey);
        readiness.IsOnboarding.Should().BeTrue();
        readiness.Steps.Should().ContainSingle().Which.Should().BeSameAs(readiness.NextRecommendedAction, "step 1 is the current step");
        readiness.ArtifactLoad.Should().Be(ArtifactLoadState.Required);
        readiness.CanRelease.Should().BeFalse();
        readiness.ReleaseReadinessPercent.Should().BeNull("nothing is assessed without a workspace: no 0%");
        fixture.VerifyBackendNeverCalled();
    }

    [Fact]
    public async Task PersonModule_WithFiveRoles_IsLoaded_AndNeverRecommendsLoadingArtifacts()
    {
        var fixture = new Fixture(WorkspaceSnapshots.Loaded("Person Module", "person-module", "Person Module", AllRoles));
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Available, isCurrent: true),
            Step("ArtifactTraceability", "Artifact Traceability", WorkflowStepStatus.Available),
            Step("ImplementationReview", "Implementation Review", WorkflowStepStatus.Locked),
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceLoaded.Should().BeTrue();
        readiness.WorkspaceName.Should().Be("Person Module");
        readiness.ProjectName.Should().Be("Person Module");
        readiness.Workspace.AvailableRoleCount.Should().Be(5);
        readiness.NextRecommendedAction!.Key.Should().Be("SpecificationExplorer");
        readiness.Steps.Should().NotContain(step => step.Key == WorkflowReadinessService.LoadWorkspaceKey);
        fixture.WorkflowApi.Verify(api => api.BuildWorkflowStepsAsync(It.IsAny<Guid>(), true, true, true, true, true), Times.Once);
    }

    [Fact]
    public async Task TwoRoleProject_PassesExactRoleAvailabilityToTheBackend()
    {
        var fixture = new Fixture(WorkspaceSnapshots.Loaded("Docs", "docs", "Docs", WorkspaceArtifactType.Specification, WorkspaceArtifactType.Tasks));
        fixture.WorkflowApi.SetupBuildSteps([Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Available, isCurrent: true)]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceLoaded.Should().BeTrue();
        readiness.Workspace.AvailableRoleCount.Should().Be(2);
        fixture.WorkflowApi.Verify(api => api.BuildWorkflowStepsAsync(It.IsAny<Guid>(), false, true, false, true, false), Times.Once);
    }

    [Fact]
    public async Task LoadedWorkspaceWithoutArtifacts_RecommendsLoadingArtifacts_AsStepOne()
    {
        var fixture = new Fixture(WorkspaceSnapshots.Loaded("Source only", "source-only", "Source only"));
        fixture.WorkflowApi.SetupBuildSteps([Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Locked)]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceLoaded.Should().BeTrue();
        readiness.ArtifactLoad.Should().Be(ArtifactLoadState.Required, "a workspace without artifacts has not completed step 1");
        readiness.NextRecommendedAction!.Title.Should().Be("Load project artifacts");
    }

    [Fact]
    public async Task SeveralSpecificationsWithoutAChoice_RecommendChoosingOne_BeforeReviewSteps()
    {
        var snapshot = WorkspaceSnapshots.Loaded("Docs", null, null, AllRoles);
        snapshot = snapshot with
        {
            Roles = snapshot.Roles.Select(r => r.Role == WorkspaceArtifactType.Specification ? WorkspaceSnapshots.Available(r.Role, count: 3) : r).ToList()
        };
        var fixture = new Fixture(snapshot);
        fixture.WorkflowApi.SetupBuildSteps([Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Available, isCurrent: true)]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.Workspace.AvailableRoleCount.Should().Be(5, "several artifacts of a role are one available role");
        readiness.Workspace.ArtifactCount.Should().Be(7);
        readiness.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.ChooseArtifactsKey);
        readiness.NextRecommendedAction.Route.Should().Be("specification-explorer");
    }

    [Fact]
    public async Task UnreadableWorkspace_IsAnError_NotNoWorkspace()
    {
        var error = CurrentWorkspaceSnapshot.None() with
        {
            State = CurrentWorkspaceState.Error, ProjectSlug = "person-module", WorkspaceName = "Unable to load workspace", Error = "offline"
        };
        var fixture = new Fixture(error);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceError.Should().BeTrue();
        readiness.NextRecommendedAction.Should().BeNull("no recommendation is computed from an unread workspace");
        readiness.Steps.Should().BeEmpty();
        fixture.VerifyBackendNeverCalled();
    }

    [Fact]
    public async Task NothingReviewedYet_ReleaseReadinessIsNotAssessed_NotZero()
    {
        var fixture = new Fixture(WorkspaceSnapshots.AllRoles());
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Available, isCurrent: true),
            Step("ArtifactTraceability", "Artifact Traceability", WorkflowStepStatus.Locked),
            Step("ImplementationReview", "Implementation Review", WorkflowStepStatus.Locked),
            Step("ReviewContextValidation", "ReviewContext Validation", WorkflowStepStatus.Available, requiresApproval: false)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.ReleaseReadinessPercent.Should().BeNull();
        readiness.RequiredReviewCount.Should().Be(3);
        readiness.ApprovedReviewCount.Should().Be(0);
        readiness.Steps.Should().NotContain(step => step.Key == "ReviewContextValidation");
        readiness.OverallReadiness.ArtifactReadiness.Should().Be(100);
        readiness.CanRelease.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewedStep_MakesReadinessAssessed_WithoutCountingAsApproved()
    {
        var fixture = new Fixture(WorkspaceSnapshots.AllRoles());
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Reviewed, approvalState: ApprovalState.Pending),
            Step("ArtifactTraceability", "Artifact Traceability", WorkflowStepStatus.Available),
            Step("ImplementationReview", "Implementation Review", WorkflowStepStatus.Locked)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.ReleaseReadinessPercent.Should().Be(0, "a review decision exists, so 0 approved is an assessed 0");
        readiness.CanRelease.Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkflowStepStatus.Approved, WorkflowStepStatus.Available, WorkflowStepStatus.Locked, "ArtifactTraceability", false, 33)]
    [InlineData(WorkflowStepStatus.Approved, WorkflowStepStatus.Approved, WorkflowStepStatus.Available, "ImplementationReview", false, 66)]
    [InlineData(WorkflowStepStatus.Approved, WorkflowStepStatus.Approved, WorkflowStepStatus.Approved, null, true, 100)]
    public async Task ReviewApprovalChain_DerivesNextActionAndReleaseReadiness(
        WorkflowStepStatus specificationStatus,
        WorkflowStepStatus traceabilityStatus,
        WorkflowStepStatus implementationStatus,
        string? expectedCurrentStep,
        bool expectedRelease,
        int expectedPercent)
    {
        var fixture = new Fixture(WorkspaceSnapshots.AllRoles());
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationExplorer", "Specification Explorer", specificationStatus, expectedCurrentStep == "SpecificationExplorer"),
            Step("ArtifactTraceability", "Artifact Traceability", traceabilityStatus, expectedCurrentStep == "ArtifactTraceability"),
            Step("ImplementationReview", "Implementation Review", implementationStatus, expectedCurrentStep == "ImplementationReview"),
            Step("ReviewContextValidation", "ReviewContext Validation", WorkflowStepStatus.Available, requiresApproval: false)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.CanRelease.Should().Be(expectedRelease);
        readiness.ReleaseReadinessPercent.Should().Be(expectedPercent);
        if (expectedCurrentStep is null)
            readiness.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.OpenReviewKeyPrefix + "quality-review",
                "document steps done: the next applicable review, not nothing");
        else
            readiness.NextRecommendedAction!.Key.Should().Be(expectedCurrentStep);
    }

    [Fact]
    public async Task ApprovedButLockedStep_DoesNotAllowRelease()
    {
        var fixture = new Fixture(WorkspaceSnapshots.Loaded("Docs", "docs", "Docs", WorkspaceArtifactType.Constitution));
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Locked, approvalState: ApprovalState.Approved),
            Step("ArtifactTraceability", "Artifact Traceability", WorkflowStepStatus.Locked, approvalState: ApprovalState.Approved),
            Step("ImplementationReview", "Implementation Review", WorkflowStepStatus.Locked, approvalState: ApprovalState.Approved)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.CanRelease.Should().BeFalse("an approval whose artifacts are gone no longer unlocks release");
    }

    [Fact]
    public async Task WorkspaceChange_RaisesReadinessChanged()
    {
        var fixture = new Fixture(CurrentWorkspaceSnapshot.None());
        var raised = 0;
        fixture.Service.ReadinessChanged += () => raised++;

        fixture.Projection.Raise(p => p.Changed += null);

        raised.Should().Be(1);
    }

    private static WorkflowStepViewModel Step(
        string key,
        string title,
        WorkflowStepStatus status,
        bool isCurrent = false,
        bool requiresApproval = true,
        ApprovalState? approvalState = null) =>
        new()
        {
            Number = key switch
            {
                "SpecificationExplorer" => 1,
                "ArtifactTraceability" => 2,
                "ImplementationReview" => 3,
                _ => 4
            },
            Key = key,
            Title = title,
            Description = title,
            Route = key,
            ActionLabel = title,
            Color = "#2563eb",
            Status = status,
            CanOpen = status != WorkflowStepStatus.Locked,
            IsCurrent = isCurrent,
            IsFuture = status == WorkflowStepStatus.Locked,
            RequiresApproval = requiresApproval,
            RequiresManualReview = requiresApproval,
            ApprovalState = approvalState ?? (status == WorkflowStepStatus.Approved ? ApprovalState.Approved : ApprovalState.Pending),
            ReviewState = status is WorkflowStepStatus.Approved or WorkflowStepStatus.Reviewed ? ReviewState.Reviewed : ReviewState.NotStarted,
            Prerequisites = status == WorkflowStepStatus.Locked ? PrerequisiteState.Missing : PrerequisiteState.Available
        };

    private sealed class Fixture
    {
        public Mock<ICurrentWorkspaceProjection> Projection { get; }
        public Mock<IRecommendedWorkflowApiService> WorkflowApi { get; } = new();
        public WorkflowReadinessService Service { get; }

        public Fixture(CurrentWorkspaceSnapshot snapshot)
        {
            Projection = WorkspaceSnapshots.Projection(snapshot);
            // No active Target Environment: no source and no target, so only the documents input varies in these tests.
            var contexts = new Mock<IFrontendAnalysisContextFactory>();
            contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" });
            var applicability = new ProjectApplicabilityState(Mock.Of<ITechnologyCoverageApiService>(), contexts.Object, Projection.Object, Mock.Of<IFrontendAnalysisSettingsService>());
            Service = new WorkflowReadinessService(Projection.Object, applicability, WorkflowApi.Object, NullLogger<WorkflowReadinessService>.Instance);
        }

        public void VerifyBackendNeverCalled() =>
            WorkflowApi.Verify(api => api.BuildWorkflowStepsAsync(
                It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }
}

file static class RecommendedWorkflowApiMockExtensions
{
    public static void SetupBuildSteps(this Mock<IRecommendedWorkflowApiService> workflowApi, List<WorkflowStepViewModel> steps)
    {
        workflowApi
            .Setup(api => api.BuildWorkflowStepsAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<bool>()))
            .ReturnsAsync(steps);
    }
}
