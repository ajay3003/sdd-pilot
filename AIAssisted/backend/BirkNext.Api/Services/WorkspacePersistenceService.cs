using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BirkNext.Api.Services;

public class WorkspacePersistenceService : IWorkspacePersistenceService
{
    private readonly AppDbContext _db;
    private readonly ILogger<WorkspacePersistenceService> _logger;
    private Guid? _currentWorkspaceId;
    private string? _currentUserId = "default-user"; // TODO: Get from auth context

    public WorkspacePersistenceService(AppDbContext db, ILogger<WorkspacePersistenceService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<SavedWorkspace> SaveCurrentAsync(string? name = null)
    {
        if (!_currentWorkspaceId.HasValue)
        {
            return await SaveAsAsync(name ?? $"Workspace_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        }

        var workspace = await _db.SavedWorkspaces.FindAsync(_currentWorkspaceId);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Current workspace {_currentWorkspaceId} not found");
        }

        if (!string.IsNullOrWhiteSpace(name) && name != workspace.Name)
        {
            workspace.Name = name;
        }

        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        workspace.AutoSaved = false;
        workspace.ArtifactSetHash = await ComputeArtifactSetHashAsync(_currentWorkspaceId.Value);

        _db.SavedWorkspaces.Update(workspace);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Saved workspace {WorkspaceId} with name {Name}", workspace.Id, workspace.Name);
        return workspace;
    }

    public async Task<SavedWorkspace> SaveAsAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Workspace name cannot be empty", nameof(name));
        }

        var workspace = new SavedWorkspace
        {
            Id = Guid.NewGuid(),
            UserId = _currentUserId ?? "default-user",
            Name = name,
            ProjectName = "", // TODO: Get from current project context
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AutoSaved = false
        };

        // Copy current artifacts to new workspace
        var artifacts = await GetCurrentArtifactsFromSessionAsync();
        if (artifacts.Any())
        {
            foreach (var artifact in artifacts)
            {
                var saved = new SavedWorkspaceArtifact
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    ArtifactType = artifact.ArtifactType,
                    FileName = artifact.FileName,
                    Content = artifact.Content,
                    ContentHash = ComputeContentHash(artifact.Content),
                    Encoding = "utf-8",
                    LastModified = DateTimeOffset.UtcNow,
                    ParseVersion = "1.0",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                workspace.Artifacts.Add(saved);
            }
        }

        workspace.ArtifactSetHash = await ComputeArtifactSetHashAsync(workspace.Id);

        _db.SavedWorkspaces.Add(workspace);
        await _db.SaveChangesAsync();

        _currentWorkspaceId = workspace.Id;
        _logger.LogInformation("Created new workspace {WorkspaceId} with name {Name}", workspace.Id, workspace.Name);
        return workspace;
    }

    public async Task<SavedWorkspace?> LoadAsync(Guid workspaceId)
    {
        var workspace = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .FirstOrDefaultAsync(w => w.Id == workspaceId && !w.IsDeleted);

        if (workspace == null)
        {
            _logger.LogWarning("Workspace {WorkspaceId} not found", workspaceId);
            return null;
        }

        workspace.LastOpenedAt = DateTimeOffset.UtcNow;
        _db.SavedWorkspaces.Update(workspace);
        await _db.SaveChangesAsync();

        _currentWorkspaceId = workspace.Id;
        _logger.LogInformation("Loaded workspace {WorkspaceId} with name {Name}", workspace.Id, workspace.Name);
        _logger.LogInformation("Restoring {ArtifactCount} artifacts from workspace", workspace.Artifacts.Count);

        // Artifacts are included in the returned workspace object
        // Frontend will restore them to WorkspaceArtifactRepository and rebuild ReviewContext
        return workspace;
    }

    public async Task<List<SavedWorkspace>> ListAsync(string userId)
    {
        return await _db.SavedWorkspaces
            .Where(w => w.UserId == userId && !w.IsDeleted)
            .OrderByDescending(w => w.UpdatedAt)
            .ToListAsync();
    }

    public async Task<SavedWorkspace> RenameAsync(Guid workspaceId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Workspace name cannot be empty", nameof(newName));
        }

        var workspace = await _db.SavedWorkspaces.FindAsync(workspaceId);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found");
        }

        workspace.Name = newName;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;

        _db.SavedWorkspaces.Update(workspace);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Renamed workspace {WorkspaceId} to {Name}", workspace.Id, newName);
        return workspace;
    }

    public async Task<SavedWorkspace> DuplicateAsync(Guid workspaceId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Workspace name cannot be empty", nameof(newName));
        }

        var original = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .FirstOrDefaultAsync(w => w.Id == workspaceId && !w.IsDeleted);

        if (original == null)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found");
        }

        var duplicate = new SavedWorkspace
        {
            Id = Guid.NewGuid(),
            UserId = original.UserId,
            Name = newName,
            ProjectName = original.ProjectName,
            Description = original.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = original.Version,
            ParserVersion = original.ParserVersion,
            ReviewContextVersion = original.ReviewContextVersion,
            AutoSaved = false
        };

        // Copy artifacts
        foreach (var artifact in original.Artifacts)
        {
            var copied = new SavedWorkspaceArtifact
            {
                Id = Guid.NewGuid(),
                WorkspaceId = duplicate.Id,
                ArtifactType = artifact.ArtifactType,
                FileName = artifact.FileName,
                OriginalPath = artifact.OriginalPath,
                Content = artifact.Content,
                ContentHash = artifact.ContentHash,
                Encoding = artifact.Encoding,
                LastModified = artifact.LastModified,
                ParseVersion = artifact.ParseVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            duplicate.Artifacts.Add(copied);
        }

        duplicate.ArtifactSetHash = await ComputeArtifactSetHashAsync(duplicate.Id);

        _db.SavedWorkspaces.Add(duplicate);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Duplicated workspace {OriginalId} to {DuplicateId} with name {Name}",
            original.Id, duplicate.Id, newName);
        return duplicate;
    }

    public async Task DeleteAsync(Guid workspaceId)
    {
        var workspace = await _db.SavedWorkspaces.FindAsync(workspaceId);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found");
        }

        workspace.IsDeleted = true;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;

        _db.SavedWorkspaces.Update(workspace);
        await _db.SaveChangesAsync();

        if (_currentWorkspaceId == workspaceId)
        {
            _currentWorkspaceId = null;
        }

        _logger.LogInformation("Soft-deleted workspace {WorkspaceId}", workspaceId);
    }

    public async Task<SavedWorkspace> AutoSaveAsync(string? generatedName = null)
    {
        if (!_currentWorkspaceId.HasValue)
        {
            var name = generatedName ?? $"Auto_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var workspace = await SaveAsAsync(name);
            workspace.AutoSaved = true;
            _db.SavedWorkspaces.Update(workspace);
            await _db.SaveChangesAsync();
            return workspace;
        }

        var current = await _db.SavedWorkspaces.FindAsync(_currentWorkspaceId);
        if (current == null)
        {
            throw new InvalidOperationException($"Current workspace {_currentWorkspaceId} not found");
        }

        current.UpdatedAt = DateTimeOffset.UtcNow;
        current.AutoSaved = true;
        current.ArtifactSetHash = await ComputeArtifactSetHashAsync(_currentWorkspaceId.Value);

        _db.SavedWorkspaces.Update(current);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Auto-saved workspace {WorkspaceId}", current.Id);
        return current;
    }

    public async Task SetCurrentWorkspaceAsync(Guid workspaceId)
    {
        var exists = await _db.SavedWorkspaces.AnyAsync(w => w.Id == workspaceId && !w.IsDeleted);
        if (!exists)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found");
        }

        _currentWorkspaceId = workspaceId;
        _logger.LogInformation("Set current workspace to {WorkspaceId}", workspaceId);
    }

    public async Task<Guid?> GetCurrentWorkspaceIdAsync()
    {
        return _currentWorkspaceId;
    }

    public async Task ClearCurrentWorkspaceAsync()
    {
        _currentWorkspaceId = null;
        _logger.LogInformation("Cleared current workspace");
        await Task.CompletedTask;
    }

    public async Task SaveArtifactAsync(Guid workspaceId, WorkspaceArtifactDto artifact)
    {
        var workspace = await _db.SavedWorkspaces.FindAsync(workspaceId);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found");
        }

        var existing = await _db.SavedWorkspaceArtifacts
            .FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.ArtifactType == artifact.ArtifactType);

        if (existing != null)
        {
            existing.Content = artifact.Content;
            existing.FileName = artifact.FileName;
            existing.ContentHash = ComputeContentHash(artifact.Content);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            _db.SavedWorkspaceArtifacts.Update(existing);
        }
        else
        {
            var saved = new SavedWorkspaceArtifact
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                ArtifactType = artifact.ArtifactType,
                FileName = artifact.FileName,
                Content = artifact.Content,
                ContentHash = ComputeContentHash(artifact.Content),
                Encoding = "utf-8",
                LastModified = DateTimeOffset.UtcNow,
                ParseVersion = "1.0",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.SavedWorkspaceArtifacts.Add(saved);
        }

        await _db.SaveChangesAsync();

        workspace.ArtifactSetHash = await ComputeArtifactSetHashAsync(workspaceId);
        _db.SavedWorkspaces.Update(workspace);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Saved artifact {ArtifactType} for workspace {WorkspaceId}",
            artifact.ArtifactType, workspaceId);
    }

    public async Task<WorkspaceArtifactDto?> GetArtifactAsync(Guid workspaceId, ArtifactType type)
    {
        var artifact = await _db.SavedWorkspaceArtifacts
            .FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.ArtifactType == type);

        if (artifact == null)
        {
            return null;
        }

        return new WorkspaceArtifactDto
        {
            ArtifactType = artifact.ArtifactType,
            FileName = artifact.FileName,
            Content = artifact.Content
        };
    }

    public async Task<List<SavedWorkspaceArtifact>> GetArtifactsAsync(Guid workspaceId)
    {
        return await _db.SavedWorkspaceArtifacts
            .Where(a => a.WorkspaceId == workspaceId)
            .ToListAsync();
    }

    public async Task<bool> HasUnsavedChangesAsync()
    {
        if (!_currentWorkspaceId.HasValue)
        {
            return false;
        }

        var workspace = await _db.SavedWorkspaces.FindAsync(_currentWorkspaceId);
        if (workspace == null)
        {
            return false;
        }

        var currentHash = await ComputeArtifactSetHashAsync(_currentWorkspaceId.Value);
        return currentHash != workspace.ArtifactSetHash;
    }

    public async Task<string> ComputeArtifactSetHashAsync(Guid workspaceId)
    {
        var artifacts = await _db.SavedWorkspaceArtifacts
            .Where(a => a.WorkspaceId == workspaceId)
            .OrderBy(a => a.ArtifactType)
            .ToListAsync();

        using (var sha256 = SHA256.Create())
        {
            var combined = string.Concat(artifacts.Select(a => a.ContentHash ?? ""));
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(hash);
        }
    }

    public async Task UpdateDirtyStateAsync(Guid workspaceId, string newHash)
    {
        var workspace = await _db.SavedWorkspaces.FindAsync(workspaceId);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found");
        }

        workspace.ArtifactSetHash = newHash;
        workspace.UpdatedAt = DateTimeOffset.UtcNow;

        _db.SavedWorkspaces.Update(workspace);
        await _db.SaveChangesAsync();
    }

    public async Task<WorkspaceStateDto> GetCurrentStateAsync()
    {
        if (!_currentWorkspaceId.HasValue)
        {
            return new WorkspaceStateDto
            {
                CurrentWorkspaceId = null,
                Status = WorkspaceStatus.NotSaved,
                ArtifactCount = 0,
                IsDirty = false
            };
        }

        var workspace = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .FirstOrDefaultAsync(w => w.Id == _currentWorkspaceId);

        if (workspace == null)
        {
            return new WorkspaceStateDto
            {
                CurrentWorkspaceId = null,
                Status = WorkspaceStatus.NotSaved,
                ArtifactCount = 0,
                IsDirty = false
            };
        }

        var isDirty = await HasUnsavedChangesAsync();
        var status = workspace.AutoSaved ? WorkspaceStatus.AutoSaved :
                     isDirty ? WorkspaceStatus.UnsavedChanges :
                     WorkspaceStatus.Saved;

        return new WorkspaceStateDto
        {
            CurrentWorkspaceId = workspace.Id,
            WorkspaceName = workspace.Name,
            ProjectName = workspace.ProjectName,
            ArtifactCount = workspace.Artifacts.Count,
            Status = status,
            LastSavedAt = workspace.UpdatedAt,
            IsDirty = isDirty
        };
    }

    public async Task<bool> WorkspaceExistsAsync(Guid workspaceId)
    {
        return await _db.SavedWorkspaces.AnyAsync(w => w.Id == workspaceId && !w.IsDeleted);
    }

    public async Task<string> ExportJsonAsync(Guid workspaceId)
    {
        var workspace = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .FirstOrDefaultAsync(w => w.Id == workspaceId && !w.IsDeleted);

        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace {workspaceId} not found");
        }

        var export = new
        {
            schemaVersion = "1.0",
            workspace = new
            {
                id = workspace.Id,
                name = workspace.Name,
                projectName = workspace.ProjectName,
                description = workspace.Description,
                createdAt = workspace.CreatedAt,
                updatedAt = workspace.UpdatedAt,
                version = workspace.Version,
                parserVersion = workspace.ParserVersion,
                reviewContextVersion = workspace.ReviewContextVersion
            },
            artifacts = workspace.Artifacts.Select(a => new
            {
                artifactType = a.ArtifactType.ToString(),
                fileName = a.FileName,
                originalPath = a.OriginalPath,
                content = a.Content,
                contentHash = a.ContentHash,
                encoding = a.Encoding,
                parseVersion = a.ParseVersion
            }).ToList(),
            exportedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        _logger.LogInformation("Exported workspace {WorkspaceId} to JSON", workspaceId);
        return json;
    }

    public async Task<SavedWorkspace> ImportJsonAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var schemaVersion = root.GetProperty("schemaVersion").GetString();
        if (schemaVersion != "1.0")
        {
            throw new InvalidOperationException($"Unsupported schema version: {schemaVersion}");
        }

        var workspaceObj = root.GetProperty("workspace");
        var workspace = new SavedWorkspace
        {
            Id = Guid.NewGuid(),
            UserId = _currentUserId ?? "default-user",
            Name = workspaceObj.GetProperty("name").GetString() ?? "Imported",
            ProjectName = workspaceObj.GetProperty("projectName").GetString() ?? "",
            Description = workspaceObj.GetProperty("description").GetString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AutoSaved = false
        };

        var artifacts = root.GetProperty("artifacts");
        foreach (var artifactObj in artifacts.EnumerateArray())
        {
            var typeStr = artifactObj.GetProperty("artifactType").GetString();
            if (!Enum.TryParse<ArtifactType>(typeStr, out var type))
            {
                continue;
            }

            var content = artifactObj.GetProperty("content").GetString() ?? "";
            var artifact = new SavedWorkspaceArtifact
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                ArtifactType = type,
                FileName = artifactObj.GetProperty("fileName").GetString() ?? "",
                OriginalPath = artifactObj.GetProperty("originalPath").GetString(),
                Content = content,
                ContentHash = ComputeContentHash(content),
                Encoding = artifactObj.GetProperty("encoding").GetString() ?? "utf-8",
                ParseVersion = artifactObj.GetProperty("parseVersion").GetString() ?? "1.0",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            workspace.Artifacts.Add(artifact);
        }

        workspace.ArtifactSetHash = await ComputeArtifactSetHashAsync(workspace.Id);

        _db.SavedWorkspaces.Add(workspace);
        await _db.SaveChangesAsync();

        _currentWorkspaceId = workspace.Id;
        _logger.LogInformation("Imported workspace {WorkspaceId} from JSON", workspace.Id);
        return workspace;
    }

    // Helper methods
    private string ComputeContentHash(string content)
    {
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(hash);
        }
    }

    private async Task<List<WorkspaceArtifactDto>> GetCurrentArtifactsFromSessionAsync()
    {
        // TODO: Get from WorkspaceSession service
        return await Task.FromResult(new List<WorkspaceArtifactDto>());
    }
}
