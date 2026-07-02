using BirkNext.Web.Services;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

public sealed class WorkspaceArtifactStatusServiceTests
{
    [Fact]
    public void GetStatus_empty_workspace_returns_all_false()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(It.IsAny<WorkspaceArtifactKind>())).Returns(false);
        workspaceMock.Setup(w => w.CurrentProject).Returns((string?)null);

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);
        var status = service.GetStatus();

        status.HasConstitution.Should().BeFalse();
        status.HasSpecification.Should().BeFalse();
        status.HasPlan.Should().BeFalse();
        status.HasTasks.Should().BeFalse();
        status.HasDataModel.Should().BeFalse();
        status.ArtifactCount.Should().Be(0);
        status.ActiveProjectName.Should().BeNull();
        status.IsEmpty.Should().BeTrue();
        status.IsPartiallyLoaded.Should().BeFalse();
        status.IsFullyLoaded.Should().BeFalse();
    }

    [Fact]
    public void GetStatus_partial_artifacts_returns_correct_count()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Specification)).Returns(true);
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Plan)).Returns(true);
        workspaceMock.Setup(w => w.Has(It.IsNotIn(WorkspaceArtifactKind.Specification, WorkspaceArtifactKind.Plan))).Returns(false);
        workspaceMock.Setup(w => w.CurrentProject).Returns("person-adapter");

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);
        var status = service.GetStatus();

        status.HasConstitution.Should().BeFalse();
        status.HasSpecification.Should().BeTrue();
        status.HasPlan.Should().BeTrue();
        status.HasTasks.Should().BeFalse();
        status.HasDataModel.Should().BeFalse();
        status.ArtifactCount.Should().Be(2);
        status.ActiveProjectName.Should().Be("person-adapter");
        status.IsEmpty.Should().BeFalse();
        status.IsPartiallyLoaded.Should().BeTrue();
        status.IsFullyLoaded.Should().BeFalse();
    }

    [Fact]
    public void GetStatus_all_artifacts_loaded_returns_fully_loaded()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(It.IsAny<WorkspaceArtifactKind>())).Returns(true);
        workspaceMock.Setup(w => w.CurrentProject).Returns("person-module");

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);
        var status = service.GetStatus();

        status.HasConstitution.Should().BeTrue();
        status.HasSpecification.Should().BeTrue();
        status.HasPlan.Should().BeTrue();
        status.HasTasks.Should().BeTrue();
        status.HasDataModel.Should().BeTrue();
        status.ArtifactCount.Should().Be(5);
        status.ActiveProjectName.Should().Be("person-module");
        status.IsEmpty.Should().BeFalse();
        status.IsPartiallyLoaded.Should().BeFalse();
        status.IsFullyLoaded.Should().BeTrue();
    }

    [Fact]
    public void GetStatus_availability_dictionary_maps_correctly()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Constitution)).Returns(true);
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Specification)).Returns(false);
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Plan)).Returns(true);
        workspaceMock.Setup(w => w.Has(It.IsNotIn(WorkspaceArtifactKind.Constitution, WorkspaceArtifactKind.Plan))).Returns(false);
        workspaceMock.Setup(w => w.CurrentProject).Returns((string?)null);

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);
        var status = service.GetStatus();

        var availability = status.Availability;
        availability[WorkspaceArtifactKind.Constitution].Should().BeTrue();
        availability[WorkspaceArtifactKind.Specification].Should().BeFalse();
        availability[WorkspaceArtifactKind.Plan].Should().BeTrue();
        availability[WorkspaceArtifactKind.Tasks].Should().BeFalse();
        availability[WorkspaceArtifactKind.DataModel].Should().BeFalse();
    }

    [Fact]
    public void StatusChanged_event_fires_on_artifact_load()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(It.IsAny<WorkspaceArtifactKind>())).Returns(false);
        workspaceMock.Setup(w => w.CurrentProject).Returns((string?)null);

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);
        var changeCount = 0;
        service.StatusChanged += () => changeCount++;

        var status1 = service.GetStatus();
        changeCount.Should().Be(0);

        // Simulate artifact being loaded
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Specification)).Returns(true);

        var status2 = service.GetStatus();
        changeCount.Should().Be(1);
        status2.HasSpecification.Should().BeTrue();
        status2.ArtifactCount.Should().Be(1);
    }

    [Fact]
    public void StatusChanged_event_fires_on_project_change()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Specification)).Returns(true);
        workspaceMock.Setup(w => w.CurrentProject).Returns("person-adapter");

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);
        var changeCount = 0;
        service.StatusChanged += () => changeCount++;

        var status1 = service.GetStatus();
        changeCount.Should().Be(0);

        // Simulate project change
        workspaceMock.Setup(w => w.CurrentProject).Returns("hendelse-adapter");

        var status2 = service.GetStatus();
        changeCount.Should().Be(1);
        status2.ActiveProjectName.Should().Be("hendelse-adapter");
    }

    [Fact]
    public void StatusChanged_event_does_not_fire_when_status_unchanged()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(It.IsAny<WorkspaceArtifactKind>())).Returns(true);
        workspaceMock.Setup(w => w.CurrentProject).Returns("person-adapter");

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);
        var changeCount = 0;
        service.StatusChanged += () => changeCount++;

        var status1 = service.GetStatus();
        changeCount.Should().Be(0);

        // Call GetStatus again without changes
        var status2 = service.GetStatus();
        changeCount.Should().Be(0);
        status2.Should().BeEquivalentTo(status1);
    }

    [Fact]
    public void Multiple_GetStatus_calls_preserve_cache()
    {
        var workspaceMock = new Mock<IWorkspaceSessionService>();
        workspaceMock.Setup(w => w.Has(WorkspaceArtifactKind.Specification)).Returns(true);
        workspaceMock.Setup(w => w.Has(It.IsNotIn(WorkspaceArtifactKind.Specification))).Returns(false);
        workspaceMock.Setup(w => w.CurrentProject).Returns("test-project");

        var service = new WorkspaceArtifactStatusService(workspaceMock.Object);

        var status1 = service.GetStatus();
        var status2 = service.GetStatus();
        var status3 = service.GetStatus();

        status1.Should().BeEquivalentTo(status2);
        status2.Should().BeEquivalentTo(status3);
        workspaceMock.Verify(w => w.Has(It.IsAny<WorkspaceArtifactKind>()), Times.AtLeast(5));
    }
}
