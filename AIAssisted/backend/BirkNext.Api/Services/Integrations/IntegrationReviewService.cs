using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
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

public sealed class IntegrationReviewService(IIntegrationCatalogService catalog, IntegrationReviewEngine engine, AppDbContext db, ILogger<IntegrationReviewService> logger) : IIntegrationReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IntegrationReviewReadiness> ReadinessAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default) =>
        engine.Readiness(await catalog.GetAsync(environmentId, environmentType, targetUrl, ct));

    public async Task<IntegrationReviewResult> RunAsync(IntegrationReviewRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        var configured = await catalog.GetAsync(request.EnvironmentId, environmentType, targetUrl, ct);
        var result = await engine.RunAsync(configured, request, ct);
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
