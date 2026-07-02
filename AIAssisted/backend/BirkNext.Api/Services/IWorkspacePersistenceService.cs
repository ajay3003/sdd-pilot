using BirkNext.Api.Models;
using System.Text.Json.Serialization;

namespace BirkNext.Api.Services;

public class WorkspaceArtifactDto
{
    public ArtifactType ArtifactType { get; set; }
    public string FileName { get; set; } = "";
    public string Content { get; set; } = "";
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
    Task<SavedWorkspace> SaveCurrentAsync(string? name = null);
    Task<SavedWorkspace> SaveAsAsync(string name);
    Task<SavedWorkspace?> LoadAsync(Guid workspaceId);
    Task<List<SavedWorkspace>> ListAsync(string userId);
    Task<SavedWorkspace> RenameAsync(Guid workspaceId, string newName);
    Task<SavedWorkspace> DuplicateAsync(Guid workspaceId, string newName);
    Task DeleteAsync(Guid workspaceId);

    // Auto-save
    Task<SavedWorkspace> AutoSaveAsync(string? generatedName = null);

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
