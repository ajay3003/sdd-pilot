using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Performance Test Review: definitions (versioned), approved test data, readiness, runs (start/status/cancel), baselines and comparison.
/// Every safety rule is enforced here and again before the provider starts — Production and production hosts are refused whatever the client
/// sends; destinations are relative to the definition's configured target origin; no script or credential is ever accepted.
/// </summary>
[ApiController]
[Route("api/performance-tests")]
public sealed class PerformanceTestsController(PerformanceTestStore store, PerformanceTestReadinessService readiness, PerformanceTestExecutionService execution,
    PerformanceTestProviderRegistry providers, PerformanceTestOptions options, TimeProvider? clock = null) : ControllerBase
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    [HttpGet]
    public async Task<ActionResult<PerformanceTestOverview>> Overview([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        await execution.ReconcileAsync(store, environmentId, ct);
        return Ok(new PerformanceTestOverview
        {
            Definitions = await store.DefinitionsAsync(environmentId, false, ct), DataProfiles = await store.DataProfilesAsync(environmentId, ct),
            Provider = await providers.StatusAsync(PerformanceProviderIds.K6, ct), Limits = options.Limits(null),
        });
    }

    [HttpGet("providers")]
    public async Task<ActionResult<List<PerformanceProviderStatus>>> Providers(CancellationToken ct)
    {
        var list = new List<PerformanceProviderStatus>();
        foreach (var p in providers.All) list.Add(await providers.StatusAsync(p.ProviderId, ct));
        return Ok(list);
    }

    [HttpPost("definitions")]
    public Task<ActionResult<PerformanceTestDefinition>> Create([FromQuery] string environmentId, [FromBody] PerformanceTestDefinition definition, CancellationToken ct) =>
        Save(environmentId, definition with { Id = Guid.NewGuid().ToString("N") }, ct);

    [HttpPut("definitions/{id}")]
    public async Task<ActionResult<PerformanceTestDefinition>> Update([FromQuery] string environmentId, string id, [FromBody] PerformanceTestDefinition definition, CancellationToken ct) =>
        await store.DefinitionAsync(environmentId, id, ct) is null ? NotFound() : await Save(environmentId, definition with { Id = id }, ct);

    private async Task<ActionResult<PerformanceTestDefinition>> Save(string environmentId, PerformanceTestDefinition definition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 120) return BadRequest("A definition needs a name (at most 120 characters).");
        // Production is refused at save as well as at run: a production definition can never exist.
        if (PerformanceTestSafety.EnvironmentBlock(definition.EnvironmentType) is { } env && definition.EnvironmentType.Trim().Equals("Production", StringComparison.OrdinalIgnoreCase))
            return BadRequest(env);
        var (origin, error) = PerformanceTestSafety.Origin(definition.TargetOrigin, definition.EnvironmentType, options);
        if (origin is null) return BadRequest(error);
        if (definition.ProviderId != PerformanceProviderIds.K6 && providers.Find(definition.ProviderId) is null) return BadRequest($"Unknown provider '{definition.ProviderId}'.");
        return Ok(await store.SaveDefinitionAsync(environmentId, definition with { TargetOrigin = origin.ToString().TrimEnd('/') }, Now, ct));
    }

    [HttpDelete("definitions/{id}")]
    public async Task<IActionResult> Delete([FromQuery] string environmentId, string id, CancellationToken ct) =>
        await store.DeleteDefinitionAsync(environmentId, id, ct) is { } outcome ? Ok(new { outcome }) : NotFound();

    [HttpPost("data-profiles")]
    public async Task<ActionResult<PerformanceTestDataProfile>> SaveDataProfile([FromQuery] string environmentId, [FromBody] PerformanceTestDataProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (string.IsNullOrWhiteSpace(profile.Name)) return BadRequest("A test data profile needs a name.");
        if (PerformanceTestSafety.DataValueIssues(profile).FirstOrDefault() is { } issue) return BadRequest(issue.Message);
        return Ok(await store.SaveDataProfileAsync(environmentId, profile, Now, ct));
    }

    [HttpGet("definitions/{id}/readiness")]
    public async Task<ActionResult<PerformanceTestReadiness>> Readiness([FromQuery] string environmentId, string id, CancellationToken ct)
    {
        var definition = await store.DefinitionAsync(environmentId, id, ct);
        if (definition is null) return NotFound();
        var baseline = await store.ActiveBaselineAsync(environmentId, id, definition.ComparisonFingerprint, ct);
        return Ok(await readiness.EvaluateAsync(definition, await store.DataProfileAsync(environmentId, definition.Scenario.TestDataProfileId, ct), baseline is not null, ct));
    }

    [HttpPost("runs")]
    public async Task<IActionResult> Run([FromQuery] string environmentId, [FromBody] PerformanceRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var started = await execution.StartAsync(environmentId, request, ct);
        if (started.Conflict is not null) return Conflict(new { message = started.Conflict });
        if (started.Run is null) return UnprocessableEntity(new { message = "The performance test cannot run.", blockers = started.Blockers });
        return Accepted(started.Run);
    }

    [HttpGet("runs")]
    public async Task<ActionResult<List<PerformanceTestRun>>> Runs([FromQuery] string environmentId, [FromQuery] string? definitionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        await execution.ReconcileAsync(store, environmentId, ct);
        return Ok(await store.RunsAsync(environmentId, definitionId, 200, ct));
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<PerformanceTestRun>> GetRun([FromQuery] string environmentId, Guid runId, CancellationToken ct) =>
        await store.RunAsync(runId, ct) is { } run && run.EnvironmentId == environmentId ? Ok(run) : NotFound();

    [HttpPost("runs/{runId:guid}/cancel")]
    public async Task<ActionResult<PerformanceTestRun>> Cancel([FromQuery] string environmentId, Guid runId, CancellationToken ct)
    {
        if (await store.RunAsync(runId, ct) is not { } run || run.EnvironmentId != environmentId) return NotFound();
        return Ok(await execution.CancelAsync(runId, ct));
    }

    [HttpGet("baselines")]
    public async Task<ActionResult<List<PerformanceBaseline>>> Baselines([FromQuery] string environmentId, [FromQuery] string? definitionId, CancellationToken ct) =>
        Ok(await store.BaselinesAsync(environmentId, definitionId, ct));

    [HttpPost("baselines")]
    public async Task<ActionResult<PerformanceBaselinePromotionResult>> Promote([FromQuery] string environmentId, [FromBody] PerformanceBaselinePromotion request, CancellationToken ct)
    {
        var result = await store.PromoteAsync(environmentId, request, Now, ct);
        return result.Error is null ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("baselines/{baselineId}/archive")]
    public async Task<ActionResult<PerformanceBaseline>> Archive([FromQuery] string environmentId, string baselineId, CancellationToken ct) =>
        await store.ArchiveBaselineAsync(environmentId, baselineId, Now, ct) is { } b ? Ok(b) : NotFound();

    /// <summary>Compares a run with another run or a (historical/active) baseline. Ad-hoc: never stored on the run; the run's original drift stays as recorded.</summary>
    [HttpGet("compare")]
    public async Task<ActionResult<PerformanceRunComparison>> Compare([FromQuery] string environmentId, [FromQuery] Guid current, [FromQuery] Guid? reference, [FromQuery] string? baselineId, CancellationToken ct)
    {
        if (await store.RunAsync(current, ct) is not { } run || run.EnvironmentId != environmentId) return NotFound();
        PerformanceBaseline? baseline = null;
        if (baselineId is not null && (baseline = await store.BaselineAsync(baselineId, ct)) is null) return NotFound();
        var referenceId = baseline?.RunId ?? reference;
        if (referenceId is null || await store.RunAsync(referenceId.Value, ct) is not { } other || other.EnvironmentId != environmentId) return BadRequest("Choose a reference run or baseline.");
        var original = baseline is not null && baseline.BaselineId == run.BaselineIdAtRun;
        var notes = PerformanceTestRules.CompatibilityNotes(run, other);
        return Ok(new PerformanceRunComparison
        {
            Kind = baseline is null ? PerformanceComparisonKind.RunVsRun : original ? PerformanceComparisonKind.OriginalBaseline
                : baseline.Status == PerformanceBaselineStatus.Active ? PerformanceComparisonKind.RunVsActiveBaseline : PerformanceComparisonKind.RunVsHistoricalBaseline,
            AdHoc = !original, CurrentRunId = run.RunId, ReferenceRunId = other.RunId, ReferenceBaselineId = baseline?.BaselineId, Compatible = notes.Count == 0, CompatibilityNotes = notes,
            Deltas = PerformanceTestRules.Deltas(other.Metrics, run.Metrics),
            Drift = original ? run.Drift : baseline is not null ? PerformanceTestRules.Drift(run, other, baseline, run.DefinitionSnapshot.DriftPolicies) : null,
        });
    }
}
