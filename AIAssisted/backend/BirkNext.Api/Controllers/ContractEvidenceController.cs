using BirkNext.Api.Services.SchemaEvidence;
using BirkNext.ContractSchemaEvidence;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Shared Contract &amp; Schema Evidence: provider capabilities and read-only queries over the contract evidence an exact Source Analysis
/// snapshot stored. GET only — nothing is parsed again, fetched, selected, bound or stored here.
/// </summary>
[ApiController]
[Route("api/contract-evidence")]
public sealed class ContractEvidenceController(ContractEvidenceService service) : ControllerBase
{
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<ContractEvidenceProviderStatus>> Providers() => Ok(ContractEvidenceService.Providers());

    [HttpGet("{snapshotId:guid}")]
    public async Task<ActionResult<ContractEvidenceView>> Get(Guid snapshotId, [FromQuery] string? environmentId, CancellationToken ct) =>
        await service.GetAsync(environmentId ?? "", snapshotId, ct) is { } view ? Ok(view) : NotFound();

    [HttpGet("{snapshotId:guid}/constraints")]
    public async Task<ActionResult<IReadOnlyList<ContractConstraintFact>>> Constraints(Guid snapshotId, [FromQuery] string contract, [FromQuery] string? environmentId, CancellationToken ct) =>
        await service.ConstraintsAsync(environmentId ?? "", snapshotId, contract, ct) is { } constraints ? Ok(constraints) : NotFound();

    [HttpGet("compare")]
    public async Task<ActionResult<IReadOnlyList<ContractChangeFact>>> Compare([FromQuery] Guid before, [FromQuery] Guid after, [FromQuery] string contract,
        [FromQuery] string? environmentId, CancellationToken ct) =>
        await service.CompareAsync(environmentId ?? "", before, after, contract, ct) is { } changes ? Ok(changes) : NotFound();
}
