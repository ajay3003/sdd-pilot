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
    Task<IntegrationReviewReadiness> ReadinessAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default);
    Task<IntegrationReviewResult> RunAsync(IntegrationReviewRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default);
    Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class IntegrationReviewService(IIntegrationCatalogService catalog, IntegrationReviewEngine engine, IIntegrationContractStore contracts, AppDbContext db, ILogger<IntegrationReviewService> logger,
    IApplicationMessagingStore? messaging = null, BirkNext.Api.Services.Integrations.Scim.IScimEvidenceService? scim = null,
    SourceEvidence.IqrSourceStore? source = null) : IIntegrationReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IntegrationReviewReadiness> ReadinessAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        var configured = await catalog.GetAsync(environmentId, environmentType, targetUrl, ct);
        var readiness = engine.Readiness(configured, new IntegrationContractSet(await contracts.LoadAsync(environmentId, ct)), messaging is null ? null : await messaging.GetAsync(environmentId, ct));
        // SCIM identity provisioning is summarized beside the other layers; it never changes whether the review can run.
        readiness = scim is null ? readiness : readiness with { Scim = [.. await scim.ReadinessAsync(configured, ct)] };
        return source is null ? readiness : SourceEvidence.IqrSourceReview.Augment(readiness,
            (await source.ListAsync(environmentId, ct)).Where(s => configured.Integrations.Any(i => i.Id == s.IntegrationId)).ToList());
    }

    public async Task<IntegrationReviewResult> RunAsync(IntegrationReviewRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        var configured = await catalog.GetAsync(request.EnvironmentId, environmentType, targetUrl, ct);
        if (request.SourceSelections.Count > 50 || request.SourceSelections.Select(s => s.IntegrationId).Distinct().Count() != request.SourceSelections.Count)
            throw new SourceEvidence.InvalidSourceSelectionException("Select at most one source snapshot per integration (maximum 50).");
        var selected = new List<IqrSourceSnapshot>();
        foreach (var selection in request.SourceSelections)
        {
            if (!configured.Integrations.Any(i => i.Enabled && i.Id == selection.IntegrationId))
                throw new SourceEvidence.InvalidSourceSelectionException("Source selection must belong to an enabled configured integration.");
            var snapshot = source is null ? null : await source.GetAsync(request.EnvironmentId, selection.IntegrationId, selection.SnapshotId, ct);
            selected.Add(snapshot ?? throw new SourceEvidence.InvalidSourceSelectionException("Selected source snapshot is unavailable for this integration."));
        }
        // Contract drift compares with what the PREVIOUS run recorded, not with whatever is stored now.
        var previous = await db.IntegrationReviewRuns.AsNoTracking().Where(r => r.EnvironmentId == request.EnvironmentId).OrderByDescending(r => r.CompletedAt).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        var previousContracts = previous is null ? [] : JsonSerializer.Deserialize<IntegrationReviewResult>(previous, Json)?.ContractSnapshot ?? [];
        request = request with { EnvironmentType = environmentType ?? request.EnvironmentType };
        var result = await engine.RunAsync(configured, request, new IntegrationContractSet(await contracts.LoadAsync(request.EnvironmentId, ct)), previousContracts,
            messaging is null ? null : await messaging.GetAsync(request.EnvironmentId, ct), ct);
        result = SourceEvidence.IqrSourceReview.Augment(result, selected);
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
