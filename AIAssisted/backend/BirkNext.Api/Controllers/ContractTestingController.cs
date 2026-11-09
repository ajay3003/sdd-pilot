using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using Microsoft.AspNetCore.Authorization;
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

    [HttpGet("contracts/{key}/coverage")]
    public async Task<ActionResult<ContractCoverageReport>> Coverage(string key, [FromQuery] int? version, CancellationToken ct) =>
        await tests.CoverageAsync(key, version, ct) is { } report ? Ok(report) : NotFound();

    [HttpGet("contracts/{key}/retention")]
    public async Task<ActionResult<ContractRetention>> Retention(string key, CancellationToken ct) =>
        await registry.RetentionAsync(key, ct) is { } retention ? Ok(retention) : NotFound();

    public sealed record LifecycleRequest(ContractLifecycle Lifecycle);
    public sealed record HiddenRequest(bool Hidden);

    [HttpPut("contracts/{key}/lifecycle")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> SetLifecycle(string key, [FromBody] LifecycleRequest request, CancellationToken ct) =>
        Refusal(await registry.SetLifecycleAsync(key, request.Lifecycle, ct));

    [HttpPut("contracts/{key}/versions/{version:int}/hidden")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> SetHidden(string key, int version, [FromBody] HiddenRequest request, CancellationToken ct) =>
        Refusal(await registry.SetHiddenAsync(key, version, request.Hidden, ct));

    /// <summary>Deletes an unreferenced version; <paramref name="confirm"/> must be the contract key. Referenced versions are refused (archive instead).</summary>
    [HttpDelete("contracts/{key}/versions/{version:int}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> DeleteVersion(string key, int version, [FromQuery] string? confirm, CancellationToken ct) =>
        Refusal(await registry.DeleteVersionAsync(key, version, confirm, ct));

    [HttpDelete("contracts/{key}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> DeleteContract(string key, [FromQuery] string? confirm, CancellationToken ct) =>
        Refusal(await registry.DeleteContractAsync(key, confirm, ct));

    private IActionResult Refusal(string? error) =>
        error is null ? NoContent() : error == "Not found." ? NotFound() : UnprocessableEntity(new { message = error });

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

    /// <summary>
    /// Typed synthetic test data for the contract's tests (one key-value set per operation in Test Data), validated against the contract and
    /// approved and pinned automatically only when every required value is contract-derived and certain; otherwise the fields needing a human are
    /// named. Writes Test Data and bindings; never sends a request.
    /// </summary>
    [HttpPost("contracts/{key}/test-data")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ContractTestDataReport>> GenerateTestData(string key, [FromBody] ContractTestDataRequest? request,
        [FromServices] ContractTestDataService data, CancellationToken ct) =>
        await data.GenerateAsync(key, request ?? new ContractTestDataRequest(), ct) is { } report ? Ok(report) : NotFound();

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

    /// <summary>A reviewable update proposal to the latest contract version (nothing is saved).</summary>
    [HttpGet("tests/{id:guid}/migration")]
    public async Task<ActionResult<ContractTestMigrationProposal>> Migration(Guid id, CancellationToken ct) =>
        await tests.MigrationAsync(id, ct) is { } proposal ? Ok(proposal) : NotFound();

    public sealed record ApplyMigrationRequest(int ExpectedRevision);

    [HttpPost("tests/{id:guid}/migration")]
    public async Task<ActionResult<ContractTestSaveResult>> ApplyMigration(Guid id, [FromBody] ApplyMigrationRequest request, CancellationToken ct)
    {
        var result = await tests.ApplyMigrationAsync(id, request.ExpectedRevision, ct);
        return result.Saved ? Ok(result) : result.Test is null ? NotFound() : result.Conflict ? Conflict(result) : UnprocessableEntity(result);
    }

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
    /// No credential configured is a normal state, not an error: 204 (a 404 made every page visit log a browser console error).
    public ActionResult<ApiServiceCredentialView> Credential([FromServices] ApiServiceCredentialStore store, string environmentId) =>
        store.Get(environmentId) is { } view ? Ok(view) : NoContent();

    [HttpPut("credentials")]
    [RequestSizeLimit(64 * 1024)]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public IActionResult SaveCredential([FromServices] ApiServiceCredentialStore store, [FromBody] ApiServiceCredentialSave save)
    {
        var (view, error) = store.Save(save, DateTimeOffset.UtcNow);
        if (view is null) return UnprocessableEntity(new { message = error });
        ExecutionCredentialResolver.Forget(save.EnvironmentId);
        return Ok(view);
    }

    [HttpDelete("credentials/{environmentId}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public IActionResult DeleteCredential([FromServices] ApiServiceCredentialStore store, string environmentId)
    {
        ExecutionCredentialResolver.Forget(environmentId);
        return store.Delete(environmentId) ? NoContent() : NotFound();
    }

    [HttpGet("chains")]
    public async Task<ActionResult<IReadOnlyList<ContractTestChain>>> Chains(CancellationToken ct) => Ok(await tests.ChainsAsync(ct));

    [HttpGet("chains/{id:guid}")]
    public async Task<ActionResult<ContractTestChain>> Chain(Guid id, [FromQuery] int? revision, CancellationToken ct) =>
        await tests.ChainAsync(id, revision, ct) is { } chain ? Ok(chain) : NotFound();

    [HttpPost("chains")]
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<ContractChainSaveResult>> CreateChain([FromBody] ContractChainSave save, CancellationToken ct)
    {
        var result = await tests.SaveChainAsync(null, save, ct);
        return result.Chain is null ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpPut("chains/{id:guid}")]
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<ContractChainSaveResult>> SaveChain(Guid id, [FromBody] ContractChainSave save, CancellationToken ct)
    {
        var result = await tests.SaveChainAsync(id, save, ct);
        return result.Conflict ? Conflict(result) : result.Problems is ["Not found."] ? NotFound() : result.Chain is null ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpDelete("chains/{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> DeleteChain(Guid id, CancellationToken ct) => await tests.DeleteChainAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("chains/{id:guid}/runs")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    public async Task<ActionResult<ContractTestRunResult>> RunChain(Guid id, [FromBody] ContractChainRunRequest request, CancellationToken ct)
    {
        var result = await tests.RunChainAsync(id, request, ct);
        return result.Run?.RunId is null ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpPost("runs")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    public async Task<ActionResult<ContractTestRunResult>> Run([FromBody] ContractTestRunRequest request, CancellationToken ct)
    {
        var result = await tests.RunAsync(request, ct);
        return result.Run?.RunId is null ? UnprocessableEntity(result) : Ok(result);
    }
}
