using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using Microsoft.AspNetCore.Authorization;
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
    PerformanceTestProviderRegistry providers, PerformanceTestOptions options, TimeProvider? clock = null, Services.PerformanceTests.Resources.ResourceObservationRegistry? resources = null,
    PerformanceLocalExecutionStore? localExecution = null, PerformanceTrialService? trial = null, PerformanceReadinessCheckService? readinessCheck = null) : ControllerBase
{
    // ── Local execution setup: Container TLS trust (machine-local; metadata only; TLS verification is never disabled) ─────────

    /// <summary>Local execution summary and the Container TLS trust card. Never returns certificate content.</summary>
    [HttpGet("local-execution")]
    public ActionResult<PerformanceLocalExecutionStatus> LocalExecution() =>
        localExecution is null ? NotFound() : Ok(localExecution.Status());

    /// <summary>Validates and stores a CA bundle host path (.pem/.crt/.cer PEM certificates only; keys and PFX refused). Nothing is mounted unless it loads.</summary>
    [HttpPut("local-execution/ca-bundle")]
    public ActionResult<PerformanceLocalExecutionStatus> ConfigureCaBundle([FromBody] ContainerCaBundleRequest request)
    {
        if (localExecution is null) return NotFound();
        var (ok, message, _) = localExecution.Configure(request?.Path, (clock ?? TimeProvider.System).GetUtcNow());
        if (!ok) return UnprocessableEntity(new { message });
        providers.InvalidateStatus(PerformanceProviderIds.K6);
        return Ok(localExecution.Status());
    }

    /// <summary>TLS trust: Automatic (retrieve and validate a missing intermediate against an already trusted root) or ManualOnly. No mode disables
    /// TLS verification; there is no "trust all" setting.</summary>
    [HttpPut("local-execution/tls-repair-mode")]
    public ActionResult<PerformanceLocalExecutionStatus> SetTlsRepairMode([FromBody] ContainerTlsRepairModeRequest request)
    {
        if (localExecution is null) return NotFound();
        if (!localExecution.SetRepairMode(request?.Mode)) return UnprocessableEntity(new { message = $"Mode must be {TlsRepairModes.Automatic} or {TlsRepairModes.ManualOnly}." });
        return Ok(localExecution.Status());
    }

    /// <summary>Removes the CA bundle chosen in System Settings; the file on disk is not touched.</summary>
    [HttpDelete("local-execution/ca-bundle")]
    public ActionResult<PerformanceLocalExecutionStatus> RemoveCaBundle()
    {
        if (localExecution is null) return NotFound();
        localExecution.Remove();
        providers.InvalidateStatus(PerformanceProviderIds.K6);
        return Ok(localExecution.Status());
    }

    /// <summary>Approved Resource Stability targets of an environment with their provider status (configured targets only — no container enumeration).</summary>
    [HttpGet("resource-targets")]
    public async Task<ActionResult<IReadOnlyList<ResourceTargetStatus>>> ResourceTargets([FromQuery] string environmentId, [FromQuery] string? environmentType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (resources is null) return Ok(Array.Empty<ResourceTargetStatus>());
        return Ok(await resources.TargetStatusAsync(environmentId, environmentType ?? "", ct));
    }

    /// <summary>Resource observation providers and what each can measure here (separate from the k6 provider status).</summary>
    [HttpGet("resource-providers")]
    public async Task<ActionResult<IReadOnlyList<ResourceProviderCapability>>> ResourceProviders(CancellationToken ct) =>
        Ok(resources is null ? Array.Empty<ResourceProviderCapability>() : await resources.CapabilitiesAsync(ct));

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
        // The Target Environment's proxy identity, as a run start sends it: the API credential is then checked exactly as the run would check it.
        var identity = Request.Query.TryGetValue("profileId", out var profile) && Request.Query.TryGetValue("contextFingerprint", out var fingerprint)
            ? new BirkNext.LocalHttpsProxy.AuthenticatedReviewIdentity(BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.LocalHttpsProxy, profile.ToString(), fingerprint.ToString()) : null;
        return Ok(await readiness.EvaluateAsync(definition, await store.DataProfileAsync(environmentId, definition.Scenario.TestDataProfileId, ct), baseline is not null, ct, identity));
    }

    /// <summary>Explicit container-network check: one GET of the target origin from a k6 container (same network, proxy and CA as a run).</summary>
    [HttpPost("definitions/{id}/network-check")]
    public async Task<ActionResult<PerformanceReachability>> NetworkCheck([FromQuery] string environmentId, string id, CancellationToken ct)
    {
        var definition = await store.DefinitionAsync(environmentId, id, ct);
        if (definition is null) return NotFound();
        if (PerformanceTestSafety.EnvironmentBlock(definition.EnvironmentType) is { } env) return UnprocessableEntity(new { message = env });
        if (PerformanceTestSafety.Origin(definition.TargetOrigin, definition.EnvironmentType, options).Error is { } error) return UnprocessableEntity(new { message = error });
        return Ok(await providers.CheckReachabilityOnceAsync(definition, ct));
    }

    /// <summary>
    /// The coordinated Check readiness (explicit action only): readiness of the saved version, plus — when eligible — the same bounded container-network
    /// check (one GET of the target origin from the k6 container; never a load test), then readiness again. Recent evidence under the same network/TLS
    /// configuration is reused; a safety refusal or an unavailable runtime skips the check with the reason. GET readiness stays side-effect free.
    /// </summary>
    [HttpPost("definitions/{id}/readiness-check")]
    public async Task<ActionResult<PerformanceReadinessCheck>> ReadinessCheck([FromQuery] string environmentId, string id, CancellationToken ct)
    {
        if (readinessCheck is null) return NotFound();
        var definition = await store.DefinitionAsync(environmentId, id, ct);
        if (definition is null) return NotFound();
        var baseline = await store.ActiveBaselineAsync(environmentId, id, definition.ComparisonFingerprint, ct);
        var identity = Request.Query.TryGetValue("profileId", out var profile) && Request.Query.TryGetValue("contextFingerprint", out var fingerprint)
            ? new BirkNext.LocalHttpsProxy.AuthenticatedReviewIdentity(BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.LocalHttpsProxy, profile.ToString(), fingerprint.ToString()) : null;
        return Ok(await readinessCheck.CheckAsync(definition, await store.DataProfileAsync(environmentId, definition.Scenario.TestDataProfileId, ct), baseline is not null, identity, ct));
    }

    // ── Smart Scenario Engine sources (stored contract evidence; the engine itself runs with the browser's discovery evidence) ──────

    /// <summary>
    /// Send validation request: each enabled step once, in order (no load), returning bounded, redacted request/response samples and what each
    /// output extracts. The same environment, target and method safety as a run; authenticated steps are not sent. Nothing is stored.
    /// </summary>
    [HttpPost("trial")]
    public async Task<ActionResult<PerformanceTrialResult>> Trial([FromQuery] string environmentId, [FromBody] PerformanceTrialRequest request, CancellationToken ct)
    {
        if (trial is null) return NotFound();
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (request?.Definition is null) return BadRequest("A definition is required.");
        if (!string.Equals(request.Definition.EnvironmentId, environmentId, StringComparison.Ordinal)) return BadRequest("The definition belongs to another environment.");
        return Ok(await trial.RunAsync(request, ct));
    }

    /// <summary>Source Analysis snapshots that hold OpenAPI/GraphQL contracts, newest first, for an explicit choice (none is selected for the user).</summary>
    [HttpGet("scenario-sources")]
    public async Task<ActionResult<IReadOnlyList<ScenarioSourceSnapshot>>> ScenarioSources([FromServices] PerformanceScenarioSources sources, [FromQuery] string? environmentId, CancellationToken ct) =>
        Ok(await sources.SnapshotsAsync(environmentId ?? "", ct));

    /// <summary>The stored API contracts of exactly this snapshot (never the newest instead).</summary>
    [HttpGet("scenario-contracts")]
    public async Task<ActionResult<ScenarioContractCatalog>> ScenarioContracts([FromServices] PerformanceScenarioSources sources, [FromQuery] string? environmentId, [FromQuery] Guid snapshotId, CancellationToken ct) =>
        await sources.ContractsAsync(environmentId ?? "", snapshotId, ct) is { } catalog ? Ok(catalog) : NotFound("The Source Analysis snapshot does not exist or Source Analysis is disabled.");

    // ── Safe setup profiles (desired state only: scenario, workload, thresholds, target reference) ─────────────────────────

    /// <summary>The current Target Environment the page is on (the target is never taken from a profile); validated like a definition save.</summary>
    private (Services.SetupProfiles.PerformanceSetupTarget? Target, string? Error) SetupTarget(string? targetOrigin, string? environmentType, string? environmentName)
    {
        var (origin, error) = PerformanceTestSafety.Origin(targetOrigin, environmentType, options);
        return origin is null ? (null, error) : (new(origin.ToString().TrimEnd('/'), environmentType ?? "", environmentName ?? ""), null);
    }

    [HttpPost("setup-profile/preview")]
    [RequestSizeLimit(Services.SetupProfiles.SetupProfileParser.MaxBytes * 2)]
    public async Task<ActionResult<BirkNext.Integrations.SetupProfilePreview>> PreviewSetup([FromServices] Services.SetupProfiles.PerformanceSetupProfileService profiles, [FromQuery] string environmentId,
        [FromQuery] string? targetOrigin, [FromQuery] string? environmentType, [FromQuery] string? environmentName, [FromBody] BirkNext.Integrations.SetupProfileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var (target, error) = SetupTarget(targetOrigin, environmentType, environmentName);
        return target is null ? BadRequest(error) : Ok(await profiles.PreviewAsync(environmentId, request, target, ct));
    }

    [HttpPost("setup-profile/apply")]
    [RequestSizeLimit(Services.SetupProfiles.SetupProfileParser.MaxBytes * 2)]
    public async Task<ActionResult<BirkNext.Integrations.SetupApplyResult>> ApplySetup([FromServices] Services.SetupProfiles.PerformanceSetupProfileService profiles, [FromQuery] string environmentId,
        [FromQuery] string? targetOrigin, [FromQuery] string? environmentType, [FromQuery] string? environmentName, [FromBody] BirkNext.Integrations.SetupApplyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var (target, error) = SetupTarget(targetOrigin, environmentType, environmentName);
        return target is null ? BadRequest(error) : Ok(await profiles.ApplyAsync(environmentId, request, target, ct));
    }

    [HttpGet("setup-profile/export")]
    public async Task<ActionResult<BirkNext.Integrations.SetupProfileExport>> ExportSetup([FromServices] Services.SetupProfiles.PerformanceSetupProfileService profiles, [FromQuery] string environmentId,
        [FromQuery] string? definitionId, [FromQuery] BirkNext.Integrations.SetupProfileFormat format = BirkNext.Integrations.SetupProfileFormat.Yaml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await profiles.ExportAsync(environmentId, definitionId, format, ct) is { } export ? Ok(export) : NotFound("No saved performance definition to export.");
    }

    [HttpGet("setup-profile/example")]
    public ActionResult<BirkNext.Integrations.SetupProfileExport> SetupExample() =>
        Ok(new BirkNext.Integrations.SetupProfileExport(BirkNext.Integrations.SetupProfileFormat.Yaml, "performance-baseline.example.yaml",
            Services.SetupProfiles.PerformanceSetupProfileService.Example(), Services.SetupProfiles.PerformanceSetupProfileService.ExportExcluded));

    /// <summary>Pulls the provider image — only when PerformanceTests:Container:AllowImagePull is true (explicit action; never at run time).</summary>
    [HttpPost("providers/{providerId}/prepare")]
    public async Task<IActionResult> Prepare(string providerId, CancellationToken ct)
    {
        if (providers.Find(providerId) is not { } provider) return NotFound();
        var error = await provider.PrepareAsync(ct);
        providers.InvalidateStatus(providerId);
        return error is null ? Ok(await providers.StatusAsync(providerId, ct)) : UnprocessableEntity(new { message = error });
    }

    /// <summary>Removes stale BirkNext-managed performance-test containers (label-filtered; never other containers).</summary>
    [HttpPost("maintenance/cleanup")]
    public async Task<ActionResult<object>> Cleanup(CancellationToken ct) => Ok(new { removed = await execution.CleanupOrphansAsync(true, ct) });

    [HttpPost("runs")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
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
        await execution.CurrentRunAsync(store, runId, ct) is { } run && run.EnvironmentId == environmentId ? Ok(run) : NotFound();

    [HttpPost("runs/{runId:guid}/cancel")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    public async Task<ActionResult<PerformanceTestRun>> Cancel([FromQuery] string environmentId, Guid runId, CancellationToken ct)
    {
        if (await store.RunAsync(runId, ct) is not { } run || run.EnvironmentId != environmentId) return NotFound();
        return Ok(await execution.CancelAsync(runId, ct));
    }

    [HttpGet("baselines")]
    public async Task<ActionResult<List<PerformanceBaseline>>> Baselines([FromQuery] string environmentId, [FromQuery] string? definitionId, CancellationToken ct) =>
        Ok(await store.BaselinesAsync(environmentId, definitionId, ct));

    [HttpPost("baselines")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<PerformanceBaselinePromotionResult>> Promote([FromQuery] string environmentId, [FromBody] PerformanceBaselinePromotion request, CancellationToken ct)
    {
        var result = await store.PromoteAsync(environmentId, request, Now, ct, Initiator);
        return result.Error is null ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("baselines/{baselineId}/archive")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<PerformanceBaseline>> Archive([FromQuery] string environmentId, string baselineId, CancellationToken ct) =>
        await store.ArchiveBaselineAsync(environmentId, baselineId, Now, ct) is { } b ? Ok(b) : NotFound();

    private string Initiator => User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";

    /// <summary>Proposes a run as the next baseline (eligibility review recorded). Never used for comparison until approved.</summary>
    [HttpPost("baselines/proposals")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<PerformanceBaselineDecisionResult>> Propose([FromQuery] string environmentId, [FromBody] PerformanceBaselineProposal request, CancellationToken ct)
    {
        var result = await store.ProposeAsync(environmentId, request, Initiator, Now, ct);
        return result.Error is null ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("baselines/{baselineId}/approve")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<PerformanceBaselineDecisionResult>> Approve([FromQuery] string environmentId, string baselineId, [FromBody] PerformanceBaselineDecision decision, CancellationToken ct)
    {
        var result = await store.ApproveAsync(environmentId, baselineId, decision, Initiator, Now, ct);
        return result.Error is null ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("baselines/{baselineId}/reject")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<PerformanceBaselineDecisionResult>> Reject([FromQuery] string environmentId, string baselineId, [FromBody] PerformanceBaselineDecision decision, CancellationToken ct)
    {
        var result = await store.RejectAsync(environmentId, baselineId, decision, Initiator, Now, ct);
        return result.Error is null ? Ok(result) : UnprocessableEntity(result);
    }

    /// <summary>Deletes one finished run (explicit confirmation). The source run of any proposed or approved baseline is protected.</summary>
    [HttpPost("runs/{runId:guid}/delete")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> DeleteRun([FromQuery] string environmentId, Guid runId, [FromBody] PerformanceRunDeletion request, CancellationToken ct) =>
        await store.DeleteRunAsync(environmentId, runId, request, ct) is { } error ? UnprocessableEntity(new { message = error }) : NoContent();

    /// <summary>
    /// Regression of one or more runs against an approved baseline: comparability, per-metric tolerance verdicts (Confirmed needs repeated
    /// comparable runs), thresholds beside it. Read-only; nothing is stored. Default current run: the latest completed run after the baseline's run.
    /// </summary>
    [HttpGet("regression")]
    public async Task<ActionResult<PerformanceRegressionReport>> Regression([FromQuery] string environmentId, [FromQuery] string baselineId, [FromQuery] string? runs, CancellationToken ct)
    {
        var resolved = await ResolveRegressionAsync(environmentId, baselineId, runs, ct);
        return resolved.Error is { } error ? UnprocessableEntity(new { message = error }) : Ok(resolved.Report);
    }

    /// <summary>The same regression as a sanitized, self-describing export (metrics, units, rules and limitations; never request content).</summary>
    [HttpGet("regression/export")]
    public async Task<IActionResult> RegressionExport([FromQuery] string environmentId, [FromQuery] string baselineId, [FromQuery] string? runs, CancellationToken ct)
    {
        var resolved = await ResolveRegressionAsync(environmentId, baselineId, runs, ct);
        if (resolved.Error is { } error) return UnprocessableEntity(new { message = error });
        var definition = await store.DefinitionAsync(environmentId, resolved.BaselineRun!.DefinitionId, ct);
        var export = new
        {
            kind = "birknext.performance.regression", schemaVersion = 1, generatedAt = Now,
            definition = new { id = resolved.BaselineRun.DefinitionId, name = definition?.Name ?? resolved.BaselineRun.DefinitionSnapshot.Name, environmentId,
                environmentType = resolved.BaselineRun.EnvironmentType, targetOrigin = resolved.BaselineRun.TargetOrigin },
            baseline = resolved.Baseline,
            baselineRun = Summary(resolved.BaselineRun),
            currentRuns = resolved.Current!.Select(Summary),
            report = resolved.Report,
            units = PerformanceRegressionRules.RegressionKeys.ToDictionary(k => k, PerformanceRegressionRules.Unit),
            completeness = "Metrics only (summary percentiles per run). Request and response content is not part of a load-test result.",
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(export, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(bytes, "application/json", $"performance-regression-{resolved.Baseline!.BaselineId[..8]}-{Now:yyyyMMdd-HHmmss}.json");
    }

    /// <summary>Persisted runs of a definition over time, each marked with its comparability against the (selected or active) baseline.</summary>
    [HttpGet("trends")]
    public async Task<ActionResult<PerformanceTrend>> Trends([FromQuery] string environmentId, [FromQuery] string definitionId, [FromQuery] string? baselineId, CancellationToken ct)
    {
        var runs = await store.RunsAsync(environmentId, definitionId, 200, ct);
        var baselines = await store.BaselinesAsync(environmentId, definitionId, ct);
        var baseline = baselineId is null ? baselines.FirstOrDefault(b => b.Status == PerformanceBaselineStatus.Active) : baselines.FirstOrDefault(b => b.BaselineId == baselineId);
        if (baselineId is not null && baseline is null) return NotFound();
        var baselineRun = baseline is null ? null : runs.FirstOrDefault(r => r.RunId == baseline.RunId) ?? await store.RunAsync(baseline.RunId, ct);
        return Ok(PerformanceRegressionRules.Trend(definitionId, baseline, baselineRun, runs));
    }

    private static object Summary(PerformanceTestRun r) => new
    {
        r.RunId, r.DefinitionId, r.DefinitionVersion, r.ComparisonFingerprint, r.State, r.StartedAt, r.FinishedAt, r.VersionLabel, r.ProviderId, r.ProviderVersion, r.ImageDigest,
        r.TargetOrigin, r.EnvironmentType, r.MetricsSource, Workload = r.DefinitionSnapshot.Workload, TestDataProfileId = r.DefinitionSnapshot.Scenario.TestDataProfileId,
        Authentication = r.DefinitionSnapshot.AuthenticationReference is null ? "none" : "configured", r.Metrics, r.ThresholdResults, r.Verdict,
    };

    private sealed record ResolvedRegression(PerformanceRegressionReport? Report, PerformanceBaseline? Baseline, PerformanceTestRun? BaselineRun, List<PerformanceTestRun>? Current, string? Error);

    private async Task<ResolvedRegression> ResolveRegressionAsync(string environmentId, string baselineId, string? runs, CancellationToken ct)
    {
        if (await store.BaselineAsync(baselineId, ct) is not { } baseline || baseline.EnvironmentId != environmentId) return new(null, null, null, null, "The baseline does not exist in this environment.");
        if (baseline.Status is PerformanceBaselineStatus.Proposed or PerformanceBaselineStatus.Rejected)
            return new(null, null, null, null, $"A {baseline.Status.ToString().ToLowerInvariant()} baseline is not a reference: approve it first.");
        if (await store.RunAsync(baseline.RunId, ct) is not { } baselineRun) return new(null, null, null, null, "The baseline's run is missing.");
        List<PerformanceTestRun> current = [];
        if (!string.IsNullOrWhiteSpace(runs))
        {
            foreach (var part in runs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(20))
            {
                if (!Guid.TryParse(part, out var id) || await store.RunAsync(id, ct) is not { } run || run.EnvironmentId != environmentId)
                    return new(null, null, null, null, $"Run {part} does not exist in this environment.");
                if (run.RunId == baselineRun.RunId) return new(null, null, null, null, "A baseline is not compared with its own run.");
                current.Add(run);
            }
        }
        else
        {
            var latest = (await store.RunsAsync(environmentId, baselineRun.DefinitionId, 200, ct))
                .FirstOrDefault(r => r.RunId != baselineRun.RunId && r.State == PerformanceRunState.Completed && (r.CreatedAt > baselineRun.CreatedAt));
            if (latest is null) return new(null, null, null, null, "No completed run after the baseline's run to compare.");
            current.Add(latest);
        }
        var report = PerformanceRegressionRules.Evaluate(baseline, baselineRun, current, current[^1].DefinitionSnapshot.DriftPolicies);
        return new(report, baseline, baselineRun, current, null);
    }

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
            ResourceDrift = original ? run.ResourceDrift
                : Services.PerformanceTests.Resources.ResourceStabilityAnalyzer.Drift(run, other, baseline?.BaselineId, run.DefinitionSnapshot.ResourceObservation?.DriftPolicies ?? []),
        });
    }
}
