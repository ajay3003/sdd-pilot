using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services.Explorers;

/// <summary>
/// What a page can say about one artifact role of the current workspace. One model for every explorer and review page, derived from
/// <see cref="ArtifactExplorerState"/> (the explorers' role authority) — pages never keep their own booleans for it.
/// "No project loaded", "the role has no artifact" and "the role has several candidates" are different states and never share a message.
/// </summary>
public enum WorkspaceArtifactState
{
    /// <summary>No imported project, no Sample Project and no document in the manual workspace.</summary>
    NoWorkspace,
    /// <summary>A workspace exists, but no artifact of this role was found in it.</summary>
    RoleUnavailable,
    /// <summary>Several candidates and none is selected or authoritative: the user chooses; nothing is picked automatically.</summary>
    RoleAmbiguous,
    RoleSelected,
    /// <summary>The project's documents cannot be read (the Sample Project left the catalog, or a read failed). Not "absent".</summary>
    Unsupported,
}

/// <summary>Where the current workspace's artifacts come from, for wording and the "review project artifacts" link.</summary>
public enum WorkspaceArtifactOrigin { None, ManualWorkspace, SampleProject, ProjectImport }

public static class WorkspaceArtifactStates
{
    /// <param name="state">The role's explorer state.</param>
    /// <param name="workspaceHasArtifacts">Whether the manual workspace holds any artifact of any role (only relevant without a project).</param>
    public static WorkspaceArtifactState Of(ArtifactExplorerState state, bool workspaceHasArtifacts) => state.Status switch
    {
        ExplorerArtifactStatus.Loaded => WorkspaceArtifactState.RoleSelected,
        ExplorerArtifactStatus.SelectionRequired => WorkspaceArtifactState.RoleAmbiguous,
        ExplorerArtifactStatus.ProjectUnavailable or ExplorerArtifactStatus.Error => WorkspaceArtifactState.Unsupported,
        _ => state.HasProject || workspaceHasArtifacts ? WorkspaceArtifactState.RoleUnavailable : WorkspaceArtifactState.NoWorkspace,
    };

    public static WorkspaceArtifactOrigin OriginOf(string? scope, bool workspaceHasArtifacts) =>
        ProjectImportScope.IsImport(scope) ? WorkspaceArtifactOrigin.ProjectImport
        : scope is not null ? WorkspaceArtifactOrigin.SampleProject
        : workspaceHasArtifacts ? WorkspaceArtifactOrigin.ManualWorkspace
        : WorkspaceArtifactOrigin.None;

    /// <summary>Where the user reviews what the current project contains: the import page for an imported project, Sample Projects for a sample.</summary>
    public static string? ReviewArtifactsRoute(WorkspaceArtifactOrigin origin) => origin switch
    {
        WorkspaceArtifactOrigin.ProjectImport => ProjectInputPresentation.ProjectImportRoute,
        WorkspaceArtifactOrigin.SampleProject => ProjectInputPresentation.SampleProjectsRoute,
        _ => null,
    };

    /// <summary>Whether the manual workspace (no project) has any artifact of the SDD workflow roles.</summary>
    public static async Task<bool> ManualWorkspaceHasArtifactsAsync(IArtifactExplorerContext context, CancellationToken cancellationToken = default)
    {
        if (context.CurrentScope is not null) return true;
        foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles)
            if ((await context.GetStateAsync(role, cancellationToken)).Artifacts.Count > 0) return true;
        return false;
    }
}
