using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

public sealed class WorkflowReadinessServiceTests
{
    [Fact]
    public async Task EmptyWorkspace_ReturnsEmptyReadinessWithoutBackendWorkflowState()
    {
        var fixture = new Fixture();
        fixture.ArtifactStatus.Setup(s => s.GetStatus()).Returns(EmptyArtifacts());
        fixture.WorkspaceRestore.Setup(s => s.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync((CurrentWorkspaceMetadata?)null);
        fixture.WorkspacePersistence.Setup(s => s.GetCurrentStateAsync()).ReturnsAsync((CurrentWorkspaceStateDto?)null);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceLoaded.Should().BeFalse();
        readiness.WorkspaceName.Should().Be("No workspace loaded");
        readiness.ArtifactStatus.ArtifactCount.Should().Be(0);
        readiness.NextRecommendedAction.Should().NotBeNull();
        readiness.NextRecommendedAction!.Title.Should().Be("Load Sample Project");
        readiness.SpecificationReviewState.Should().BeNull();
        readiness.TraceabilityState.Should().BeNull();
        readiness.ImplementationReviewState.Should().BeNull();
        readiness.CanRelease.Should().BeFalse();
        readiness.ReleaseReason.Should().Contain("Load a workspace");
        readiness.OverallReadiness.OverallReadiness.Should().Be(0);
        readiness.OverallReadiness.ArtifactReadiness.Should().Be(0);
        readiness.OverallReadiness.ReviewReadiness.Should().Be(0);
        readiness.OverallReadiness.ApprovalReadiness.Should().Be(0);
        readiness.Steps.Should().NotContain(step => step.Status == WorkflowStepStatus.Approved);
        fixture.WorkflowApi.Verify(api => api.BuildWorkflowStepsAsync(
            It.IsAny<Guid>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task WorkspaceLoaded_DisplaysArtifactStatusAndBlocksReleaseBeforeApprovals()
    {
        var fixture = new Fixture();
        fixture.ArtifactStatus.Setup(s => s.GetStatus()).Returns(new WorkspaceArtifactStatus(
            HasConstitution: true,
            HasSpecification: true,
            HasPlan: true,
            HasTasks: false,
            HasDataModel: false,
            ArtifactCount: 3,
            ActiveProjectName: "Sample Project"));
        fixture.WorkspaceRestore.Setup(s => s.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync(Workspace(count: 3));
        fixture.WorkspacePersistence.Setup(s => s.GetCurrentStateAsync()).ReturnsAsync(CurrentState(count: 3));
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationReview", "Specification Review", WorkflowStepStatus.Available, isCurrent: true),
            Step("ArtifactTraceability", "Artifact Traceability", WorkflowStepStatus.Locked),
            Step("ImplementationReview", "Implementation Review", WorkflowStepStatus.Locked),
            Step("ReviewContextValidation", "ReviewContext Validation", WorkflowStepStatus.Available, requiresApproval: false)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceLoaded.Should().BeTrue();
        readiness.WorkspaceName.Should().Be("Saved workspace");
        readiness.ArtifactStatus.ArtifactCount.Should().Be(3);
        readiness.Artifacts.Count(a => a.IsLoaded).Should().Be(3);
        readiness.Steps.Should().NotContain(step => step.Key == "ReviewContextValidation");
        readiness.OverallReadiness.ArtifactReadiness.Should().Be(60);
        readiness.OverallReadiness.ReviewReadiness.Should().Be(0);
        readiness.OverallReadiness.ApprovalReadiness.Should().Be(0);
        readiness.OverallReadiness.OverallReadiness.Should().Be(0);
        readiness.NextRecommendedAction!.Key.Should().Be("SpecificationReview");
        readiness.CanRelease.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewedSteps_DoNotIncreaseReleaseReadinessWithoutApproval()
    {
        var fixture = new Fixture();
        fixture.ArtifactStatus.Setup(s => s.GetStatus()).Returns(LoadedArtifacts());
        fixture.WorkspaceRestore.Setup(s => s.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync(Workspace());
        fixture.WorkspacePersistence.Setup(s => s.GetCurrentStateAsync()).ReturnsAsync(CurrentState());
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationReview", "Specification Review", WorkflowStepStatus.Reviewed, approvalState: ApprovalState.Pending),
            Step("ArtifactTraceability", "Artifact Traceability", WorkflowStepStatus.Available),
            Step("ImplementationReview", "Implementation Review", WorkflowStepStatus.Locked)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.OverallReadiness.ArtifactReadiness.Should().Be(100);
        readiness.OverallReadiness.ApprovalReadiness.Should().Be(0);
        readiness.OverallReadiness.OverallReadiness.Should().Be(0);
        readiness.CanRelease.Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkflowStepStatus.Approved, WorkflowStepStatus.Available, WorkflowStepStatus.Locked, "ArtifactTraceability", false)]
    [InlineData(WorkflowStepStatus.Approved, WorkflowStepStatus.Approved, WorkflowStepStatus.Available, "ImplementationReview", false)]
    [InlineData(WorkflowStepStatus.Approved, WorkflowStepStatus.Approved, WorkflowStepStatus.Approved, null, true)]
    public async Task ReviewApprovalChain_DerivesNextActionAndReleaseReadiness(
        WorkflowStepStatus specificationStatus,
        WorkflowStepStatus traceabilityStatus,
        WorkflowStepStatus implementationStatus,
        string? expectedCurrentStep,
        bool expectedRelease)
    {
        var fixture = new Fixture();
        fixture.ArtifactStatus.Setup(s => s.GetStatus()).Returns(LoadedArtifacts());
        fixture.WorkspaceRestore.Setup(s => s.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync(Workspace());
        fixture.WorkspacePersistence.Setup(s => s.GetCurrentStateAsync()).ReturnsAsync(CurrentState());
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationReview", "Specification Review", specificationStatus, expectedCurrentStep == "SpecificationReview"),
            Step("ArtifactTraceability", "Artifact Traceability", traceabilityStatus, expectedCurrentStep == "ArtifactTraceability"),
            Step("ImplementationReview", "Implementation Review", implementationStatus, expectedCurrentStep == "ImplementationReview"),
            Step("ReviewContextValidation", "ReviewContext Validation", WorkflowStepStatus.Available, requiresApproval: false)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.CanRelease.Should().Be(expectedRelease);
        if (expectedCurrentStep is null)
        {
            readiness.NextRecommendedAction.Should().BeNull();
            readiness.Steps.Should().NotContain(step => step.Key == "ReviewContextValidation");
        }
        else
        {
            readiness.NextRecommendedAction!.Key.Should().Be(expectedCurrentStep);
        }
    }

    [Fact]
    public async Task WorkspaceCleared_ResetsReadinessAndSuppressesStaleReleaseState()
    {
        var fixture = new Fixture();
        fixture.ArtifactStatus.Setup(s => s.GetStatus()).Returns(EmptyArtifacts());
        fixture.WorkspaceRestore.Setup(s => s.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync((CurrentWorkspaceMetadata?)null);
        fixture.WorkspacePersistence.Setup(s => s.GetCurrentStateAsync()).ReturnsAsync((CurrentWorkspaceStateDto?)null);
        fixture.WorkflowApi.SetupBuildSteps([
            Step("SpecificationReview", "Specification Review", WorkflowStepStatus.Approved),
            Step("ArtifactTraceability", "Artifact Traceability", WorkflowStepStatus.Approved),
            Step("ImplementationReview", "Implementation Review", WorkflowStepStatus.Approved)
        ]);

        var readiness = await fixture.Service.GetReadinessAsync();

        readiness.WorkspaceLoaded.Should().BeFalse();
        readiness.ArtifactStatus.ArtifactCount.Should().Be(0);
        readiness.CanRelease.Should().BeFalse();
        readiness.Steps.Should().ContainSingle(step => step.Key == "LoadWorkspace");
        fixture.WorkflowApi.Verify(api => api.BuildWorkflowStepsAsync(
            It.IsAny<Guid>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
    }

    private static WorkspaceArtifactStatus EmptyArtifacts() =>
        new(false, false, false, false, false, 0, null);

    private static WorkspaceArtifactStatus LoadedArtifacts(int count = 5, bool hasDataModel = true) =>
        new(true, true, true, true, hasDataModel, count, "Sample Project");

    private static CurrentWorkspaceMetadata Workspace(int count = 5) =>
        new()
        {
            WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WorkspaceName = "Saved workspace",
            ProjectName = "Sample Project",
            ArtifactCount = count,
            LoadedAt = DateTimeOffset.UtcNow
        };

    private static CurrentWorkspaceStateDto CurrentState(int count = 5) =>
        new()
        {
            CurrentWorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WorkspaceName = "Saved workspace",
            ProjectName = "Sample Project",
            ArtifactCount = count,
            Status = "Saved",
            LastSavedAt = DateTimeOffset.UtcNow
        };

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
                "SpecificationReview" => 1,
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
        public Mock<IWorkspaceArtifactStatusService> ArtifactStatus { get; } = new();
        public Mock<IWorkspaceSessionRestoreService> WorkspaceRestore { get; } = new();
        public Mock<IWorkspaceSessionService> WorkspaceSession { get; } = new();
        public Mock<IWorkspaceUpdateCoordinator> Updates { get; } = new();
        public Mock<IWorkspacePersistenceApiService> WorkspacePersistence { get; } = new();
        public Mock<IRecommendedWorkflowApiService> WorkflowApi { get; } = new();

        public WorkflowReadinessService Service { get; }

        public Fixture()
        {
            Service = new WorkflowReadinessService(
                ArtifactStatus.Object,
                WorkspaceRestore.Object,
                WorkspaceSession.Object,
                Updates.Object,
                WorkspacePersistence.Object,
                WorkflowApi.Object,
                NullLogger<WorkflowReadinessService>.Instance);
        }
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
