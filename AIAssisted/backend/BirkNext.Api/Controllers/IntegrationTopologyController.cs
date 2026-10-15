using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.IntegrationTopology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Integration Topology: the canonical, read-only view of an environment's components, interfaces and connections, rebuilt from its sources
/// on every read. Observations the browser holds may be supplied for merging (nothing is stored). Reading OpenAPI documents of trusted
/// components is an explicit discovery action and needs the integration-configuration permission.
/// </summary>
[ApiController]
[Route("api/integration-topology")]
public sealed class IntegrationTopologyController(IntegrationTopologyService topology) : ControllerBase
{
    [HttpGet("{environmentId}")]
    public async Task<ActionResult<IntegrationTopologyView>> Get(string environmentId, CancellationToken ct) =>
        Ok((await topology.BuildAsync(environmentId, new TopologyRequest(null), ct)).View);

    /// <summary>The topology with runtime observations merged as Observed evidence (method, host and path only).</summary>
    [HttpPost("{environmentId}")]
    public async Task<ActionResult<IntegrationTopologyView>> WithObservations(string environmentId, [FromBody] TopologyRequest request, CancellationToken ct) =>
        Ok((await topology.BuildAsync(environmentId, request with { DiscoverContracts = false }, ct)).View);

    /// <summary>Reads the OpenAPI documents of trusted components that declare one (their own host only), then returns the topology.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{environmentId}/discover")]
    public async Task<ActionResult<IntegrationTopologyView>> Discover(string environmentId, [FromBody] TopologyRequest request, CancellationToken ct) =>
        Ok((await topology.BuildAsync(environmentId, request with { DiscoverContracts = true }, ct)).View);

    [HttpPost("{environmentId}/profiles/{profileId}/paths")]
    public async Task<ActionResult<IReadOnlyList<TopologyPathResult>>> Paths(string environmentId, string profileId, [FromBody] TopologyRequest? request, CancellationToken ct) =>
        await topology.PathsAsync(environmentId, profileId, (request ?? new TopologyRequest(null)) with { DiscoverContracts = false }, ct) is { } paths ? Ok(paths) : NotFound();
}
