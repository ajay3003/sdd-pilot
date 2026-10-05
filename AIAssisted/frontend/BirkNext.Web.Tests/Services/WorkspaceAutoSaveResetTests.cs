using BirkNext.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

public sealed class WorkspaceAutoSaveResetTests
{
    [Fact]
    public async Task PausedAutoSaveDoesNotPersistPendingOrNewChanges()
    {
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(x => x.AutoSaveAsync(It.IsAny<string?>()))
            .ReturnsAsync(new SavedWorkspaceDto { Id = Guid.NewGuid(), Name = "stale" });
        var workspace = new WorkspaceArtifactRepository();
        var service = new WorkspaceAutoSaveService(workspace, persistence.Object,
            Mock.Of<IWorkspaceSessionRestoreService>(), new WorkspaceUpdateCoordinator(),
            NullLogger<WorkspaceAutoSaveService>.Instance, autoSaveIntervalMs: 25, autoSaveThrottleMs: 0);

        workspace.CurrentProject = "before-reset";
        await service.PauseForResetAsync();
        workspace.ClearAll();
        workspace.CurrentProject = "after-reset-empty";
        await Task.Delay(100);

        persistence.Verify(x => x.AutoSaveAsync(It.IsAny<string?>()), Times.Never);
        service.ResumeAfterReset();
    }
}
