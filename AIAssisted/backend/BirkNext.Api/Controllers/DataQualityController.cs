using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.DataQuality;
using BirkNext.DataQuality;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Controllers;

/// <summary>
/// System Settings → Data Quality (2026-11-27): user-initiated, read-only audits of a project's dashboard figures, requirement identities and
/// source/import. Launching an audit and reading its record-level details needs the same permission as changing system settings. Nothing in the
/// business domain is written; finished runs go to the bounded diagnostics store only.
/// </summary>
[ApiController]
[Route("api/data-quality")]
[Authorize(Policy = BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.Policy)]
public sealed class DataQualityController(DataQualityRunCoordinator runs) : ControllerBase
{
    /// <summary>Projects that have a specification catalog or a discovery, with their catalog revisions (newest first). Cheap; no audit runs.</summary>
    [HttpGet("projects")]
    public async Task<ActionResult<List<DataQualityProjectOption>>> Projects([FromServices] AppDbContext db, CancellationToken ct)
    {
        var catalogs = await db.SpecificationCatalogs.AsNoTracking()
            .Select(c => new { c.Id, c.ProjectKey, c.Revision, c.ArchiveSha256, c.CapturedAt }).ToListAsync(ct);
        var discovered = await db.ScenarioDiscoveryResults.AsNoTracking().Select(d => new { d.ProjectKey, d.ApplicationKey }).Distinct().ToListAsync(ct);
        var keys = catalogs.Select(c => c.ProjectKey).Concat(discovered.Select(d => d.ProjectKey)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        return keys.Select(k => new DataQualityProjectOption(k, k, [.. catalogs.Where(c => c.ProjectKey == k).OrderByDescending(c => c.Revision).Take(10).Select(c => new DataQualityBaseline
        {
            CatalogId = c.Id, CatalogProjectKey = c.ProjectKey, Revision = c.Revision, ArchiveSha256 = c.ArchiveSha256, CapturedAt = c.CapturedAt,
            Application = discovered.FirstOrDefault(d => d.ProjectKey == k && d.ApplicationKey is not null)?.ApplicationKey,
            Label = $"Revisjon {c.Revision} · {c.CapturedAt:yyyy-MM-dd HH:mm}{(c.ArchiveSha256 is { Length: >= 12 } sha ? $" · arkiv {sha[..12]}" : "")}",
        })])).ToList();
    }

    [HttpPost("runs")]
    public ActionResult<DataQualityRun> Start([FromBody] DataQualityRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectKey) || request.ProjectKey.Length > 300) return BadRequest(new { error = "Velg et prosjekt." });
        if (request.Categories is not { Count: > 0 }) return BadRequest(new { error = "Velg minst én kontrollkategori." });
        try
        {
            var run = runs.Start(request with { ProjectKey = request.ProjectKey.Trim() }, User.Identity?.Name ?? "lokal bruker");
            return Accepted($"api/data-quality/runs/{run.RunId}", run);
        }
        catch (DataQualityRunConflictException e) { return Conflict(new { error = e.Message }); }
    }

    [HttpGet("runs")]
    public ActionResult<List<DataQualityRunSummary>> Recent() => runs.Recent();

    [HttpGet("runs/{runId}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ActionResult<DataQualityRun> Get(string runId) => runs.Get(runId) is { } run ? run : NotFound();

    [HttpPost("runs/{runId}/cancel")]
    public ActionResult<DataQualityRun> Cancel(string runId) => runs.Cancel(runId) is { } run ? run : NotFound();

    /// <summary>The browser stage's production values (API → UI), kept with the run's report in the diagnostics store.</summary>
    [HttpPost("runs/{runId}/ui-values")]
    public ActionResult<DataQualityRun> UiValues(string runId, [FromBody] List<DataQualityUiValue> values) =>
        values.Count > 5000 ? BadRequest() : runs.AttachUiValues(runId, values) is { } run ? run : NotFound();

    [HttpGet("runs/{runId}/report.md")]
    public IActionResult Markdown(string runId) => runs.Get(runId)?.Report is { } r
        ? File(Encoding.UTF8.GetBytes(DataQualityMarkdown.Write(r)), "text/markdown; charset=utf-8", $"birknext-data-quality-{runId}.md") : NotFound();

    [HttpGet("runs/{runId}/report.json")]
    public IActionResult Json(string runId) => runs.Get(runId)?.Report is { } r
        ? File(JsonSerializer.SerializeToUtf8Bytes(r, DataQualityRunStore.Json), "application/json", $"birknext-data-quality-{runId}.json") : NotFound();
}
