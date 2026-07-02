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
    public async Task<ActionResult<SavedWorkspace>> SaveCurrent([FromBody] SaveRequest? request = null)
    {
        try
        {
            var result = await _service.SaveCurrentAsync(request?.Name);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving current workspace");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("save-as")]
    public async Task<ActionResult<SavedWorkspace>> SaveAs([FromBody] SaveAsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        try
        {
            var result = await _service.SaveAsAsync(request.Name);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving workspace as {Name}", request.Name);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("load/{workspaceId}")]
    public async Task<ActionResult<SavedWorkspace>> Load(Guid workspaceId)
    {
        try
        {
            var result = await _service.LoadAsync(workspaceId);
            if (result == null)
            {
                return NotFound(new { error = "Workspace not found" });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("list")]
    public async Task<ActionResult<List<SavedWorkspace>>> List()
    {
        try
        {
            // TODO: Get actual userId from auth context
            var result = await _service.ListAsync("default-user");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing workspaces");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("rename/{workspaceId}")]
    public async Task<ActionResult<SavedWorkspace>> Rename(Guid workspaceId, [FromBody] RenameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.NewName))
        {
            return BadRequest(new { error = "NewName is required" });
        }

        try
        {
            var result = await _service.RenameAsync(workspaceId, request.NewName);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("duplicate/{workspaceId}")]
    public async Task<ActionResult<SavedWorkspace>> Duplicate(Guid workspaceId, [FromBody] DuplicateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.NewName))
        {
            return BadRequest(new { error = "NewName is required" });
        }

        try
        {
            var result = await _service.DuplicateAsync(workspaceId, request.NewName);
            return Ok(result);
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
    public async Task<ActionResult<SavedWorkspace>> AutoSave([FromBody] AutoSaveRequest? request = null)
    {
        try
        {
            var result = await _service.AutoSaveAsync(request?.GeneratedName);
            return Ok(result);
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
            var result = await _service.GetCurrentStateAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current workspace state");
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
    public async Task<ActionResult<SavedWorkspace>> Import([FromBody] ImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Json))
        {
            return BadRequest(new { error = "Json is required" });
        }

        try
        {
            var result = await _service.ImportJsonAsync(request.Json);
            return Ok(result);
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
    }

    public class SaveAsRequest
    {
        public string? Name { get; set; }
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
    }

    public class ImportRequest
    {
        public string? Json { get; set; }
    }
}
