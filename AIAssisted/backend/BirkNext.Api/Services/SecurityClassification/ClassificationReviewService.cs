using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.SecurityClassification;

public interface IClassificationReviewService
{
    Task<ClassificationOverview> OverviewAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Source Analysis snapshots, related-source candidates and the combined evidence of a scope. Read-only: no upload, no runtime call.</summary>
    Task<ClassificationScopeOptions> SourceScopeAsync(string environmentId, ClassificationSourceScopeRequest? scope, CancellationToken ct = default);
    Task<(ClassificationTestContext? Context, string? Error)> SaveContextAsync(string environmentId, ClassificationTestContext context, CancellationToken ct = default);
    /// <summary>Removes the temporary in-memory context of this environment. Touches no stored row.</summary>
    void ClearContext(string environmentId);
    /// <summary>Runs the review on the request's exact source scope. Throws <see cref="InvalidSourceSelectionException"/> for an invalid scope.</summary>
    Task<ClassificationReviewResult> RunAsync(string environmentId, ClassificationRunRequest request, CancellationToken ct = default);
    Task<ClassificationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default);
    /// <summary>The source/configuration part for Integration Quality Review (no live checks, not stored separately).</summary>
    Task<ClassificationReviewResult?> ReviewAsync(string environmentId, string? environmentType, CancellationToken ct = default);
}

/// <summary>
/// Security Classification / Gradert tilgang review. Source evidence comes from Source Analysis snapshots chosen per run (one primary plus
/// explicitly included related snapshots, read through <see cref="ClassificationSourceScopeService"/> from the Source Analysis store); this
/// review uploads no source. Runs are immutable rows (facts and derived observations — never PII, tokens or raw payloads) that keep the exact
/// source scope. Rows of kind "source" written by earlier versions (uploaded archives) stay untouched; they are read only as legacy input for
/// the Integration Quality Review contribution when no run has a source scope yet. The test context is temporary: it lives in <see cref="ClassificationTestContextStore"/> (process memory) and is
/// never written to the database; runs keep only a value-free <see cref="ClassificationContextSummary"/>. A row of kind "context" that an earlier
/// version may have written is left untouched and is never read as the active context. Live checks are safe GraphQL queries for configured
/// synthetic test children only; the default is no live check at all.
/// </summary>
public sealed class ClassificationReviewService(AppDbContext db, IClassificationLiveProbe probe, ClassificationTestContextStore contexts, ILogger<ClassificationReviewService> logger,
    string? scope = null) : IClassificationReviewService
{
    /// <summary>Caller scope of the in-memory context (the authenticated principal, or "local").</summary>
    public string Scope { get; set; } = scope ?? "local";
    private ClassificationTestContext ActiveContext(string environmentId) => contexts.Get(Scope, environmentId) ?? new();
    private readonly ClassificationSourceScopeService _scopes = new(new IqrSourceStore(db));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Legacy uploaded-archive analyses (no longer written).</summary>
    private const string SourceKind = "source";
    /// <summary>The kind earlier versions used for a stored context; never written or read as the active context any more.</summary>
    public const string LegacyContextKind = "context";
    private const string RunKind = "run";
    /// <summary>Counts captured further apart than this are not compared (stale comparison), whatever they say.</summary>
    public static readonly TimeSpan CountAlignment = TimeSpan.FromHours(1);

    private async Task<T?> LatestAsync<T>(string environmentId, string kind, CancellationToken ct) where T : class
    {
        var json = await db.SecurityClassificationEvidence.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.Kind == kind).OrderByDescending(r => r.CreatedAt).Select(r => r.Json).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, Json);
    }

    private async Task StoreAsync(string environmentId, string kind, DateTimeOffset at, object value, Guid? id, CancellationToken ct)
    {
        db.SecurityClassificationEvidence.Add(new SecurityClassificationEvidenceRecord { Id = id ?? Guid.NewGuid(), EnvironmentId = environmentId, Kind = kind, CreatedAt = at, Json = JsonSerializer.Serialize(value, Json) });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ClassificationOverview> OverviewAsync(string environmentId, CancellationToken ct = default)
    {
        var parsed = await RunsAsync(environmentId, ct);
        // No "current source" here: the page proposes the latest run's exact scope (or none) and asks Source Analysis for its evidence.
        return new ClassificationOverview
        {
            Context = ActiveContext(environmentId), Latest = parsed.FirstOrDefault(),
            History = parsed.Select(r => new ClassificationRunSummary(r.RunId, r.CompletedAt, r.Overall, r.Findings.Count, r.Live.Observations.Count)).ToList(),
        };
    }

    private async Task<List<ClassificationReviewResult>> RunsAsync(string environmentId, CancellationToken ct)
    {
        var runs = await db.SecurityClassificationEvidence.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.Kind == RunKind).OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
        return runs.Select(r => JsonSerializer.Deserialize<ClassificationReviewResult>(r.Json, Json)).OfType<ClassificationReviewResult>().ToList();
    }

    public Task<ClassificationScopeOptions> SourceScopeAsync(string environmentId, ClassificationSourceScopeRequest? scope, CancellationToken ct = default) => _scopes.OptionsAsync(environmentId, scope, ct);

    private static ClassificationSourceScopeRequest Request(ClassificationSourceScope scope) => new()
    {
        PrimarySnapshotId = scope.Primary.SnapshotId, RelatedSnapshotIds = scope.Related.Select(r => r.SnapshotId).ToList(), ExcludedSuggestions = scope.ExcludedSuggestions,
    };

    public async Task<(ClassificationTestContext? Context, string? Error)> SaveContextAsync(string environmentId, ClassificationTestContext context, CancellationToken ct = default)
    {
        if (context.Validate() is { } invalid) return (null, invalid);
        var saved = context with { Environment = context.Environment?.Trim().ToUpperInvariant(), GraphQlEndpoint = context.GraphQlEndpoint?.Trim().TrimEnd('/'), UpdatedAt = DateTimeOffset.UtcNow };
        contexts.Set(Scope, environmentId, saved);
        logger.LogInformation("Security classification temporary test context for {EnvironmentId} set in memory: {Environment}, {Children} test child(ren), approved {Approved}.",
            environmentId, saved.Environment ?? "(none)", saved.TestChildren.Count, saved.ApprovedByTestLead);
        await Task.CompletedTask;
        return (saved, null);
    }

    public void ClearContext(string environmentId)
    {
        var removed = contexts.Clear(Scope, environmentId);
        logger.LogInformation("Security classification temporary test context for {EnvironmentId} cleared from memory ({Removed}).", environmentId, removed ? "was set" : "was not set");
    }

    public async Task<ClassificationReviewResult> RunAsync(string environmentId, ClassificationRunRequest request, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        ClassificationSourceEvidence? source = null;
        if (request.SourceScope is { } scope)
        {
            var (resolved, error) = await _scopes.ResolveAsync(environmentId, scope, ct);
            source = resolved ?? throw new InvalidSourceSelectionException(error ?? "The source scope is invalid.");
        }
        var context = ActiveContext(environmentId);
        var live = await probe.ProbeAsync(context, request, source?.Levels ?? [], ct);
        var result = ClassificationEvaluator.Evaluate(environmentId, source, context, live, request.SourceCounts, request.TargetCounts, started, DateTimeOffset.UtcNow);
        await StoreAsync(environmentId, RunKind, result.CompletedAt, result, result.RunId, ct);
        logger.LogInformation("Security classification review for {EnvironmentId}: {Overall}, live {Live} ({Observations} observation(s)), {Findings} finding(s), source scope {Scope}.",
            environmentId, result.Overall, live.State, live.Observations.Count, result.Findings.Count,
            source?.Scope is { } s ? string.Join(",", new[] { s.Primary }.Concat(s.Related).Select(e => e.Fingerprint[..Math.Min(12, e.Fingerprint.Length)])) : "none");
        return result;
    }

    public async Task<ClassificationReviewResult?> ReviewAsync(string environmentId, string? environmentType, CancellationToken ct = default)
    {
        // The latest run's exact source scope (never a newer snapshot); before any such run, a legacy uploaded-archive analysis if one exists.
        ClassificationSourceEvidence? source = null;
        if ((await RunsAsync(environmentId, ct)).FirstOrDefault(r => r.SourceScope is not null)?.SourceScope is { } scope)
            source = (await _scopes.ResolveAsync(environmentId, Request(scope), ct)).Evidence;
        source ??= await LatestAsync<ClassificationSourceEvidence>(environmentId, SourceKind, ct);
        if (source is null) return null;
        var context = ActiveContext(environmentId);
        var live = new ClassificationLiveEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "Integration Quality Review does not run live security checks (they need per-run test identities).", CapturedAt = DateTimeOffset.UtcNow };
        return ClassificationEvaluator.Evaluate(environmentId, source, context, live, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    public async Task<ClassificationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var record = await db.SecurityClassificationEvidence.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.Kind == RunKind, ct);
        return record is null ? null : JsonSerializer.Deserialize<ClassificationReviewResult>(record.Json, Json);
    }
}

/// <summary>Pure evaluation: summary, pipeline, checks, findings and count comparison from source, context, live and count evidence.</summary>
public static class ClassificationEvaluator
{
    public static List<ClassificationCountComparison> CompareCounts(ClassificationCountEvidence? source, ClassificationCountEvidence? target, TimeSpan alignment)
    {
        if (source is null || target is null)
            return [new ClassificationCountComparison { Nivaa = -1, State = CountComparisonState.NotAvailable, Detail = source is null && target is null ? "No classification counts were supplied." : $"Counts for the {(source is null ? "source" : "target")} system were not supplied." }];
        var apart = (source.CapturedAt - target.CapturedAt).Duration();
        var windowsDiffer = source.WindowStart is { } ss && target.WindowStart is { } ts && (source.WindowEnd ?? source.CapturedAt) is var se && (target.WindowEnd ?? target.CapturedAt) is var te && (ss > te || ts > se);
        var aligned = apart <= alignment && !windowsDiffer;
        return source.Counts.Keys.Union(target.Counts.Keys).OrderBy(l => l).Select(level =>
        {
            long? s = source.Counts.TryGetValue(level, out var sv) ? sv : null;
            long? t = target.Counts.TryGetValue(level, out var tv) ? tv : null;
            var state = s is null || t is null ? CountComparisonState.NotAvailable : !aligned ? CountComparisonState.NotComparable : s == t ? CountComparisonState.Match : CountComparisonState.Mismatch;
            return new ClassificationCountComparison
            {
                Nivaa = level, Source = s, Target = t, State = state,
                Detail = state switch
                {
                    CountComparisonState.NotAvailable => "One of the systems reported no count for this level.",
                    CountComparisonState.NotComparable => windowsDiffer ? "The review windows do not overlap — stale comparison." : $"Captured {apart.TotalHours:0.#} h apart (more than {alignment.TotalHours:0.#} h) — stale comparison.",
                    CountComparisonState.Match => "Counts match at aligned capture times. Data consistency only — this says nothing about authorization.",
                    _ => $"Differs by {Math.Abs((s ?? 0) - (t ?? 0))}. A consistency gap to investigate (timing, filtering, failed deliveries) — not in itself a security breach.",
                },
            };
        }).ToList();
    }

    /// <summary>Severity order of a state (lower = worse).</summary>
    public static int RankOf(ClassificationState state) => Array.IndexOf(Rank, state);

    private static readonly ClassificationState[] Rank =
    [
        ClassificationState.Fail, ClassificationState.IssueDetected, ClassificationState.NeedsDecision, ClassificationState.Warning, ClassificationState.DocumentedOnly, ClassificationState.NotFound,
        ClassificationState.Partial, ClassificationState.NotAvailable, ClassificationState.NotTested, ClassificationState.NoIndicatorsObserved, ClassificationState.NotAssessedHere, ClassificationState.NotApplicable,
        ClassificationState.Configured, ClassificationState.SourceVerified, ClassificationState.Observed, ClassificationState.Verified, ClassificationState.Pass,
    ];

    public static ClassificationState Worst(IEnumerable<ClassificationState> states) => states.OrderBy(s => Array.IndexOf(Rank, s)).DefaultIfEmpty(ClassificationState.NotTested).First();

    private static ClassificationArea AreaOf(ClassificationSurface surface) => surface switch
    {
        ClassificationSurface.DirectProfile or ClassificationSurface.NonexistentComparison => ClassificationArea.DirectAccess,
        ClassificationSurface.AuditLog => ClassificationArea.AuditAccess,
        _ => ClassificationArea.Search,
    };

    public static ClassificationReviewResult Evaluate(string environmentId, ClassificationSourceEvidence? source, ClassificationTestContext context, ClassificationLiveEvidence live,
        ClassificationCountEvidence? sourceCounts, ClassificationCountEvidence? targetCounts, DateTimeOffset started, DateTimeOffset completed)
    {
        if (source is not null) source = ClassificationSourceAnalyzer.Normalize(source);
        ClassificationFact? F(string id) => source?.Facts.FirstOrDefault(f => f.Id == id);
        bool Has(string id, params ClassificationState[] states) => F(id) is { } f && (states.Length == 0 || states.Contains(f.State));
        var checks = new List<ClassificationCheck>();
        if (source is not null)
            checks.AddRange(source.Facts.Select(f => new ClassificationCheck { CheckId = f.Id, Area = f.Area, TestType = f.TestType, Title = f.Title, State = f.State, Detail = f.Detail, Provenance = IntegrationEvidenceSource.SourceCode, Locations = f.Locations, Sources = f.Sources }));
        // Configuration: the test context (ids and labels only).
        var levels = source?.Levels ?? [];
        foreach (var level in levels.Select(l => l.Nivaa).DefaultIfEmpty().Distinct().Where(_ => levels.Count > 0))
        {
            var child = context.TestChildren.FirstOrDefault(c => c.Nivaa == level);
            checks.Add(new ClassificationCheck
            {
                CheckId = $"context-child-{level}", Area = ClassificationArea.ChildAccess, TestType = ClassificationTestType.Functional, Title = $"Level {level} test child",
                State = child is { BarnRegistreringId: not null } ? ClassificationState.Configured : ClassificationState.NotTested,
                Detail = child is { BarnRegistreringId: not null } ? $"Configured (synthetic id{(child.BirkId is null ? "" : " and BiRK id")})." : "Missing — no synthetic test child for this level.", Provenance = IntegrationEvidenceSource.Configuration,
            });
        }
        // Live observations.
        checks.AddRange(live.Observations.Select((o, i) => new ClassificationCheck
        {
            CheckId = $"live-{o.Surface}-{o.Identity}-{o.Nivaa}", Area = AreaOf(o.Surface), TestType = o.TestType,
            Title = $"Level {o.Nivaa} · {o.Identity} · {ClassificationLabels.Surface(o.Surface)}", State = o.State,
            Detail = $"Expected {o.Expected}; observed {o.Observed}. {o.Detail}", Provenance = IntegrationEvidenceSource.NetworkProbe,
        }));
        // What the safe review never does.
        checks.Add(new ClassificationCheck { CheckId = "browser-storage", Area = ClassificationArea.Privacy, TestType = ClassificationTestType.NonFunctional, Title = "Browser storage / route leakage",
            State = ClassificationState.NotTested, Detail = "Not tested: Browser Companion storage, URL and title checks for the synthetic test child are not part of this version. Browser evidence would not prove server authorization.", Provenance = IntegrationEvidenceSource.Configuration });
        checks.Add(new ClassificationCheck { CheckId = "level-change", Area = ClassificationArea.Pipeline, TestType = ClassificationTestType.Functional, Title = "Level change 0 → 2 and 2 → 0 at runtime",
            State = ClassificationState.NotTested, Detail = "Not tested: changing a child's classification is a mutation and is never done by the safe review.", Provenance = IntegrationEvidenceSource.Configuration });
        checks.Add(new ClassificationCheck { CheckId = "parallel-authorization", Area = ClassificationArea.ChildAccess, TestType = ClassificationTestType.NonFunctional, Title = "Concurrent authorization isolation",
            State = ClassificationState.NotTested, Detail = "Not tested: bounded concurrent requests are not run.", Provenance = IntegrationEvidenceSource.Configuration });
        checks.Add(new ClassificationCheck { CheckId = "metric-runtime", Area = ClassificationArea.Observability, TestType = ClassificationTestType.NonFunctional, Title = "Rejection metric at runtime",
            State = ClassificationState.NotAvailable, Detail = "Runtime value not read (no telemetry source connected) — not 0 rejections.", Provenance = IntegrationEvidenceSource.ApplicationInsights });
        // Counts.
        var comparisons = CompareCounts(sourceCounts, targetCounts, ClassificationReviewService.CountAlignment);
        checks.AddRange(comparisons.Where(c => c.Nivaa >= 0).Select(c => new ClassificationCheck
        {
            CheckId = $"count-{c.Nivaa}", Area = ClassificationArea.Consistency, TestType = ClassificationTestType.DataConsistency, Title = $"Level {c.Nivaa} count: source {c.Source?.ToString() ?? "–"} vs target {c.Target?.ToString() ?? "–"}",
            State = c.State switch { CountComparisonState.Match => ClassificationState.Observed, CountComparisonState.Mismatch => ClassificationState.Warning, _ => ClassificationState.NotAvailable },
            Detail = c.Detail, Provenance = IntegrationEvidenceSource.Configuration,
        }));

        // ── Findings ─────────────────────────────────────────────────────────────────────────────────────────────────
        var findings = new List<ClassificationFinding>();
        // A source finding keeps the exact snapshot(s) its facts came from; a cross-source finding cites each one.
        void Add(string rule, ClassificationSeverity severity, ClassificationArea area, string title, string detail, IEnumerable<SourceLocation> locations, string recommendation, params ClassificationFact?[] from) =>
            findings.Add(new ClassificationFinding
            {
                RuleId = rule, Severity = severity, Area = area, Title = title, Detail = detail, Evidence = locations.Select(l => $"{l.File}:{l.Line}").Distinct().Take(6).ToList(), Recommendation = recommendation,
                Sources = from.OfType<ClassificationFact>().SelectMany(f => f.Sources).GroupBy(s => s.SnapshotId).Select(g => g.First() with { Locations = g.SelectMany(s => s.Locations).Distinct().Take(6).ToList() }).ToList(),
            });
        if (Has("cdc-deserializer", ClassificationState.IssueDetected))
            Add("cdc-classification-constant", ClassificationSeverity.High, ClassificationArea.Pipeline, "Security classification is reset to a constant in the production CDC path",
                $"{F("cdc-deserializer")!.Detail} {F("cdc-mapper-guard-consistency")?.Detail} The Kode 6/7 guard therefore allows every event; a level-2/3 row that reaches the adapter is forwarded instead of rejected. The documented primary protection is BiRK's source filtering, which BirkNext cannot observe.",
                F("cdc-deserializer")!.Locations.Concat(F("cdc-mapper-guard-consistency")?.Locations ?? []),
                "Read the classification from the envelope's record (\"after\", or \"before\" for deletes) using the exact column name, and add the proposed raw-Debezium regression tests.",
                F("cdc-deserializer"), F("cdc-mapper-guard-consistency"));
        foreach (var unguarded in source?.Facts.Where(f => f.Area == ClassificationArea.GraphQL && f.State == ClassificationState.IssueDetected) ?? [])
            Add("graphql-unguarded", ClassificationSeverity.High, ClassificationArea.GraphQL, unguarded.Title, unguarded.Detail, unguarded.Locations,
                "Require an operation (and the graded check for graded children) before answering, and apply the classification filter in the lookup.", unguarded);
        if (Has("access-profile-query-excludes"))
            Add("profile-graded-unreachable", ClassificationSeverity.Medium, ClassificationArea.DirectAccess, "Authorized graded access to the profile and revision log is not possible", F("access-profile-query-excludes")!.Detail,
                F("access-profile-query-excludes")!.Locations, "Decide whether graded children are in scope; if they are, let the query return them and keep the service's per-child Person:SeGradertBarn check.", F("access-profile-query-excludes"));
        if (Has("cdc-unknown-level"))
            Add("unknown-level-default", ClassificationSeverity.Medium, ClassificationArea.Pipeline, "Unknown classification defaults to level 0", F("cdc-unknown-level")!.Detail, F("cdc-unknown-level")!.Locations,
                "Decide the rule for missing/unknown levels (reject, quarantine or default) and document it; test it with null, negative, 4+ and non-numeric values.", F("cdc-unknown-level"));
        if (Has("grant-implementation", ClassificationState.DocumentedOnly, ClassificationState.NotFound))
            Add("grant-not-implemented", ClassificationSeverity.Medium, ClassificationArea.Grants, "Granting graded access is not implemented", F("grant-implementation")!.Detail, F("grant-implementation")!.Locations, "Implement or remove the documented workflow.", F("grant-implementation"));
        foreach (var id in new[] { "graphql-endpoint-auth", "audit-log-access", "search-grant-source", "model-terminology", "privacy-logging" }.Where(id => Has(id, ClassificationState.Warning, ClassificationState.IssueDetected)))
            Add(id, id is "graphql-endpoint-auth" ? ClassificationSeverity.Medium : ClassificationSeverity.Low, F(id)!.Area, F(id)!.Title, F(id)!.Detail, F(id)!.Locations, "Review.", F(id));
        foreach (var o in live.Observations.Where(o => o.State == ClassificationState.Fail))
        {
            var disclosure = o.Identity == ClassificationIdentity.Unauthorized;
            findings.Add(new ClassificationFinding
            {
                RuleId = disclosure ? "live-disclosure" : "live-positive-control", Severity = disclosure ? ClassificationSeverity.Critical : ClassificationSeverity.Medium, Area = AreaOf(o.Surface),
                Title = disclosure ? $"Level {o.Nivaa} test child disclosed via {ClassificationLabels.Surface(o.Surface)}" : $"Positive control failed for level {o.Nivaa} via {ClassificationLabels.Surface(o.Surface)}",
                Detail = $"Expected {o.Expected}; observed {o.Observed}. {o.Detail}", Evidence = [live.Target ?? ""], Recommendation = disclosure ? "Treat as a security incident for the test environment and fix server-side authorization." : "Check the grant, the test data and source rules that hide graded children.",
            });
        }

        // ── Pipeline with runtime state ──────────────────────────────────────────────────────────────────────────────
        var pipeline = (source?.Pipeline ?? []).Select(s => s with { Runtime = ClassificationState.NotTested, RuntimeDetail = s.RuntimeDetail.Length > 0 ? s.RuntimeDetail : "No CDC event is observed by the safe review." }).ToList();

        // ── Summary ──────────────────────────────────────────────────────────────────────────────────────────────────
        ClassificationSummaryRow Row(ClassificationArea area, string? title = null)
        {
            var items = checks.Where(c => c.Area == area).ToList();
            var runtime = items.Where(c => c.Provenance == IntegrationEvidenceSource.NetworkProbe && c.State != ClassificationState.NotAvailable).ToList();
            var sourceItems = items.Where(c => c.Provenance == IntegrationEvidenceSource.SourceCode).ToList();
            var state = runtime.Count > 0 ? Worst(runtime.Select(r => r.State).Concat(sourceItems.Select(s => s.State).Where(s => s is ClassificationState.IssueDetected))) : sourceItems.Count > 0 ? Worst(sourceItems.Select(s => s.State)) : items.Count > 0 ? Worst(items.Select(i => i.State)) : ClassificationState.NotTested;
            var evidence = runtime.Count > 0 ? $"Runtime ({runtime.Count} check(s)) + source" : sourceItems.Count > 0 ? "Source evidence only" : items.Count > 0 ? "Configuration" : "No evidence";
            if (area == ClassificationArea.Observability) return ObservabilityRow(area, title, state, evidence);
            return new ClassificationSummaryRow(area, title ?? ClassificationLabels.Area(area), state, evidence);
        }
        // Observability: a metric defined in source is not a metric observed at runtime. Source/config evidence without runtime telemetry is
        // Partial (never "Configured" overall); an unavailable runtime value is Not available — never 0.
        ClassificationSummaryRow ObservabilityRow(ClassificationArea area, string? title, ClassificationState state, string evidence)
        {
            var definition = F("guard-metric");
            var telemetry = checks.FirstOrDefault(c => c.CheckId == "metric-runtime");
            var telemetryObserved = telemetry is not null && ClassificationLabels.IsRuntimeResult(telemetry.State);
            var parts = new List<ClassificationSubStatus>
            {
                definition is null
                    ? new("Metric definition", ClassificationState.NotFound, "No rejection metric was found in the analyzed source.")
                    : new("Metric definition", definition.State, $"{definition.Title[(definition.Title.StartsWith("Metric ") ? 7 : 0)..]} is defined in source."),
                new("Runtime telemetry", telemetry?.State ?? ClassificationState.NotAvailable, telemetryObserved ? telemetry!.Detail : "Runtime telemetry evidence is unavailable (not 0)."),
            };
            if (!telemetryObserved && state is ClassificationState.Configured or ClassificationState.SourceVerified)
            {
                state = ClassificationState.Partial;
                evidence = definition is null ? "Source/configuration only; runtime telemetry unavailable"
                    : $"{definition.Title[(definition.Title.StartsWith("Metric ") ? 7 : 0)..]} is defined in source; runtime telemetry evidence is unavailable.";
            }
            return new ClassificationSummaryRow(area, title ?? ClassificationLabels.Area(area), state, evidence) { Parts = parts };
        }
        var summary = new[]
        {
            ClassificationArea.Model, ClassificationArea.Pipeline, ClassificationArea.Guard, ClassificationArea.DirectAccess, ClassificationArea.Search, ClassificationArea.GraphQL,
            ClassificationArea.AuditAccess, ClassificationArea.ChildAccess, ClassificationArea.Grants, ClassificationArea.EmergencyAccess, ClassificationArea.ReadLogging,
            ClassificationArea.Caching, ClassificationArea.Privacy, ClassificationArea.Observability, ClassificationArea.Consistency,
        }.Select(a => Row(a)).ToList();

        // ── Missing ──────────────────────────────────────────────────────────────────────────────────────────────────
        var missing = new List<string>();
        if (source is null) missing.Add("Source evidence — choose a Source Analysis snapshot to analyze the classification model, the CDC path and the access paths.");
        if (live.State != IntegrationEvidenceState.Available) missing.Add($"Live security checks: {live.Reason}");
        foreach (var level in levels.Where(l => !context.TestChildren.Any(c => c.Nivaa == l.Nivaa && c.BarnRegistreringId is not null)))
            missing.Add($"A synthetic level {level.Nivaa} ({level.Verdi}) test child in the approved context.");
        if (comparisons.All(c => c.State == CountComparisonState.NotAvailable)) missing.Add("Classification count evidence from the source (BiRK) and target (M2LB) captured at aligned times.");
        missing.Add("Telemetry for birk.kode67.rejections and security log events (runtime).");
        var metricFact = F("guard-metric");
        var readiness = ClassificationPrerequisites.Readiness(ClassificationSourceReadiness.For(source), levels, context,
            countsAvailable: comparisons.Any(c => c.State is CountComparisonState.Match or CountComparisonState.Mismatch),
            metricName: metricFact is null ? null : metricFact.Title.StartsWith("Metric ") ? metricFact.Title[7..] : metricFact.Title,
            telemetryObserved: checks.Any(c => c.CheckId == "metric-runtime" && ClassificationLabels.IsRuntimeResult(c.State)));
        var missingItems = readiness.Where(r => r.Counts || r.Group == ClassificationMissingGroup.Secondary)
            .Select(r => new ClassificationMissingItem { Id = r.Id, Group = r.Group, Title = r.Title, Detail = r.Detail, ConfiguresContext = r.ConfiguresContext }).ToList();

        var overall = findings.Any(f => f.Severity is ClassificationSeverity.Critical or ClassificationSeverity.High) || checks.Any(c => c.State is ClassificationState.Fail) ? ClassificationOverall.IssueDetected
            : source is null && live.Observations.Count == 0 ? ClassificationOverall.NotTestable
            : live.Observations.Count > 0 && live.Observations.All(o => o.State == ClassificationState.Pass) && !checks.Any(c => c.State is ClassificationState.IssueDetected or ClassificationState.NeedsDecision) ? ClassificationOverall.Verified
            : ClassificationOverall.Partial;
        return new ClassificationReviewResult
        {
            RunId = Guid.NewGuid(), EnvironmentId = environmentId, StartedAt = started, CompletedAt = completed, Overall = overall,
            SourceAnalyzedAt = source?.AnalyzedAt, SourceArchives = source?.Archives ?? [], Levels = levels, Summary = summary, Pipeline = pipeline, Checks = checks,
            Findings = findings.OrderBy(f => f.Severity).ToList(), Live = live, SourceCounts = sourceCounts, TargetCounts = targetCounts, CountComparisons = comparisons,
            TestCoverage = source?.TestCoverage ?? [], ProposedTests = source?.ProposedTests ?? [], ContextSummary = ClassificationContextSummary.From(context), Missing = missing, MissingItems = missingItems, Readiness = readiness, SourceScope = source?.Scope,
            Limitations =
            [
                "Live checks are GraphQL queries for configured synthetic test children only; no mutation, classification change, grant or broad search is ever performed.",
                source?.Scope is null ? "Source evidence describes the analyzed archive, not necessarily the deployed revision." : "Source evidence describes the selected Source Analysis snapshot(s), not necessarily the deployed revision.",
                "Source evidence shows implementation/configuration paths only; it does not prove runtime enforcement.",
                "Timing differences between responses are not measured.",
                .. source?.Limitations ?? [],
            ],
        };
    }

    // ── Integration Quality Review contribution (conservative) ──────────────────────────────────────────────────────

    public static (List<IntegrationCheck> Checks, List<IntegrationReviewFinding> Findings) ReviewChecks(ClassificationReviewResult result)
    {
        const string subject = "security-classification";
        static IntegrationReviewDomain? Domain(ClassificationCheck c) => c.Area switch
        {
            ClassificationArea.Model => IntegrationReviewDomain.Configuration,
            ClassificationArea.Pipeline or ClassificationArea.Guard or ClassificationArea.Consistency => IntegrationReviewDomain.DataQuality,
            ClassificationArea.Observability => IntegrationReviewDomain.Observability,
            ClassificationArea.DirectAccess or ClassificationArea.Search or ClassificationArea.GraphQL or ClassificationArea.AuditAccess or ClassificationArea.ChildAccess or ClassificationArea.ReadLogging => IntegrationReviewDomain.Security,
            _ => null,
        };
        static IntegrationCheckStatus Status(ClassificationCheck c) => c.State switch
        {
            ClassificationState.Pass => IntegrationCheckStatus.Pass,
            ClassificationState.Fail => IntegrationCheckStatus.Fail,
            ClassificationState.SourceVerified => IntegrationCheckStatus.Detected,
            ClassificationState.Configured => IntegrationCheckStatus.Configured,
            ClassificationState.Observed or ClassificationState.Verified => IntegrationCheckStatus.Observed,
            ClassificationState.IssueDetected or ClassificationState.Warning or ClassificationState.NeedsDecision or ClassificationState.DocumentedOnly or ClassificationState.NotFound or ClassificationState.Partial => IntegrationCheckStatus.Warning,
            ClassificationState.NoIndicatorsObserved => IntegrationCheckStatus.NoIndicatorsObserved,
            ClassificationState.NotAvailable => IntegrationCheckStatus.Unavailable,
            _ => IntegrationCheckStatus.NotAssessed,
        };
        var checks = result.Checks.Where(c => Domain(c) is not null).Select(c => new IntegrationCheck
        {
            CheckId = $"classification-{c.CheckId}", Domain = Domain(c)!.Value, Scope = IntegrationCheckScope.Platform, SubjectId = subject, Title = c.Title, Status = Status(c),
            Expectation = $"{ClassificationLabels.TestType(c.TestType)} — {ClassificationLabels.Area(c.Area)}", Evidence = c.Detail, Explanation = ClassificationLabels.State(c.State),
            Provenance = c.Provenance, CapturedAt = result.CompletedAt,
        }).ToList();
        checks.Add(new IntegrationCheck
        {
            CheckId = "classification-message-flow", Domain = IntegrationReviewDomain.MessageFlow, Scope = IntegrationCheckScope.Platform, SubjectId = subject, Title = "Classification through the CDC message flow",
            Status = IntegrationCheckStatus.NotAssessed, Expectation = "Runtime evidence of a classified event through the CDC path", Evidence = "No CDC event is observed; the path is assessed from source only.",
            Explanation = "Not assessed", Provenance = IntegrationEvidenceSource.Configuration, CapturedAt = result.CompletedAt,
        });
        var findings = result.Findings.Select(f => new IntegrationReviewFinding
        {
            Key = $"classification:{f.RuleId}:{f.Title}", RuleId = f.RuleId,
            Domain = f.Area switch { ClassificationArea.Pipeline or ClassificationArea.Guard => IntegrationReviewDomain.DataQuality, ClassificationArea.Model => IntegrationReviewDomain.Configuration, _ => IntegrationReviewDomain.Security },
            Severity = f.Severity switch { ClassificationSeverity.Critical => IntegrationFindingSeverityV2.Critical, ClassificationSeverity.High => IntegrationFindingSeverityV2.High, ClassificationSeverity.Medium => IntegrationFindingSeverityV2.Medium, ClassificationSeverity.Low => IntegrationFindingSeverityV2.Low, _ => IntegrationFindingSeverityV2.Info },
            Title = f.Title, Subject = "Security classification", Evidence = [f.Detail, .. f.Evidence], Recommendation = f.Recommendation, AffectedIntegrations = [subject],
        }).ToList();
        return (checks, findings);
    }
}
