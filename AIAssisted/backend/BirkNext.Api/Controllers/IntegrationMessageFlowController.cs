using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/integration-message-flow")]
public sealed class IntegrationMessageFlowController(IIntegrationMessageFlowStore store) : ControllerBase
{
    private static MessageFlowSaveProblem Problem(string message, string property) =>
        new(message, [new ConfigurationFieldError("Contracts & correlation", "Source XML Schema contract", $"altinnConfiguration.{char.ToLowerInvariant(property[0])}{property[1..]}", message)]);

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

    /// <summary>Message-flow test configuration is IQR configuration (journey readiness reads it): writes need IntegrationConfiguration.Write.</summary>
    [Authorize(Policy = BirkNext.Api.Services.ActiveEventTesting.BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut("{environmentId}")]
    public async Task<ActionResult<IntegrationMessageFlowPackage>> Save(string environmentId, [FromBody] IntegrationMessageFlowPackage request,
        [FromServices] BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider sources, CancellationToken ct)
    {
        // A snapshot reference only means something together with a selected XML Schema contract (the schema's snapshot). Without a contract
        // it is not stored: the canonical source evidence is the IQR binding, never a copy in this configuration.
        if (string.IsNullOrWhiteSpace(request.AltinnConfiguration.SourceContractId))
            request = request with { AltinnConfiguration = request.AltinnConfiguration with { SourceSnapshotId = null, SourceContractId = null } };
        else if (request.AltinnConfiguration.SourceSnapshotId is not { } snapshotId)
            return BadRequest(Problem("The selected XML Schema contract has no Source Analysis snapshot.", "SourceContractId"));
        else
        {
            if (!sources.SourceAnalysisEnabled) return Conflict(Problem("Source Analysis is unavailable; remove the source contract selection or enable the feature.", "SourceContractId"));
            var snapshot = await sources.ResolveAsync(environmentId, snapshotId, ct);
            if (snapshot is null || snapshot.EvidenceDomains?.Contracts.Contracts.All(c => c.Id != request.AltinnConfiguration.SourceContractId || c.Type != BirkNext.SourceDomains.SourceContractType.XmlSchema) != false)
                return BadRequest(Problem("The selected XML Schema contract is not present in the exact selected Source Analysis snapshot.", "SourceContractId"));
        }
        var (package, problem) = await store.SaveDetailedAsync(environmentId, request, ct);
        return package is null ? BadRequest(problem) : Ok(package);
    }
}
