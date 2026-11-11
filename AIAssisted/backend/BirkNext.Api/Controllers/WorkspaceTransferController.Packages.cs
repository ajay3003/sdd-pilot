using Path = System.IO.Path;
using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.WorkspaceTransfer;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

public sealed partial class WorkspaceTransferController
{
    private string Actor => User.Identity?.IsAuthenticated == true && User.FindFirst("birknext:session")?.Value == "local" ? "local-session:loopback"
        : User.Identity?.Name is { Length: > 0 } name ? $"authenticated-user:{TransferJson.Sha256(name)[..12]}" : "local-session:loopback";

    public sealed record ScopesRequest(IReadOnlyList<string>? KnownEnvironmentIds);

    /// <summary>Projects and integration environments that can be exported, with counts read from the stores.</summary>
    [HttpPost("packages/scopes")]
    [RequestSizeLimit(64 * 1024)]
    public Task<TransferScopes> Scopes([FromBody] ScopesRequest request, [FromServices] WorkspacePackageService packages, CancellationToken ct) =>
        packages.ScopesAsync(request.KnownEnvironmentIds ?? [], ct);

    /// <summary>The dependency closure, security review and blockers of an export. Writes nothing.</summary>
    [HttpPost("packages/export/preview")]
    [RequestSizeLimit(1024 * 1024)]
    public Task<IActionResult> PreviewExport([FromBody] ExportRequest request, [FromServices] WorkspacePackageService packages, CancellationToken ct) =>
        Guarded(async () => Ok(await packages.PreviewExportAsync(request, ct)));

    [HttpPost("packages/export")]
    [RequestSizeLimit(1024 * 1024)]
    public Task<IActionResult> Export([FromBody] ExportRequest request, [FromServices] WorkspacePackageService packages, CancellationToken ct) =>
        Guarded(async () => Ok(await packages.ExportAsync(request, Actor, ct)));

    [HttpGet("packages/exports/{fileName}/download")]
    public IActionResult DownloadPackage(string fileName, [FromServices] WorkspacePackageService packages)
    {
        try { var path = packages.ResolveExport(fileName); return PhysicalFile(path, "application/zip", Path.GetFileName(path)); }
        catch (TransferRequestException ex) { return Refused(ex); }
    }

    public sealed record StagedPackage(Guid OperationId, string Sha256, long Bytes);

    /// <summary>Upload a .birknext package as the raw request body (streamed to staging, never parsed here, never multipart).</summary>
    [HttpPost("packages/import")]
    [RequestSizeLimit(512L * 1024 * 1024)]
    [Consumes("application/octet-stream", "application/zip")]
    public Task<IActionResult> StagePackage([FromServices] WorkspacePackageService packages, CancellationToken ct) =>
        Guarded(async () => { var (id, sha, bytes) = await packages.StageAsync(Request.Body, Actor, ct); return Ok(new StagedPackage(id, sha, bytes)); });

    public sealed record PreviewImportRequest(IReadOnlyList<EnvironmentCandidate>? Candidates);

    /// <summary>Inspect, validate and classify a staged package against this installation. Writes only the operation record.</summary>
    [HttpPost("packages/import/{operationId:guid}/preview")]
    [RequestSizeLimit(256 * 1024)]
    public Task<IActionResult> PreviewImport(Guid operationId, [FromBody] PreviewImportRequest request, [FromServices] WorkspacePackageService packages, CancellationToken ct) =>
        Guarded(async () => Ok(await packages.PreviewImportAsync(operationId, request.Candidates ?? [], ct)));

    /// <summary>Apply an approved plan (fingerprint required; the destination must be unchanged since the preview).</summary>
    [HttpPost("packages/import/{operationId:guid}/apply")]
    [RequestSizeLimit(256 * 1024)]
    public Task<IActionResult> ApplyImport(Guid operationId, [FromBody] ImportApplyRequest request, [FromServices] WorkspacePackageService packages, CancellationToken ct) =>
        Guarded(async () => Ok(await packages.ApplyImportAsync(request with { OperationId = operationId }, Actor, ct)));

    [HttpGet("packages/import/{operationId:guid}/report")]
    public async Task<IActionResult> ImportReport(Guid operationId, [FromServices] WorkspacePackageService packages, CancellationToken ct) =>
        await packages.ImportReportAsync(operationId, ct) is { } report ? Ok(report) : NotFound(new TransferError("NOT_FOUND", "No report for that import yet."));

    [HttpGet("operations")]
    public Task<IReadOnlyList<TransferOperationSummary>> Operations([FromServices] WorkspacePackageService packages, CancellationToken ct, [FromQuery] int take = 50) =>
        packages.HistoryAsync(take, ct);
}
