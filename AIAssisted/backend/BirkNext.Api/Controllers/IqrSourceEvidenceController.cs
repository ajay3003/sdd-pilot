using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/integration-review/source")]
[Route("api/source-analysis")]
public sealed class IqrSourceEvidenceController(IqrSourceStore store, IIntegrationCatalogService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IqrSourceSnapshot>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await store.ListAsync(environmentId, ct));

    [HttpPost("{integrationId}")]
    [RequestSizeLimit(IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    public async Task<ActionResult<IqrSourceSnapshot>> Analyze([FromQuery] string environmentId, string integrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (!(await catalog.GetAsync(environmentId, null, null, ct)).Integrations.Any(i => i.Id == integrationId)) return NotFound();
        return await AnalyzeArchive(environmentId, integrationId, ct);
    }

    [HttpPost("snapshots")]
    [RequestSizeLimit(IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024)]
    public Task<ActionResult<IqrSourceSnapshot>> AnalyzeSourceSnapshot([FromQuery] string environmentId, CancellationToken ct) =>
        AnalyzeArchive(environmentId, "source-analysis", ct);

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
