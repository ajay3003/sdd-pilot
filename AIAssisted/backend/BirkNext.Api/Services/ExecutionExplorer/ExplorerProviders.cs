using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.CriticalE2E;
using BirkNext.ExecutionEvidence;
using BirkNext.ExecutionExplorer;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.Parquet;
using BirkNext.PerformanceTests;
using BirkNext.Applicability;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExecutionExplorer;

/// <summary>
/// A read-only adapter over one evidence owner. <see cref="RecentAsync"/> returns at most <c>window</c> rows, newest first, already narrowed by the
/// filters the owner's store can apply (environment, time); <see cref="DetailAsync"/> reads one stored result. Neither ever executes anything.
/// </summary>
public interface IExplorerProvider
{
    IReadOnlyList<ExplorerSource> Sources { get; }
    Task<List<ExplorerRun>> RecentAsync(ExplorerQuery query, int window, CancellationToken ct);
    Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct);
}

internal static class ExplorerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static T? Read<T>(string json) where T : class { try { return JsonSerializer.Deserialize<T>(json, Options); } catch (JsonException) { return null; } }
}

// ── Execution Evidence: Contract Testing runs and chains, Integration Suite runs (flat and composite) ─────────────────────────────────────────────

public sealed class ExecutionEvidenceExplorerProvider(AppDbContext db, ExecutionEvidenceStore store) : IExplorerProvider
{
    /// <summary>Composite verdicts read the run's calls; bounded per request.</summary>
    public const int MaxCompositeVerdicts = 100;

    public IReadOnlyList<ExplorerSource> Sources { get; } = [ExplorerSource.ContractTests, ExplorerSource.ContractChain, ExplorerSource.IntegrationSuite, ExplorerSource.CompositeE2E];

    public async Task<List<ExplorerRun>> RecentAsync(ExplorerQuery q, int window, CancellationToken ct)
    {
        var query = db.ExecutionRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.EnvironmentId)) query = query.Where(r => r.EnvironmentId == q.EnvironmentId);
        if (q.From is { } from) query = query.Where(r => r.StartedAt >= from);
        if (q.To is { } to) query = query.Where(r => r.StartedAt <= to);
        if (q.Protected is { } p) query = query.Where(r => r.Protected == p);
        // Only the summary column: request/response bodies live in execution_calls and are never read for the history.
        var rows = await query.OrderByDescending(r => r.StartedAt).Take(window).Select(r => new { r.Id, r.SummaryJson, r.Protected }).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var links = (await db.IntegrationSuiteRuns.AsNoTracking().Where(l => ids.Contains(l.RunId)).ToListAsync(ct)).GroupBy(l => l.RunId).ToDictionary(g => g.Key, g => g.First());
        var projects = await ContractProjectsAsync(ct);
        var list = new List<ExplorerRun>();
        var verdicts = 0;
        foreach (var r in rows)
        {
            if (ExplorerJson.Read<ExecutionRunSummary>(r.SummaryJson) is not { } s) continue;
            var link = links.GetValueOrDefault(r.Id);
            CompositeRunView? composite = null;
            if (link?.PlanJson is not null && verdicts++ < MaxCompositeVerdicts) composite = await IntegrationSuiteService.ReadCompositeRunAsync(db, store, link, ct);
            list.Add(Project(s with { Protected = r.Protected }, link, composite, projects));
        }
        return list;
    }

    /// <summary>Contract key → owning project (a contract without a resolved project maps to nothing: the run stays unattributed).</summary>
    private async Task<Dictionary<string, string>> ContractProjectsAsync(CancellationToken ct) =>
        (await db.ApiContracts.AsNoTracking().Where(c => c.ProjectKey != null).Select(c => new { c.Key, c.ProjectKey }).ToListAsync(ct))
            .GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().ProjectKey!, StringComparer.Ordinal);

    internal static string? ProjectOf(IEnumerable<string> contractRefs, IReadOnlyDictionary<string, string> projects)
    {
        var keys = contractRefs.Select(r => r.Split('@')[0]).Select(k => projects.GetValueOrDefault(k)).Distinct().ToList();
        // Unattributed when any contract has no project or the run spans several projects: never guessed.
        return keys.Count == 1 && keys[0] is { } only ? only : null;
    }

    internal static ExplorerRun Project(ExecutionRunSummary s, IntegrationSuiteRunRecord? link, CompositeRunView? composite, IReadOnlyDictionary<string, string> projects)
    {
        var source = link?.PlanJson is not null ? ExplorerSource.CompositeE2E : link is not null ? ExplorerSource.IntegrationSuite : s.ChainRef is not null ? ExplorerSource.ContractChain : ExplorerSource.ContractTests;
        var (outcome, reason) = composite is not null ? ExplorerStatus.Composite(composite.Verdict, composite.VerdictReason)
            : link?.PlanJson is not null ? (ExplorerOutcome.NotAssessed, "The composite verdict was not computed in this list (bounded); open the run.")
            : ExplorerStatus.Execution(s.State, s.Counts);
        var executed = s.Counts.Executed;
        var evidence = executed == 0 ? EvidenceCompleteness.SummaryOnly : s.CallsWithCompleteEvidence >= executed ? EvidenceCompleteness.Complete
            : s.CallsWithCompleteEvidence > 0 ? EvidenceCompleteness.Partial : EvidenceCompleteness.Missing;
        List<ExplorerCapability> caps = [ExplorerCapability.Calls, ExplorerCapability.Attempts, ExplorerCapability.Assertions, ExplorerCapability.RequestResponse,
            ExplorerCapability.Timeline, ExplorerCapability.Compare, ExplorerCapability.Protect, ExplorerCapability.Delete, ExplorerCapability.Export];
        if (source == ExplorerSource.CompositeE2E) caps.AddRange([ExplorerCapability.Stages, ExplorerCapability.Correlation]);
        var stages = composite?.Stages ?? [];
        return new ExplorerRun
        {
            Source = source, Category = source is ExplorerSource.CompositeE2E or ExplorerSource.IntegrationSuite ? ExplorerCategory.IntegrationTest : ExplorerCategory.FunctionalTest,
            SourceRunId = s.RunId.ToString("D"), DisplayId = ExplorerStatus.Short(s.RunId.ToString("N")), Name = s.Name, StartedAt = s.StartedAt, CompletedAt = s.CompletedAt,
            DurationMs = s.CompletedAt is { } c ? (c - s.StartedAt).TotalMilliseconds : null, EnvironmentId = s.EnvironmentId,
            ProjectKey = ProjectOf(s.ContractRefs, projects) ?? SuiteProject(link),
            Outcome = outcome, SourceStatus = composite is not null ? $"{s.State} · {Upper(composite.Verdict.ToString())}" : s.State.ToString(), OutcomeReason = reason,
            Total = composite is not null ? stages.Count : s.Counts.Planned,
            Passed = composite is not null ? stages.Count(x => x.State == CompositeStageState.Succeeded) : s.Counts.Passed,
            Failed = composite is not null ? stages.Count(x => x.State is CompositeStageState.Failed or CompositeStageState.UnknownOutcome) : s.Counts.Failed + s.Counts.Errors,
            NotAssessed = composite is not null ? stages.Count(x => x.State is CompositeStageState.NotRun or CompositeStageState.TimedOut or CompositeStageState.NotObservable or CompositeStageState.Blocked)
                : s.Counts.NotAssessed + s.Counts.Skipped,
            CountLabel = composite is not null ? "stages" : "calls", Evidence = evidence, Protected = s.Protected,
            SuiteRef = link is not null ? $"{link.SuiteKey}" : s.ChainRef, ContractRefs = s.ContractRefs, Capabilities = caps,
            OwnerHref = link is not null ? $"integration-suites?area=execute&suite={link.SuiteId:D}" : "api-quality-review/contracts",
            OwnerLabel = link is not null ? "Integration Test Suites" : "Contract Testing",
        };
    }

    /// <summary>Suite keys are "scope:…:owningProject:hash" for composite suites; the owning project is recorded in the key.</summary>
    private static string? SuiteProject(IntegrationSuiteRunRecord? link) =>
        link?.SuiteKey.Split(':') is { Length: >= 4 } parts && parts[1] == "composite" ? parts[2] : null;

    internal static string Upper(string pascal) => string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]) ? "_" + c : c.ToString())).ToUpperInvariant();

    public async Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var runId)) return null;
        var detail = await store.GetAsync(runId, new ExecutionCallQuery(0, 200), ct);
        if (detail is null) return null;
        var link = await db.IntegrationSuiteRuns.AsNoTracking().FirstOrDefaultAsync(l => l.RunId == runId, ct);
        var composite = link?.PlanJson is not null ? await IntegrationSuiteService.ReadCompositeRunAsync(db, store, link, ct) : null;
        var run = Project(detail.Run, link, composite, await ContractProjectsAsync(ct));
        if (run.Source != source) return null;
        var items = detail.Calls.OrderBy(c => c.Sequence).Select(c => new ExplorerItem(c.CallId.ToString("D"), c.Definition.Name, c.Definition.Protocol == ExecutionProtocol.GraphQl ? "GraphQL" : $"{c.Definition.Method}",
            c.State.ToString(), Reason(c), c.Definition.OperationRef, c.CompletedAt is { } e && c.StartedAt is { } b ? (e - b).TotalMilliseconds : null)).ToList();
        var timeline = new List<ExplorerTimelineEvent>();
        foreach (var c in detail.Calls.OrderBy(c => c.Sequence))
            foreach (var a in c.Attempts.OrderBy(a => a.AttemptNumber))
            {
                var stage = composite?.Stages.FirstOrDefault(st => st.CallIds.Contains(c.CallId))?.StageId ?? c.Definition.StepId ?? c.Definition.Name;
                var correlation = $"X-BirkNext-Execution-Id {runId:N}.{c.Sequence}.{a.AttemptNumber}";
                timeline.Add(new(a.StartedAt, stage, a.Transmissions > 0 ? $"Submitted {c.Definition.Method} (attempt {a.AttemptNumber})" : $"Not sent (attempt {a.AttemptNumber})",
                    a.Transport.ToString(), $"call {c.CallId:N}#attempt{a.AttemptNumber}", correlation));
                if (a.CompletedAt is { } done)
                    timeline.Add(new(done, stage, a.Transport switch
                    {
                        TransportResult.ResponseReceived => $"Responded {a.Response?.StatusCode}",
                        TransportResult.TimedOut => "Timed out (no response)",
                        TransportResult.ConnectionFailed => "Failed (connection)",
                        TransportResult.Cancelled => "Cancelled",
                        _ => "Not sent",
                    }, a.Transport == TransportResult.ResponseReceived ? c.State.ToString() : a.Transport == TransportResult.TimedOut && c.Definition.Method is not ("GET" or "HEAD") ? "UnknownOutcome" : a.Transport.ToString(),
                        $"call {c.CallId:N}#attempt{a.AttemptNumber}", correlation));
            }
        if (composite is not null)
            // A stage whose calls were never transmitted (skipped after an earlier failure, or never planned) was not observed: listed as such,
            // never given a time or an intermediate transition.
            foreach (var st in composite.Stages.Where(st => !detail.Calls.Any(c => st.CallIds.Contains(c.CallId) && c.Attempts.Any(a => a.Transmissions > 0))))
                timeline.Add(new(null, st.StageId, "Not observed by this run", st.State.ToString(), null, "none"));
        var trace = new List<ExplorerTrace>();
        foreach (var cref in detail.Run.ContractRefs) trace.Add(new("Contract version", cref, "exercised by the run", "api-quality-review/contracts"));
        if (detail.Run.ChainRef is { } chain) trace.Add(new("Chain revision", chain, "executed definition", "api-quality-review/contracts"));
        if (detail.Run.SourceSnapshotId is { } snap) trace.Add(new("Source snapshot", snap.ToString("D"), "contracts taken from", $"source-analysis?snapshot={snap:D}"));
        if (link is not null) trace.Add(new("Integration suite", link.SuiteKey, link.PlanJson is not null ? "composite run of" : "run of", $"integration-suites?area=execute&suite={link.SuiteId:D}"));
        foreach (var c in detail.Calls)
        {
            if (c.Definition.OperationRef is { } op) trace.Add(new("Contract operation", op, $"call {c.Sequence}", null));
            if (c.Definition.DefinitionVersion is { } dv) trace.Add(new("Test definition", dv, $"call {c.Sequence}", null));
            foreach (var td in c.Definition.TestDataRefs) trace.Add(new("Test Data version", td, $"call {c.Sequence}", "test-data"));
            if (c.Definition.DatasetRow is { } row) trace.Add(new("Dataset row", row, $"call {c.Sequence}", "test-data"));
        }
        if (composite is not null)
            foreach (var corr in composite.Correlation) trace.Add(new("Correlation", $"{corr.Boundary} · {corr.Field}", corr.State.ToString(), null));
        var limitations = new List<string>();
        if (detail.TotalCalls > detail.Calls.Count) limitations.Add($"{detail.TotalCalls - detail.Calls.Count} more call(s): open the call explorer for all of them.");
        if (composite is not null) limitations.Add(composite.Kind == CompositeRunKind.SubflowTest ? "Subflow test: never a full end-to-end result." : "Full end-to-end run: verified only when every mandatory stage succeeded and every boundary is correlated in this one run.");
        limitations.Add("Opening a run reads stored evidence only; nothing is re-sent.");
        return new ExplorerRunDetail
        {
            Run = run, Items = composite is not null ? [.. composite.Stages.Select(st => new ExplorerItem(st.StageId, st.Label, "Stage", st.State.ToString(), st.Detail, null))] : items,
            ItemsLabel = composite is not null ? "Stages" : "Calls", Timeline = [.. timeline.OrderBy(t => t.At ?? DateTimeOffset.MaxValue)], Traceability = [.. trace.DistinctBy(t => (t.Kind, t.Reference, t.Relationship))],
            Metrics =
            [
                new("calls", "Logical calls", detail.Run.Counts.Planned.ToString()), new("attempts", "Attempts", detail.Run.Counts.Attempts.ToString()),
                new("wire", "Requests on the wire", detail.Run.WireTransmissions.ToString()), new("evidence", "Calls with complete evidence", $"{detail.Run.CallsWithCompleteEvidence} of {detail.Run.Counts.Executed}"),
                new("auth", "Authentication", detail.Run.AuthenticationMode ?? "none"), new("target", "Target", detail.Run.TargetOrigin),
            ],
            Limitations = limitations, ExecutionRunId = runId,
        };
    }

    private static string? Reason(ExecutionCallRecord c)
    {
        var failed = c.Assertions.Where(a => a.Outcome == AssertionOutcome.Failed).Select(a => a.Explanation).FirstOrDefault();
        var assessed = c.Assertions.Count(a => a.Outcome != AssertionOutcome.NotAssessed);
        return failed ?? c.Reason ?? (c.Assertions.Count == 0 ? "No assertions." : $"{assessed} of {c.Assertions.Count} assertion(s) assessed.");
    }

    /// <summary>Two Execution Evidence runs, call by call (definition reference, else name + dataset row). Comparable only within one environment
    /// and when they executed the same definitions; otherwise the reasons are returned and nothing is compared.</summary>
    public async Task<ExplorerComparison> CompareAsync(Guid baselineId, Guid currentId, CancellationToken ct)
    {
        var projects = await ContractProjectsAsync(ct);
        async Task<(ExplorerRun? Run, ExecutionRunSummary? Summary, List<(string Key, string Name, ExecutionCallRow Row)> Calls)> Load(Guid id)
        {
            var row = await db.ExecutionRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row is null || ExplorerJson.Read<ExecutionRunSummary>(row.SummaryJson) is not { } s) return (null, null, []);
            var link = await db.IntegrationSuiteRuns.AsNoTracking().FirstOrDefaultAsync(l => l.RunId == id, ct);
            var calls = await db.ExecutionCalls.AsNoTracking().Where(c => c.RunId == id).OrderBy(c => c.Sequence).Take(500).ToListAsync(ct);
            return (Project(s with { Protected = row.Protected }, link, null, projects), s, [.. calls.Select(c => (Key(c), c.Name, c))]);
        }
        var (a, sa, ca) = await Load(baselineId);
        var (b, sb, cb) = await Load(currentId);
        var reasons = new List<string>();
        if (a is null || b is null) return new(a, b, false, ["A run was not found."], [], []);
        if (a.Source != b.Source) reasons.Add($"Different kinds of run ({a.Source} vs {b.Source}).");
        if (!string.Equals(a.EnvironmentId, b.EnvironmentId, StringComparison.Ordinal)) reasons.Add("Different Target Environments: results are not comparable.");
        if (sa!.ChainRef?.Split("@r")[0] != sb!.ChainRef?.Split("@r")[0]) reasons.Add("Different chains.");
        var shared = ca.Select(x => x.Key).Intersect(cb.Select(x => x.Key)).Count();
        if (shared == 0) reasons.Add("The runs executed no common test definition.");
        var limitations = new List<string> { "Comparison of stored, redacted evidence: equal responses never prove equal domain state." };
        if (sa.ChainRef != sb.ChainRef && reasons.Count == 0) limitations.Add($"Chain revisions differ ({sa.ChainRef} → {sb.ChainRef}).");
        if (!sa.ContractRefs.SequenceEqual(sb.ContractRefs)) limitations.Add($"Contract versions differ ({string.Join(", ", sa.ContractRefs)} → {string.Join(", ", sb.ContractRefs)}).");
        if (reasons.Count > 0) return new(a, b, false, reasons, [], limitations);
        var rows = new List<ExplorerComparisonRow>();
        var before = ca.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        var after = cb.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var key in before.Keys.Union(after.Keys))
        {
            var x = before.GetValueOrDefault(key);
            var y = after.GetValueOrDefault(key);
            if (x.Row is null) { rows.Add(new(key, y.Name, null, y.Row!.State, ComparisonChange.OnlyInCurrent, null)); continue; }
            if (y.Row is null) { rows.Add(new(key, x.Name, x.Row.State, null, ComparisonChange.OnlyInBaseline, null)); continue; }
            bool Bad(string s) => s is nameof(ExecutionState.Failed) or nameof(ExecutionState.Error) or nameof(ExecutionState.TimedOut) or nameof(ExecutionState.Blocked);
            var regressed = RegressedAssertions(x.Row, y.Row);
            var change = Bad(y.Row.State) && !Bad(x.Row.State) ? ComparisonChange.NewFailure
                : Bad(x.Row.State) && !Bad(y.Row.State) ? ComparisonChange.ResolvedFailure
                : regressed.Count > 0 ? ComparisonChange.Regressed
                : Bad(x.Row.State) && Bad(y.Row.State) ? ComparisonChange.StillFailing
                : x.Row.State != y.Row.State || x.Row.HttpStatus != y.Row.HttpStatus ? ComparisonChange.ChangedStatus
                : x.Row.DurationMs is { } d0 && y.Row.DurationMs is { } d1 && Math.Abs(d1 - d0) > Math.Max(100, d0 * 0.5) ? ComparisonChange.LatencyChanged
                : ComparisonChange.Unchanged;
            var detail = change switch
            {
                ComparisonChange.Regressed or ComparisonChange.NewFailure when regressed.Count > 0 => $"Assertion(s) passed before, fail now: {string.Join(", ", regressed)}",
                ComparisonChange.ChangedStatus => $"HTTP {x.Row.HttpStatus?.ToString() ?? "–"} → {y.Row.HttpStatus?.ToString() ?? "–"}",
                ComparisonChange.LatencyChanged => $"{x.Row.DurationMs:0} ms → {y.Row.DurationMs:0} ms (one sample each; not a performance result)",
                _ => null,
            };
            rows.Add(new(key, y.Name, x.Row.State, y.Row.State, change, detail));
        }
        return new(a, b, true, [], [.. rows.OrderBy(r => r.Change).ThenBy(r => r.Name, StringComparer.Ordinal)], limitations);
    }

    private static string Key(ExecutionCallRow c) => c.DefinitionRef is { } d ? d.Split("@r")[0] : $"name:{c.Name}";

    private static List<string> RegressedAssertions(ExecutionCallRow before, ExecutionCallRow after)
    {
        var x = ExplorerJson.Read<ExecutionCallRecord>(before.DocumentJson)?.Assertions ?? [];
        var y = ExplorerJson.Read<ExecutionCallRecord>(after.DocumentJson)?.Assertions ?? [];
        return [.. y.Where(n => n.Outcome == AssertionOutcome.Failed && x.Any(o => o.Id == n.Id && o.Outcome == AssertionOutcome.Passed)).Select(n => n.Id)];
    }
}

// ── Integration Journeys (Skolenærvær pack and other journey packs) ───────────────────────────────────────────────────────────────────────────────

public sealed class JourneyExplorerProvider(AppDbContext db) : IExplorerProvider
{
    public IReadOnlyList<ExplorerSource> Sources { get; } = [ExplorerSource.IntegrationJourney];

    public async Task<List<ExplorerRun>> RecentAsync(ExplorerQuery q, int window, CancellationToken ct)
    {
        var query = db.IntegrationJourneyRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.EnvironmentId)) query = query.Where(r => r.EnvironmentId == q.EnvironmentId);
        if (q.From is { } from) query = query.Where(r => r.StartedAt >= from);
        if (q.To is { } to) query = query.Where(r => r.StartedAt <= to);
        var rows = await query.OrderByDescending(r => r.StartedAt).Take(window).Select(r => r.ResultJson).ToListAsync(ct);
        return [.. rows.Select(ExplorerJson.Read<IntegrationJourneyRun>).OfType<IntegrationJourneyRun>().Select(Project)];
    }

    internal static ExplorerRun Project(IntegrationJourneyRun r) => new()
    {
        Source = ExplorerSource.IntegrationJourney, Category = ExplorerCategory.IntegrationTest, SourceRunId = r.RunId.ToString("D"), DisplayId = ExplorerStatus.Short(r.RunId.ToString("N")),
        Name = $"{r.JourneyName} · {r.ScenarioName}".Trim(' ', '·'), StartedAt = r.StartedAt, CompletedAt = r.CompletedAt,
        DurationMs = r.CompletedAt is { } c ? (c - r.StartedAt).TotalMilliseconds : null, EnvironmentId = r.EnvironmentId,
        Outcome = ExplorerStatus.Journey(r.OverallState, r.StateReason), SourceStatus = r.OverallState.ToString(), OutcomeReason = r.StateReason,
        Total = r.Steps.Count, Passed = r.Steps.Count(s => ExplorerStatus.JourneyStepVerified(s.State)), Failed = r.Steps.Count(s => ExplorerStatus.JourneyStepFailed(s.State)),
        NotAssessed = r.Steps.Count(s => !ExplorerStatus.JourneyStepVerified(s.State) && !ExplorerStatus.JourneyStepFailed(s.State)), CountLabel = "steps",
        Evidence = r.Steps.Count == 0 ? EvidenceCompleteness.SummaryOnly : r.Steps.All(s => s.Evidence.Length > 0) ? EvidenceCompleteness.Complete
            : r.Steps.Any(s => s.Evidence.Length > 0) ? EvidenceCompleteness.Partial : EvidenceCompleteness.Missing,
        SuiteRef = $"{r.PackId}/{r.JourneyId}/{r.ScenarioId}", Capabilities = [ExplorerCapability.Stages, ExplorerCapability.Timeline, ExplorerCapability.Correlation],
        OwnerHref = r.PackId.Contains("skole", StringComparison.OrdinalIgnoreCase) ? "extensions/skolenaervaer" : "integration-quality-review", OwnerLabel = "Integration Journeys",
    };

    public async Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var runId)) return null;
        var json = await db.IntegrationJourneyRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        if (json is null || ExplorerJson.Read<IntegrationJourneyRun>(json) is not { } r) return null;
        var trace = new List<ExplorerTrace>();
        foreach (var snap in r.EvidenceContext?.SourceSnapshots ?? []) trace.Add(new("Source snapshot", $"{snap.Archive} ({snap.SnapshotId:D})", "run started with", $"source-analysis?snapshot={snap.SnapshotId:D}"));
        foreach (var td in r.EvidenceContext?.TestData ?? []) trace.Add(new("Test Data version", td.Fingerprint is { } fp ? $"{td.Alias} ({fp})" : td.Alias, "resolved for the run", "test-data"));
        return new ExplorerRunDetail
        {
            Run = Project(r), ItemsLabel = "Steps",
            Items = [.. r.Steps.Select(s => new ExplorerItem(s.StepId, s.Label, "Step", s.State.ToString(), string.Join(" ", new[] { s.Verification, s.Reason }.Where(x => x.Length > 0)), s.Correlation))],
            // Recorded capture times only; a step without one is shown without a time, never given one.
            Timeline = [.. r.Steps.Select(s => new ExplorerTimelineEvent(s.CapturedAt, s.StepId, s.Label, s.State.ToString(), s.EvidenceSource.Length > 0 ? s.EvidenceSource : null,
                s.Correlation is null ? "none" : "recorded reference"))],
            Traceability = trace, Limitations = [.. r.Limitations, "Opening a run reads stored evidence only; nothing is re-sent."],
            Metrics = [new("inputs", "Inputs (redacted)", r.InputSummary.Count == 0 ? "–" : string.Join("; ", r.InputSummary))],
        };
    }
}

// ── Critical E2E Regression (browser flows; file-backed history) ───────────────────────────────────────────────────────────────────────────────

public sealed class BrowserE2EExplorerProvider(ICriticalE2EStore store) : IExplorerProvider
{
    public IReadOnlyList<ExplorerSource> Sources { get; } = [ExplorerSource.BrowserE2E];

    public Task<List<ExplorerRun>> RecentAsync(ExplorerQuery q, int window, CancellationToken ct) =>
        Task.FromResult(store.History(q.EnvironmentId ?? "", window).Where(r => (q.From is null || r.StartedAt >= q.From) && (q.To is null || r.StartedAt <= q.To)).Select(Project).ToList());

    internal static ExplorerRun Project(CriticalE2ERunResult r) => new()
    {
        Source = ExplorerSource.BrowserE2E, Category = ExplorerCategory.BrowserE2E, SourceRunId = r.RunId, DisplayId = ExplorerStatus.Short(r.RunId), Name = r.FlowName,
        StartedAt = r.StartedAt, CompletedAt = r.CompletedAt, DurationMs = r.DurationMs, EnvironmentId = r.EnvironmentId,
        Outcome = ExplorerStatus.BrowserE2E(r.Status), SourceStatus = r.Status.ToString(), OutcomeReason = r.FailureReason ?? r.OutcomeText,
        Total = r.StepResults.Count, Passed = r.StepResults.Count(s => s.Status == CriticalE2EStatus.Passed), Failed = r.StepResults.Count(s => s.Status == CriticalE2EStatus.Failed),
        NotAssessed = r.StepResults.Count(s => s.Status is not (CriticalE2EStatus.Passed or CriticalE2EStatus.Failed)), CountLabel = "steps",
        Evidence = r.StepResults.Count == 0 ? EvidenceCompleteness.SummaryOnly : r.EvidenceReferences.Count > 0 ? EvidenceCompleteness.Complete : EvidenceCompleteness.Partial,
        SuiteRef = $"{r.FlowId}@v{r.FlowVersion}", Capabilities = [ExplorerCapability.Stages, ExplorerCapability.Timeline],
        OwnerHref = "critical-e2e-regression", OwnerLabel = "Critical E2E Regression",
    };

    public Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        var r = store.History("", CriticalE2EStore.MaxHistoryPerEnvironment).FirstOrDefault(x => x.RunId == id);
        if (r is null) return Task.FromResult<ExplorerRunDetail?>(null);
        return Task.FromResult<ExplorerRunDetail?>(new ExplorerRunDetail
        {
            Run = Project(r), ItemsLabel = "Steps",
            Items = [.. r.StepResults.Select(s => new ExplorerItem(s.StepId, s.Description, s.IsFinalAssertion ? "Final assertion" : "Step", s.Status.ToString(), s.SanitizedError ?? s.SafeSummary, null, s.DurationMs))],
            Timeline = [.. r.StepResults.Select(s => new ExplorerTimelineEvent(s.StartedAt, s.StepId, s.Description, s.Status.ToString(), null, r.CorrelationId))],
            Traceability = [new("Flow definition", $"{r.FlowId}@v{r.FlowVersion}", "executed", "critical-e2e-regression"), .. r.EvidenceReferences.Select(e => new ExplorerTrace("Evidence", e, "recorded", null))],
            Metrics = [new("release", "Release evidence", r.ReleaseEvidence ? "Yes" : r.ReleaseEvidenceNote ?? "No"), new("downstream", "Downstream", r.DownstreamOutcome ?? "Not verified by this flow"),
                new("build", "Build", r.BuildId ?? "–")],
            Limitations = ["Browser evidence is what the attended Edge session recorded; opening it never drives the browser again."],
        });
    }
}

// ── Performance Test Review (k6 runs) ─────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class PerformanceExplorerProvider(AppDbContext db) : IExplorerProvider
{
    public IReadOnlyList<ExplorerSource> Sources { get; } = [ExplorerSource.Performance];

    public async Task<List<ExplorerRun>> RecentAsync(ExplorerQuery q, int window, CancellationToken ct)
    {
        var query = db.PerformanceTestRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.EnvironmentId)) query = query.Where(r => r.EnvironmentId == q.EnvironmentId);
        if (q.From is { } from) query = query.Where(r => r.CreatedAt >= from);
        if (q.To is { } to) query = query.Where(r => r.CreatedAt <= to);
        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(window).Select(r => r.DocumentJson).ToListAsync(ct);
        return [.. rows.Select(ExplorerJson.Read<PerformanceTestRun>).OfType<PerformanceTestRun>().Select(Project)];
    }

    internal static ExplorerRun Project(PerformanceTestRun r)
    {
        var (outcome, reason) = ExplorerStatus.Performance(r.State, r.Verdict, r.StateReason);
        return new()
        {
            Source = ExplorerSource.Performance, Category = ExplorerCategory.Performance, SourceRunId = r.RunId.ToString("D"), DisplayId = ExplorerStatus.Short(r.RunId.ToString("N")),
            Name = string.IsNullOrWhiteSpace(r.DefinitionSnapshot.Name) ? r.DefinitionId : r.DefinitionSnapshot.Name, StartedAt = r.StartedAt ?? r.CreatedAt, CompletedAt = r.FinishedAt,
            DurationMs = r.FinishedAt is { } f && r.StartedAt is { } s ? (f - s).TotalMilliseconds : null, EnvironmentId = r.EnvironmentId, ProjectKey = r.ProjectId,
            Outcome = outcome, SourceStatus = $"{r.State} · {r.Verdict}", OutcomeReason = reason,
            Total = r.ThresholdResults.Count, Passed = r.ThresholdResults.Count(t => t.Outcome == CheckOutcome.Pass), Failed = r.ThresholdResults.Count(t => t.Outcome == CheckOutcome.Fail),
            NotAssessed = r.ThresholdResults.Count(t => t.Outcome is not (CheckOutcome.Pass or CheckOutcome.Fail or CheckOutcome.Warning)), CountLabel = "thresholds",
            Evidence = r.Metrics is null ? EvidenceCompleteness.SummaryOnly : r.MetricsPartial ? EvidenceCompleteness.Partial : EvidenceCompleteness.Complete,
            SuiteRef = $"{r.DefinitionId}@v{r.DefinitionVersion}", Capabilities = [ExplorerCapability.Metrics],
            OwnerHref = "performance-test-review", OwnerLabel = "Performance Test Review",
        };
    }

    public async Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var runId)) return null;
        var json = await db.PerformanceTestRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.DocumentJson).FirstOrDefaultAsync(ct);
        if (json is null || ExplorerJson.Read<PerformanceTestRun>(json) is not { } r) return null;
        var m = r.Metrics;
        return new ExplorerRunDetail
        {
            Run = Project(r), ItemsLabel = "Thresholds",
            Items = [.. r.ThresholdResults.Select(t => new ExplorerItem(t.ThresholdId, $"{t.Metric} {t.Operator} {t.Expected}", $"Threshold ({t.Severity})", t.Outcome.ToString(),
                $"Measured {(t.Measured is { } v ? v.ToString("0.##") : "–")}. {t.Explanation}", null))],
            Metrics = m is null ? [] :
            [
                new("requests", "Requests", m.RequestCount.ToString()), new("errors", "Error rate", m.ErrorRatePercent is { } er ? $"{er:0.##} %" : "–"),
                new("p95", "p95 latency", m.Latency.P95Ms is { } p95 ? $"{p95:0} ms" : "–"), new("p99", "p99 latency", m.Latency.P99Ms is { } p99 ? $"{p99:0} ms" : "–"),
                new("rps", "Requests/s", m.RequestsPerSecond is { } rps ? $"{rps:0.#}" : "–"),
                new("baseline", "Baseline comparison", r.Drift?.State.ToString() ?? "No baseline"), new("source", "Metrics source", r.MetricsSource),
            ],
            Traceability = [new("Performance definition", $"{r.DefinitionId}@v{r.DefinitionVersion}", "executed", "performance-test-review"),
                .. r.BaselineIdAtRun is { } b ? new[] { new ExplorerTrace("Baseline", b, "compared with", "performance-test-review") } : []],
            Limitations = [.. r.Limitations, "A completed load test is not a pass: the threshold verdict decides; thresholds are performance results, not functional assertions."],
        };
    }
}

// ── Parquet Quality Review (file validations: assessments of data files, not executions) ────────────────────────────────────────────────────────

public sealed class ParquetExplorerProvider(AppDbContext db) : IExplorerProvider
{
    public IReadOnlyList<ExplorerSource> Sources { get; } = [ExplorerSource.ParquetValidation];

    public async Task<List<ExplorerRun>> RecentAsync(ExplorerQuery q, int window, CancellationToken ct)
    {
        var query = db.ParquetInspections.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.EnvironmentId)) query = query.Where(r => r.EnvironmentId == q.EnvironmentId);
        if (q.From is { } from) query = query.Where(r => r.SavedAt >= from);
        if (q.To is { } to) query = query.Where(r => r.SavedAt <= to);
        var rows = await query.OrderByDescending(r => r.SavedAt).Take(window).Select(r => r.DocumentJson).ToListAsync(ct);
        return [.. rows.Select(ExplorerJson.Read<ParquetInspectionEvidence>).OfType<ParquetInspectionEvidence>().Select(Project)];
    }

    internal static ExplorerRun Project(ParquetInspectionEvidence e)
    {
        var results = e.Validation?.Results ?? [];
        int pass = results.Count(r => r.Outcome == ParquetRuleOutcome.Pass), fail = results.Count(r => r.Outcome == ParquetRuleOutcome.Fail), inc = results.Count(r => r.Outcome == ParquetRuleOutcome.Inconclusive);
        return new()
        {
            Source = ExplorerSource.ParquetValidation, Category = ExplorerCategory.DataValidation, SourceRunId = e.Id.ToString("D"), DisplayId = ExplorerStatus.Short(e.Id.ToString("N")),
            Name = e.FileName, StartedAt = e.SavedAt, CompletedAt = e.SavedAt, EnvironmentId = e.EnvironmentId, ProjectKey = e.Association?.ProjectKey is { Length: > 0 } pk ? pk : null,
            Outcome = ExplorerStatus.Parquet(pass, fail, inc), SourceStatus = e.Validation is null ? "No rules validated" : $"{pass} pass · {fail} fail · {inc} inconclusive",
            OutcomeReason = e.Validation is null ? "Saved without a rule validation." : $"{e.Validation.Scope}: {e.Validation.RowsExamined} row(s) examined.",
            Total = results.Count, Passed = pass, Failed = fail, NotAssessed = inc, CountLabel = "rules", Evidence = EvidenceCompleteness.SummaryOnly,
            SuiteRef = e.Validation is { } v ? v.RuleSetRef ?? $"{v.RuleSetName} v{v.RuleSetVersion}" : null,
            ContractRefs = e.Association is { } a ? [$"{a.ContractKey}@{a.ContractVersion}"] : [], Capabilities = [ExplorerCapability.Rules],
            OwnerHref = "parquet-viewer", OwnerLabel = "Parquet Quality Review",
        };
    }

    public async Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var evidenceId)) return null;
        var json = await db.ParquetInspections.AsNoTracking().Where(r => r.Id == evidenceId).Select(r => r.DocumentJson).FirstOrDefaultAsync(ct);
        if (json is null || ExplorerJson.Read<ParquetInspectionEvidence>(json) is not { } e) return null;
        var trace = new List<ExplorerTrace> { new("File", $"{e.FileName} · SHA-256 {e.Sha256}", "validated", null), new("Schema fingerprint", e.SchemaFingerprint, "of the file", null) };
        if (e.Association is { } a) trace.Add(new("Dataset contract", $"{a.ContractKey} {a.ContractVersion} ({a.ProjectKey})", "validated against", null));
        foreach (var req in (e.Validation?.Results ?? []).SelectMany(r => r.RequirementIds).Distinct()) trace.Add(new("Requirement", req, "rule traces to", null));
        if (e.Correlation is { RunId: { } run } c) trace.Add(new("Execution run", run.ToString("D"), $"correlation {c.Level}", $"execution-results?run={run:D}"));
        return new ExplorerRunDetail
        {
            Run = Project(e), ItemsLabel = "Rules",
            Items = [.. (e.Validation?.Results ?? []).Select(r => new ExplorerItem(r.RuleId, r.RuleName ?? r.RuleId, r.Kind.ToString(), r.Outcome.ToString(), $"Expected {r.Expected}; actual {r.Actual}. {r.Explanation}", r.Column))],
            Metrics = [new("rows", "Rows in file", e.Rows.ToString()), new("examined", "Rows examined", e.Validation?.RowsExamined.ToString() ?? "–"), new("scope", "Assessment", e.Validation?.Scope ?? "–"),
                new("ruleset", "Rule set", e.Validation is { } v ? $"{v.RuleSetName} v{v.RuleSetVersion}" : "–")],
            Traceability = trace,
            Limitations = [.. e.Limitations, "A data-file validation, not an execution. It proves nothing about upstream end-to-end processing unless a correlation to a run is confirmed."],
        };
    }
}

// ── Security Classification (Kode 6/7): runtime verification vs static review ────────────────────────────────────────────────────────────────

public sealed class SecurityReviewExplorerProvider(AppDbContext db) : IExplorerProvider
{
    public IReadOnlyList<ExplorerSource> Sources { get; } = [ExplorerSource.SecurityReview];

    public async Task<List<ExplorerRun>> RecentAsync(ExplorerQuery q, int window, CancellationToken ct)
    {
        var query = db.SecurityClassificationEvidence.AsNoTracking().Where(r => r.Kind == "run");
        if (!string.IsNullOrWhiteSpace(q.EnvironmentId)) query = query.Where(r => r.EnvironmentId == q.EnvironmentId);
        if (q.From is { } from) query = query.Where(r => r.CreatedAt >= from);
        if (q.To is { } to) query = query.Where(r => r.CreatedAt <= to);
        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(window).Select(r => r.Json).ToListAsync(ct);
        return [.. rows.Select(ExplorerJson.Read<ClassificationReviewResult>).OfType<ClassificationReviewResult>().Select(Project)];
    }

    internal static ExplorerRun Project(ClassificationReviewResult r)
    {
        var isStatic = r.ReviewKind == ClassificationReviewKinds.Static;
        return new()
        {
            Source = ExplorerSource.SecurityReview, Category = isStatic ? ExplorerCategory.StaticAssessment : ExplorerCategory.SecurityRuntime,
            SourceRunId = r.RunId.ToString("D"), DisplayId = ExplorerStatus.Short(r.RunId.ToString("N")),
            Name = $"Security classification (Kode 6/7) · {(isStatic ? "static review" : "runtime verification")}", StartedAt = r.StartedAt, CompletedAt = r.CompletedAt,
            DurationMs = (r.CompletedAt - r.StartedAt).TotalMilliseconds, EnvironmentId = r.EnvironmentId,
            Outcome = ExplorerStatus.Security(r.Overall), SourceStatus = $"{r.ReviewKind ?? ClassificationReviewKinds.Combined} · {r.Overall}",
            OutcomeReason = isStatic ? "Static assessment of source/configuration evidence: no runtime claim." : $"{r.Findings.Count} finding(s).",
            Total = r.Checks.Count, Passed = r.Checks.Count(c => c.State is ClassificationState.Pass or ClassificationState.Verified),
            Failed = r.Checks.Count(c => c.State is ClassificationState.Fail or ClassificationState.IssueDetected),
            NotAssessed = r.Checks.Count(c => c.State is not (ClassificationState.Pass or ClassificationState.Verified or ClassificationState.Fail or ClassificationState.IssueDetected)),
            CountLabel = "checks", Evidence = EvidenceCompleteness.SummaryOnly,
            SuiteRef = r.StaticScopeKey, Capabilities = [ExplorerCapability.Rules], OwnerHref = "security-classification-review", OwnerLabel = "Security Classification",
        };
    }

    public async Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var runId)) return null;
        var rows = await db.SecurityClassificationEvidence.AsNoTracking().Where(r => r.Kind == "run" && r.Id == runId).Select(r => r.Json).ToListAsync(ct);
        var r = rows.Select(ExplorerJson.Read<ClassificationReviewResult>).OfType<ClassificationReviewResult>().FirstOrDefault()
            ?? (await db.SecurityClassificationEvidence.AsNoTracking().Where(x => x.Kind == "run").OrderByDescending(x => x.CreatedAt).Take(500).Select(x => x.Json).ToListAsync(ct))
                .Select(ExplorerJson.Read<ClassificationReviewResult>).OfType<ClassificationReviewResult>().FirstOrDefault(x => x.RunId == runId);
        if (r is null) return null;
        return new ExplorerRunDetail
        {
            Run = Project(r), ItemsLabel = "Checks",
            Items = [.. r.Checks.Select(c => new ExplorerItem(c.CheckId, c.Title, $"{c.TestType} check", c.State.ToString(), c.Detail, null))],
            Traceability = [.. r.SourceArchives.Select(a => new ExplorerTrace("Source archive", $"{a.FileName} · SHA-256 {a.Sha256}", "reviewed", null))],
            Limitations = [r.ReviewKind == ClassificationReviewKinds.Static ? "STATIC ASSESSMENT: source and configuration evidence only; not an execution and not a runtime verification."
                : "Runtime verification with an approved synthetic context; protected personal data is never shown."],
        };
    }
}
