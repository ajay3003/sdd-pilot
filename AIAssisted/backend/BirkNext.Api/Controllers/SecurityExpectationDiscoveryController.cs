using BirkNext.Api.Services.SecurityExpectations;
using BirkNext.SecurityExpectations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/target-environments/{environmentId}/security-expectations")]
public sealed class SecurityExpectationDiscoveryController(ISecurityExpectationDiscoveryService service) : ControllerBase
{
    [HttpGet("source-snapshot")]
    public async Task<ActionResult<BirkNext.Integrations.IqrSourceSnapshot>> Source(string environmentId, CancellationToken ct) =>
        Ok(await service.CurrentSourceAsync(environmentId, ct));
    [HttpGet("discovery")]
    public async Task<ActionResult<IReadOnlyList<SecurityExpectationDiscoveryResult>>> List(string environmentId, CancellationToken ct) =>
        Ok(await service.ListAsync(environmentId, ct));
    [HttpPost("discover")]
    public async Task<ActionResult<SecurityExpectationDiscoveryResult>> Discover(string environmentId, SecurityDiscoveryRequest request, CancellationToken ct) {
        try { return Ok(await service.DiscoverAsync(environmentId, request, ct)); }
        catch(SecurityDiscoveryReviewException e) { return Conflict(new { message=e.Message }); }
    }
    [HttpPost("accept")]
    public Task<ActionResult<SecurityCandidateReviewResponse>> Accept(string environmentId, SecurityCandidateReviewRequest request, CancellationToken ct) => Review(environmentId,request,true,ct);
    [HttpPost("reject")]
    public Task<ActionResult<SecurityCandidateReviewResponse>> Reject(string environmentId, SecurityCandidateReviewRequest request, CancellationToken ct) => Review(environmentId,request,false,ct);
    private async Task<ActionResult<SecurityCandidateReviewResponse>> Review(string environmentId, SecurityCandidateReviewRequest request, bool accept, CancellationToken ct) {
        try { return Ok(await service.ReviewAsync(environmentId,request,accept,ct)); }
        catch(SecurityDiscoveryReviewException e) { return Conflict(new { message=e.Message }); }
    }
}
