using BirkNext.Api.Services.Integrations.Scim;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Security Classification / Gradert tilgang review. Source archives are analyzed in memory; the test context holds synthetic ids and
/// identity labels only; a run's tokens are used for that run and never stored. Live checks are fixed GraphQL queries against configured
/// test children in an approved DEV/QA context — never Production, never a mutation, never a search for real classified children.
/// </summary>
[ApiController]
[Route("api/security-classification")]
public sealed class SecurityClassificationController(IClassificationReviewService reviews) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ClassificationOverview>> Overview([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await reviews.OverviewAsync(environmentId, ct));

    [HttpPost("source")]
    [RequestSizeLimit(4 * ScimSourceReader.MaxArchiveBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = 4 * ScimSourceReader.MaxArchiveBytes)]
    public async Task<ActionResult<ClassificationSourceEvidence>> Analyze([FromQuery] string environmentId, CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest("Upload source archives as multipart form data.");
        var form = await Request.ReadFormAsync(ct);
        if (form.Files.Count is 0 or > 4) return BadRequest("Upload one to four source archives (.zip).");
        var archives = new List<(string, byte[])>();
        foreach (var file in form.Files)
        {
            if (file.Length > ScimSourceReader.MaxArchiveBytes) return BadRequest($"{file.FileName} is larger than the upload limit.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            archives.Add((file.FileName, buffer.ToArray()));
        }
        var (evidence, error) = await reviews.AnalyzeAsync(environmentId, archives, ct);
        return error is not null ? BadRequest(error) : Ok(evidence);
    }

    [HttpPut("context")]
    public async Task<ActionResult<ClassificationTestContext>> SaveContext([FromQuery] string environmentId, [FromBody] ClassificationTestContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var (saved, error) = await reviews.SaveContextAsync(environmentId, context, ct);
        return error is not null ? BadRequest(error) : Ok(saved);
    }

    [HttpPost("runs")]
    public async Task<ActionResult<ClassificationReviewResult>> Run([FromQuery] string environmentId, [FromBody] ClassificationRunRequest request, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await reviews.RunAsync(environmentId, request, ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ClassificationReviewResult>> GetRun(Guid runId, CancellationToken ct) =>
        await reviews.GetRunAsync(runId, ct) is { } run ? Ok(run) : NotFound();
}
