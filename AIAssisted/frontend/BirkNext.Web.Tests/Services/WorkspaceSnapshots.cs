using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>Current-workspace snapshots for tests that need a workspace without building the repository behind it.</summary>
internal static class WorkspaceSnapshots
{
    public static CurrentWorkspaceSnapshot Loaded(string workspaceName, string? projectSlug, string? projectName, params WorkspaceArtifactType[] roles) =>
        new(CurrentWorkspaceState.Loaded, Guid.Parse("11111111-1111-1111-1111-111111111111"), workspaceName, projectSlug, projectName,
            projectSlug is not null,
            CurrentWorkspaceSnapshot.WorkflowRoles.Select(role => roles.Contains(role) ? Available(role) : Missing(role)).ToList(),
            "NotSaved", null, 1);

    public static CurrentWorkspaceSnapshot AllRoles(string workspaceName = "Saved workspace", string? project = "sample-project") =>
        Loaded(workspaceName, project, project, [.. CurrentWorkspaceSnapshot.WorkflowRoles]);

    public static ArtifactRoleStatus Available(WorkspaceArtifactType role, int count = 1) =>
        new(role, ArtifactRoleAvailability.Available, count, count == 1 ? ArtifactRoleSelection.Selected : ArtifactRoleSelection.SelectionRequired,
            count == 1 ? new ExplorerArtifact($"sample:{role}.md", role, role.ToString(), $"{role}.md", $"{role}.md",
                ExplorerArtifactSource.SampleProject, null, null, 0, "Unknown", ExplorerArtifactCurrentness.Current, null) : null,
            null);

    public static ArtifactRoleStatus Missing(WorkspaceArtifactType role) =>
        new(role, ArtifactRoleAvailability.Missing, 0, ArtifactRoleSelection.None, null, null);

    /// <summary>A projection that always returns <paramref name="snapshot"/>.</summary>
    public static Mock<ICurrentWorkspaceProjection> Projection(CurrentWorkspaceSnapshot snapshot)
    {
        var projection = new Mock<ICurrentWorkspaceProjection>();
        projection.Setup(p => p.GetAsync()).ReturnsAsync(snapshot);
        projection.SetupGet(p => p.Current).Returns(snapshot);
        return projection;
    }
}
