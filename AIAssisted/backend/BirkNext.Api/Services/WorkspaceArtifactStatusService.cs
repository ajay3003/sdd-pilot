using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>
/// Tracks which artifacts are loaded in a workspace.
/// Provides runtime artifact availability for workflow status computation.
/// </summary>
public interface IWorkspaceArtifactStatusService
{
    /// <summary>
    /// Get current artifact availability for a workspace.
    /// </summary>
    Task<WorkspaceArtifactStatus> GetStatusAsync(Guid workspaceId);

    /// <summary>
    /// Check if a specific artifact type is loaded.
    /// </summary>
    Task<bool> HasArtifactAsync(Guid workspaceId, string artifactType);

    /// <summary>
    /// Check if a specific artifact type is loaded in the current workspace.
    /// </summary>
    Task<bool> HasArtifactAsync(WorkspaceArtifactKind artifactKind);

    /// <summary>
    /// Get a specific artifact from the current workspace.
    /// </summary>
    Task<SavedWorkspaceArtifact?> GetArtifactAsync(WorkspaceArtifactKind artifactKind);

    /// <summary>
    /// Get count of loaded artifacts.
    /// </summary>
    Task<int> GetLoadedArtifactCountAsync(Guid workspaceId);
}

public enum WorkspaceArtifactKind
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel,
    Research
}

/// <summary>
/// Artifact availability snapshot for a workspace.
/// </summary>
public class WorkspaceArtifactStatus
{
    public Guid WorkspaceId { get; set; }
    public bool HasConstitution { get; set; }
    public bool HasSpecification { get; set; }
    public bool HasPlan { get; set; }
    public bool HasTasks { get; set; }
    public bool HasDataModel { get; set; }

    public int LoadedCount =>
        (HasConstitution ? 1 : 0) +
        (HasSpecification ? 1 : 0) +
        (HasPlan ? 1 : 0) +
        (HasTasks ? 1 : 0) +
        (HasDataModel ? 1 : 0);

    public Dictionary<string, bool> ArtifactMap => new()
    {
        { "Constitution", HasConstitution },
        { "Specification", HasSpecification },
        { "Plan", HasPlan },
        { "Tasks", HasTasks },
        { "DataModel", HasDataModel }
    };

    public bool HasAllRequired => HasConstitution && HasSpecification && HasPlan && HasTasks;

    public bool HasMinimalSet => HasSpecification && (HasConstitution || HasPlan || HasTasks);
}

public class WorkspaceArtifactStatusService : IWorkspaceArtifactStatusService
{
    private readonly AppDbContext _db;
    private readonly ILogger<WorkspaceArtifactStatusService> _logger;

    public WorkspaceArtifactStatusService(
        AppDbContext db,
        ILogger<WorkspaceArtifactStatusService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<WorkspaceArtifactStatus> GetStatusAsync(Guid workspaceId)
    {
        try
        {
            var artifacts = await _db.SavedWorkspaceArtifacts
                .Where(a => a.WorkspaceId == workspaceId)
                .Select(a => a.ArtifactType)
                .ToListAsync();

            var artifactSet = new HashSet<ArtifactType>(artifacts);

            return new WorkspaceArtifactStatus
            {
                WorkspaceId = workspaceId,
                HasConstitution = artifactSet.Contains(ArtifactType.Constitution),
                HasSpecification = artifactSet.Contains(ArtifactType.Specification),
                HasPlan = artifactSet.Contains(ArtifactType.Plan),
                HasTasks = artifactSet.Contains(ArtifactType.Tasks),
                HasDataModel = artifactSet.Contains(ArtifactType.DataModel)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting artifact status for workspace {WorkspaceId}", workspaceId);
            return new WorkspaceArtifactStatus { WorkspaceId = workspaceId };
        }
    }

    public async Task<bool> HasArtifactAsync(Guid workspaceId, string artifactTypeName)
    {
        try
        {
            if (!Enum.TryParse<ArtifactType>(artifactTypeName, out var artifactType))
                return false;

            return await _db.SavedWorkspaceArtifacts
                .AnyAsync(a => a.WorkspaceId == workspaceId && a.ArtifactType == artifactType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking artifact {ArtifactType} in workspace {WorkspaceId}", artifactTypeName, workspaceId);
            return false;
        }
    }

    public async Task<bool> HasArtifactAsync(WorkspaceArtifactKind artifactKind)
    {
        var artifact = await GetArtifactAsync(artifactKind);
        return artifact is not null;
    }

    public async Task<SavedWorkspaceArtifact?> GetArtifactAsync(WorkspaceArtifactKind artifactKind)
    {
        try
        {
            if (!Enum.TryParse<ArtifactType>(artifactKind.ToString(), out var artifactType))
                return null;

            var workspaceId = await GetCurrentWorkspaceIdAsync();
            if (workspaceId == Guid.Empty)
                return null;

            return await _db.SavedWorkspaceArtifacts
                .Where(a => a.WorkspaceId == workspaceId && a.ArtifactType == artifactType)
                .OrderByDescending(a => a.UpdatedAt)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting artifact {ArtifactKind} from current workspace", artifactKind);
            return null;
        }
    }

    public async Task<int> GetLoadedArtifactCountAsync(Guid workspaceId)
    {
        try
        {
            return await _db.SavedWorkspaceArtifacts
                .Where(a => a.WorkspaceId == workspaceId)
                .Select(a => a.ArtifactType)
                .Distinct()
                .CountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting artifacts in workspace {WorkspaceId}", workspaceId);
            return 0;
        }
    }

    private async Task<Guid> GetCurrentWorkspaceIdAsync()
    {
        var workspace = await _db.SavedWorkspaces
            .Where(w => !w.IsDeleted)
            .OrderByDescending(w => w.UpdatedAt)
            .FirstOrDefaultAsync();

        return workspace?.Id ?? Guid.Empty;
    }
}
