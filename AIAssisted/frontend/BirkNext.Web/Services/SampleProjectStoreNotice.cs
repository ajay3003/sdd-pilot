namespace BirkNext.Web.Services;

/// <summary>
/// Pages that read the backend's per-Sample-Project requirement/test/trace store (Spec Drift, change history, delta
/// reviews, traceability suggestions). Project Import and manual imports do not write that store, so for such a workspace these pages
/// have nothing to assess — they must say so instead of telling the user to pick a Sample Project (which would replace their project).
/// </summary>
public static class SampleProjectStoreNotice
{
    /// <summary>
    /// True when the workspace holds an imported project or documents that did not come from a Sample Project selection
    /// (<see cref="IWorkspaceArtifactRepository.CurrentProject"/> is the Sample Project slug only).
    /// </summary>
    public static bool HasNonSampleWorkspace(IWorkspaceArtifactRepository workspace) =>
        string.IsNullOrWhiteSpace(workspace.CurrentProject)
        && (workspace.SddLifecycle.CurrentProjectImportId is not null || CurrentWorkspaceSnapshot.WorkflowRoles.Any(workspace.Has));

    /// <summary>The neutral explanation shown when there is no Sample Project to read from.</summary>
    public static string Message(string feature, IWorkspaceArtifactRepository workspace) =>
        HasNonSampleWorkspace(workspace)
            ? $"Not assessed for this workspace: {feature} reads requirement and test links stored for a Sample Project. " +
              "Documents imported into the current workspace are not part of that store, so there is nothing to compare yet."
            : $"No project loaded. Import a project or select a Sample Project to use {feature}.";
}
