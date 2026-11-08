using BirkNext.Api.Services.ContractTesting;
using BirkNext.ContractTesting;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Contract-Driven Testing: import contracts (upload or URL) into the contract registry, explore versions and operations, compare versions,
/// generate and bind contract tests, check readiness and start a run. Reading never executes anything; only POST .../runs sends requests, and
/// it goes through the Execution Evidence run service and its safety policy.
/// </summary>
[ApiController]
[Route("api/contract-testing")]
public sealed class ContractTestingController(ContractRegistryService registry, ContractTestService tests) : ControllerBase
{
    [HttpPost("contracts")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<ContractImportResult>> Import([FromBody] ContractImportRequest request, CancellationToken ct)
    {
        var result = await registry.ImportAsync(request, ct);
        return result.Imported || result.Unchanged ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpGet("contracts")]
    public async Task<ActionResult<IReadOnlyList<ApiContractSummary>>> List(CancellationToken ct) => Ok(await registry.ListAsync(ct));

    [HttpGet("contracts/{key}")]
    public async Task<ActionResult<ApiContractDetail>> Get(string key, [FromQuery] int? version, CancellationToken ct) =>
        await registry.GetAsync(key, version, ct) is { } detail ? Ok(detail) : NotFound();

    [HttpGet("contracts/{key}/diff")]
    public async Task<ActionResult<ContractVersionDiff>> Diff(string key, [FromQuery] int from, [FromQuery] int to, CancellationToken ct) =>
        await tests.DiffAsync(key, from, to, ct) is { } diff ? Ok(diff) : NotFound();

    public sealed record GenerateRequest(int? Version, List<string>? Operations);

    [HttpPost("contracts/{key}/tests")]
    public async Task<IActionResult> Generate(string key, [FromBody] GenerateRequest request, CancellationToken ct)
    {
        var (created, skipped) = await tests.GenerateAsync(key, request.Version, request.Operations, ct);
        return created.Count == 0 && skipped.Any(s => s.Contains("does not exist", StringComparison.Ordinal)) ? NotFound(new { skipped }) : Ok(new { created, skipped });
    }

    /// <summary>Tests with readiness for the Target Environment given in the body (POST because the environment is not a URL concern).</summary>
    [HttpPost("contracts/{key}/tests/query")]
    public async Task<ActionResult<IReadOnlyList<ContractTestView>>> Tests(string key, [FromBody] ContractTestEnvironment? environment, CancellationToken ct) =>
        Ok(await tests.ListAsync(key, environment, ct));

    [HttpPost("tests/{id:guid}/query")]
    public async Task<ActionResult<ContractTestView>> Test(Guid id, [FromBody] ContractTestEnvironment? environment, CancellationToken ct) =>
        await tests.GetAsync(id, environment, ct) is { } view ? Ok(view) : NotFound();

    [HttpGet("tests/{id:guid}/datasets")]
    public async Task<ActionResult<IReadOnlyList<ContractDatasetOption>>> Datasets(Guid id, [FromQuery] string? environmentId, CancellationToken ct) =>
        Ok(await tests.DatasetOptionsAsync(id, environmentId, ct));

    [HttpPut("tests/{id:guid}/binding")]
    public async Task<IActionResult> Bind(Guid id, [FromBody] ContractTestBindRequest request, CancellationToken ct)
    {
        var (test, error) = await tests.BindAsync(id, request, ct);
        return test is not null ? Ok(test) : error == "Not found." ? NotFound() : UnprocessableEntity(new { message = error });
    }

    [HttpPost("tests/{id:guid}/regenerate")]
    public async Task<IActionResult> Regenerate(Guid id, CancellationToken ct)
    {
        var (test, error) = await tests.RegenerateAsync(id, ct);
        return test is not null ? Ok(test) : error == "Not found." ? NotFound() : UnprocessableEntity(new { message = error });
    }

    [HttpDelete("tests/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => await tests.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("runs")]
    public async Task<ActionResult<ContractTestRunResult>> Run([FromBody] ContractTestRunRequest request, CancellationToken ct)
    {
        var result = await tests.RunAsync(request, ct);
        return result.Run?.RunId is null ? UnprocessableEntity(result) : Ok(result);
    }
}
