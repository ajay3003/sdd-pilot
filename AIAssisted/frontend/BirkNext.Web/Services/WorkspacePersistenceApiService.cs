using System.Net.Http.Json;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend HTTP client for backend WorkspacePersistenceService.
/// Handles communication with the backend API for workspace operations.
/// </summary>
public interface IWorkspacePersistenceApiService
{
    // Workspace operations
    Task<SavedWorkspaceDto?> SaveCurrentAsync(string? name = null);
    Task<SavedWorkspaceDto?> SaveAsAsync(string name);
    Task<SavedWorkspaceDto?> LoadAsync(Guid workspaceId);
    Task<List<SavedWorkspaceDto>> ListAsync();
    Task<SavedWorkspaceDto?> RenameAsync(Guid workspaceId, string newName);
    Task<SavedWorkspaceDto?> DuplicateAsync(Guid workspaceId, string newName);
    Task DeleteAsync(Guid workspaceId);

    // Auto-save
    Task<SavedWorkspaceDto?> AutoSaveAsync(string? generatedName = null);

    // Current workspace
    Task<CurrentWorkspaceStateDto?> GetCurrentStateAsync();

    // Export/Import
    Task<string?> ExportJsonAsync(Guid workspaceId);
    Task<SavedWorkspaceDto?> ImportJsonAsync(string json);
}

public class SavedWorkspaceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastOpenedAt { get; set; }
    public int Version { get; set; }
    public string ParserVersion { get; set; } = "1.0";
    public string ReviewContextVersion { get; set; } = "1.0";
    public string? ArtifactSetHash { get; set; }
    public bool AutoSaved { get; set; }
    public bool Favorite { get; set; }
    public List<SavedWorkspaceArtifactDto> Artifacts { get; set; } = new();
}

public class SavedWorkspaceArtifactDto
{
    public string ArtifactType { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? OriginalPath { get; set; }
    public string Content { get; set; } = "";
    public string? ContentHash { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public string ParseVersion { get; set; } = "1.0";
}

public class CurrentWorkspaceStateDto
{
    public Guid? CurrentWorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? ProjectName { get; set; }
    public int ArtifactCount { get; set; }
    public string Status { get; set; } = "NotSaved";
    public DateTimeOffset? LastSavedAt { get; set; }
    public bool IsDirty { get; set; }
}

public class WorkspacePersistenceApiService : IWorkspacePersistenceApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WorkspacePersistenceApiService> _logger;
    private readonly IWorkspaceArtifactRepository _artifactRepository;

    public WorkspacePersistenceApiService(
        HttpClient httpClient,
        ILogger<WorkspacePersistenceApiService> logger,
        IWorkspaceArtifactRepository artifactRepository)
    {
        _httpClient = httpClient;
        _logger = logger;
        _artifactRepository = artifactRepository;
    }

    public async Task<SavedWorkspaceDto?> SaveCurrentAsync(string? name = null)
    {
        try
        {
            var artifacts = GetArtifactsFromRepository();
            var response = await _httpClient.PostAsJsonAsync(
                "api/workspace-persistence/save-current",
                new { name, artifacts });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SaveCurrent failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SavedWorkspaceDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving current workspace");
            return null;
        }
    }

    public async Task<SavedWorkspaceDto?> SaveAsAsync(string name)
    {
        try
        {
            var artifacts = GetArtifactsFromRepository();
            var response = await _httpClient.PostAsJsonAsync(
                "api/workspace-persistence/save-as",
                new { name, artifacts });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SaveAs failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SavedWorkspaceDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving workspace as {Name}", name);
            return null;
        }
    }

    public async Task<SavedWorkspaceDto?> LoadAsync(Guid workspaceId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SavedWorkspaceDto>(
                $"api/workspace-persistence/load/{workspaceId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading workspace {WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task<List<SavedWorkspaceDto>> ListAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("DIAG: [PersistenceApi] ListAsync CALLED");
            var workspaces = await _httpClient.GetFromJsonAsync<List<SavedWorkspaceDto>>(
                "api/workspace-persistence/list");
            System.Diagnostics.Debug.WriteLine($"DIAG: [PersistenceApi] ListAsync returned {workspaces?.Count ?? 0} workspaces");
            if (workspaces != null)
            {
                foreach (var ws in workspaces)
                {
                    System.Diagnostics.Debug.WriteLine($"DIAG: [PersistenceApi]   - Id={ws.Id}, name={ws.Name}, artifacts={ws.Artifacts.Count}");
                }
            }
            return workspaces ?? new();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DIAG: [PersistenceApi] ListAsync error: {ex.Message}");
            return new();
        }
    }

    public async Task<SavedWorkspaceDto?> RenameAsync(Guid workspaceId, string newName)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"api/workspace-persistence/rename/{workspaceId}",
                new { newName });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Rename failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SavedWorkspaceDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming workspace {WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task<SavedWorkspaceDto?> DuplicateAsync(Guid workspaceId, string newName)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"api/workspace-persistence/duplicate/{workspaceId}",
                new { newName });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Duplicate failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SavedWorkspaceDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error duplicating workspace {WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task DeleteAsync(Guid workspaceId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync(
                $"api/workspace-persistence/delete/{workspaceId}");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Delete failed with status {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting workspace {WorkspaceId}", workspaceId);
        }
    }

    public async Task<SavedWorkspaceDto?> AutoSaveAsync(string? generatedName = null)
    {
        try
        {
            _logger.LogInformation("DIAG: [AutoSaveAsync] ENTERED");
            var artifacts = GetArtifactsFromRepository();

            _logger.LogInformation("TRACE: [WorkspacePersistenceApiService.AutoSaveAsync]");
            _logger.LogInformation("  GeneratedName={Name}", generatedName);
            _logger.LogInformation("  RequestArtifacts={Count}", artifacts.Count);

            var request = new { generatedName, artifacts };
            _logger.LogInformation("DIAG: [AutoSaveAsync] Request object created with {ArtifactCount} artifacts", artifacts.Count);

            var response = await _httpClient.PostAsJsonAsync(
                "api/workspace-persistence/auto-save",
                request);

            _logger.LogInformation("DIAG: [AutoSaveAsync] POST returned status {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AutoSave failed with status {StatusCode}", response.StatusCode);
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("  Error response: {Error}", errorContent);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<SavedWorkspaceDto>();
            _logger.LogInformation("DIAG: [AutoSaveAsync] Response received with {ArtifactCount} artifacts", result?.Artifacts.Count ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error auto-saving workspace");
            return null;
        }
    }

    public async Task<CurrentWorkspaceStateDto?> GetCurrentStateAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("DIAG: [PersistenceApi] GetCurrentStateAsync CALLED");
            var result = await _httpClient.GetFromJsonAsync<CurrentWorkspaceStateDto>(
                "api/workspace-persistence/current-state");
            System.Diagnostics.Debug.WriteLine($"DIAG: [PersistenceApi] GetCurrentStateAsync returned: workspaceId={result?.CurrentWorkspaceId}, artifacts={result?.ArtifactCount}, status={result?.Status}");
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DIAG: [PersistenceApi] GetCurrentStateAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> ExportJsonAsync(Guid workspaceId)
    {
        try
        {
            return await _httpClient.GetStringAsync(
                $"api/workspace-persistence/export/{workspaceId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting workspace {WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task<SavedWorkspaceDto?> ImportJsonAsync(string json)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/workspace-persistence/import",
                new { json });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Import failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SavedWorkspaceDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing workspace from JSON");
            return null;
        }
    }

    private List<SavedWorkspaceArtifactDto> GetArtifactsFromRepository()
    {
        var artifacts = _artifactRepository.GetAllArtifacts()
            .Select(item => new SavedWorkspaceArtifactDto
            {
                ArtifactType = item.Type.ToString(),
                FileName = item.Artifact.FileName ?? $"{item.Type}",
                OriginalPath = item.Artifact.SourcePath,
                Content = item.Artifact.Text,
                ContentHash = null,
                Encoding = "utf-8",
                ParseVersion = "1.0"
            })
            .ToList();

        _logger.LogInformation("DIAG: [GetArtifactsFromRepository] Loaded {Count} artifacts from repository", artifacts.Count);
        foreach (var artifact in artifacts)
        {
            _logger.LogInformation("  - {Type}: {ContentLength} bytes", artifact.ArtifactType, artifact.Content?.Length ?? 0);
        }

        return artifacts;
    }
}
