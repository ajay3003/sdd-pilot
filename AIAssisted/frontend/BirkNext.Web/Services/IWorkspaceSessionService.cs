namespace BirkNext.Web.Services;

public enum WorkspaceArtifactKind
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel
}

public sealed record WorkspaceArtifact(string Text, DateTime LoadedAt);

public interface IWorkspaceSessionService
{
    string? ProjectName { get; set; }

    WorkspaceArtifact? Constitution { get; }
    WorkspaceArtifact? Specification { get; }
    WorkspaceArtifact? Plan { get; }
    WorkspaceArtifact? Tasks { get; }
    WorkspaceArtifact? DataModel { get; }

    void Set(WorkspaceArtifactKind kind, string text);
    WorkspaceArtifact? Get(WorkspaceArtifactKind kind);
    bool Has(WorkspaceArtifactKind kind);
}
