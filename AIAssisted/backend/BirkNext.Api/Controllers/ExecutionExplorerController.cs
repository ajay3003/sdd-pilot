using System.Text;
using BirkNext.Api.Services.ExecutionExplorer;
using BirkNext.ExecutionExplorer;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test Execution &amp; Evidence: a read-only projection over the existing evidence owners. Every endpoint is a GET that reads stored results; none
/// executes, re-sends, deletes or changes anything (protection, deletion and retention stay on the Execution Evidence endpoints and their policies).
/// </summary>
[ApiController]
[Route("api/execution-explorer")]
public sealed class ExecutionExplorerController(ExecutionExplorerService explorer) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<ExplorerOverview>> Overview([FromQuery] string? environmentId, [FromQuery] string? project, [FromQuery] bool unattributed = false,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null, CancellationToken ct = default) =>
        Ok(await explorer.OverviewAsync(new ExplorerQuery { EnvironmentId = Blank(environmentId), ProjectKey = Blank(project), IncludeUnattributed = unattributed, From = from, To = to }, ct));

    [HttpGet("runs")]
    public async Task<ActionResult<ExplorerPage>> Runs([FromQuery] string? environmentId, [FromQuery] string? project, [FromQuery] bool unattributed = false,
        [FromQuery] string? sources = null, [FromQuery] string? categories = null, [FromQuery] string? outcomes = null, [FromQuery] string? search = null,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null, [FromQuery(Name = "protected")] bool? isProtected = null,
        [FromQuery] EvidenceCompleteness? evidence = null, [FromQuery(Name = "static")] bool includeStatic = false, [FromQuery] int skip = 0, [FromQuery] int take = 25,
        CancellationToken ct = default) =>
        Ok(await explorer.ListAsync(new ExplorerQuery
        {
            EnvironmentId = Blank(environmentId), ProjectKey = Blank(project), IncludeUnattributed = unattributed, Sources = Enums<ExplorerSource>(sources),
            Categories = Enums<ExplorerCategory>(categories), Outcomes = Enums<ExplorerOutcome>(outcomes), Search = Blank(search), From = from, To = to, Protected = isProtected,
            Evidence = evidence, IncludeStatic = includeStatic, Skip = skip, Take = take,
        }, ct));

    [HttpGet("runs/{source}/{id}")]
    public async Task<ActionResult<ExplorerRunDetail>> Run(ExplorerSource source, string id, CancellationToken ct) =>
        await explorer.DetailAsync(source, id, ct) is { } detail ? Ok(detail) : NotFound(new { message = "No stored result with this reference." });

    [HttpGet("compare")]
    public async Task<ActionResult<ExplorerComparison>> Compare([FromQuery] ExplorerSource source, [FromQuery] string baseline, [FromQuery] string current, CancellationToken ct) =>
        Ok(await explorer.CompareAsync(source, baseline, current, ct));

    /// <summary>A sanitized Markdown report generated on demand from stored evidence (no bodies, headers or values; bounded).</summary>
    [HttpGet("runs/{source}/{id}/report")]
    public async Task<IActionResult> Report(ExplorerSource source, string id, CancellationToken ct) =>
        await explorer.ReportAsync(source, id, ct) is { } report ? File(Encoding.UTF8.GetBytes(report.Markdown), "text/markdown; charset=utf-8", report.FileName) : NotFound();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<T> Enums<T>(string? csv) where T : struct, Enum =>
        [.. (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => Enum.TryParse<T>(v, true, out var e) ? (T?)e : null).OfType<T>().Distinct()];
}
