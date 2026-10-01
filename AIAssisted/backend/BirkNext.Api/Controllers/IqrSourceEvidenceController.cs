using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Source Analysis: the single source-ingestion entry point (ZIP upload → immutable snapshot). Reviews read snapshots through the shared
/// source-evidence provider; none of them uploads source. The per-integration upload Integration Quality Review once had is removed — the
/// snapshots it created stay in the store and in historical IQR results.
/// </summary>
[ApiController]
[Route("api/integration-review/source")]
[Route("api/source-analysis")]
public sealed class IqrSourceEvidenceController(IqrSourceStore store, BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider sources) : ControllerBase
{
    /// <summary>Integration Quality Review's view of Source Analysis: snapshots for binding one to an integration (read-only metadata).</summary>
    [HttpGet("scope")]
    public async Task<ActionResult<BirkNext.SourceEvidence.ReviewSourceOptions>> Scope([FromQuery] string environmentId, [FromQuery] Guid? primary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        return Ok(BirkNext.Api.Services.SourceAnalysis.ReviewSourceEvidenceProvider.Options(sources.SourceAnalysisEnabled, snapshots, IqrEvidence,
            primary is { } p ? new BirkNext.SourceEvidence.ReviewSourceScopeRequest { PrimarySnapshotId = p } : null, _ => []));
    }

    /// <summary>What a snapshot holds for Integration Quality Review (source-defined behaviour only; never runtime processing).</summary>
    public static BirkNext.Api.Services.SourceAnalysis.ConsumerSourceEvidence IqrEvidence(IqrSourceSnapshot s) =>
        new(true, s.Rules.Count == 0 && s.IntegrationPath is null ? "No integration implementation evidence was found in this snapshot." : null,
            $"{s.Rules.Count} implementation rule(s) · {s.IntegrationPath?.Hops.Count ?? 0} integration path hop(s)");

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IqrSourceSnapshot>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await store.ListAsync(environmentId, ct));

    [HttpPost("snapshots")]
    [RequestSizeLimit(IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    public Task<ActionResult<IqrSourceSnapshot>> AnalyzeSourceSnapshot([FromQuery] string environmentId, CancellationToken ct) =>
        AnalyzeArchive(environmentId, IqrSourceStore.SourceAnalysisOwner, ct);

    private async Task<ActionResult<IqrSourceSnapshot>> AnalyzeArchive(string environmentId, string integrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (!Request.HasFormContentType) return BadRequest("Upload a .zip source archive as multipart form data.");
        var form = await Request.ReadFormAsync(ct);
        if (form.Files.Count != 1) return BadRequest("Upload exactly one .zip source archive.");
        var file = form.Files[0];
        if (file.Length is <= 0 or > IqrSourceArchiveReader.MaxArchiveBytes) return BadRequest("Source archive must be between 1 byte and 50 MB.");
        using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > IqrSourceArchiveReader.MaxArchiveBytes) return BadRequest("Source archive exceeds 50 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        var (snapshot, error) = await store.AnalyzeAsync(environmentId, integrationId, file.FileName, buffer.ToArray(), ct);
        return error is null ? Ok(snapshot) : BadRequest(new { message = error });
    }
}
