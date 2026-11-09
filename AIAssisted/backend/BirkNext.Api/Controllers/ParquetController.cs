using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Parquet;
using BirkNext.Parquet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Parquet data explorer (read-only). A file is uploaded into a temporary session (BirkNext-named copy, expires); metadata, schema, bounded rows,
/// explicit full-scan profile, rule validation and comparison run against it; durable evidence keeps no row values. No endpoint takes a server path.
/// </summary>
[ApiController]
[Route("api/parquet")]
public sealed class ParquetController(ParquetInspectionService parquet) : ControllerBase
{
    private string Initiator => User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";

    [HttpPost("sessions")]
    [RequestSizeLimit(ParquetInspectionService.MaxFileBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ParquetInspectionService.MaxFileBytes + 1024 * 1024)]
    public async Task<ActionResult<ParquetOverview>> Open(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return UnprocessableEntity(new { message = "The file is empty." });
        await using var stream = file.OpenReadStream();
        var (overview, error) = await parquet.OpenAsync(file.FileName, stream, file.Length, ct);
        return overview is null ? UnprocessableEntity(new { message = error }) : Ok(overview);
    }

    [HttpGet("sessions/{id:guid}")]
    public ActionResult<ParquetOverview> Overview(Guid id) => parquet.Session(id) is { } s ? Ok(s.Overview) : NotFound(new { message = "The session has expired; open the file again." });

    [HttpPost("sessions/{id:guid}/rows")]
    public ActionResult<ParquetRowsResult> Rows(Guid id, [FromBody] ParquetRowsRequest request, CancellationToken ct) =>
        parquet.Rows(id, request, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpPost("sessions/{id:guid}/profile")]
    public ActionResult<ParquetProfile> Profile(Guid id, [FromBody] List<string>? columns, CancellationToken ct) =>
        parquet.Profile(id, columns, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpPost("sessions/{id:guid}/validate")]
    public ActionResult<ParquetValidation> Validate(Guid id, [FromBody] ParquetRuleSet rules, CancellationToken ct) =>
        parquet.Validate(id, rules, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpPost("compare")]
    public ActionResult<ParquetComparison> Compare([FromBody] ParquetCompareRequest request, CancellationToken ct) =>
        parquet.Compare(request.BaselineSessionId, request.CurrentSessionId, request.KeyColumn, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    /// <summary>Removes the session's temporary copy (never the tester's original file).</summary>
    [HttpDelete("sessions/{id:guid}")]
    public IActionResult Close(Guid id) => parquet.Close(id) ? NoContent() : NotFound();

    [HttpPost("evidence")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ParquetInspectionEvidence>> Save([FromBody] ParquetEvidenceRequest request, CancellationToken ct)
    {
        var (evidence, error) = await parquet.SaveEvidenceAsync(request, Initiator, ct);
        return evidence is null ? UnprocessableEntity(new { message = error }) : Ok(evidence);
    }

    [HttpGet("evidence")]
    public async Task<ActionResult<List<ParquetInspectionEvidence>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await parquet.EvidenceAsync(environmentId, ct));

    /// <summary>Sanitized export of saved evidence: metadata, fingerprints, rule results, comparison counts; no row values.</summary>
    [HttpGet("evidence/{id:guid}/export")]
    public async Task<IActionResult> Export([FromQuery] string environmentId, Guid id, CancellationToken ct)
    {
        if (await parquet.EvidenceAsync(environmentId, id, ct) is not { } evidence) return NotFound();
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { kind = "birknext.parquet.inspection", schemaVersion = 1, evidence,
            completeness = "Metadata, schema fingerprint, rule results and comparison counts. Row values are never part of saved Parquet evidence." },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(bytes, "application/json", $"parquet-inspection-{id:N}.json");
    }

    [HttpDelete("evidence/{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> DeleteEvidence([FromQuery] string environmentId, Guid id, CancellationToken ct) =>
        await parquet.DeleteEvidenceAsync(environmentId, id, ct) ? NoContent() : NotFound();
}
