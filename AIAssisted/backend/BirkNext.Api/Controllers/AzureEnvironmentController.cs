using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Azure Environment Analysis: sign-in (dedicated Edge profile or device code, the person's own MFA/PIM), read-only analysis of a selected
/// scope into an immutable snapshot, and the observed-evidence reads other reviews use. The POST routes here act on BirkNext's own state
/// (sign-in session, a new snapshot); every request BirkNext sends to Azure is a GET or a predefined Resource Graph query.
/// </summary>
[ApiController]
[Route("api/azure-environment")]
public sealed class AzureEnvironmentController(IAzureSignInService signIn, IAzureEnvironmentCollector collector, AzureEnvironmentSnapshotStore store,
    IAzureEnvironmentEvidenceProvider evidence, IReviewSourceEvidenceProvider sources) : ControllerBase
{
    [HttpGet("status")]
    public ActionResult<AzureConnectionStatus> Status() => Ok(signIn.Status());

    [HttpPost("sign-in/interactive")]
    public ActionResult<AzureConnectionStatus> SignInInteractive() => Feature() ?? Ok(signIn.StartInteractive());

    [HttpPost("sign-in/device-code")]
    public ActionResult<AzureConnectionStatus> SignInDeviceCode() => Feature() ?? Ok(signIn.StartDeviceCode());

    /// <summary>A new token for the signed-in person (e.g. after they activated a PIM role themselves). Never activates anything.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<AzureConnectionStatus>> Refresh(CancellationToken ct) => Feature() ?? Ok(await signIn.RefreshAsync(ct));

    [HttpPost("sign-out")]
    public async Task<ActionResult<AzureConnectionStatus>> SignOut() => Ok(await signIn.SignOutAsync());

    [HttpGet("subscriptions")]
    public async Task<ActionResult<object>> Subscriptions(CancellationToken ct)
    {
        if (Feature() is { } disabled) return disabled;
        try
        {
            var (subscriptions, capability) = await collector.SubscriptionsAsync(ct);
            return Ok(new { subscriptions, capability });
        }
        catch (AzureNotSignedInException e) { return Conflict(new { message = e.Message }); }
    }

    [HttpPost("analyze")]
    public async Task<ActionResult<AzureEnvironmentSnapshot>> Analyze(AzureAnalysisRequest request, CancellationToken ct)
    {
        if (Feature() is { } disabled) return disabled;
        try
        {
            var snapshot = await collector.CollectAsync(request, ct);
            await store.SaveAsync(snapshot, ct);
            return Ok(snapshot);
        }
        catch (AzureNotSignedInException e) { return Conflict(new { message = e.Message }); }
        catch (AzureAnalysisRequestException e) { return BadRequest(new { message = e.Message }); }
    }

    [HttpGet("snapshots")]
    public async Task<ActionResult<IReadOnlyList<AzureEnvironmentSnapshotSummary>>> Snapshots([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await evidence.ListAsync(environmentId, ct));

    [HttpGet("snapshots/{id:guid}")]
    public async Task<ActionResult<AzureEnvironmentSnapshot>> Snapshot(Guid id, [FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await evidence.ResolveAsync(environmentId, id, ct) is { } snapshot ? Ok(snapshot) : NotFound();
    }

    /// <summary>Declared (one Source Analysis snapshot's Infrastructure evidence) vs Observed (one Azure snapshot). Source is never re-parsed.</summary>
    [HttpGet("comparison")]
    public async Task<ActionResult<DeclaredObservedComparison>> Comparison([FromQuery] string environmentId, [FromQuery] Guid? azureSnapshotId, [FromQuery] Guid? sourceSnapshotId,
        [FromQuery] string? environment, CancellationToken ct)
    {
        if (Feature() is { } disabled) return disabled;
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        if (!sources.SourceAnalysisEnabled) return Conflict(new { message = ReviewSourceEvidenceProvider.Disabled });
        if (await evidence.ResolveAsync(environmentId, azureSnapshotId, ct) is not { } observed) return NotFound(new { message = "No Azure snapshot is available for this Target Environment." });
        var source = sourceSnapshotId is { } sid ? await sources.ResolveAsync(environmentId, sid, ct)
            : (await sources.ListAsync(environmentId, ct)).Where(s => s.EvidenceDomains?.Infrastructure.Resources.Count > 0).OrderByDescending(s => s.AnalyzedAt).FirstOrDefault();
        if (source is null) return NotFound(new { message = sourceSnapshotId is null ? "No Source Analysis snapshot with infrastructure evidence exists for this Target Environment." : "The selected source snapshot is unavailable; nothing is substituted." });
        var (label, basis) = !string.IsNullOrWhiteSpace(environment) ? (SourceEnvironments.Normalize(environment), "chosen for this comparison")
            : observed.Scope.Environment is { } scoped ? (scoped, "stated when the Azure scope was analyzed")
            : SourceEnvironments.FromName(environmentId) is { } inferred ? (inferred, "inferred from the Target Environment name") : ((SourceEnvironmentLabel?)null, "not stated: only literal declared names are compared");
        var comparison = DeclaredObservedComparer.Compare(source.EvidenceDomains?.Infrastructure, source.Id, source.EvidenceDomains?.SourceFingerprint ?? source.Archive.Sha256, observed, label, basis);
        return Ok(sourceSnapshotId is null ? comparison with { Limitations = [.. comparison.Limitations, "Source snapshot: the newest one with infrastructure evidence (none was selected)."] } : comparison);
    }

    /// <summary>Whether a resource (kind + name or host) was observed — for IQR, Security and Observability consumers. Read-only.</summary>
    [HttpGet("lookup")]
    public async Task<ActionResult<ObservedResourceLookup>> Lookup([FromQuery] string environmentId, [FromQuery] InfrastructureResourceKind kind, [FromQuery] string? name,
        [FromQuery] string? parent, [FromQuery] Guid? snapshotId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await evidence.LookupAsync(environmentId, kind, name, parent, snapshotId, ct));

    /// <summary>Observed values a person may copy into the Target Environment. Nothing is saved.</summary>
    [HttpGet("target-suggestions")]
    public async Task<ActionResult<IReadOnlyList<AzureTargetSuggestion>>> TargetSuggestions([FromQuery] string environmentId, [FromQuery] Guid? snapshotId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await evidence.TargetSuggestionsAsync(environmentId, snapshotId, ct));

    private ActionResult? Feature() => evidence.Enabled ? null : Conflict(new { message = AzureEnvironmentEvidenceProvider.Disabled });
}
