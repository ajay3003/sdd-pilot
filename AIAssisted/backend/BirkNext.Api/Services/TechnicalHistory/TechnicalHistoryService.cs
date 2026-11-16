using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.ApiReview;
using BirkNext.TechnicalHistory;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TechnicalHistory;

/// <summary>
/// Server-side history of technical runs (API Quality Review today) and the follow-up of their findings. Runs are append-only and immutable; a
/// finding's follow-up keeps an append-only history; a retest is a later stored run, linked and evaluated once (never re-evaluated, never
/// overwriting the original run). Projects are isolated: another project's run or finding is not found.
/// </summary>
public sealed class TechnicalHistoryService(AppDbContext db, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public sealed record Result<T>(T? Value, int Status, string? Error);

    // ── Runs ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<StoredApiReviewRun>> StoreApiReviewRunAsync(StoreApiReviewRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return new(null, 400, "The run's Target Environment is required.");
        if (request.Report is not { } report || report.GeneratedAt == default) return new(null, 400, "A finished review report is required.");
        var env = request.EnvironmentId.Trim();
        var existing = await db.ApiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.EnvironmentId == env && r.GeneratedAt == report.GeneratedAt, ct);
        if (existing is not null) return new(Summary(existing), 200, null); // idempotent: the same run is stored once
        var findings = report.Findings;
        var row = new ApiReviewRunRecord
        {
            Id = Guid.NewGuid(), ProjectKey = string.IsNullOrWhiteSpace(request.ProjectKey) ? null : request.ProjectKey.Trim(), EnvironmentId = env,
            EnvironmentName = string.IsNullOrWhiteSpace(report.Environment.Name) ? env : report.Environment.Name, StartedAt = report.StartedAt, GeneratedAt = report.GeneratedAt,
            Targets = report.Targets.Count, Completed = report.Targets.Count(t => t.Status == ApiReviewTargetStatus.Completed),
            Blocked = report.Targets.Count(t => t.Status == ApiReviewTargetStatus.Blocked),
            High = findings.Count(f => f.Severity is ApiReviewSeverity.Critical or ApiReviewSeverity.High), Medium = findings.Count(f => f.Severity == ApiReviewSeverity.Medium),
            Low = findings.Count(f => f.Severity == ApiReviewSeverity.Low), Info = findings.Count(f => f.Severity == ApiReviewSeverity.Info),
            ReportJson = JsonSerializer.Serialize(report, Json), RecordedAt = Now,
        };
        db.ApiReviewRuns.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.ApiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.EnvironmentId == env && r.GeneratedAt == report.GeneratedAt, ct) is { } winner) return new(Summary(winner), 200, null);
            throw;
        }
        return new(Summary(row), 200, null);
    }

    /// <summary>Stored runs, newest first: of one environment, optionally only those recorded for a project.</summary>
    public async Task<IReadOnlyList<StoredApiReviewRun>> ApiReviewRunsAsync(string? environmentId, string? projectKey, int take, CancellationToken ct)
    {
        var q = db.ApiReviewRuns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(environmentId)) q = q.Where(r => r.EnvironmentId == environmentId.Trim());
        if (!string.IsNullOrWhiteSpace(projectKey)) q = q.Where(r => r.ProjectKey == projectKey.Trim());
        var rows = await q.OrderByDescending(r => r.GeneratedAt).Take(Math.Clamp(take, 1, 200))
            .Select(r => new { r.Id, r.ProjectKey, r.EnvironmentId, r.EnvironmentName, r.StartedAt, r.GeneratedAt, r.Targets, r.Completed, r.Blocked, r.High, r.Medium, r.Low, r.Info, r.RecordedAt })
            .ToListAsync(ct);
        return [.. rows.Select(r => new StoredApiReviewRun(r.Id, r.ProjectKey, r.EnvironmentId, r.EnvironmentName, r.StartedAt, r.GeneratedAt, r.Targets, r.Completed, r.Blocked, r.High, r.Medium, r.Low, r.Info, r.RecordedAt))];
    }

    public async Task<ApiReviewReport?> ApiReviewReportAsync(Guid id, CancellationToken ct) =>
        await db.ApiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } row ? JsonSerializer.Deserialize<ApiReviewReport>(row.ReportJson, Json) : null;

    private static StoredApiReviewRun Summary(ApiReviewRunRecord r) =>
        new(r.Id, r.ProjectKey, r.EnvironmentId, r.EnvironmentName, r.StartedAt, r.GeneratedAt, r.Targets, r.Completed, r.Blocked, r.High, r.Medium, r.Low, r.Info, r.RecordedAt);

    // ── Finding follow-up ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TechnicalFinding>> FindingsAsync(string projectKey, CancellationToken ct) =>
        [.. (await db.TechnicalFindings.AsNoTracking().Where(f => f.ProjectKey == projectKey.Trim()).ToListAsync(ct)).Select(Read).OrderByDescending(f => f.FirstSeenAt)];

    /// <summary>Starts following up a finding of a stored run (idempotent per finding identity: the existing follow-up is returned).</summary>
    public async Task<Result<TechnicalFinding>> TrackAsync(TrackTechnicalFindingRequest request, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectKey)) return new(null, 400, "Select a project first.");
        var key = request.ProjectKey.Trim();
        var run = await db.ApiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RunId, ct);
        if (run is null || (run.ProjectKey is not null && run.ProjectKey != key)) return new(null, 404, "No such stored review run in this project.");
        var report = JsonSerializer.Deserialize<ApiReviewReport>(run.ReportJson, Json)!;
        var finding = report.Findings.FirstOrDefault(f => f.Id == request.FindingId);
        if (finding is null) return new(null, 404, "That finding is not in the stored run.");
        var findingKey = TechnicalFindingRules.Key(finding);
        var existing = await db.TechnicalFindings.AsNoTracking().FirstOrDefaultAsync(f => f.ProjectKey == key && f.Source == TechnicalFindingRules.ApiQualityReview && f.FindingKey == findingKey, ct);
        if (existing is not null) return new(Read(existing), 200, null);
        var by = string.IsNullOrWhiteSpace(request.By) ? actor : request.By.Trim();
        var created = new TechnicalFinding
        {
            Id = Guid.NewGuid(), ProjectKey = key, Source = TechnicalFindingRules.ApiQualityReview, FindingKey = findingKey, Title = finding.Title, Severity = finding.Severity.ToString(),
            Endpoint = finding.Endpoint, EnvironmentId = run.EnvironmentId, OriginalRunId = run.Id, FirstSeenAt = run.GeneratedAt, Status = TechnicalFindingStatus.New, Version = 1,
            History = [new(Now, by, TechnicalFindingStatus.New, TechnicalFindingStatus.New, "Follow-up started", run.Id, "Present")],
        };
        db.TechnicalFindings.Add(new TechnicalFindingRecord
        {
            Id = created.Id, ProjectKey = key, Source = created.Source, FindingKey = findingKey, Status = created.Status.ToString(), ContentJson = JsonSerializer.Serialize(created, Json), Version = 1, UpdatedAt = Now,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.TechnicalFindings.AsNoTracking().FirstOrDefaultAsync(f => f.ProjectKey == key && f.Source == created.Source && f.FindingKey == findingKey, ct) is { } winner) return new(Read(winner), 200, null);
            throw;
        }
        return new(created, 200, null);
    }

    /// <summary>
    /// Moves a follow-up. "Verified resolved" needs a retest: a later stored run of the same environment in which the finding is absent; a retest
    /// in which it is still present can only reopen it. The original run is never touched.
    /// </summary>
    public async Task<Result<TechnicalFinding>> UpdateAsync(string projectKey, Guid id, UpdateTechnicalFindingRequest request, string actor, CancellationToken ct)
    {
        var row = await db.TechnicalFindings.FirstOrDefaultAsync(f => f.Id == id && f.ProjectKey == projectKey.Trim(), ct);
        if (row is null) return new(null, 404, "No such finding follow-up in this project.");
        if (row.Version != request.Version) return new(null, 409, "The follow-up was changed by someone else. Reload; nothing was saved.");
        var finding = Read(row);
        if (!TechnicalFindingRules.CanMove(finding.Status, request.Status))
            return new(null, 400, $"A finding that is {TechnicalFindingRules.Label(finding.Status).ToLowerInvariant()} cannot become {TechnicalFindingRules.Label(request.Status).ToLowerInvariant()}.");
        string? outcome = null;
        if (request.RetestRunId is { } retestId)
        {
            var retest = await db.ApiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == retestId, ct);
            if (retest is null || (retest.ProjectKey is not null && retest.ProjectKey != finding.ProjectKey)) return new(null, 400, "The retest run is not a stored run of this project.");
            if (retest.EnvironmentId != finding.EnvironmentId) return new(null, 400, "The retest run reviewed another environment.");
            if (retest.GeneratedAt <= finding.FirstSeenAt || retest.Id == finding.OriginalRunId) return new(null, 400, "The retest run must be a later run than the one that found the issue.");
            var report = JsonSerializer.Deserialize<ApiReviewReport>(retest.ReportJson, Json)!;
            var targetReviewed = report.Targets.Any(t => finding.FindingKey.Split('|') is [_, var target, ..] && t.Target.TargetId == target
                && t.Status is ApiReviewTargetStatus.Completed or ApiReviewTargetStatus.PartiallyCompleted);
            outcome = report.Findings.Any(f => TechnicalFindingRules.Key(f) == finding.FindingKey) ? "Still present" : targetReviewed ? "Absent" : "Not reviewed";
        }
        if (request.Status == TechnicalFindingStatus.VerifiedResolved && outcome != "Absent")
            return new(null, 400, outcome is null ? "Link the retest run that no longer shows the finding." : outcome == "Not reviewed"
                ? "The retest run did not review the finding's target, so it cannot verify the fix." : "The retest run still shows the finding; reopen it instead.");
        var by = string.IsNullOrWhiteSpace(request.By) ? actor : request.By.Trim();
        var external = string.IsNullOrWhiteSpace(request.ExternalReference) ? finding.ExternalReference : request.ExternalReference.Trim()[..Math.Min(request.ExternalReference.Trim().Length, 300)];
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 2000)];
        var updated = finding with
        {
            Status = request.Status, ExternalReference = external, LastRetestRunId = request.RetestRunId ?? finding.LastRetestRunId, Version = row.Version + 1,
            History = [.. finding.History, new(Now, by, finding.Status, request.Status, note, request.RetestRunId, outcome, external != finding.ExternalReference ? external : null)],
        };
        row.Status = updated.Status.ToString(); row.ContentJson = JsonSerializer.Serialize(updated, Json); row.Version = updated.Version; row.UpdatedAt = Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(null, 409, "The follow-up was changed by someone else. Reload; nothing was saved."); }
        return new(updated, 200, null);
    }

    private static TechnicalFinding Read(TechnicalFindingRecord r) => JsonSerializer.Deserialize<TechnicalFinding>(r.ContentJson, Json)! with { Version = r.Version };
}
