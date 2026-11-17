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
public sealed class TechnicalHistoryService(AppDbContext db, TimeProvider? clock = null, BirkNext.Api.Services.IntegrationSuites.IntegrationSuiteService? suites = null)
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

    /// <summary>A stored report by id; another project's run is not found (runs recorded without a project are readable by any).</summary>
    public async Task<ApiReviewReport?> ApiReviewReportAsync(Guid id, CancellationToken ct, string? projectKey = null) =>
        await db.ApiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } row
        && (string.IsNullOrWhiteSpace(projectKey) || row.ProjectKey is null || row.ProjectKey == projectKey.Trim())
            ? JsonSerializer.Deserialize<ApiReviewReport>(row.ReportJson, Json) : null;

    private static StoredApiReviewRun Summary(ApiReviewRunRecord r) =>
        new(r.Id, r.ProjectKey, r.EnvironmentId, r.EnvironmentName, r.StartedAt, r.GeneratedAt, r.Targets, r.Completed, r.Blocked, r.High, r.Medium, r.Low, r.Info, r.RecordedAt);

    // ── Specialist review runs (Frontend Quality Review, safe fuzzing, authorization checks) ─────────────────────────────────────

    public const int MaxReviewReportBytes = 8 * 1024 * 1024;

    /// <summary>Property names whose non-empty string values are credentials; a report carrying one is refused rather than stored.</summary>
    private static readonly string[] CredentialNames = ["password", "passwd", "secret", "clientsecret", "accesstoken", "refreshtoken", "idtoken", "authorization",
        "cookie", "setcookie", "apikey", "bearer"];

    /// <summary>Stores a finished specialist review (append-only, idempotent per kind + environment + generation time). Refuses a report that carries
    /// a credential value: such a report stays in the browser only.</summary>
    public async Task<Result<StoredTechnicalReviewRun>> StoreReviewRunAsync(StoreTechnicalReviewRunRequest request, CancellationToken ct)
    {
        if (!TechnicalReviewKinds.All.Contains(request.Kind)) return new(null, 400, "Unknown review kind.");
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return new(null, 400, "The run's Target Environment is required.");
        if (request.GeneratedAt == default) return new(null, 400, "A finished review is required.");
        if (request.Report.ValueKind != JsonValueKind.Object) return new(null, 400, "The report must be a JSON object.");
        var json = request.Report.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxReviewReportBytes) return new(null, 413, "The report is too large to store.");
        if (CredentialPath(request.Report, "$") is { } path) return new(null, 400, $"The report holds a credential value ({path}); it was not stored.");
        var env = request.EnvironmentId.Trim();
        var existing = await db.TechnicalReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Kind == request.Kind && r.EnvironmentId == env && r.GeneratedAt == request.GeneratedAt, ct);
        if (existing is not null) return new(Stored(existing), 200, null);
        var row = new TechnicalReviewRunRecord
        {
            Id = Guid.NewGuid(), Kind = request.Kind, ProjectKey = string.IsNullOrWhiteSpace(request.ProjectKey) ? null : request.ProjectKey.Trim(), EnvironmentId = env,
            EnvironmentName = Clip(string.IsNullOrWhiteSpace(request.EnvironmentName) ? env : request.EnvironmentName.Trim(), 200),
            TargetUrl = request.TargetUrl is { Length: > 0 } u ? Clip(u.Trim(), 2000) : null, SourceSnapshotId = request.SourceSnapshotId, GeneratedAt = request.GeneratedAt,
            Outcome = Clip(request.Outcome?.Trim() ?? "", 60), Checks = Math.Max(0, request.Checks), Problems = Math.Max(0, request.Problems),
            Summary = Clip(request.Summary?.Trim() ?? "", 500), ReportSha256 = BirkNext.Api.Services.Specifications.SpecificationCatalogService.Sha(json), ReportJson = json, RecordedAt = Now,
        };
        db.TechnicalReviewRuns.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.TechnicalReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Kind == request.Kind && r.EnvironmentId == env && r.GeneratedAt == request.GeneratedAt, ct) is { } winner)
                return new(Stored(winner), 200, null);
            throw;
        }
        return new(Stored(row), 200, null);
    }

    public async Task<IReadOnlyList<StoredTechnicalReviewRun>> ReviewRunsAsync(string? kind, string? environmentId, string? projectKey, int take, CancellationToken ct)
    {
        var q = db.TechnicalReviewRuns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(r => r.Kind == kind.Trim());
        if (!string.IsNullOrWhiteSpace(environmentId)) q = q.Where(r => r.EnvironmentId == environmentId.Trim());
        if (!string.IsNullOrWhiteSpace(projectKey)) q = q.Where(r => r.ProjectKey == projectKey.Trim());
        var rows = await q.OrderByDescending(r => r.GeneratedAt).Take(Math.Clamp(take, 1, 200))
            .Select(r => new TechnicalReviewRunRecord
            {
                Id = r.Id, Kind = r.Kind, ProjectKey = r.ProjectKey, EnvironmentId = r.EnvironmentId, EnvironmentName = r.EnvironmentName, TargetUrl = r.TargetUrl,
                SourceSnapshotId = r.SourceSnapshotId, GeneratedAt = r.GeneratedAt, Outcome = r.Outcome, Checks = r.Checks, Problems = r.Problems, Summary = r.Summary,
                ReportSha256 = r.ReportSha256, RecordedAt = r.RecordedAt,
            }).ToListAsync(ct);
        return [.. rows.Select(Stored)];
    }

    /// <summary>The stored report JSON of a review run; another project's run is not found.</summary>
    public async Task<string?> ReviewRunReportAsync(Guid id, string? projectKey, CancellationToken ct) =>
        await db.TechnicalReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } row
        && (string.IsNullOrWhiteSpace(projectKey) || row.ProjectKey is null || row.ProjectKey == projectKey.Trim()) ? row.ReportJson : null;

    private static StoredTechnicalReviewRun Stored(TechnicalReviewRunRecord r) => new(r.Id, r.Kind, r.ProjectKey, r.EnvironmentId, r.EnvironmentName, r.TargetUrl, r.SourceSnapshotId,
        r.GeneratedAt, r.Outcome, r.Checks, r.Problems, r.Summary, r.ReportSha256, r.RecordedAt);

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>The JSON path of the first property that looks like a credential and holds a non-empty, unredacted string; null when none.</summary>
    internal static string? CredentialPath(JsonElement e, string path, int depth = 0)
    {
        if (depth > 64) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    var name = p.Name.Replace("-", "").Replace("_", "").ToLowerInvariant();
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v && !IsRedacted(v) && CredentialNames.Contains(name))
                        return $"{path}.{p.Name}";
                    if (CredentialPath(p.Value, $"{path}.{p.Name}", depth + 1) is { } inner) return inner;
                }
                return null;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray()) { if (CredentialPath(item, $"{path}[{i}]", depth + 1) is { } inner) return inner; i++; }
                return null;
            default: return null;
        }
    }

    private static bool IsRedacted(string v) => v.Trim() is "[redacted]" or "[REDACTED]" or "redacted" or "<redacted>" || v.Trim().All(c => c is '*' or '•');

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
            ComparisonBasis = ContractBasis(report, finding.TargetId),
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
        // Staying in the same status only records a retest observation (e.g. "Not comparable": review needed).
        var observation = request.Status == finding.Status;
        if (observation && request.RetestRunId is null) return new(null, 400, "Nothing to record: choose another status or link a retest run.");
        if (!observation && !TechnicalFindingRules.CanMove(finding.Status, request.Status))
            return new(null, 400, $"A finding that is {TechnicalFindingRules.Label(finding.Status).ToLowerInvariant()} cannot become {TechnicalFindingRules.Label(request.Status).ToLowerInvariant()}.");
        string? outcome = null;
        if (request.RetestRunId is { } retestId)
        {
            var (value, problem) = finding.Source == TechnicalFindingRules.IntegrationSuiteStage
                ? await StageRetestOutcomeAsync(finding, retestId, ct) : await ApiRetestOutcomeAsync(finding, retestId, ct);
            if (problem is not null) return new(null, 400, problem);
            outcome = value;
        }
        if (request.Status == TechnicalFindingStatus.VerifiedResolved && TechnicalFindingRules.WhyNotVerified(outcome) is { } why)
            return new(null, 400, why);
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

    /// <summary>API Quality Review retest: a later stored run of the same project and environment. Absent only when the finding's target was fully
    /// reviewed and no longer shows it; a partially reviewed target is inconclusive and a changed contract version is not comparable.</summary>
    private async Task<(string? Outcome, string? Problem)> ApiRetestOutcomeAsync(TechnicalFinding finding, Guid retestId, CancellationToken ct)
    {
        var retest = await db.ApiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == retestId, ct);
        if (retest is null || (retest.ProjectKey is not null && retest.ProjectKey != finding.ProjectKey)) return (null, "The retest run is not a stored run of this project.");
        if (retest.EnvironmentId != finding.EnvironmentId) return (null, "The retest run reviewed another environment.");
        if (retest.GeneratedAt <= finding.FirstSeenAt || retest.Id == finding.OriginalRunId) return (null, "The retest run must be a later run than the one that found the issue.");
        var report = JsonSerializer.Deserialize<ApiReviewReport>(retest.ReportJson, Json)!;
        if (report.Findings.Any(f => TechnicalFindingRules.Key(f) == finding.FindingKey)) return (TechnicalFindingRules.StillPresent, null);
        var targetId = finding.FindingKey.Split('|') is [_, var t, ..] ? t : "";
        var target = report.Targets.FirstOrDefault(r => r.Target.TargetId == targetId);
        if (target is null || target.Status is ApiReviewTargetStatus.Blocked or ApiReviewTargetStatus.NotTested) return (TechnicalFindingRules.NotReviewed, null);
        if (finding.ComparisonBasis is { } basis && ContractBasis(report, targetId) is { } now && now != basis) return (TechnicalFindingRules.NotComparable, null);
        return (target.Status == ApiReviewTargetStatus.Completed ? TechnicalFindingRules.Absent : TechnicalFindingRules.Inconclusive, null);
    }

    /// <summary>The reviewed contract of a target ("OpenAPI 2.1 Loans API"), when the report names one.</summary>
    private static string? ContractBasis(ApiReviewReport report, string targetId) =>
        report.Targets.FirstOrDefault(t => t.Target.TargetId == targetId)?.Contract is { Available: true } c ? $"{c.Kind} {c.Version ?? "?"} {c.Title}".Trim() : null;

    // ── Integration suite stage findings ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts following up a failed stage of a pinned composite run (idempotent per suite and stage). Legacy runs without a pinned
    /// definition cannot be followed up: a later retest could not be shown to be comparable.</summary>
    public async Task<Result<TechnicalFinding>> TrackStageAsync(TrackStageFindingRequest request, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectKey)) return new(null, 400, "Select a project first.");
        if (suites is null) return new(null, 400, "Integration suites are not available in this host.");
        var key = request.ProjectKey.Trim();
        if (await suites.CompositeRunByRunIdAsync(request.RunId, ct) is not { } found || found.Link.SuiteKey != (await SuiteKeyAsync(request.SuiteId, ct)))
            return new(null, 404, "No such composite run of this suite.");
        var (link, view, definition) = found;
        if (definition is null) return new(null, 400, "This run was recorded before BirkNext pinned what a run executed; start a new run to follow up its failures.");
        if (definition.ProjectKey is { } owner && owner != key) return new(null, 404, "No such composite run in this project.");
        var stage = view.Stages.FirstOrDefault(s => s.StageId == request.StageId);
        if (stage is null || !definition.Steps.Any(s => s.StageId == request.StageId)) return new(null, 404, "That stage was not part of the run.");
        if (stage.State is not (BirkNext.IntegrationSuites.CompositeStageState.Failed or BirkNext.IntegrationSuites.CompositeStageState.TimedOut))
            return new(null, 400, "Only a failed stage is followed up as a finding.");
        var findingKey = TechnicalFindingRules.StageKey(link.SuiteKey, request.StageId);
        var existing = await db.TechnicalFindings.AsNoTracking().FirstOrDefaultAsync(f => f.ProjectKey == key && f.Source == TechnicalFindingRules.IntegrationSuiteStage && f.FindingKey == findingKey, ct);
        if (existing is not null) return new(Read(existing), 200, null);
        var by = string.IsNullOrWhiteSpace(request.By) ? actor : request.By.Trim();
        var created = new TechnicalFinding
        {
            Id = Guid.NewGuid(), ProjectKey = key, Source = TechnicalFindingRules.IntegrationSuiteStage, FindingKey = findingKey,
            Title = string.IsNullOrWhiteSpace(stage.Detail) ? $"{stage.Label} failed" : $"{stage.Label}: {stage.Detail}",
            Severity = "High", Endpoint = stage.Label, EnvironmentId = definition.EnvironmentId, OriginalRunId = link.RunId, FirstSeenAt = link.StartedAt,
            Status = TechnicalFindingStatus.New, Version = 1, ComparisonBasis = StageBasis(definition, request.StageId), SuiteId = link.SuiteId, StageId = request.StageId,
            History = [new(Now, by, TechnicalFindingStatus.New, TechnicalFindingStatus.New, "Follow-up started", link.RunId, TechnicalFindingRules.StillPresent)],
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

    private async Task<string?> SuiteKeyAsync(Guid suiteId, CancellationToken ct) =>
        await db.IntegrationSuites.AsNoTracking().Where(s => s.Id == suiteId).Select(s => s.SuiteKey).FirstOrDefaultAsync(ct);

    /// <summary>The stored test revisions of a stage's steps in a pinned definition ("s2-mu:{test}@r2,…").</summary>
    private static string StageBasis(BirkNext.IntegrationSuites.CompositeExecutionDefinition d, string stageId) =>
        string.Join(",", d.Steps.Where(s => s.StageId == stageId).OrderBy(s => s.StepId, StringComparer.Ordinal).Select(s => $"{s.StepId}:{s.TestId}@r{s.TestRevision}"));

    /// <summary>Stage retest: a later pinned run (a stage retest or a new run) of the same suite, project and environment whose stage ran the same
    /// test revisions. Absent = the stage succeeded; failed again = still present; not reached = not reviewed; unknown or still running =
    /// inconclusive; other test revisions or a legacy run = not comparable.</summary>
    private async Task<(string? Outcome, string? Problem)> StageRetestOutcomeAsync(TechnicalFinding finding, Guid retestId, CancellationToken ct)
    {
        if (suites is null) return (null, "Integration suites are not available in this host.");
        if (await suites.CompositeRunByRunIdAsync(retestId, ct) is not { } found || TechnicalFindingRules.StageKey(found.Link.SuiteKey, finding.StageId ?? "") != finding.FindingKey)
            return (null, "The retest run is not a composite run of this finding's suite.");
        var (link, view, definition) = found;
        if (link.RunId == finding.OriginalRunId || link.StartedAt <= finding.FirstSeenAt) return (null, "The retest run must be a later run than the one that found the issue.");
        if (definition is null) return (TechnicalFindingRules.NotComparable, null);
        if (definition.ProjectKey is { } owner && owner != finding.ProjectKey) return (null, "The retest run is not a run of this project.");
        if (definition.EnvironmentId != finding.EnvironmentId) return (null, "The retest run tested another environment.");
        var stage = view.Stages.FirstOrDefault(s => s.StageId == finding.StageId);
        if (stage is null || !definition.Steps.Any(s => s.StageId == finding.StageId)) return (TechnicalFindingRules.NotReviewed, null);
        if (finding.ComparisonBasis is { } basis && StageBasis(definition, finding.StageId!) != basis) return (TechnicalFindingRules.NotComparable, null);
        return stage.State switch
        {
            BirkNext.IntegrationSuites.CompositeStageState.Succeeded => (TechnicalFindingRules.Absent, null),
            BirkNext.IntegrationSuites.CompositeStageState.Failed or BirkNext.IntegrationSuites.CompositeStageState.TimedOut => (TechnicalFindingRules.StillPresent, null),
            BirkNext.IntegrationSuites.CompositeStageState.NotRun or BirkNext.IntegrationSuites.CompositeStageState.Blocked => (TechnicalFindingRules.NotReviewed, null),
            _ => (TechnicalFindingRules.Inconclusive, null),
        };
    }

    private static TechnicalFinding Read(TechnicalFindingRecord r) => JsonSerializer.Deserialize<TechnicalFinding>(r.ContentJson, Json)! with { Version = r.Version };
}
