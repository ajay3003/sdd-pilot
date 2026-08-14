namespace BirkNext.Api.Models;

public class SavedWorkspace
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastOpenedAt { get; set; }
    public int Version { get; set; } = 1;
    public string ParserVersion { get; set; } = "1.0";
    public string ReviewContextVersion { get; set; } = "1.0";
    public string? ArtifactSetHash { get; set; }
    public bool AutoSaved { get; set; } = false;
    public bool IsCurrent { get; set; } = false;
    public bool Favorite { get; set; } = false;
    public string? TagsJson { get; set; }
    public bool IsDeleted { get; set; } = false;

    // Navigation
    public ICollection<SavedWorkspaceArtifact> Artifacts { get; set; } = new List<SavedWorkspaceArtifact>();
}
