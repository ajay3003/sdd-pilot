using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IWorkspaceArtifactRepository
{
    string? ProjectName { get; set; }
    string? CurrentProject { get; set; }

    void Set(WorkspaceArtifactType type, string text,
             string? fileName = null, string? sourcePath = null, DateTime? lastModified = null);
    WorkspaceArtifact? Get(WorkspaceArtifactType type);
    IReadOnlyList<SddArtifactRevision> GetArtifactRevisions(WorkspaceArtifactType type);
    WorkspaceArtifact? GetRevision(Guid revisionId);
    void SelectRevision(Guid revisionId);
    SddArtifactRevision? AddArtifactRevision(WorkspaceArtifactType type, string text, string? fileName, string? sourcePath,
        string? workspaceScope, string? origin, bool select);
    bool Has(WorkspaceArtifactType type);
    void Clear(WorkspaceArtifactType type);
    IEnumerable<(WorkspaceArtifactType Type, WorkspaceArtifact Artifact)> GetAllArtifacts();
    SddLifecycleState SddLifecycle { get; }
    void ResetSddLifecycle();
    void RestoreSddLifecycle(string? json);
    void SetArtifactAuthority(Guid revisionId, string authority);
}
