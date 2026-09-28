using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Application messaging (Wolverine) evidence for a Target Environment: upload source archives for read-only syntax analysis, read the
/// extracted evidence, bind an analyzed application to an Integrations consumer. Uploaded source is analyzed in memory and never stored.
/// </summary>
[ApiController]
[Route("api/integrations/application-messaging")]
public sealed class ApplicationMessagingController(IApplicationMessagingStore store) : ControllerBase
{
    public sealed record BindingRequest(string? Consumer);

    [HttpGet]
    public async Task<ActionResult<ApplicationMessagingEvidenceSet>> Get([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await store.GetAsync(environmentId, ct) is { } set ? Ok(set) : NoContent();
    }

    [HttpPost]
    [RequestSizeLimit(4 * SourceArchiveReader.MaxArchiveBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = 4 * SourceArchiveReader.MaxArchiveBytes)]
    public async Task<ActionResult<ApplicationMessagingEvidenceSet>> Analyze([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (!Request.HasFormContentType) return BadRequest("Upload the source archives as multipart form data.");
        var form = await Request.ReadFormAsync(ct);
        if (form.Files.Count is 0 or > 4) return BadRequest("Upload one to four source archives (.zip).");
        var archives = new List<(string, byte[])>();
        foreach (var file in form.Files)
        {
            if (file.Length > SourceArchiveReader.MaxArchiveBytes) return BadRequest($"{file.FileName} is larger than the {SourceArchiveReader.MaxArchiveBytes / (1024 * 1024)} MB limit.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            archives.Add((file.FileName, buffer.ToArray()));
        }
        var (set, error) = await store.AnalyzeAsync(environmentId, archives, ct);
        return error is not null ? BadRequest(error) : Ok(set);
    }

    [HttpPut("{applicationId}/binding")]
    public async Task<ActionResult<ApplicationMessagingEvidenceSet>> Bind([FromQuery] string environmentId, string applicationId, [FromBody] BindingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await store.BindAsync(environmentId, applicationId, request.Consumer, ct) is { } set ? Ok(set) : NotFound();
    }

    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : await store.DeleteAsync(environmentId, ct) ? NoContent() : NotFound();
}
