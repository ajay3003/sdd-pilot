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

    /// <summary>
    /// Refuses a workspace write made before the last local data reset. Without this, a browser tab that still holds the previous project
    /// recreates it: auto-save and save-current create a new workspace from the payload when none is current.
    /// </summary>
    private ActionResult? StaleAfterReset(int? clientEpoch)
    {
        var state = HttpContext.RequestServices.GetService<BirkNext.Api.Services.LocalDataReset.LocalDataResetState>();
        if (state is null || state.Accepts(clientEpoch)) return null;
        _logger.LogWarning("Workspace write refused: client reset epoch {Client} is older than {Current}", clientEpoch, state.Epoch);
        return Conflict(new { code = "stale-reset-epoch", resetEpoch = state.Epoch, error = "Local data was reset; this browser state is from before the reset and was not saved." });
    }

    [HttpPost("save-current")]
    public async Task<ActionResult<SavedWorkspaceDto>> SaveCurrent([FromBody] SaveRequest? request = null)
    {
        if (StaleAfterReset(request?.ResetEpoch) is { } stale) return stale;
        try
        {
            var result = await _service.SaveCurrentAsync(request?.Name, request?.Artifacts ?? new(), request?.SddLifecycleJson);
            var dto = MapWorkspaceToDto(result, lifecycle: false);
            return Ok(dto);
        }
        catch (WorkspaceDocumentBodiesMissingException missing)
        {
            _logger.LogWarning("Workspace save refused: {Count} document bodies missing", missing.Missing.Count);
            return Conflict(new { code = "document-bodies-missing", missing = missing.Missing.Take(200), error = missing.Message });
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
            var result = await _service.SaveAsAsync(request.Name, request.Artifacts ?? new(), request.SddLifecycleJson);
            var dto = MapWorkspaceToDto(result, lifecycle: false);
            return Ok(dto);
        }
        catch (WorkspaceDocumentBodiesMissingException missing)
        {
            _logger.LogWarning("Workspace save refused: {Count} document bodies missing", missing.Missing.Count);
            return Conflict(new { code = "document-bodies-missing", missing = missing.Missing.Take(200), error = missing.Message });
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
            dto.SddLifecycleJson = await _service.WithDocumentBodiesAsync(dto.SddLifecycleJson);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading workspace {WorkspaceId}", workspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The workspace as application start restores it: identity, artifacts and the lifecycle without document bodies. The full workspace stays
    /// available from load/{id}; the bodies from documents/{id}.
    /// </summary>
    [HttpGet("bootstrap/{workspaceId}")]
    public async Task<ActionResult<WorkspaceBootstrapDto>> Bootstrap(Guid workspaceId)
    {
        var result = await _service.LoadAsync(workspaceId);
        if (result == null) return NotFound(new { error = "Workspace not found" });
        var full = MapWorkspaceToDto(result);
        var lifecycle = await _service.WithDocumentBodiesAsync(result.SddLifecycleJson);
        var documents = WorkspaceLifecycleDocuments.Documents(lifecycle);
        var dto = new WorkspaceBootstrapDto
        {
            Id = full.Id, UserId = full.UserId, Name = full.Name, ProjectName = full.ProjectName, Description = full.Description, CreatedAt = full.CreatedAt,
            UpdatedAt = full.UpdatedAt, LastOpenedAt = full.LastOpenedAt, Version = full.Version, ParserVersion = full.ParserVersion,
            ReviewContextVersion = full.ReviewContextVersion, ArtifactSetHash = full.ArtifactSetHash, AutoSaved = full.AutoSaved, Favorite = full.Favorite,
            Artifacts = full.Artifacts,
            SddLifecycleJson = WorkspaceLifecycleDocuments.Manifest(lifecycle),
            DocumentsDeferred = documents.Revisions.Count + documents.GeneratedDocuments.Count > 0,
            DeferredDocuments = documents.Revisions.Count + documents.GeneratedDocuments.Count,
            DeferredDocumentCharacters = documents.Revisions.Values.Sum(c => (long)c.Length) + documents.GeneratedDocuments.Values.Sum(c => (long)c.Length),
        };
        return Ok(dto);
    }

    /// <summary>The document bodies of a saved workspace's lifecycle (revision content by id, generated documents by fingerprint).</summary>
    [HttpGet("documents/{workspaceId}")]
    public async Task<ActionResult<WorkspaceDocumentsDto>> Documents(Guid workspaceId)
    {
        var result = await _service.LoadAsync(workspaceId);
        return result == null ? NotFound(new { error = "Workspace not found" }) : Ok(WorkspaceLifecycleDocuments.Documents(await _service.WithDocumentBodiesAsync(result.SddLifecycleJson)));
    }

    /// <summary>Which content addresses of document bodies are not stored yet (the client uploads only those).</summary>
    [HttpPost("bodies/missing")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<DocumentBodiesMissingResponse>> MissingBodies([FromBody] DocumentBodiesMissingRequest? request)
    {
        if (request?.Addresses is not { Count: > 0 } addresses) return Ok(new DocumentBodiesMissingResponse());
        if (addresses.Count > 50_000) return BadRequest(new { error = "At most 50 000 addresses per request." });
        return Ok(new DocumentBodiesMissingResponse { Missing = [.. await _service.MissingDocumentBodiesAsync(addresses)] });
    }

    /// <summary>
    /// Stores one batch of document bodies by content address (verified here; a wrong address refuses the whole batch). Batches are bounded
    /// (<see cref="WorkspaceDocumentBodies.MaxBatchCharacters"/>), so a large workspace is uploaded in several requests, never one oversized one.
    /// </summary>
    [HttpPost("bodies")]
    [RequestSizeLimit(24 * 1024 * 1024)]
    public async Task<ActionResult<WorkspaceDocumentBodyUploadResult>> StoreBodies([FromBody] DocumentBodiesUploadRequest? request)
    {
        if (StaleAfterReset(request?.ResetEpoch) is { } stale) return stale;
        if (request?.Bodies is not { Count: > 0 } bodies) return BadRequest(new { error = "No bodies." });
        try { return Ok(await _service.StoreDocumentBodiesAsync(bodies)); }
        catch (ArgumentException invalid) { return BadRequest(new { code = "invalid-document-body", error = invalid.Message }); }
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
            // A list is summaries: no lifecycle and no artifact bodies (load/{id} and bootstrap/{id} return one workspace's state).
            var dtos = result.Select(w => MapWorkspaceToDto(w, lifecycle: false, artifactContent: false)).ToList();
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

    /// <summary>Whether the lifecycle JSON names a current Project Import (CurrentProjectImportId). Malformed JSON counts as none.</summary>
    internal static bool HasCurrentProjectImport(string? sddLifecycleJson)
    {
        if (string.IsNullOrWhiteSpace(sddLifecycleJson)) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(sddLifecycleJson);
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.TryGetProperty("CurrentProjectImportId", out var id)
                && id.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString());
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    [HttpPost("auto-save")]
    public async Task<ActionResult<SavedWorkspaceDto>> AutoSave([FromBody] AutoSaveRequest? request = null)
    {
        if (StaleAfterReset(request?.ResetEpoch) is { } stale) return stale;
        try
        {
            _logger.LogInformation("TRACE: [WorkspacePersistenceController.AutoSave]");
            _logger.LogInformation("  ProjectName={Project}", request?.ProjectName);
            _logger.LogInformation("  RequestArtifacts={Count}", request?.Artifacts?.Count ?? 0);

            // Nothing to save and no workspace to update (e.g. right after a local data reset): creating an empty "Auto_…" workspace
            // would make the installation look like it has a project again.
            // An active Project Import is a project even before any document role is selected (a source-only import has none).
            if ((request?.Artifacts?.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(request?.ProjectName)
                && !HasCurrentProjectImport(request?.SddLifecycleJson)
                && await _service.GetCurrentWorkspaceIdAsync() is null)
                return NoContent();

            var result = await _service.AutoSaveAsync(request?.GeneratedName, request?.ProjectName, request?.Artifacts ?? new(), request?.SddLifecycleJson);
            _logger.LogInformation("  ResponseArtifacts={Count}", result.Artifacts.Count);

            var dto = MapWorkspaceToDto(result, lifecycle: false);
            return Ok(dto);
        }
        catch (WorkspaceDocumentBodiesMissingException missing)
        {
            _logger.LogWarning("Workspace save refused: {Count} document bodies missing", missing.Missing.Count);
            return Conflict(new { code = "document-bodies-missing", missing = missing.Missing.Take(200), error = missing.Message });
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
            if (result is not null) result.ResetEpoch = HttpContext.RequestServices.GetService<BirkNext.Api.Services.LocalDataReset.LocalDataResetState>()?.Epoch ?? 0;
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
        public string? SddLifecycleJson { get; set; }
        /// <summary>The local data reset epoch the client last saw (from current-state or the reset response).</summary>
        public int? ResetEpoch { get; set; }
    }

    public class SaveAsRequest
    {
        public string? Name { get; set; }
        public List<WorkspaceArtifactDto> Artifacts { get; set; } = new();
        public string? SddLifecycleJson { get; set; }
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
        public string? SddLifecycleJson { get; set; }
        /// <summary>The local data reset epoch the client last saw (from current-state or the reset response).</summary>
        public int? ResetEpoch { get; set; }
    }

    public sealed class DocumentBodiesMissingRequest
    {
        public List<string> Addresses { get; set; } = [];
    }

    public sealed class DocumentBodiesMissingResponse
    {
        public List<string> Missing { get; set; } = [];
    }

    public sealed class DocumentBodiesUploadRequest
    {
        public List<WorkspaceDocumentBodyUpload> Bodies { get; set; } = [];
        public int? ResetEpoch { get; set; }
    }

    public class ImportRequest
    {
        public string? Json { get; set; }
    }

    /// <param name="lifecycle">False for write responses and lists: the caller already holds (or does not need) the lifecycle.</param>
    /// <param name="artifactContent">False for lists: artifacts are listed, their bodies are not.</param>
    private SavedWorkspaceDto MapWorkspaceToDto(SavedWorkspace workspace, bool lifecycle = true, bool artifactContent = true)
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
            SddLifecycleJson = lifecycle ? workspace.SddLifecycleJson : null,
            Favorite = workspace.Favorite,
            Artifacts = workspace.Artifacts
                .Select(a => new SavedWorkspaceArtifactResponseDto
                {
                    ArtifactType = a.ArtifactType.ToString(),
                    FileName = a.FileName,
                    OriginalPath = a.OriginalPath,
                    Content = artifactContent ? a.Content : "",
                    ContentHash = a.ContentHash,
                    Encoding = a.Encoding,
                    ParseVersion = a.ParseVersion
                })
                .ToList()
        };
    }
}
