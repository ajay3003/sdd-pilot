using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Integration Quality Review → Evidence: the canonical source-evidence binding of each configured integration (one immutable Source Analysis
/// snapshot). Reads stay open (ids and fingerprints only); binding requires the integration-configuration write permission, because test packs
/// and providers read these bindings instead of selecting snapshots of their own. Nothing is analyzed or contacted.
/// </summary>
[ApiController]
[Route("api/integration-review/evidence-bindings")]
public sealed class IntegrationEvidenceBindingsController(IIntegrationEvidenceBindingStore bindings) : ControllerBase
{
    /// <summary>The configuration values the integration's bound snapshot declares (semantic key, value, provenance, agreement/conflict), for
    /// consumers that offer them as suggestions. Unbound: no values — the newest snapshot never stands in. Read-only; nothing is applied.</summary>
    [HttpGet("{integrationId}/configuration-values")]
    public async Task<ActionResult<IntegrationSourceConfiguration>> ConfigurationValues(string integrationId, [FromQuery] string environmentId,
        [FromServices] IqrSourceStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var binding = (await bindings.ListAsync(environmentId, ct)).FirstOrDefault(item => item.IntegrationId == integrationId);
        if (binding is null) return Ok(new IntegrationSourceConfiguration(integrationId, null, []));
        var snapshot = await store.FindSourceAnalysisAsync(binding.SnapshotId, ct);
        return Ok(new IntegrationSourceConfiguration(integrationId, binding,
            BirkNext.SourceDomains.SourceConfigurationResolver.Resolve(BirkNext.Api.Services.SourceAnalysis.Evidence.SourceConfigurationValueCollector.Values(snapshot?.EvidenceDomains))));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IntegrationEvidenceBinding>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await bindings.ListAsync(environmentId, ct));

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut]
    public async Task<ActionResult<IntegrationEvidenceBinding?>> Set([FromQuery] string environmentId, [FromBody] IntegrationEvidenceBindingRequest request, CancellationToken ct)
    {
        var (binding, error) = await bindings.SetAsync(environmentId, request, ct);
        return error is null ? Ok(binding) : BadRequest(new { message = error });
    }
}
