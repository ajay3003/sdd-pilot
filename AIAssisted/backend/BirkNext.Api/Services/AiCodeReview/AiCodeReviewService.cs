using System.Text.Json;
using BirkNext.AiCodeReview;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.AiCodeReview;

public interface IAiCodeReviewService
{
    Task<ReviewSourceOptions> SourcesAsync(CancellationToken ct = default);
    Task<AiCodeReviewOutcome> RunAsync(AiCodeReviewRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AiCodeReviewRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<AiCodeReviewResult?> GetAsync(Guid runId, CancellationToken ct = default);
}

public sealed record AiCodeReviewOutcome(AiCodeReviewResult? Result, int Status, string? Error)
{
    public static AiCodeReviewOutcome Fail(int status, string error) => new(null, status, error);
}

/// <summary>
/// Runs the Code Risk Review over Source Analysis snapshots through the shared source-evidence provider (no upload, no rescan),
/// stores each run bound to its exact current/baseline snapshot ids, and lists history without re-binding a run to newer snapshots.
/// </summary>
public sealed class AiCodeReviewService(AppDbContext db, IReviewSourceEvidenceProvider sources) : IAiCodeReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReviewSourceOptions> SourcesAsync(CancellationToken ct = default)
    {
        if (!sources.SourceAnalysisEnabled) return new ReviewSourceOptions { SourceAnalysisEnabled = false };
        var list = await sources.ListAsync("", ct);
        return new ReviewSourceOptions
        {
            Snapshots = ReviewSourceEvidenceProvider.Describe(list, s => s.CodeRiskEvidence is null
                ? new ConsumerSourceEvidence(false, "Analyzed before code-risk observations existed: source-level rules are not assessed until the source is analyzed again.")
                : new ConsumerSourceEvidence(true, null, $"{s.CodeRiskEvidence.ProductionFiles} production / {s.CodeRiskEvidence.TestFiles} test C# file(s)")),
        };
    }

    public async Task<AiCodeReviewOutcome> RunAsync(AiCodeReviewRequest request, CancellationToken ct = default)
    {
        if (!sources.SourceAnalysisEnabled) return AiCodeReviewOutcome.Fail(409, ReviewSourceEvidenceProvider.Disabled);
        if (request.CurrentSnapshotId == Guid.Empty) return AiCodeReviewOutcome.Fail(400, "Choose the current Source Analysis snapshot.");
        if (request.BaselineSnapshotId == request.CurrentSnapshotId) return AiCodeReviewOutcome.Fail(400, "The baseline must be a different snapshot than the current one.");
        if (request.Scope == AiReviewScope.ChangedFiles && request.BaselineSnapshotId is null) return AiCodeReviewOutcome.Fail(400, "Changed-files scope needs a baseline snapshot.");
        var current = await sources.ResolveAsync("", request.CurrentSnapshotId, ct);
        if (current is null) return AiCodeReviewOutcome.Fail(404, "The selected current snapshot is unavailable; nothing is substituted.");
        IqrSourceSnapshot? baseline = null;
        if (request.BaselineSnapshotId is { } baselineId)
        {
            baseline = await sources.ResolveAsync("", baselineId, ct);
            if (baseline is null) return AiCodeReviewOutcome.Fail(404, "The selected baseline snapshot is unavailable; nothing is substituted.");
            // Never compare against an unrelated project: both snapshots must have the same repository identity.
            if (ReviewSourceEvidenceProvider.Identity(baseline).Key != ReviewSourceEvidenceProvider.Identity(current).Key)
                return AiCodeReviewOutcome.Fail(400, $"The baseline belongs to another repository ({ReviewSourceEvidenceProvider.Identity(baseline).DisplayName}); a change review compares two snapshots of the same repository.");
        }
        var result = AiCodeReviewEngine.Review(current, baseline, request, DateTimeOffset.UtcNow, ct);
        // Dependency evidence is owned by Dependency Review: its stored runs are consumed (never re-run or re-imported here).
        result = result with { Readiness = [.. result.Readiness, await DependencyReviewCoverageAsync(current, ct)] };
        var label = $"{result.Current.Repository} · {(baseline is null ? "current snapshot" : "change review")} · {result.Findings.Count} finding(s)";
        db.AiCodeReviewRuns.Add(new AiCodeReviewRunRecord
        {
            Id = result.RunId, CompletedAt = result.CompletedAt, CurrentSnapshotId = current.Id, BaselineSnapshotId = baseline?.Id,
            Label = label.Length > 300 ? label[..300] : label, ResultJson = JsonSerializer.Serialize(result, Json),
        });
        await db.SaveChangesAsync(ct);
        return new(result, 200, null);
    }

    /// <summary>
    /// The latest Dependency Review run that reviewed this exact snapshot (Available), else one that reviewed another snapshot of the same
    /// repository (Potentially stale), else none. Legacy runs over uploaded archives have no snapshot binding and are not matched.
    /// </summary>
    internal async Task<AiReadinessItem> DependencyReviewCoverageAsync(IqrSourceSnapshot current, CancellationToken ct)
    {
        const string blocks = "Dependency health and update-policy context";
        var runs = await db.DependencyReviewRuns.AsNoTracking().OrderByDescending(r => r.CompletedAt).Take(50).Select(r => new { r.CompletedAt, r.ResultJson }).ToListAsync(ct);
        var repository = ReviewSourceEvidenceProvider.Identity(current).Key;
        DateTimeOffset? sameRepository = null;
        foreach (var run in runs)
        {
            DependencyReviewResult? result;
            try { result = JsonSerializer.Deserialize<DependencyReviewResult>(run.ResultJson, Json); } catch (JsonException) { continue; }
            if (result?.SourceScope is not { } scope) continue;
            var entries = new[] { scope.Primary }.Concat(scope.Related).ToList();
            if (entries.Any(e => e.SnapshotId == current.Id))
                return new("dependency-review", "Dependency Review", true, $"Run {run.CompletedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC reviewed this snapshot", blocks,
                    $"Dependency Review run {run.CompletedAt.UtcDateTime:yyyy-MM-dd}");
            if (sameRepository is null && entries.Any(e => e.RepositoryKey == repository)) sameRepository = run.CompletedAt;
        }
        return sameRepository is { } at
            ? new("dependency-review", "Dependency Review", true, $"Latest run {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC reviewed another snapshot of this repository", blocks,
                $"Dependency Review run {at.UtcDateTime:yyyy-MM-dd}", Stale: true)
            : new("dependency-review", "Dependency Review", false, "No Dependency Review run for this repository", blocks);
    }

    public async Task<IReadOnlyList<AiCodeReviewRunSummary>> HistoryAsync(CancellationToken ct = default)
    {
        var records = await db.AiCodeReviewRuns.AsNoTracking().OrderByDescending(r => r.CompletedAt).Take(20).ToListAsync(ct);
        return records.Select(r =>
        {
            var result = JsonSerializer.Deserialize<AiCodeReviewResult>(r.ResultJson, Json);
            return new AiCodeReviewRunSummary(r.Id, r.CompletedAt, result?.Mode ?? AiReviewMode.CurrentSnapshot, r.CurrentSnapshotId, r.BaselineSnapshotId, r.Label, result?.Findings.Count ?? 0);
        }).ToList();
    }

    public async Task<AiCodeReviewResult?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        var json = await db.AiCodeReviewRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<AiCodeReviewResult>(json, Json);
    }
}
