namespace BirkNext.Web.Services;

public enum WorkspaceArtifactType
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel,
    Research
}

public interface IWorkspaceArtifactRepository
{
    string? ProjectName { get; set; }
    string? CurrentProject { get; set; }

    void Set(WorkspaceArtifactType type, string text,
             string? fileName = null, string? sourcePath = null, DateTime? lastModified = null);
    WorkspaceArtifact? Get(WorkspaceArtifactType type);
    bool Has(WorkspaceArtifactType type);
    void Clear(WorkspaceArtifactType type);
    IEnumerable<(WorkspaceArtifactType Type, WorkspaceArtifact Artifact)> GetAllArtifacts();
}
