using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
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

    /// <summary>OpenAPI / GraphQL schema contracts found by Source Analysis, with the store version each was imported as (reading only).</summary>
    [HttpGet("source-contracts")]
    public async Task<ActionResult<IReadOnlyList<SourceContractCandidate>>> SourceContracts(CancellationToken ct) => Ok(await registry.SourceCandidatesAsync(ct));

    [HttpPost("contracts/from-source")]
    public async Task<ActionResult<ContractImportResult>> ImportFromSource([FromBody] SourceContractImportRequest request, CancellationToken ct)
    {
        var result = await registry.ImportFromSourceAsync(request, ct);
        return result.Imported || result.Unchanged ? Ok(result) : UnprocessableEntity(result);
    }

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

    public sealed record ProposeRequest(string Operation);

    /// <summary>Negative proposals for one operation (saved as Drafts, never run).</summary>
    [HttpPost("contracts/{key}/tests/negatives")]
    public async Task<IActionResult> ProposeNegatives(string key, [FromBody] ProposeRequest request, CancellationToken ct)
    {
        var (created, skipped) = await tests.ProposeNegativesAsync(key, request.Operation, ct);
        return created.Count == 0 && skipped.Any(s => s.Contains("does not exist", StringComparison.Ordinal) || s.Contains("is not in", StringComparison.Ordinal))
            ? NotFound(new { skipped }) : Ok(new { created, skipped });
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

    /// <summary>Saves an edit as a new revision (409 when the test changed since the editor's revision; 422 when the content is invalid).</summary>
    [HttpPut("tests/{id:guid}")]
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<ContractTestSaveResult>> Edit(Guid id, [FromBody] ContractTestEdit edit, CancellationToken ct)
    {
        var result = await tests.UpdateAsync(id, edit, ct);
        return result.Saved ? Ok(result) : result.Test is null ? NotFound() : result.Conflict ? Conflict(result) : UnprocessableEntity(result);
    }

    [HttpPost("tests/{id:guid}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid id, CancellationToken ct)
    {
        var (test, error) = await tests.DuplicateAsync(id, ct);
        return test is not null ? Ok(test) : NotFound(new { message = error });
    }

    [HttpGet("tests/{id:guid}/revisions")]
    public async Task<ActionResult<IReadOnlyList<ContractTestRevisionInfo>>> Revisions(Guid id, CancellationToken ct) => Ok(await tests.RevisionsAsync(id, ct));

    [HttpGet("tests/{id:guid}/revisions/{revision:int}")]
    public async Task<ActionResult<ContractTestDefinition>> Revision(Guid id, int revision, CancellationToken ct) =>
        await tests.RevisionAsync(id, revision, ct) is { } test ? Ok(test) : NotFound();

    [HttpGet("tests/{id:guid}/compare")]
    public async Task<ActionResult<ContractTestRevisionDiff>> CompareRevisions(Guid id, [FromQuery] int from, [FromQuery] int to, CancellationToken ct) =>
        await tests.CompareAsync(id, from, to, ct) is { } diff ? Ok(diff) : NotFound();

    public sealed record RevertRequest(int Revision, int ExpectedRevision);

    [HttpPost("tests/{id:guid}/revert")]
    public async Task<ActionResult<ContractTestSaveResult>> Revert(Guid id, [FromBody] RevertRequest request, CancellationToken ct)
    {
        var result = await tests.RevertAsync(id, request.Revision, request.ExpectedRevision, ct);
        return result.Saved ? Ok(result) : result.Test is null ? NotFound() : result.Conflict ? Conflict(result) : UnprocessableEntity(result);
    }

    [HttpDelete("tests/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => await tests.DeleteAsync(id, ct) ? NoContent() : NotFound();

    public sealed record BindingResponse(ContractEndpointBinding? Binding, List<ContractServerProposal> Proposals);

    /// <summary>The contract's endpoint and authentication binding in one Target Environment, with base-path proposals from its declared servers.</summary>
    [HttpGet("contracts/{key}/binding")]
    public async Task<ActionResult<BindingResponse>> Binding(string key, [FromQuery] string environmentId, CancellationToken ct)
    {
        var latest = await registry.LatestVersionAsync(key, ct);
        if (latest is null || await registry.DocumentAsync(key, latest.Value, ct) is not { } doc) return NotFound();
        return Ok(new BindingResponse(await tests.BindingAsync(key, environmentId, ct), ContractTestService.ServerProposals(doc)));
    }

    [HttpPut("contracts/{key}/binding")]
    public async Task<IActionResult> SaveBinding(string key, [FromBody] ContractEndpointBindingSave save, CancellationToken ct)
    {
        var (binding, error) = await tests.SaveBindingAsync(key, save, ct);
        return binding is not null ? Ok(binding) : error == "Not found." ? NotFound() : UnprocessableEntity(new { message = error });
    }

    /// <summary>The Target Environment's unattended API service credential: configuration only; the secret is write-only and never returned.</summary>
    [HttpGet("credentials/{environmentId}")]
    public ActionResult<ApiServiceCredentialView> Credential([FromServices] ApiServiceCredentialStore store, string environmentId) =>
        store.Get(environmentId) is { } view ? Ok(view) : NotFound();

    [HttpPut("credentials")]
    [RequestSizeLimit(64 * 1024)]
    public IActionResult SaveCredential([FromServices] ApiServiceCredentialStore store, [FromBody] ApiServiceCredentialSave save)
    {
        var (view, error) = store.Save(save, DateTimeOffset.UtcNow);
        if (view is null) return UnprocessableEntity(new { message = error });
        ExecutionCredentialResolver.Forget(save.EnvironmentId);
        return Ok(view);
    }

    [HttpDelete("credentials/{environmentId}")]
    public IActionResult DeleteCredential([FromServices] ApiServiceCredentialStore store, string environmentId)
    {
        ExecutionCredentialResolver.Forget(environmentId);
        return store.Delete(environmentId) ? NoContent() : NotFound();
    }

    [HttpPost("runs")]
    public async Task<ActionResult<ContractTestRunResult>> Run([FromBody] ContractTestRunRequest request, CancellationToken ct)
    {
        var result = await tests.RunAsync(request, ct);
        return result.Run?.RunId is null ? UnprocessableEntity(result) : Ok(result);
    }
}
