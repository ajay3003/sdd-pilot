using BirkNext.Api.Services.DependencyReview;
using BirkNext.Dependencies;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Dependency / supply-chain review (Renovate policy). Read-only: uploaded archives and configs are analyzed in memory; nothing is written to a
/// repository, no branch/commit/PR is created and Renovate is never triggered. Simulations use synthetic candidate versions only.
/// </summary>
[ApiController]
[Route("api/dependency-review")]
public sealed class DependencyReviewController(IDependencyReviewService reviews) : ControllerBase
{
    [HttpPost("runs")]
    [RequestSizeLimit(4 * DependencyReviewService.MaxArchiveBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = 4 * DependencyReviewService.MaxArchiveBytes)]
    public async Task<ActionResult<DependencyReviewResult>> Run(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest("Upload repository archives as multipart form data.");
        var form = await Request.ReadFormAsync(ct);
        var archives = new List<(string, byte[])>();
        var overrides = new List<(string, string, string)>();
        foreach (var file in form.Files)
        {
            if (file.Length > DependencyReviewService.MaxArchiveBytes) return BadRequest($"{file.FileName} is larger than the upload limit.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            // Form field "config:<repository>" = a separately supplied Renovate config for that repository; everything else is an archive.
            if (file.Name.StartsWith("config:", StringComparison.Ordinal))
                overrides.Add((file.Name["config:".Length..], file.FileName, System.Text.Encoding.UTF8.GetString(buffer.ToArray())));
            else archives.Add((file.FileName, buffer.ToArray()));
        }
        if (archives.Count is 0 or > 4) return BadRequest("Upload one to four repository archives (.zip).");
        var (result, error) = await reviews.RunAsync(form["label"].ToString(), archives, overrides, ct);
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
}
