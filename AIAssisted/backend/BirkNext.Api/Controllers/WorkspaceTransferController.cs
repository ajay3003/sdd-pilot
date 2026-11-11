using Path = System.IO.Path;
using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.WorkspaceTransfer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Backup &amp; Transfer. Every endpoint requires the WorkspaceTransfer capability. Passphrases arrive only in request bodies; they are never
/// logged, echoed or stored. Errors carry a stable code and a message that never contains a secret.
/// </summary>
[ApiController]
[Route("api/workspace-transfer")]
[Authorize(Policy = WorkspaceTransferPermissions.Policy)]
public sealed partial class WorkspaceTransferController(FullBackupService backups, PostgresTools tools, ManagedFileStores files) : ControllerBase
{
    public sealed record BackupStatus(bool ToolsConfigured, string? ToolProblem, string? DumpToolVersion, string? RestoreToolVersion, string Mode, string BackupDirectory,
        IReadOnlyList<string> ManagedFiles, IReadOnlyList<BackupExclusion> Excluded, string ApplicationVersion, string ApplicationCommit);

    [HttpGet("backups/status")]
    public async Task<BackupStatus> Status(CancellationToken ct)
    {
        var problem = tools.ConfigurationProblem();
        var (version, commit) = FullBackupService.ApplicationIdentity();
        return new BackupStatus(problem is null, problem,
            problem is null ? await tools.VersionAsync("pg_dump", ct) : null, problem is null ? await tools.VersionAsync("pg_restore", ct) : null,
            tools.ContainerMode ? "ContainerExec" : "Host", backups.BackupDirectory,
            files.Existing().Select(f => $"{f.Store.Name}/{f.File}").ToList(), ManagedFileStores.Exclusions, version, commit);
    }

    [HttpGet("backups")]
    public IReadOnlyList<BackupSummary> ListBackups() => backups.List();

    [HttpPost("backups")]
    [RequestSizeLimit(16 * 1024)]
    public Task<IActionResult> CreateBackup([FromBody] BackupCreateRequest request, CancellationToken ct) =>
        Guarded(async () => Ok(await backups.CreateAsync(request.Passphrase, request.Note, ct)));

    public sealed record InspectRequest(string? Passphrase);

    [HttpPost("backups/{fileName}/inspect")]
    [RequestSizeLimit(16 * 1024)]
    public Task<IActionResult> InspectBackup(string fileName, [FromBody] InspectRequest request, CancellationToken ct) =>
        Guarded(async () => Ok(await backups.InspectAsync(fileName, request.Passphrase, ct)));

    /// <summary>Streams the encrypted backup file as it is (it never leaves the server decrypted).</summary>
    [HttpGet("backups/{fileName}/download")]
    public IActionResult DownloadBackup(string fileName)
    {
        try
        {
            var path = backups.ResolveBackup(fileName);
            return PhysicalFile(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: false);
        }
        catch (TransferRequestException ex) { return Refused(ex); }
    }

    [HttpGet("restores")]
    public IReadOnlyList<RestoreReport> ListRestores() => backups.Restores();

    [HttpPost("restores")]
    [RequestSizeLimit(16 * 1024)]
    public Task<IActionResult> Restore([FromBody] RestoreRequest request, CancellationToken ct) =>
        Guarded(async () => Ok(await backups.RestoreIsolatedAsync(request.FileName, request.Passphrase, ct)));

    private async Task<IActionResult> Guarded(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (TransferRequestException ex) { return Refused(ex); }
        catch (TransferFormatException ex) { return BadRequest(new TransferError(ex.Code, ex.Message)); }
        catch (PostgresToolException ex) { return StatusCode(StatusCodes.Status502BadGateway, new TransferError("TOOL_FAILED", ex.Message)); }
    }

    private IActionResult Refused(TransferRequestException ex) => ex.Code switch
    {
        "BUSY" or "DESTINATION_CHANGED" or "PLAN_CHANGED" or "STATE" => Conflict(new TransferError(ex.Code, ex.Message)),
        "EXPORT_BLOCKED" or "REDACTION_INCOMPLETE" or "DECISION" or "ENVIRONMENT" => UnprocessableEntity(new TransferError(ex.Code, ex.Message)),
        "PACKAGE_TOO_LARGE" => StatusCode(StatusCodes.Status413PayloadTooLarge, new TransferError(ex.Code, ex.Message)),
        "EXPIRED" => StatusCode(StatusCodes.Status410Gone, new TransferError(ex.Code, ex.Message)),
        "NOT_FOUND" => NotFound(new TransferError(ex.Code, ex.Message)),
        "INSUFFICIENT_DISK_SPACE" => StatusCode(StatusCodes.Status507InsufficientStorage, new TransferError(ex.Code, ex.Message)),
        "TOOLS_UNAVAILABLE" or "TOOLS_NOT_CONFIGURED" => StatusCode(StatusCodes.Status503ServiceUnavailable, new TransferError(ex.Code, ex.Message)),
        _ => BadRequest(new TransferError(ex.Code, ex.Message)),
    };

    public sealed record TransferError(string Code, string Message);
}
