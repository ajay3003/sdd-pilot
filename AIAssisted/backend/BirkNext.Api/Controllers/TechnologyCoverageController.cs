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
public sealed class TechnologyCoverageController(IqrSourceStore sources, IIntegrationCatalogService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProjectTechnologyCoverage>> Get([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var latest = (await sources.ListSourceAnalysisAsync(environmentId, 1, ct)).FirstOrDefault();
        // No environment type: a read-only check never upgrades or applies a template.
        var configured = await catalog.GetAsync(environmentId, null, null, ct);
        var notices = new List<string>();
        if (latest is null) notices.Add("No source archive has been analyzed for this environment.");
        else if (latest.TechnologyCoverage is null) notices.Add("The latest snapshot was analyzed before technology inventory existed; analyze the source again to see it.");
        return Ok(new ProjectTechnologyCoverage
        {
            EnvironmentId = environmentId, SourceSnapshotId = latest?.Id, SourceArchive = latest?.Archive.FileName, AnalyzedAt = latest?.AnalyzedAt,
            Source = latest?.TechnologyCoverage,
            ConfiguredIntegrations = configured.Integrations.Where(i => i.Enabled).Select(i => IntegrationTechnology.Map(i.Kind, i.DisplayName, i.SystemName))
                .Concat(configured.Platforms.Where(p => p.Enabled).Select(p => IntegrationTechnology.Map(p.Kind, p.Name)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            DomainExtensions = configured.DomainExtensions, Notices = notices,
            CiCdEvidenceVersion = latest?.EvidenceDomains?.CiCd.AnalyzerVersion,
            // Mirrors Pipeline Review: no CI/CD domain, or an older analyzer that found pipelines, needs re-analysis (v1 without pipelines is N/A).
            CiCdEvidenceOutdated = latest is not null && (latest.EvidenceDomains?.CiCd is not { } cicd
                || cicd.AnalyzerVersion < BirkNext.PipelineReview.PipelineReviewText.RequiredCiCdVersion && cicd.Pipelines.Count > 0),
        });
    }
}
