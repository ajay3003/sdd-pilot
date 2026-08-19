using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/workspace-persistence")]
public class WorkspacePersistenceController : ControllerBase
{
    private readonly IWorkspacePersistenceService _service;
    private readonly ILogger<WorkspacePersistenceController> _logger;

    public WorkspacePersistenceController(
        IWorkspacePersistenceService service,
        ILogger<WorkspacePersistenceController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpPost("save-current")]
    public async Task<ActionResult<SavedWorkspaceDto>> SaveCurrent([FromBody] SaveRequest? request = null)
    {
        try
        {
            var result = await _service.SaveCurrentAsync(request?.Name, request?.Artifacts ?? new());
            var dto = MapWorkspaceToDto(result);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving current workspace");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("save-as")]
    public async Task<ActionResult<SavedWorkspaceDto>> SaveAs([FromBody] SaveAsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        try
        {
            var result = await _service.SaveAsAsync(request.Name, request.Artifacts ?? new());
            var dto = MapWorkspaceToDto(result);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving workspace as {Name}", request.Name);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("load/{workspaceId}")]
    public async Task<ActionResult<SavedWorkspaceDto>> Load(Guid workspaceId)
    {
        try
        {
            var result = await _service.LoadAsync(workspaceId);
            if (result == null)
            {
                return NotFound(new { error = "Workspace not found" });
            }

            var dto = MapWorkspaceToDto(result);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("list")]
    public async Task<ActionResult<List<SavedWorkspaceDto>>> List()
    {
        try
        {
            _logger.LogInformation("DIAG: [Controller] List ENTERED");
            // TODO: Get actual userId from auth context
            var result = await _service.ListAsync("default-user");
            _logger.LogInformation($"DIAG: [Controller] ListAsync returned {result.Count} workspaces");
            foreach (var ws in result)
            {
                _logger.LogInformation($"DIAG: [Controller]   - Id={ws.Id}, name={ws.Name}, artifacts={ws.Artifacts.Count}");
            }
            var dtos = result.Select(MapWorkspaceToDto).ToList();
            _logger.LogInformation($"DIAG: [Controller] List returning {dtos.Count} DTOs");
            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DIAG: [Controller] Error listing workspaces");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("rename/{workspaceId}")]
    public async Task<ActionResult<SavedWorkspaceDto>> Rename(Guid workspaceId, [FromBody] RenameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.NewName))
        {
            return BadRequest(new { error = "NewName is required" });
        }

        try
        {
            var result = await _service.RenameAsync(workspaceId, request.NewName);
            var dto = MapWorkspaceToDto(result);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("duplicate/{workspaceId}")]
    public async Task<ActionResult<SavedWorkspaceDto>> Duplicate(Guid workspaceId, [FromBody] DuplicateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.NewName))
        {
            return BadRequest(new { error = "NewName is required" });
        }

        try
        {
            var result = await _service.DuplicateAsync(workspaceId, request.NewName);
            var dto = MapWorkspaceToDto(result);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error duplicating workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("delete/{workspaceId}")]
    public async Task<ActionResult> Delete(Guid workspaceId)
    {
        try
        {
            await _service.DeleteAsync(workspaceId);
            return Ok(new { message = "Workspace deleted" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("auto-save")]
    public async Task<ActionResult<SavedWorkspaceDto>> AutoSave([FromBody] AutoSaveRequest? request = null)
    {
        try
        {
            _logger.LogInformation("TRACE: [WorkspacePersistenceController.AutoSave]");
            _logger.LogInformation("  ProjectName={Project}", request?.ProjectName);
            _logger.LogInformation("  RequestArtifacts={Count}", request?.Artifacts?.Count ?? 0);

            var result = await _service.AutoSaveAsync(request?.GeneratedName, request?.ProjectName, request?.Artifacts ?? new());
            _logger.LogInformation("  ResponseArtifacts={Count}", result.Artifacts.Count);

            var dto = MapWorkspaceToDto(result);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error auto-saving workspace");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("current-state")]
    public async Task<ActionResult<WorkspaceStateDto>> GetCurrentState()
    {
        try
        {
            _logger.LogInformation("DIAG: [Controller] GetCurrentState ENTERED");
            var result = await _service.GetCurrentStateAsync();
            _logger.LogInformation($"DIAG: [Controller] GetCurrentState returned: workspaceId={result?.CurrentWorkspaceId}, artifacts={result?.ArtifactCount}");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DIAG: [Controller] Error getting current workspace state");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("export/{workspaceId}")]
    public async Task<ActionResult<string>> Export(Guid workspaceId)
    {
        try
        {
            var json = await _service.ExportJsonAsync(workspaceId);
            return Ok(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<SavedWorkspaceDto>> Import([FromBody] ImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Json))
        {
            return BadRequest(new { error = "Json is required" });
        }

        try
        {
            var result = await _service.ImportJsonAsync(request.Json);
            var dto = MapWorkspaceToDto(result);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing workspace");
            return BadRequest(new { error = ex.Message });
        }
    }

    // Request DTOs
    public class SaveRequest
    {
        public string? Name { get; set; }
        public List<WorkspaceArtifactDto> Artifacts { get; set; } = new();
    }

    public class SaveAsRequest
    {
        public string? Name { get; set; }
        public List<WorkspaceArtifactDto> Artifacts { get; set; } = new();
    }

    public class RenameRequest
    {
        public string? NewName { get; set; }
    }

    public class DuplicateRequest
    {
        public string? NewName { get; set; }
    }

    public class AutoSaveRequest
    {
        public string? GeneratedName { get; set; }
        /// <summary>
        /// For Sample Projects: canonical lowercase slug (e.g., "autorisasjon").
        /// NOT a display name. Persisted in SavedWorkspace.ProjectName for identity-only persistence.
        /// </summary>
        public string? ProjectName { get; set; }
        public List<WorkspaceArtifactDto> Artifacts { get; set; } = new();
    }

    public class ImportRequest
    {
        public string? Json { get; set; }
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
