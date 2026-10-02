using BirkNext.Api.Services.TestEvidence;
using BirkNext.TestEvidence;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test evidence: provider capabilities, source test definitions of a Source Analysis snapshot, and validate/preview of an uploaded result
/// artifact (TRX). Upload is a single explicit file — no paths, no archives, no retrieval from pipelines. Nothing is stored here; the client
/// records the previewed, immutable executions in the workspace SDD lifecycle.
/// </summary>
[ApiController]
[Route("api/test-evidence")]
public sealed class TestEvidenceController(TestExecutionImportService imports, TestEvidenceOptions options) : ControllerBase
{
    private const long FormOverhead = 64 * 1024;

    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<TestEvidenceProviderDescriptor>> Providers() => Ok(TestEvidenceProviderRegistry.Providers);

    [HttpGet("source-tests")]
    public async Task<ActionResult<SourceTestInventory>> SourceTests([FromQuery] string environmentId, [FromQuery] Guid snapshotId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest(new { message = "environmentId is required." });
        var (inventory, error) = await imports.SourceTestsAsync(environmentId, snapshotId, ct);
        return inventory is null ? NotFound(new { message = error }) : Ok(inventory);
    }

    /// <summary>Multipart: one <c>file</c> (.trx) plus optional environmentId, sourceSnapshotId, sourceBindingConfirmed, buildReference,
    /// commitReference, environmentReference.</summary>
    [HttpPost("results/preview")]
    [RequestSizeLimit(100L * 1024 * 1024 + FormOverhead)]
    [RequestFormLimits(MultipartBodyLengthLimit = 100L * 1024 * 1024 + FormOverhead)]
    public async Task<ActionResult<TestResultArtifactPreview>> Preview(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest(new { message = "Upload one .trx file as multipart form data." });
        var form = await Request.ReadFormAsync(ct);
        if (form.Files.Count != 1) return BadRequest(new { message = "Upload exactly one test result file." });
        var file = form.Files[0];
        if (file.Length <= 0) return BadRequest(new { message = "The file is empty." });
        if (file.Length > options.MaxArtifactBytes) return BadRequest(new { message = $"The file exceeds the {options.MaxArtifactBytes / (1024 * 1024)} MB limit." });
        using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > options.MaxArtifactBytes) return BadRequest(new { message = $"The file exceeds the {options.MaxArtifactBytes / (1024 * 1024)} MB limit." });
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        Guid? snapshot = Guid.TryParse(form["sourceSnapshotId"], out var id) ? id : null;
        var context = new TestResultImportContext(form["environmentId"].FirstOrDefault(), snapshot,
            bool.TryParse(form["sourceBindingConfirmed"], out var confirmed) && confirmed && snapshot is not null,
            form["buildReference"].FirstOrDefault(), form["commitReference"].FirstOrDefault(), form["environmentReference"].FirstOrDefault());
        return Ok(await imports.PreviewAsync(file.FileName, buffer.ToArray(), context, ct));
    }
}
