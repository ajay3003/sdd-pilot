using BirkNext.Api.Services.Integrations.SourceDiscovery;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Source integrations of a Target Environment: the latest stored source snapshot's integration discovery reconciled with the configured
/// catalog. Source-only and read-only — no runtime system is contacted, nothing is written, no mapping is confirmed.
/// </summary>
[ApiController]
[Route("api/integrations/source-integrations")]
public sealed class SourceIntegrationsController(SourceIntegrationService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SourceIntegrationsReport>> Get([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await service.ReportAsync(environmentId, ct));

    /// <summary>"Discover from source": re-runs discovery on the stored source snapshot (no archive re-processing, no runtime call).</summary>
    [HttpPost("discover")]
    public async Task<ActionResult<SourceIntegrationsReport>> Discover([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await service.ReportAsync(environmentId, ct));
}
