using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/project-documents")]
public class ProjectDocumentsController : ControllerBase
{
    private readonly ProjectDocumentService _service;

    public ProjectDocumentsController(ProjectDocumentService service) { _service = service; }

    [HttpGet("{kind}")]
    public async Task<IActionResult> GetDocument(string kind, CancellationToken ct)
    {
        if (!Enum.TryParse<ProjectDocumentKind>(kind, ignoreCase: true, out var parsedKind))
            return BadRequest(new { error = $"Unknown document kind '{kind}'. Valid values: constitution, plan, tasks." });

        var content = await _service.GetContentAsync(parsedKind, ct);
        if (content is null) return NoContent();

        return Ok(new { documentKind = parsedKind.ToString(), content });
    }

    [HttpPut("{kind}")]
    public async Task<IActionResult> UpsertDocument(string kind, [FromBody] UpsertDocumentRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<ProjectDocumentKind>(kind, ignoreCase: true, out var parsedKind))
            return BadRequest(new { error = $"Unknown document kind '{kind}'. Valid values: constitution, plan, tasks." });

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Content is required." });

        await _service.UpsertAsync(parsedKind, request.Content, ct);
        return NoContent();
    }

    [HttpGet("")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var summary = await _service.GetSummaryAsync(ct);
        return Ok(summary.Select(s => new
        {
            documentKind       = s.DocumentKind,
            contentLengthChars = s.ContentLengthChars,
            updatedUtc         = s.UpdatedUtc
        }));
    }
}

public class UpsertDocumentRequest { public string Content { get; set; } = string.Empty; }
