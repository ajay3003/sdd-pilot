using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/integration-message-flow")]
public sealed class IntegrationMessageFlowController(IIntegrationMessageFlowStore store) : ControllerBase
{
    [HttpGet("{environmentId}/source-contracts/{snapshotId:guid}")]
    public async Task<ActionResult<MessageFlowSourceContractOptions>> SourceContracts(string environmentId, Guid snapshotId,
        [FromServices] BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider sources, CancellationToken ct)
    {
        if (!sources.SourceAnalysisEnabled) return Conflict(new { message = "Source Analysis is unavailable in Feature Visibility." });
        var snapshot = await sources.ResolveAsync(environmentId, snapshotId, ct);
        if (snapshot is null) return NotFound();
        var contracts = snapshot.EvidenceDomains?.Contracts.Contracts.Where(c => c.Type == BirkNext.SourceDomains.SourceContractType.XmlSchema).ToList() ?? [];
        return Ok(new MessageFlowSourceContractOptions(snapshot.Id, snapshot.Archive.FileName, snapshot.Archive.Sha256, contracts));
    }

    [HttpGet("{environmentId}")]
    public async Task<ActionResult<IntegrationMessageFlowPackage>> Get(string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await store.GetAsync(environmentId, ct));

    [HttpPut("{environmentId}")]
    public async Task<ActionResult<IntegrationMessageFlowPackage>> Save(string environmentId, [FromBody] IntegrationMessageFlowPackage request,
        [FromServices] BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider sources, CancellationToken ct)
    {
        if (request.AltinnConfiguration.SourceSnapshotId is { } snapshotId)
        {
            if (!sources.SourceAnalysisEnabled) return Conflict(new { message = "Source Analysis is unavailable; remove the source contract binding or enable the feature." });
            var snapshot = await sources.ResolveAsync(environmentId, snapshotId, ct);
            if (snapshot is null || string.IsNullOrWhiteSpace(request.AltinnConfiguration.SourceContractId) ||
                snapshot.EvidenceDomains?.Contracts.Contracts.All(c => c.Id != request.AltinnConfiguration.SourceContractId || c.Type != BirkNext.SourceDomains.SourceContractType.XmlSchema) != false)
                return BadRequest(new { message = "The selected XML Schema contract is not present in the exact selected Source Analysis snapshot." });
        }
        var (package, error) = await store.SaveAsync(environmentId, request, ct);
        return package is null ? BadRequest(new { message = error }) : Ok(package);
    }
}
