using BirkNext.Api.Services.PipelineReview;
using BirkNext.Delivery;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>Shared Pipeline &amp; Delivery Evidence: provider capabilities and the normalized delivery evidence of one exact Source Analysis
/// snapshot. GET only; no YAML is read here and no Azure DevOps call is made.</summary>
[ApiController]
[Route("api/delivery-evidence")]
public sealed class DeliveryEvidenceController(DeliveryEvidenceService service) : ControllerBase
{
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<DeliveryEvidenceProviderDescriptor>> Providers() => Ok(DeliveryEvidenceService.Providers());

    [HttpGet("{snapshotId:guid}")]
    public async Task<ActionResult<DeliveryEvidence>> Get(Guid snapshotId, [FromQuery] string? environmentId, CancellationToken ct) =>
        await service.GetAsync(environmentId ?? "", snapshotId, ct) is { } evidence ? Ok(evidence) : NotFound();
}
