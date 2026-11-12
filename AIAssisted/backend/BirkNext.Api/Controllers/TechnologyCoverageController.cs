using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Technology;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Technology &amp; Analysis Coverage: what the selected environment's latest Source Analysis snapshot contains (including technologies BirkNext
/// does not analyze), which integrations are configured, and which domain extensions are enabled. Read-only: never applies a template, never
/// rescans an archive. The support matrix itself is the shared <see cref="TechnologySupportRegistry"/>.
/// </summary>
[ApiController]
[Route("api/technology-coverage")]
public sealed class TechnologyCoverageController(IqrSourceStore sources, IIntegrationCatalogService catalog,
    BirkNext.Api.Services.SourceAnalysis.SourceBaselineService? baselines = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProjectTechnologyCoverage>> Get([FromQuery] string? environmentId, CancellationToken ct, [FromQuery] Guid? snapshotId = null)
    {
        // Source technology comes from the workspace's current source snapshot and needs no Target Environment. Configured integrations
        // belong to a target: without one there are none to list (not "none configured").
        // The application's own source when the caller names it (Project Evidence's main package); otherwise the workspace's newest snapshot,
        // which may belong to another package (a shared library or an infrastructure repository imported last).
        // Without a named snapshot: the newest of the snapshots the shared source context uses (one per logical source) — never an unselected
        // alternative branch snapshot of a source that has an analysis baseline.
        // When the workspace's newest import belongs to a source with alternative versions and no analysis baseline (BASELINE_NOT_SELECTED), no
        // coverage is reported: neither the newest alternative nor another source's snapshot stands in for it.
        var notices = new List<string>();
        BirkNext.Integrations.IqrSourceSnapshot? latest;
        if (snapshotId is { } requested && await sources.FindSourceAnalysisAsync(requested, ct) is { } named) latest = named;
        else if (baselines is null) latest = (await sources.ListSourceAnalysisAsync(1, ct)).FirstOrDefault();
        else
        {
            var window = await sources.ListSourceAnalysisAsync(BirkNext.Api.Services.SourceAnalysis.SourceBaselineService.Window, ct);
            var resolved = await baselines.ResolveAsync(window, ct);
            var newest = window.OrderByDescending(s => s.AnalyzedAt).ThenByDescending(s => s.Id).FirstOrDefault();
            var key = newest is null ? null : BirkNext.Api.Services.SourceAnalysis.SourceBaselineService.KeyOf(newest);
            if (newest is not null && resolved.All(s => BirkNext.Api.Services.SourceAnalysis.SourceBaselineService.KeyOf(s) != key))
            {
                latest = null;
                notices.Add($"BASELINE_NOT_SELECTED: {Services.SourceAnalysis.ReviewSourceEvidenceProvider.Identity(newest).DisplayName} has alternative source versions and no analysis baseline. " +
                    "Select it in Source Management → Source versions; no coverage is derived from import order.");
            }
            else latest = resolved.MaxBy(s => s.AnalyzedAt);
        }
        var hasTarget = !string.IsNullOrWhiteSpace(environmentId);
        // No environment type: a read-only check never upgrades or applies a template.
        var configured = hasTarget ? await catalog.GetAsync(environmentId!, null, null, ct) : new BirkNext.Integrations.IntegrationCatalog();
        if (latest is null) { if (notices.Count == 0) notices.Add("No source archive has been analyzed yet."); }
        else if (latest.TechnologyCoverage is null) notices.Add("The latest snapshot was analyzed before technology inventory existed; analyze the source again to see it.");
        IReadOnlyList<BirkNext.Integrations.SourceSetupArea> areas = latest is null ? [] : Services.IntegrationJourneys.SourceSetupAreas.Of(latest);
        return Ok(new ProjectTechnologyCoverage
        {
            EnvironmentId = environmentId ?? "", SourceSnapshotId = latest?.Id, SourceArchive = latest?.Archive.FileName, AnalyzedAt = latest?.AnalyzedAt,
            Source = latest?.TechnologyCoverage,
            ConfiguredIntegrations = configured.Integrations.Where(i => i.Enabled).Select(i => IntegrationTechnology.Map(i.Kind, i.DisplayName, i.SystemName))
                .Concat(configured.Platforms.Where(p => p.Enabled).Select(p => IntegrationTechnology.Map(p.Kind, p.Name)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            DomainExtensions = configured.DomainExtensions, Notices = notices,
            CiCdEvidenceVersion = latest?.EvidenceDomains?.CiCd.AnalyzerVersion,
            SourceOutcome = latest is null ? null : BirkNext.Integrations.SourceSetupOutcome.Overall(areas), SourceAreas = [.. areas],
            // Mirrors Pipeline Review: no CI/CD domain, or an older analyzer that found pipelines, needs re-analysis (v1 without pipelines is N/A).
            CiCdEvidenceOutdated = latest is not null && (latest.EvidenceDomains?.CiCd is not { } cicd
                || cicd.AnalyzerVersion < BirkNext.PipelineReview.PipelineReviewText.RequiredCiCdVersion && cicd.Pipelines.Count > 0),
        });
    }
}
