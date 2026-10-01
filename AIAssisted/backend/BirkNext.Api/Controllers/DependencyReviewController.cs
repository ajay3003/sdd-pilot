using BirkNext.Api.Services.DependencyReview;
using BirkNext.Dependencies;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Dependency / supply-chain review. Source review (Renovate policy): uploaded archives and configs are analyzed in memory; nothing is written to
/// a repository, no branch/commit/PR is created and Renovate is never triggered; simulations use synthetic candidate versions only.
/// Dependency health: runs over a stored inventory (source review, SBOM, lock file, deployed evidence) without any source upload, using
/// read-only registry/advisory/provider lookups. Every run is an immutable snapshot; refresh creates a new run.
/// </summary>
[ApiController]
[Route("api/dependency-review")]
public sealed class DependencyReviewController(IDependencyReviewService reviews, IDependencyHealthService health, IDependencyReviewSourceScopeService sourceScope) : ControllerBase
{
    // Source ingestion belongs to Source Analysis: the source review reads its immutable snapshots; there is no archive upload here.

    /// <summary>Source Analysis snapshots of the environment (per repository) and, for a chosen primary, its related-source candidates.</summary>
    [HttpGet("source-scope")]
    public async Task<ActionResult<SourceScopeOptions>> SourceScope([FromQuery] string environmentId, [FromQuery] Guid? primary, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await sourceScope.OptionsAsync(environmentId, primary, ct));

    /// <summary>Reviews exactly the selected snapshots (one primary, zero or more related); the run stores the scope with fingerprints.</summary>
    [HttpPost("source-runs")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<DependencyReviewResult>> RunSource([FromBody] SourceDependencyReviewRequest request, CancellationToken ct)
    {
        if (request.ConfigOverrides.Any(o => o.Content.Length > 1024 * 1024)) return BadRequest("A Renovate config override is larger than 1 MB.");
        var (result, error) = await sourceScope.RunAsync(request, ct);
        return error is not null ? BadRequest(error) : Ok(result);
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<DependencyReviewRunSummary>>> History(CancellationToken ct) => Ok(await reviews.HistoryAsync(ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<DependencyReviewResult>> Get(Guid runId, CancellationToken ct) =>
        await reviews.GetAsync(runId, ct) is { } run ? Ok(run) : NotFound();

    [HttpPost("simulate")]
    public async Task<ActionResult<PolicySimulation>> Simulate([FromBody] PolicySimulationRequest request, CancellationToken ct)
    {
        var (simulation, error) = await reviews.SimulateAsync(request, ct);
        return error is not null ? BadRequest(error) : Ok(simulation);
    }
    // ── Dependency inventories and source-free health review ─────────────────────────────────────────────────────────

    [HttpGet("inventories")]
    public async Task<ActionResult<IReadOnlyList<InventorySummary>>> Inventories(CancellationToken ct) => Ok(await health.InventoriesAsync(ct));

    /// <summary>Upload an SBOM (CycloneDX JSON/XML, SPDX JSON) or a NuGet packages.lock.json; stored as an inventory only when valid.</summary>
    [HttpPost("inventories/import")]
    [RequestSizeLimit(InventorySources.MaxDocumentBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = InventorySources.MaxDocumentBytes + 1024 * 1024)]
    public async Task<ActionResult<InventoryImportResult>> Import(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest("Upload the document as multipart form data.");
        var form = await Request.ReadFormAsync(ct);
        if (form.Files.Count != 1) return BadRequest("Upload exactly one SBOM or lock file.");
        var file = form.Files[0];
        if (file.Length > InventorySources.MaxDocumentBytes) return BadRequest($"{file.FileName} is larger than the upload limit.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var role = Enum.TryParse<SbomRole>(form["role"].ToString(), out var r) ? r : SbomRole.BuildArtifact;
        var result = await health.ImportAsync(file.FileName, buffer.ToArray(), role, form["environment"].ToString(), form["name"].ToString(), ct);
        return result.Inventory is null ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpPost("inventories/deployed")]
    public async Task<ActionResult<InventoryImportResult>> CaptureDeployed([FromBody] DeployedCaptureRequest request, CancellationToken ct)
    {
        var result = await health.CaptureDeployedAsync(request, ct);
        return result.Inventory is null ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpPost("health")]
    public async Task<ActionResult<DependencyHealthRun>> RunHealth([FromBody] DependencyHealthRequest request, CancellationToken ct)
    {
        var (run, error) = await health.RunAsync(request, ct);
        return error is not null ? BadRequest(error) : Ok(run);
    }

    [HttpGet("health")]
    public async Task<ActionResult<IReadOnlyList<DependencyHealthRunSummary>>> HealthHistory(CancellationToken ct) => Ok(await health.HistoryAsync(ct));

    /// <summary>A stored run exactly as recorded; no registry or advisory source is queried.</summary>
    [HttpGet("health/{runId:guid}")]
    public async Task<ActionResult<DependencyHealthRun>> GetHealth(Guid runId, CancellationToken ct) =>
        await health.GetAsync(runId, ct) is { } run ? Ok(run) : NotFound();

    /// <summary>Re-collects current evidence for the same inventory and options as a NEW run; the earlier run is not changed.</summary>
    [HttpPost("health/{runId:guid}/refresh")]
    public async Task<ActionResult<DependencyHealthRun>> RefreshHealth(Guid runId, CancellationToken ct)
    {
        var (run, error) = await health.RefreshAsync(runId, ct);
        return error is not null ? BadRequest(error) : Ok(run);
    }
}
