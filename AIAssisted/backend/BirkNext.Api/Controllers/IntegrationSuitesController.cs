using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.IntegrationSuites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Generated integration suites. Generating writes drafts to their existing owners (Contract Registry/Testing, Test Data) and a reference
/// manifest — never a network call. Executing is a separate, explicitly authorised action that needs the same permission as contract runs.
/// </summary>
[ApiController]
[Route("api/integration-suites")]
public sealed class IntegrationSuitesController(IntegrationSuiteService suites) : ControllerBase
{
    private string Initiator => User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";

    [HttpGet("scopes")]
    public ActionResult<IReadOnlyList<IntegrationSuiteScope>> Scopes() => Ok(IntegrationSuiteComposer.Scopes);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IntegrationSuiteSummary>>> List(CancellationToken ct) => Ok(await suites.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<IntegrationSuite>> Get(Guid id, CancellationToken ct) => await suites.GetAsync(id, ct) is { } s ? Ok(s) : NotFound();

    [HttpPost]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<IntegrationSuiteGenerateResult>> Generate([FromBody] IntegrationSuiteGenerateRequest request, CancellationToken ct)
    {
        var result = await suites.GenerateAsync(request, Initiator, ct);
        return result.Error is null ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/reviews")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> Review(Guid id, [FromBody] IntegrationSuiteReviewRequest request, CancellationToken ct)
    {
        var (decision, error) = await suites.ReviewAsync(id, request, Initiator, ct);
        return error is null ? Ok(decision) : error.Contains("not found", StringComparison.OrdinalIgnoreCase) ? NotFound(new { error }) : BadRequest(new { error });
    }

    [HttpPost("{id:guid}/runs")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    public async Task<ActionResult<IntegrationSuiteExecuteResult>> Execute(Guid id, [FromBody] IntegrationSuiteExecuteRequest request, CancellationToken ct)
    {
        var result = await suites.ExecuteAsync(id, request, Initiator, ct);
        return result.State switch { "NotFound" => NotFound(result), "NotAuthorized" or "Refused" => BadRequest(result), _ => Ok(result) };
    }
}
