using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations;

/// <summary>Integration Quality Review over the configured catalog: pre-run readiness, a read-only run, and immutable run history.</summary>
public interface IIntegrationReviewService
{
    Task<IntegrationReviewReadiness> ReadinessAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default) =>
        ReadinessAsync(environmentId, environmentType, targetUrl, null, ct);
    /// <summary>Readiness for one configured integration (its transport's review areas and its own source evidence only), or environment-wide when null.</summary>
    Task<IntegrationReviewReadiness> ReadinessAsync(string environmentId, string? environmentType, string? targetUrl, string? integrationId, CancellationToken ct = default);
    Task<IntegrationReviewResult> RunAsync(IntegrationReviewRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default);
    Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class IntegrationReviewService(IIntegrationCatalogService catalog, IntegrationReviewEngine engine, IIntegrationContractStore contracts, AppDbContext db, ILogger<IntegrationReviewService> logger,
    IApplicationMessagingStore? messaging = null, BirkNext.Api.Services.Integrations.Scim.IScimEvidenceService? scim = null,
    SourceAnalysis.IReviewSourceEvidenceProvider? source = null, AzureEnvironment.IAzureEnvironmentEvidenceProvider? azure = null,
    IntegrationJourneys.IIntegrationJourneyService? journeys = null, SourceEvidence.IIntegrationEvidenceBindingStore? bindings = null,
    IEnumerable<IntegrationJourneys.IIntegrationJourneyPack>? packs = null) : IIntegrationReviewService
{
    private IReadOnlyList<IntegrationAlias> Aliases => [.. (packs ?? []).SelectMany(pack => pack.Aliases)];

    /// <summary>The catalog seen by a scoped review: only the selected integration and the platform it uses. Unrelated transports contribute no
    /// review area, limitation or reason.</summary>
    internal static IntegrationCatalog Scope(IntegrationCatalog catalog, string? integrationId) =>
        integrationId is null || catalog.Integrations.FirstOrDefault(item => item.Id == integrationId) is not { } selected ? catalog
        : catalog with { Integrations = [selected], Platforms = [.. catalog.Platforms.Where(platform => platform.Id == selected.PlatformId)] };

    /// <summary>Integration-specific source evidence: each in-scope integration's own canonical binding, correlated with that integration only.</summary>
    private async Task<(List<IqrSourceSnapshot> Snapshots, List<IntegrationSourceEvidenceScope> Scopes)> BoundEvidenceAsync(string environmentId,
        IReadOnlyList<IntegrationDefinition> integrations, CancellationToken ct)
    {
        if (source is null || !source.SourceAnalysisEnabled || bindings is null) return ([], []);
        var bound = await bindings.ListAsync(environmentId, ct);
        var snapshots = new List<IqrSourceSnapshot>();
        var scopes = new List<IntegrationSourceEvidenceScope>();
        foreach (var integration in integrations)
        {
            if (bound.FirstOrDefault(item => item.IntegrationId == integration.Id) is not { } binding)
            {
                scopes.Add(new(integration.Id) { Summary = "No canonical source evidence is bound to this integration." });
                continue;
            }
            if (await source.ResolveAsync(environmentId, binding.SnapshotId, ct) is not { } snapshot)
            {
                scopes.Add(new(integration.Id) { SnapshotId = binding.SnapshotId, ArchiveName = binding.ArchiveName, SnapshotBound = true,
                    Summary = $"The bound snapshot {binding.ArchiveName} is no longer stored in Source Analysis; nothing is substituted." });
                continue;
            }
            var (scoped, scope) = SourceEvidence.IntegrationSourceCorrelation.Correlate(integration, snapshot with { IntegrationId = integration.Id }, Aliases);
            snapshots.Add(scoped);
            scopes.Add(scope);
        }
        return (snapshots, scopes);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What test packs know per integration (reference metadata, canonical source). Read-only; a failure only removes the facts.</summary>
    private async Task<IReadOnlyList<IntegrationReviewEvidenceFacts>> FactsAsync(string environmentId, CancellationToken ct)
    {
        if (journeys is null) return [];
        try { return await journeys.ReviewFactsAsync(environmentId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Integration review evidence facts could not be read for {EnvironmentId}; the review continues without them.", environmentId);
            return [];
        }
    }

    public Task<IntegrationReviewReadiness> ReadinessAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default) =>
        ReadinessAsync(environmentId, environmentType, targetUrl, null, ct);

    public async Task<IntegrationReviewReadiness> ReadinessAsync(string environmentId, string? environmentType, string? targetUrl, string? integrationId, CancellationToken ct = default)
    {
        var full = await catalog.GetAsync(environmentId, environmentType, targetUrl, ct);
        var scopeId = integrationId is not null && full.Integrations.Any(item => item.Enabled && item.Id == integrationId) ? integrationId : null;
        var configured = Scope(full, scopeId);
        var facts = await FactsAsync(environmentId, ct);
        var readiness = engine.Readiness(configured, new IntegrationContractSet(await contracts.LoadAsync(environmentId, ct)), messaging is null ? null : await messaging.GetAsync(environmentId, ct),
            facts);
        // The selector always lists every enabled integration; everything else is for the scope alone.
        readiness = readiness with { ScopeIntegrationId = scopeId, Subjects = IntegrationReviewEngine.Subjects(full, facts) };
        // SCIM identity provisioning is summarized beside the other layers; it never changes whether the review can run.
        readiness = scim is null ? readiness : readiness with { Scim = [.. await scim.ReadinessAsync(configured, ct)] };
        // Source evidence: each in-scope integration's own canonical binding, correlated with that integration — never every stored snapshot.
        var (snapshots, scopes) = await BoundEvidenceAsync(environmentId, [.. configured.Integrations.Where(item => item.Enabled)], ct);
        readiness = readiness with { SourceScopes = scopes, AvailableSourceSnapshots = source is { SourceAnalysisEnabled: true } ? [.. await source.ListAsync(environmentId, ct)] : [] };
        return snapshots.Count == 0 ? readiness with { SourceSnapshots = [] } : SourceEvidence.IqrSourceReview.Augment(readiness, snapshots);
    }

    public async Task<IntegrationReviewResult> RunAsync(IntegrationReviewRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        var full = await catalog.GetAsync(request.EnvironmentId, environmentType, targetUrl, ct);
        if (request.IntegrationId is { } requested && !full.Integrations.Any(item => item.Enabled && item.Id == requested))
            throw new SourceEvidence.InvalidSourceSelectionException("The selected integration is not an enabled configured integration of this environment.");
        var configured = Scope(full, request.IntegrationId);
        // A scoped run reviews the selected integration's source only.
        if (request.IntegrationId is { } scoped) request = request with { SourceSelections = [.. request.SourceSelections.Where(selection => selection.IntegrationId == scoped)] };
        if (request.SourceSelections.Count > 50 || request.SourceSelections.Select(s => s.IntegrationId).Distinct().Count() != request.SourceSelections.Count)
            throw new SourceEvidence.InvalidSourceSelectionException("Select at most one source snapshot per integration (maximum 50).");
        var selected = new List<IqrSourceSnapshot>();
        foreach (var selection in request.SourceSelections)
        {
            if (!configured.Integrations.Any(i => i.Enabled && i.Id == selection.IntegrationId))
                throw new SourceEvidence.InvalidSourceSelectionException("Source selection must belong to an enabled configured integration.");
            if (source is { SourceAnalysisEnabled: false }) throw new SourceEvidence.InvalidSourceSelectionException(SourceAnalysis.ReviewSourceEvidenceProvider.Disabled);
            // Exactly the chosen Source Analysis snapshot — never the latest one; the run binds it to the integration it was chosen for.
            var snapshot = source is null ? null : await source.ResolveAsync(request.EnvironmentId, selection.SnapshotId, ct);
            selected.Add((snapshot ?? throw new SourceEvidence.InvalidSourceSelectionException("Selected source snapshot is unavailable in Source Analysis. Repair the selection; nothing is substituted."))
                with { IntegrationId = selection.IntegrationId });
        }
        // Contract drift compares with what the PREVIOUS run recorded, not with whatever is stored now.
        var previous = await db.IntegrationReviewRuns.AsNoTracking().Where(r => r.EnvironmentId == request.EnvironmentId).OrderByDescending(r => r.CompletedAt).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        var previousContracts = previous is null ? [] : JsonSerializer.Deserialize<IntegrationReviewResult>(previous, Json)?.ContractSnapshot ?? [];
        request = request with { EnvironmentType = environmentType ?? request.EnvironmentType };
        var result = await engine.RunAsync(configured, request, new IntegrationContractSet(await contracts.LoadAsync(request.EnvironmentId, ct)), previousContracts,
            messaging is null ? null : await messaging.GetAsync(request.EnvironmentId, ct), ct, await FactsAsync(request.EnvironmentId, ct));
        // Integration-specific evidence: each selected snapshot correlated with the integration it was chosen for.
        var correlated = selected.Select(snapshot => configured.Integrations.FirstOrDefault(item => item.Id == snapshot.IntegrationId) is { } owner
            ? SourceEvidence.IntegrationSourceCorrelation.Correlate(owner, snapshot, Aliases) : (snapshot, new IntegrationSourceEvidenceScope(snapshot.IntegrationId))).ToList();
        result = SourceEvidence.IqrSourceReview.Augment(result, [.. correlated.Select(item => item.Item1)]) with
        {
            ScopeIntegrationId = request.IntegrationId, SourceScopes = [.. correlated.Select(item => item.Item2)],
        };
        // Observed (Azure Environment Analysis): the newest stored snapshot of this Target Environment, read — never re-queried — and kept with the run.
        if (azure is { Enabled: true } && await azure.ResolveAsync(request.EnvironmentId, null, ct) is { } observed)
            result = SourceEvidence.IqrObservedAzureReview.Augment(result, observed);
        // The run stores its own configuration snapshot: editing Integrations later never re-renders this result.
        db.IntegrationReviewRuns.Add(new IntegrationReviewRunRecord
        {
            Id = result.RunId, EnvironmentId = result.EnvironmentId, CompletedAt = result.CompletedAt, Outcome = result.Outcome.ToString(),
            ResultJson = JsonSerializer.Serialize(result, Json),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Integration Quality Review run {RunId} completed but could not be saved to history.", result.RunId); }
        return result;
    }

    public async Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default)
    {
        var records = await db.IntegrationReviewRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId)
            .OrderByDescending(r => r.CompletedAt).Take(20).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<IntegrationReviewResult>(r.ResultJson, Json)).OfType<IntegrationReviewResult>()
            .Select(r => new IntegrationReviewRunSummary(r.RunId, r.CompletedAt, r.Outcome, r.TopicsReviewed, r.Findings.Count)).ToList();
    }

    public async Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var record = await db.IntegrationReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return record is null ? null : JsonSerializer.Deserialize<IntegrationReviewResult>(record.ResultJson, Json);
    }
}
