namespace BirkNext.Api.Models;

public enum ArtifactType
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel,
    Research,
    ADR,
    Contract,
    Quickstart,
    Other
}

public class SavedWorkspaceArtifact
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public ArtifactType ArtifactType { get; set; }
    public string FileName { get; set; } = "";
    public string? OriginalPath { get; set; }
    public string Content { get; set; } = "";
    public string? ContentHash { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;
    public string ParseVersion { get; set; } = "1.0";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public SavedWorkspace? Workspace { get; set; }
}
