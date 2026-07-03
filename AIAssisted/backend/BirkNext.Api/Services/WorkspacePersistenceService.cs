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

    public async Task<SavedWorkspace> SaveCurrentAsync(string? name = null, List<WorkspaceArtifactDto>? artifacts = null)
    {
        if (!_currentWorkspaceId.HasValue)
        {
            return await SaveAsAsync(name ?? $"Workspace_{DateTime.UtcNow:yyyyMMdd_HHmmss}", artifacts ?? new());
        }

        var workspace = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .FirstOrDefaultAsync(w => w.Id == _currentWorkspaceId && !w.IsDeleted);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Current workspace {_currentWorkspaceId} not found");
        }

        if (!string.IsNullOrWhiteSpace(name) && name != workspace.Name)
        {
            workspace.Name = name;
        }

        // Update artifacts if provided
        if (artifacts != null && artifacts.Any())
        {
            // Remove existing artifacts
            _db.SavedWorkspaceArtifacts.RemoveRange(workspace.Artifacts);

            // Add new artifacts
            foreach (var artifact in artifacts)
            {
                if (!string.IsNullOrWhiteSpace(artifact.Content))
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
        }

        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        workspace.AutoSaved = false;
        workspace.ArtifactSetHash = await ComputeArtifactSetHashAsync(_currentWorkspaceId.Value);

        _db.SavedWorkspaces.Update(workspace);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Saved workspace {WorkspaceId} with name {Name} and {ArtifactCount} artifacts",
            workspace.Id, workspace.Name, workspace.Artifacts.Count);
        return workspace;
    }

    public async Task<SavedWorkspace> SaveAsAsync(string name, List<WorkspaceArtifactDto>? artifacts = null)
    {
        _logger.LogInformation($"DIAG: [SaveAs] ENTERED with name={name}, artifactCount={artifacts?.Count ?? 0}");

        if (artifacts != null && artifacts.Count > 0)
        {
            _logger.LogInformation($"DIAG: [SaveAs] Artifacts received:");
            foreach (var art in artifacts)
            {
                _logger.LogInformation($"      - {art.ArtifactType}: {art.Content?.Length ?? 0} bytes, fileName={art.FileName}");
            }
        }

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
        _logger.LogInformation($"DIAG: [SaveAs] Created SavedWorkspace entity Id={workspace.Id}");

        // Copy artifacts from request (frontend already has them from WorkspaceArtifactRepository)
        if (artifacts != null && artifacts.Any())
        {
            _logger.LogInformation($"DIAG: [SaveAs] Processing {artifacts.Count} artifacts");
            int addedCount = 0;
            foreach (var artifact in artifacts)
            {
                if (!string.IsNullOrWhiteSpace(artifact.Content))
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
                    addedCount++;
                    _logger.LogInformation($"DIAG: [SaveAs] Added artifact {artifact.ArtifactType} ({artifact.Content.Length} bytes)");
                }
                else
                {
                    _logger.LogWarning($"DIAG: [SaveAs] Skipped artifact {artifact.ArtifactType} - empty content");
                }
            }
            _logger.LogInformation($"DIAG: [SaveAs] Total artifacts added: {addedCount}, Total in entity: {workspace.Artifacts.Count}");
        }
        else
        {
            _logger.LogInformation($"DIAG: [SaveAs] No artifacts provided");
        }

        workspace.ArtifactSetHash = await ComputeArtifactSetHashAsync(workspace.Id);

        _db.SavedWorkspaces.Add(workspace);
        await _db.SaveChangesAsync();
        _logger.LogInformation($"DIAG: [SaveAs] SaveChangesAsync completed, saved to DB");

        _currentWorkspaceId = workspace.Id;
        _logger.LogInformation($"DIAG: [SaveAs] RETURNING WorkspaceId={workspace.Id}, name={workspace.Name}, artifacts={workspace.Artifacts.Count}");
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
        _logger.LogInformation($"DIAG: [List] ENTERED for userId={userId}");
        var workspaces = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .Where(w => w.UserId == userId && !w.IsDeleted)
            .OrderByDescending(w => w.UpdatedAt)
            .ToListAsync();

        _logger.LogInformation($"DIAG: [List] Found {workspaces.Count} workspaces");
        foreach (var ws in workspaces)
        {
            _logger.LogInformation($"DIAG: [List]   - Id={ws.Id}, name={ws.Name}, artifacts={ws.Artifacts.Count}, autoSaved={ws.AutoSaved}");
        }
        return workspaces;
    }

    public async Task<SavedWorkspace> RenameAsync(Guid workspaceId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Workspace name cannot be empty", nameof(newName));
        }

        var workspace = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .FirstOrDefaultAsync(w => w.Id == workspaceId && !w.IsDeleted);
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

    public async Task<SavedWorkspace> AutoSaveAsync(string? generatedName = null, List<WorkspaceArtifactDto>? artifacts = null)
    {
        _logger.LogInformation($"DIAG: [AutoSaveAsync] ENTERED");
        _logger.LogInformation($"DIAG: [AutoSaveAsync]   generatedName={generatedName}");
        _logger.LogInformation($"DIAG: [AutoSaveAsync]   artifactCount={artifacts?.Count ?? 0}");
        _logger.LogInformation($"DIAG: [AutoSaveAsync]   currentWorkspaceId={_currentWorkspaceId}");

        if (artifacts != null && artifacts.Count > 0)
        {
            _logger.LogInformation($"DIAG: [AutoSaveAsync] Artifacts provided:");
            foreach (var art in artifacts)
            {
                _logger.LogInformation($"      - {art.ArtifactType}: {art.Content?.Length ?? 0} bytes, fileName={art.FileName}");
            }
        }

        _logger.LogInformation($"TRACE: AutoSaveAsync entered, currentWorkspaceId={_currentWorkspaceId}, artifactCount={artifacts?.Count ?? 0}");
        if (!_currentWorkspaceId.HasValue)
        {
            _logger.LogInformation("TRACE: No current workspace ID, calling SaveAsAsync");
            var name = generatedName ?? $"Auto_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var workspace = await SaveAsAsync(name, artifacts ?? new());
            workspace.AutoSaved = true;
            _db.SavedWorkspaces.Update(workspace);
            await _db.SaveChangesAsync();
            _logger.LogInformation($"TRACE: AutoSaveAsync created new workspace {workspace.Id} with {workspace.Artifacts.Count} artifacts");
            _logger.LogInformation($"DIAG: [AutoSaveAsync] Returned workspace with {workspace.Artifacts.Count} artifacts");
            return workspace;
        }

        _logger.LogInformation($"TRACE: Updating existing workspace {_currentWorkspaceId}");
        var current = await _db.SavedWorkspaces
            .Include(w => w.Artifacts)
            .FirstOrDefaultAsync(w => w.Id == _currentWorkspaceId && !w.IsDeleted);
        if (current == null)
        {
            _logger.LogError($"TRACE: Current workspace {_currentWorkspaceId} not found");
            throw new InvalidOperationException($"Current workspace {_currentWorkspaceId} not found");
        }

        // Update artifacts if provided
        if (artifacts != null && artifacts.Any())
        {
            _logger.LogInformation($"TRACE: Replacing {current.Artifacts.Count} artifacts with {artifacts.Count} new artifacts");
            // Remove existing artifacts
            _db.SavedWorkspaceArtifacts.RemoveRange(current.Artifacts);
            current.Artifacts.Clear();

            // Add new artifacts
            foreach (var artifact in artifacts)
            {
                if (!string.IsNullOrWhiteSpace(artifact.Content))
                {
                    var saved = new SavedWorkspaceArtifact
                    {
                        Id = Guid.NewGuid(),
                        WorkspaceId = current.Id,
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
                    current.Artifacts.Add(saved);
                }
            }
        }

        current.UpdatedAt = DateTimeOffset.UtcNow;
        current.AutoSaved = true;
        current.ArtifactSetHash = await ComputeArtifactSetHashAsync(_currentWorkspaceId.Value);

        _db.SavedWorkspaces.Update(current);
        await _db.SaveChangesAsync();

        _logger.LogInformation($"TRACE: Auto-saved workspace {current.Id} with {current.Artifacts.Count} artifacts");
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

    public Task<Guid?> GetCurrentWorkspaceIdAsync()
    {
        return Task.FromResult(_currentWorkspaceId);
    }

    public Task ClearCurrentWorkspaceAsync()
    {
        _currentWorkspaceId = null;
        _logger.LogInformation("Cleared current workspace");
        return Task.CompletedTask;
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
        // Check in-memory first (for within-request state)
        var workspaceIdToUse = _currentWorkspaceId;

        // If not in memory, try to load from database (for cross-request persistence)
        if (!workspaceIdToUse.HasValue)
        {
            var lastWorkspace = await _db.SavedWorkspaces
                .Where(w => w.UserId == (_currentUserId ?? "default-user") && !w.IsDeleted)
                .OrderByDescending(w => w.UpdatedAt)
                .FirstOrDefaultAsync();

            if (lastWorkspace != null)
            {
                workspaceIdToUse = lastWorkspace.Id;
                _logger.LogInformation($"DIAG: [GetCurrentState] Loaded workspace from database: {workspaceIdToUse}");
            }
        }

        _logger.LogInformation($"DIAG: [GetCurrentState] ENTERED, _currentWorkspaceId={_currentWorkspaceId}, workspaceIdToUse={workspaceIdToUse}");
        if (!workspaceIdToUse.HasValue)
        {
            _logger.LogInformation("DIAG: [GetCurrentState] No current workspace ID, returning NotSaved");
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
            .FirstOrDefaultAsync(w => w.Id == workspaceIdToUse);

        if (workspace == null)
        {
            _logger.LogInformation($"DIAG: [GetCurrentState] Workspace {workspaceIdToUse} not found in DB, returning NotSaved");
            return new WorkspaceStateDto
            {
                CurrentWorkspaceId = null,
                Status = WorkspaceStatus.NotSaved,
                ArtifactCount = 0,
                IsDirty = false
            };
        }

        _logger.LogInformation($"DIAG: [GetCurrentState] Found workspace {workspace.Id}, name={workspace.Name}, artifacts={workspace.Artifacts.Count}, autoSaved={workspace.AutoSaved}");
        var isDirty = await HasUnsavedChangesAsync();
        var status = workspace.AutoSaved ? WorkspaceStatus.AutoSaved :
                     isDirty ? WorkspaceStatus.UnsavedChanges :
                     WorkspaceStatus.Saved;

        _logger.LogInformation($"DIAG: [GetCurrentState] RETURNING status={status}, artifactCount={workspace.Artifacts.Count}, isDirty={isDirty}");
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
                reviewContextVersion = workspace.ReviewContextVersion,
                favorite = workspace.Favorite
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
        try
        {
            // Parse JSON with validation
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Validate schema version
            if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement))
            {
                throw new InvalidOperationException("Missing required field: schemaVersion. Export file must contain schema version.");
            }

            var schemaVersion = schemaVersionElement.GetString();
            if (string.IsNullOrEmpty(schemaVersion))
            {
                throw new InvalidOperationException("Schema version cannot be empty");
            }

            if (schemaVersion != "1.0")
            {
                throw new InvalidOperationException(
                    $"Unsupported schema version: {schemaVersion}. This application supports schema version 1.0. " +
                    "Please export from a compatible version of the application.");
            }

            // Validate workspace object
            if (!root.TryGetProperty("workspace", out var workspaceObj))
            {
                throw new InvalidOperationException("Missing required field: workspace");
            }

            // Validate required workspace fields
            if (!workspaceObj.TryGetProperty("name", out var nameElement) || string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                throw new InvalidOperationException("Missing or empty required field: workspace.name");
            }

            var workspace = new SavedWorkspace
            {
                Id = Guid.NewGuid(),
                UserId = _currentUserId ?? "default-user",
                Name = workspaceObj.GetProperty("name").GetString() ?? "Imported",
                ProjectName = workspaceObj.GetProperty("projectName").GetString() ?? "",
                Description = workspaceObj.GetProperty("description").GetString(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                AutoSaved = false,
                Favorite = workspaceObj.TryGetProperty("favorite", out var favElem) && favElem.GetBoolean()
            };

            // Import artifacts with validation
            if (root.TryGetProperty("artifacts", out var artifacts))
            {
                var artifactCount = 0;
                var skippedCount = 0;

                foreach (var artifactObj in artifacts.EnumerateArray())
                {
                    try
                    {
                        // Validate artifact type
                        if (!artifactObj.TryGetProperty("artifactType", out var typeElement))
                        {
                            _logger.LogWarning("Skipping artifact: missing artifactType");
                            skippedCount++;
                            continue;
                        }

                        var typeStr = typeElement.GetString();
                        if (!Enum.TryParse<ArtifactType>(typeStr, out var type))
                        {
                            _logger.LogWarning("Skipping artifact: unsupported type {ArtifactType}", typeStr);
                            skippedCount++;
                            continue;
                        }

                        // Validate content
                        if (!artifactObj.TryGetProperty("content", out var contentElement))
                        {
                            _logger.LogWarning("Skipping artifact: missing content for type {ArtifactType}", typeStr);
                            skippedCount++;
                            continue;
                        }

                        var content = contentElement.GetString() ?? "";
                        var artifact = new SavedWorkspaceArtifact
                        {
                            Id = Guid.NewGuid(),
                            WorkspaceId = workspace.Id,
                            ArtifactType = type,
                            FileName = artifactObj.TryGetProperty("fileName", out var fnElem)
                                ? fnElem.GetString() ?? ""
                                : "",
                            OriginalPath = artifactObj.TryGetProperty("originalPath", out var opElem)
                                ? opElem.GetString()
                                : null,
                            Content = content,
                            ContentHash = ComputeContentHash(content),
                            Encoding = artifactObj.TryGetProperty("encoding", out var encElem)
                                ? encElem.GetString() ?? "utf-8"
                                : "utf-8",
                            ParseVersion = artifactObj.TryGetProperty("parseVersion", out var pvElem)
                                ? pvElem.GetString() ?? "1.0"
                                : "1.0",
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        workspace.Artifacts.Add(artifact);
                        artifactCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing artifact during import");
                        skippedCount++;
                    }
                }

                _logger.LogInformation("Imported {ArtifactCount} artifacts, skipped {SkippedCount}",
                    artifactCount, skippedCount);
            }

            workspace.ArtifactSetHash = await ComputeArtifactSetHashAsync(workspace.Id);

            _db.SavedWorkspaces.Add(workspace);
            await _db.SaveChangesAsync();

            _currentWorkspaceId = workspace.Id;
            _logger.LogInformation("Imported workspace {WorkspaceId} ({Name}) with {ArtifactCount} artifacts",
                workspace.Id, workspace.Name, workspace.Artifacts.Count);
            return workspace;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid JSON format in import file. Please ensure the file is valid JSON.", ex);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            throw new InvalidOperationException("Missing required field in import file. Please ensure all required fields are present.", ex);
        }
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

    private SavedWorkspaceDto MapWorkspaceToDto(SavedWorkspace workspace)
    {
        return new SavedWorkspaceDto
        {
            Id = workspace.Id,
            UserId = workspace.UserId,
            Name = workspace.Name,
            ProjectName = workspace.ProjectName,
            Description = workspace.Description,
            CreatedAt = workspace.CreatedAt,
            UpdatedAt = workspace.UpdatedAt,
            LastOpenedAt = workspace.LastOpenedAt,
            Version = workspace.Version,
            ParserVersion = workspace.ParserVersion,
            ReviewContextVersion = workspace.ReviewContextVersion,
            ArtifactSetHash = workspace.ArtifactSetHash,
            AutoSaved = workspace.AutoSaved,
            Favorite = workspace.Favorite,
            Artifacts = workspace.Artifacts
                .Select(a => new SavedWorkspaceArtifactResponseDto
                {
                    ArtifactType = a.ArtifactType.ToString(),
                    FileName = a.FileName,
                    OriginalPath = a.OriginalPath,
                    Content = a.Content,
                    ContentHash = a.ContentHash,
                    Encoding = a.Encoding,
                    ParseVersion = a.ParseVersion
                })
                .ToList()
        };
    }
}
