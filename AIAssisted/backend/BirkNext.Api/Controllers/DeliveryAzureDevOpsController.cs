using BirkNext.Api.Services.DeliveryReporting.AzureDevOps;
using BirkNext.DeliveryReporting;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Delivery Reporting → Import snapshot from Azure DevOps: connections (the PAT is accepted on save only, stored encrypted and never returned),
/// explicit validation, project/repository/branch discovery and commit resolution — all read-only against Azure DevOps. The preview hands the
/// resolved commit's tree to the same import pipeline as a ZIP; confirmation uses the normal commit endpoint.
/// </summary>
[ApiController]
[Route("api/delivery-reporting/azure-devops")]
public sealed class DeliveryAzureDevOpsController(AzureDevOpsConnectionStore connections, AzureDevOpsGitClient git, DeliveryAzureDevOpsImportService imports, TimeProvider clock) : ControllerBase
{
    [HttpGet("connections")]
    public ActionResult<IReadOnlyList<AzureDevOpsConnectionView>> Connections() => Ok(connections.List());

    [HttpPost("connections")]
    public IActionResult Create([FromBody] AzureDevOpsConnectionRequest request)
    {
        var (connection, error) = connections.Save(null, request);
        return connection is null ? BadRequest(new { message = error }) : Ok(connection);
    }

    [HttpPut("connections/{id:guid}")]
    public IActionResult Update(Guid id, [FromBody] AzureDevOpsConnectionRequest request)
    {
        var (connection, error) = connections.Save(id, request);
        return connection is null ? BadRequest(new { message = error }) : Ok(connection);
    }

    /// <summary>Removes the connection only; imported snapshots and reports stay as they are.</summary>
    [HttpDelete("connections/{id:guid}")]
    public IActionResult Delete(Guid id) => connections.Delete(id) ? NoContent() : NotFound(new { message = "The connection does not exist." });

    /// <summary>Explicit validation (opening the page never contacts Azure DevOps).</summary>
    [HttpPost("connections/{id:guid}/validate")]
    public async Task<ActionResult<AzureDevOpsValidationResult>> Validate(Guid id, CancellationToken ct)
    {
        var result = await git.ValidateAsync(id, ct);
        connections.RecordValidation(id, result.Status, clock.GetUtcNow());
        return Ok(result);
    }

    [HttpGet("connections/{id:guid}/projects")]
    public Task<ActionResult<AzureDevOpsList<AzureDevOpsProjectItem>>> Projects(Guid id, CancellationToken ct) => List(() => git.ProjectsAsync(id, ct));

    [HttpGet("connections/{id:guid}/repositories")]
    public Task<ActionResult<AzureDevOpsList<AzureDevOpsRepositoryItem>>> Repositories(Guid id, [FromQuery] string project, CancellationToken ct) =>
        List(() => git.RepositoriesAsync(id, project, ct));

    [HttpGet("connections/{id:guid}/branches")]
    public Task<ActionResult<AzureDevOpsList<AzureDevOpsBranchItem>>> Branches(Guid id, [FromQuery] string project, [FromQuery] string repository, CancellationToken ct) =>
        List(() => git.BranchesAsync(id, project, repository, ct));

    [HttpPost("resolve")]
    public async Task<ActionResult<AzureDevOpsResolveResult>> Resolve([FromBody] AzureDevOpsResolveRequest request, CancellationToken ct) => Ok(await imports.ResolveAsync(request, ct));

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] AzureDevOpsResolveRequest request, CancellationToken ct)
    {
        var (preview, note, error) = await imports.PreviewAsync(request, ct);
        return preview is null ? UnprocessableEntity(new { message = error }) : Ok(new { preview, note });
    }

    private static async Task<ActionResult<AzureDevOpsList<T>>> List<T>(Func<Task<List<T>>> load)
    {
        try { return new AzureDevOpsList<T>(await load(), null); }
        catch (AzureDevOpsException ex) { return new AzureDevOpsList<T>([], ex.Message); }
    }
}
