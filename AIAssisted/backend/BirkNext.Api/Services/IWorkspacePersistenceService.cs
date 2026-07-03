using BirkNext.Api.Models;
using System.Text.Json.Serialization;

namespace BirkNext.Api.Services;

public class WorkspaceArtifactDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ArtifactType ArtifactType { get; set; }
    public string FileName { get; set; } = "";
    public string Content { get; set; } = "";
}

public class SavedWorkspaceArtifactResponseDto
{
    public string ArtifactType { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? OriginalPath { get; set; }
    public string Content { get; set; } = "";
    public string? ContentHash { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public string ParseVersion { get; set; } = "1.0";
}

public class SavedWorkspaceDto
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastOpenedAt { get; set; }
    public int Version { get; set; } = 1;
    public string ParserVersion { get; set; } = "1.0";
    public string ReviewContextVersion { get; set; } = "1.0";
    public string? ArtifactSetHash { get; set; }
    public bool AutoSaved { get; set; }
    public bool Favorite { get; set; }
    public List<SavedWorkspaceArtifactResponseDto> Artifacts { get; set; } = new();
}

public class WorkspaceStateDto
{
    public Guid? CurrentWorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? ProjectName { get; set; }
    public int ArtifactCount { get; set; }
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.NotSaved;
    public DateTimeOffset? LastSavedAt { get; set; }
    public bool IsDirty { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceStatus
{
    NotSaved,
    Saved,
    UnsavedChanges,
    AutoSaved
}

public interface IWorkspacePersistenceService
{
    // Workspace operations
    Task<SavedWorkspace> SaveCurrentAsync(string? name = null, List<WorkspaceArtifactDto>? artifacts = null);
    Task<SavedWorkspace> SaveAsAsync(string name, List<WorkspaceArtifactDto>? artifacts = null);
    Task<SavedWorkspace?> LoadAsync(Guid workspaceId);
    Task<List<SavedWorkspace>> ListAsync(string userId);
    Task<SavedWorkspace> RenameAsync(Guid workspaceId, string newName);
    Task<SavedWorkspace> DuplicateAsync(Guid workspaceId, string newName);
    Task DeleteAsync(Guid workspaceId);

    // Auto-save
    Task<SavedWorkspace> AutoSaveAsync(string? generatedName = null, List<WorkspaceArtifactDto>? artifacts = null);

    // Current workspace tracking
    Task SetCurrentWorkspaceAsync(Guid workspaceId);
    Task<Guid?> GetCurrentWorkspaceIdAsync();
    Task ClearCurrentWorkspaceAsync();

    // Artifact operations
    Task SaveArtifactAsync(Guid workspaceId, WorkspaceArtifactDto artifact);
    Task<WorkspaceArtifactDto?> GetArtifactAsync(Guid workspaceId, ArtifactType type);
    Task<List<SavedWorkspaceArtifact>> GetArtifactsAsync(Guid workspaceId);

    // Dirty tracking
    Task<bool> HasUnsavedChangesAsync();
    Task<string> ComputeArtifactSetHashAsync(Guid workspaceId);
    Task UpdateDirtyStateAsync(Guid workspaceId, string newHash);

    // State queries
    Task<WorkspaceStateDto> GetCurrentStateAsync();
    Task<bool> WorkspaceExistsAsync(Guid workspaceId);

    // Export/Import
    Task<string> ExportJsonAsync(Guid workspaceId);
    Task<SavedWorkspace> ImportJsonAsync(string json);
}
