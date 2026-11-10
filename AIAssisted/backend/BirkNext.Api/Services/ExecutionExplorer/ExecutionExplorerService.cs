using System.Text;
using BirkNext.ExecutionExplorer;

namespace BirkNext.Api.Services.ExecutionExplorer;

/// <summary>
/// Test Execution &amp; Evidence: merges the read-only providers into one history. Every request scans at most <see cref="Window"/> newest rows per owner
/// (already narrowed by environment and time in the owner's store), filters, sorts by start time and returns one bounded page; no request/response
/// body is read for the history. One unavailable owner is reported as such and never hides the others. Nothing here executes, re-sends or writes.
/// </summary>
public sealed class ExecutionExplorerService(IEnumerable<IExplorerProvider> providers, ILogger<ExecutionExplorerService>? logger = null)
{
    public const int Window = 1000;
    public const int MaxTake = 100;
    public const int MaxReportItems = 200;
    public const int MaxReportBytes = 1_000_000;

    private readonly List<IExplorerProvider> _providers = [.. providers];

    private async Task<(List<ExplorerRun> Rows, List<ExplorerProviderStatus> Status, bool Truncated)> ScanAsync(ExplorerQuery q, CancellationToken ct)
    {
        var rows = new List<ExplorerRun>();
        var status = new List<ExplorerProviderStatus>();
        var truncated = false;
        foreach (var p in _providers)
        {
            if (q.Sources.Count > 0 && !p.Sources.Any(q.Sources.Contains)) continue;
            try
            {
                // Sequential on purpose: providers share one scoped DbContext and the scan stays within one bounded window per owner.
                var found = await p.RecentAsync(q, Window, ct);
                truncated |= found.Count >= Window;
                rows.AddRange(found);
                foreach (var source in p.Sources) status.Add(new(source, found.Any(r => r.Source == source) ? ProviderState.Available : ProviderState.Empty, found.Count(r => r.Source == source), null));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning(e, "Execution explorer provider {Provider} is unavailable.", p.GetType().Name);
                foreach (var source in p.Sources) status.Add(new(source, ProviderState.Unavailable, 0, "This evidence owner could not be read; its results are not shown (the others are)."));
            }
        }
        return (rows, status, truncated);
    }

    internal static IEnumerable<ExplorerRun> Filter(IEnumerable<ExplorerRun> rows, ExplorerQuery q)
    {
        var term = q.Search?.Trim();
        return rows.Where(r =>
            (q.Sources.Count == 0 || q.Sources.Contains(r.Source))
            && (q.Categories.Count == 0 ? q.IncludeStatic || r.Category != ExplorerCategory.StaticAssessment : q.Categories.Contains(r.Category))
            && (q.Outcomes.Count == 0 || q.Outcomes.Contains(r.Outcome))
            && (q.Protected is null || r.Protected == q.Protected)
            && (q.Evidence is null || r.Evidence == q.Evidence)
            // A project filter never claims a run whose project was not recorded; those are listed only on request, labelled as unattributed.
            && (string.IsNullOrWhiteSpace(q.ProjectKey) || string.Equals(r.ProjectKey, q.ProjectKey, StringComparison.OrdinalIgnoreCase) || (q.IncludeUnattributed && r.ProjectKey is null))
            && (string.IsNullOrEmpty(term) || r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || r.SourceRunId.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (r.SuiteRef?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) || r.ContractRefs.Any(c => c.Contains(term, StringComparison.OrdinalIgnoreCase))));
    }

    public async Task<ExplorerPage> ListAsync(ExplorerQuery q, CancellationToken ct)
    {
        var take = Math.Clamp(q.Take, 1, MaxTake);
        var skip = Math.Max(0, q.Skip);
        var (rows, status, truncated) = await ScanAsync(q, ct);
        var filtered = Filter(rows, q).OrderByDescending(r => r.StartedAt).ThenBy(r => r.SourceRunId, StringComparer.Ordinal).ToList();
        var limitations = new List<string>();
        if (truncated) limitations.Add($"Only the newest {Window} results per evidence owner are scanned; narrow the time range to reach older ones.");
        if (status.Any(s => s.State == ProviderState.Unavailable)) limitations.Add("Some evidence owners could not be read: their results are missing from this list.");
        return new ExplorerPage([.. filtered.Skip(skip).Take(take)], filtered.Count, skip, take, status, limitations) { Truncated = truncated };
    }

    public async Task<ExplorerOverview> OverviewAsync(ExplorerQuery q, CancellationToken ct)
    {
        var (rows, status, truncated) = await ScanAsync(q, ct);
        var scope = Filter(rows, q with { Outcomes = [], Search = null, IncludeStatic = true, Categories = [] }).ToList();
        var executions = scope.Where(r => ExplorerStatus.IsExecution(r.Category)).ToList();
        var coverage = scope.GroupBy(r => r.Category).OrderBy(g => g.Key).Select(g => new ExplorerCoverageRow(g.Key, g.Count(),
            g.Count(r => r.Outcome == ExplorerOutcome.Passed), g.Count(r => r.Outcome == ExplorerOutcome.Failed), g.Count(r => r.Outcome == ExplorerOutcome.Partial),
            g.Count(r => r.Outcome == ExplorerOutcome.NotAssessed), g.Count(r => r.Outcome == ExplorerOutcome.Blocked),
            Denominator(g.Key, g.Count()), Missing(g.Key, [.. g]))).ToList();
        return new ExplorerOverview
        {
            TotalRuns = executions.Count, PassedRuns = executions.Count(r => r.Outcome == ExplorerOutcome.Passed), FailedRuns = executions.Count(r => r.Outcome == ExplorerOutcome.Failed),
            PartialRuns = executions.Count(r => r.Outcome == ExplorerOutcome.Partial), NotAssessedRuns = executions.Count(r => r.Outcome == ExplorerOutcome.NotAssessed),
            BlockedRuns = executions.Count(r => r.Outcome == ExplorerOutcome.Blocked), RunningRuns = executions.Count(r => r.Outcome == ExplorerOutcome.Running),
            // Only what an execution counted as executed (passed + failed); planned or skipped calls are not "executed".
            TestsExecuted = executions.Sum(r => r.Passed + r.Failed),
            Latest = executions.OrderByDescending(r => r.StartedAt).FirstOrDefault(),
            StaticAssessments = scope.Count(r => !ExplorerStatus.IsExecution(r.Category)),
            Coverage = coverage,
            Attention = [.. scope.Where(r => ExplorerStatus.NeedsAttention(r.Outcome)).OrderByDescending(r => r.StartedAt).Take(8)],
            Providers = status,
            Projects = [.. rows.Select(r => r.ProjectKey).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            Environments = [.. rows.Select(r => r.EnvironmentId).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Truncated = truncated,
            Limitations = truncated ? [$"Counts cover the newest {Window} results per evidence owner."] : [],
        };
    }

    private static string Denominator(ExplorerCategory c, int n) => c switch
    {
        ExplorerCategory.FunctionalTest => $"of {n} contract test run(s)",
        ExplorerCategory.IntegrationTest => $"of {n} integration run(s) (suite, composite and journey runs; a subflow is never a full end-to-end result)",
        ExplorerCategory.BrowserE2E => $"of {n} browser flow run(s)",
        ExplorerCategory.Performance => $"of {n} load test run(s) — threshold verdicts, not functional results",
        ExplorerCategory.DataValidation => $"of {n} saved data-file validation(s) — assessments, not executions",
        ExplorerCategory.SecurityRuntime => $"of {n} runtime security verification(s)",
        _ => $"of {n} static assessment(s) — not executions",
    };

    private static string? Missing(ExplorerCategory c, List<ExplorerRun> runs)
    {
        var notAssessed = runs.Count(r => r.Outcome == ExplorerOutcome.NotAssessed);
        var noEvidence = runs.Count(r => r.Evidence is EvidenceCompleteness.Missing or EvidenceCompleteness.Partial);
        var parts = new List<string>();
        if (notAssessed > 0) parts.Add($"{notAssessed} not assessed");
        if (noEvidence > 0) parts.Add($"{noEvidence} with incomplete evidence");
        if (c == ExplorerCategory.IntegrationTest && runs.Count(r => r.Outcome == ExplorerOutcome.Partial) is var partial and > 0) parts.Add($"{partial} partial (subflow or unobserved stage)");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public async Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        var provider = _providers.FirstOrDefault(p => p.Sources.Contains(source));
        return provider is null ? null : await provider.DetailAsync(source, id, ct);
    }

    public async Task<ExplorerComparison> CompareAsync(ExplorerSource source, string baseline, string current, CancellationToken ct)
    {
        if (_providers.OfType<ExecutionEvidenceExplorerProvider>().FirstOrDefault() is { } exec && exec.Sources.Contains(source)
            && Guid.TryParse(baseline, out var a) && Guid.TryParse(current, out var b))
            return await exec.CompareAsync(a, b, ct);
        return new ExplorerComparison(null, null, false, [$"Run comparison is supported for Execution Evidence runs. {source} keeps its own comparison (e.g. Performance Test Review baselines)."], [], []);
    }

    /// <summary>A sanitized Markdown report of one stored run: summary, items, timeline, traceability, limitations. Never bodies, headers or values;
    /// bounded in items and bytes; generated on demand and never stored.</summary>
    public async Task<ExplorerReport?> ReportAsync(ExplorerSource source, string id, CancellationToken ct)
    {
        var d = await DetailAsync(source, id, ct);
        if (d is null) return null;
        var r = d.Run;
        var md = new StringBuilder();
        md.AppendLine($"# Execution report — {Clean(r.Name)}").AppendLine();
        md.AppendLine($"Generated from stored evidence on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC. Nothing was re-executed.").AppendLine();
        md.AppendLine("## 1. Result").AppendLine();
        md.AppendLine($"| Outcome | Owner status | {Clean(r.CountLabel)} | Passed | Failed | Not assessed | Evidence |").AppendLine("|---|---|---|---|---|---|---|");
        md.AppendLine($"| **{Upper(r.Outcome)}** | {Clean(r.SourceStatus)} | {r.Total} | {r.Passed} | {r.Failed} | {r.NotAssessed} | {r.Evidence} |").AppendLine();
        if (r.OutcomeReason is { Length: > 0 } why) md.AppendLine(Clean(why)).AppendLine();
        md.AppendLine("## 2. Scope").AppendLine();
        md.AppendLine($"- Kind: {r.Source} ({(ExplorerStatus.IsExecution(r.Category) ? "execution" : "assessment, not an execution")}, {r.Category})");
        md.AppendLine($"- Run: `{r.SourceRunId}` · started {r.StartedAt:yyyy-MM-dd HH:mm:ss} UTC{(r.DurationMs is { } ms ? $" · {ms / 1000:0.#} s" : "")}");
        md.AppendLine($"- Environment: `{Clean(r.EnvironmentId)}` · project: {(r.ProjectKey is { } pk ? $"`{Clean(pk)}`" : "not recorded")}");
        if (r.SuiteRef is { } suite) md.AppendLine($"- Definition: `{Clean(suite)}`");
        foreach (var c in r.ContractRefs.Take(20)) md.AppendLine($"- Contract: `{Clean(c)}`");
        md.AppendLine();
        if (d.Items.Count > 0)
        {
            md.AppendLine($"## 3. {Clean(d.ItemsLabel)}").AppendLine().AppendLine("| # | Name | Kind | Outcome | Detail |").AppendLine("|---|---|---|---|---|");
            foreach (var (item, i) in d.Items.Take(MaxReportItems).Select((x, i) => (x, i)))
                md.AppendLine($"| {i + 1} | {Clean(item.Name)} | {Clean(item.Kind)} | {Clean(item.Outcome)} | {Clean(item.Detail ?? "")} |");
            if (d.Items.Count > MaxReportItems) md.AppendLine().AppendLine($"{d.Items.Count - MaxReportItems} more not listed.");
            md.AppendLine();
        }
        if (d.Metrics.Count > 0)
        {
            md.AppendLine("## 4. Metrics").AppendLine();
            foreach (var m in d.Metrics) md.AppendLine($"- {Clean(m.Label)}: {Clean(m.Value)}");
            md.AppendLine();
        }
        if (d.Timeline.Count > 0)
        {
            md.AppendLine("## 5. Timeline (recorded events only)").AppendLine().AppendLine("| Time | Stage | Action | Outcome |").AppendLine("|---|---|---|---|");
            foreach (var t in d.Timeline.Take(MaxReportItems)) md.AppendLine($"| {(t.At is { } at ? at.ToString("HH:mm:ss.fff") : "not observed")} | {Clean(t.Stage)} | {Clean(t.Action)} | {Clean(t.Outcome)} |");
            md.AppendLine();
        }
        if (d.Traceability.Count > 0)
        {
            md.AppendLine("## 6. Traceability (recorded references)").AppendLine();
            foreach (var t in d.Traceability.Take(MaxReportItems)) md.AppendLine($"- {Clean(t.Kind)}: `{Clean(t.Reference)}` — {Clean(t.Relationship)}");
            md.AppendLine();
        }
        md.AppendLine("## 7. Limitations").AppendLine();
        foreach (var l in d.Limitations) md.AppendLine($"- {Clean(l)}");
        md.AppendLine("- Request and response bodies, headers and resolved values are not part of this report; the stored, redacted evidence stays in BirkNext.");
        var text = md.ToString();
        if (Encoding.UTF8.GetByteCount(text) > MaxReportBytes) text = text[..(MaxReportBytes / 2)] + "\n\n(Report truncated at the size limit.)\n";
        var name = $"execution-report-{r.Source.ToString().ToLowerInvariant()}-{r.DisplayId}.md";
        return new ExplorerReport(name, text, Encoding.UTF8.GetByteCount(text));
    }

    private static string Upper(ExplorerOutcome o) => ExecutionEvidenceExplorerProvider.Upper(o.ToString());

    /// <summary>Report cells: single line, no table breaks, no markup injection, bounded.</summary>
    internal static string Clean(string s)
    {
        var one = s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("|", "/", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal)
            .Replace("<", "‹", StringComparison.Ordinal).Replace(">", "›", StringComparison.Ordinal);
        return one.Length > 400 ? one[..400] + "…" : one;
    }
}
