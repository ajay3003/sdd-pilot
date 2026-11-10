using System.Text.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Parquet;
using BirkNext.Api.Services.Parquet.Quality;
using BirkNext.Parquet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Parquet Quality Review (read-only on files). A file is uploaded into a temporary session (BirkNext-named copy, expires); metadata, schema,
/// bounded rows, exact statistics, contract discovery from Source Analysis snapshots, generated and own rules, baseline comparison and durable
/// evidence (no row values) run against it. No endpoint takes a server path or reaches another system.
/// </summary>
[ApiController]
[Route("api/parquet")]
public sealed class ParquetController(ParquetInspectionService parquet, ParquetQualityService quality) : ControllerBase
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

    /// <summary>Which of the given sessions are still live (recent files of this browser); expired ones are simply absent.</summary>
    [HttpGet("sessions")]
    public ActionResult<List<ParquetOverview>> Sessions([FromQuery] string? ids) =>
        Ok(parquet.Sessions((ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)));

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

    /// <summary>Attaches a delivery manifest (_manifest.json, ≤ 64 KB) to the session; only its documented fields are kept.</summary>
    [HttpPost("sessions/{id:guid}/manifest")]
    [RequestSizeLimit(ParquetInspectionService.MaxManifestBytes + 1024)]
    public ActionResult<ParquetManifestSummary> Manifest(Guid id, [FromBody] JsonElement manifest) =>
        parquet.AttachManifest(id, manifest.GetRawText()) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpGet("sessions/{id:guid}/contracts")]
    public async Task<ActionResult<ContractDiscovery>> Discover(Guid id, [FromQuery] string? project, CancellationToken ct) =>
        await quality.DiscoverAsync(id, project, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    /// <summary>Every dataset contract of the analysed projects (for selecting one by hand); filtered by project when given.</summary>
    [HttpGet("contracts")]
    public async Task<ActionResult<List<ContractCandidate>>> Contracts([FromQuery] string? project, CancellationToken ct)
    {
        var (contracts, _) = await quality.ContractsAsync(project, null, ct);
        return Ok(contracts.Select(c => new ContractCandidate { Source = c.Source, Contract = c.Contract, Match = ContractMatchKind.Unresolved, Reasons = ["Listed from Source Analysis; not matched to a file."], ExpectedColumns = c.Contract.Columns.Count }).ToList());
    }

    /// <summary>Checks a chosen contract and declared mappings against the file; returns the association as the server vouches for it.</summary>
    [HttpPost("sessions/{id:guid}/association")]
    public async Task<ActionResult<ContractAssociation>> Associate(Guid id, [FromBody] ContractAssociation association, CancellationToken ct)
    {
        if (parquet.Session(id) is not { } s) return UnprocessableEntity(new { message = "The Parquet session has expired; open the file again." });
        var (resolved, _, error) = await quality.ResolveAsync(s.Overview, association, ct);
        return resolved is null ? UnprocessableEntity(new { message = error }) : Ok(resolved);
    }

    [HttpPost("sessions/{id:guid}/rules")]
    public async Task<ActionResult<RuleGeneration>> Generate(Guid id, [FromBody] ContractAssociation? association, CancellationToken ct) =>
        await quality.GenerateAsync(id, association is { ContractKey.Length: > 0 } ? association : null, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpPost("sessions/{id:guid}/review")]
    public async Task<ActionResult<ParquetQualityResult>> Review(Guid id, [FromBody] ParquetQualityRequest request, CancellationToken ct) =>
        await quality.ReviewAsync(id, request, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpPost("compare")]
    public ActionResult<ParquetComparison> Compare([FromBody] ParquetCompareRequest request, CancellationToken ct) =>
        parquet.CompareByKeys(request.BaselineSessionId, request.CurrentSessionId, request.KeyColumns.Count > 0 ? request.KeyColumns : request.KeyColumn is { Length: > 0 } k ? [k] : [], ct) is var (result, error) && result is not null
            ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpPost("baseline")]
    public async Task<ActionResult<ParquetBaselineResult>> Baseline([FromBody] ParquetBaselineRequest request, CancellationToken ct) =>
        await quality.BaselineAsync(request, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpGet("rule-sets")]
    public async Task<ActionResult<List<ParquetRuleSetVersion>>> RuleSets([FromQuery] string project, [FromQuery] string contract, [FromQuery] string version, CancellationToken ct) =>
        Ok(await quality.RuleSetsAsync(project ?? "", contract ?? "", version ?? "", ct));

    /// <summary>A test lead approves generated business rules (and keeps own rules) as a new immutable rule-set version.</summary>
    [HttpPost("sessions/{id:guid}/rule-sets")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ParquetRuleSetVersion>> Approve(Guid id, [FromBody] ParquetApprovalRequest request, CancellationToken ct) =>
        await quality.ApproveAsync(id, request.Approval, request.Association, Initiator, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    /// <summary>Removes the session's temporary copy (never the tester's original file).</summary>
    [HttpDelete("sessions/{id:guid}")]
    public IActionResult Close(Guid id) => parquet.Close(id) ? NoContent() : NotFound();

    [HttpPost("evidence")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ParquetInspectionEvidence>> Save([FromBody] ParquetEvidenceRequest request, CancellationToken ct)
    {
        var (evidence, error) = await quality.SaveEvidenceAsync(request, Initiator, ct);
        return evidence is null ? UnprocessableEntity(new { message = error }) : Ok(Safe(evidence));
    }

    [HttpGet("evidence")]
    public async Task<ActionResult<List<ParquetInspectionEvidence>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok((await parquet.EvidenceAsync(environmentId, ct)).Select(Safe).ToList());

    [HttpGet("evidence/{id:guid}")]
    public async Task<ActionResult<ParquetInspectionEvidence>> Get([FromQuery] string environmentId, Guid id, CancellationToken ct) =>
        await parquet.EvidenceAsync(environmentId ?? "", id, ct) is { } e ? Ok(Safe(e)) : NotFound();

    /// <summary>Sanitized export of saved evidence: metadata, fingerprints, rule results and counts; no row values.</summary>
    [HttpGet("evidence/{id:guid}/export")]
    public async Task<IActionResult> Export([FromQuery] string environmentId, Guid id, CancellationToken ct)
    {
        if (await parquet.EvidenceAsync(environmentId, id, ct) is not { } evidence) return NotFound();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { kind = "birknext.parquet.inspection", schemaVersion = 2, evidence = Safe(evidence),
            completeness = "Metadata, schema fingerprint, contract association, rule results, requirement coverage and counts. Row values are never part of saved Parquet evidence." },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(bytes, "application/json", $"parquet-inspection-{id:N}.json");
    }

    /// <summary>Tester-oriented Markdown quality report generated from the saved evidence (never from the temporary file).</summary>
    [HttpGet("evidence/{id:guid}/report")]
    public async Task<IActionResult> Report([FromQuery] string environmentId, Guid id, CancellationToken ct)
    {
        if (await parquet.EvidenceAsync(environmentId, id, ct) is not { } evidence) return NotFound();
        return File(System.Text.Encoding.UTF8.GetBytes(ParquetQualityService.Report(Safe(evidence))), "text/markdown", $"parquet-quality-report-{id:N}.md");
    }

    [HttpPost("evidence/{id:guid}/approve-baseline")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ParquetInspectionEvidence>> ApproveBaseline([FromQuery] string environmentId, Guid id, CancellationToken ct) =>
        await quality.ApproveBaselineAsync(environmentId ?? "", id, Initiator, ct) is var (result, error) && result is not null ? Ok(Safe(result)) : UnprocessableEntity(new { message = error });

    [HttpDelete("evidence/{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> DeleteEvidence([FromQuery] string environmentId, Guid id, CancellationToken ct) =>
        await parquet.DeleteEvidenceAsync(environmentId, id, ct) ? NoContent() : NotFound();

    /// <summary>Evidence saved before the quality review could hold comparison record keys (row values): they are never returned or exported.</summary>
    private static ParquetInspectionEvidence Safe(ParquetInspectionEvidence e) =>
        e.Comparison is { Records.Count: > 0 } c && c.Records.Any(r => r.Key.Length > 0) ? e with { Comparison = c with { Records = [.. c.Records.Select(r => r with { Key = "" })] } } : e;
}
