using BirkNext.ExecutionEvidence;
using BirkNext.ExecutionExplorer;
using BirkNext.Web.Services;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Pages;

/// <summary>
/// Test Execution &amp; Evidence — the unified, read-only explorer (Overview · Runs &amp; Results · Evidence &amp; Traceability · Storage &amp; Retention).
/// Results come from the backend projection over the evidence owners; this page never computes an outcome, never executes and never changes a
/// result (protection, deletion and retention remain the Execution Evidence operations under Storage &amp; Retention).
/// </summary>
public partial class ExecutionResults
{
    [SupplyParameterFromQuery(Name = "area")] public string? AreaQuery { get; set; }
    [SupplyParameterFromQuery(Name = "project")] public string? ProjectQuery { get; set; }
    [SupplyParameterFromQuery(Name = "env")] public string? EnvQuery { get; set; }
    [SupplyParameterFromQuery(Name = "range")] public string? RangeQuery { get; set; }
    [SupplyParameterFromQuery(Name = "unattributed")] public bool UnattributedQuery { get; set; }
    [SupplyParameterFromQuery(Name = "static")] public bool StaticQuery { get; set; }
    [SupplyParameterFromQuery(Name = "rq")] public string? RunSearchQuery { get; set; }
    [SupplyParameterFromQuery(Name = "rsrc")] public string? RunSourcesQuery { get; set; }
    [SupplyParameterFromQuery(Name = "rout")] public string? RunOutcomesQuery { get; set; }
    [SupplyParameterFromQuery(Name = "rprot")] public bool RunProtectedQuery { get; set; }
    [SupplyParameterFromQuery(Name = "rev")] public string? RunEvidenceQuery { get; set; }
    [SupplyParameterFromQuery(Name = "rpage")] public int? RunPageQuery { get; set; }
    [SupplyParameterFromQuery(Name = "source")] public string? SourceQueryRaw { get; set; }
    [SupplyParameterFromQuery(Name = "id")] public string? ExplorerIdQuery { get; set; }
    [SupplyParameterFromQuery(Name = "dtab")] public string? DetailTabQuery { get; set; }
    [SupplyParameterFromQuery(Name = "cmp")] public string? CompareQuery { get; set; }

    public const int RunPageSize = 20;

    internal static readonly (string Key, string Label, string Hint)[] Areas =
    [
        ("overview", "Overview", "what ran and how it ended"),
        ("runs", "Runs & Results", "history, filters, details"),
        ("evidence", "Evidence & Traceability", "coverage and references"),
        ("storage", "Storage & Retention", "protect, export, delete"),
    ];

    private IExecutionExplorerApiService? Explorer => Services.GetService(typeof(IExecutionExplorerApiService)) as IExecutionExplorerApiService;

    private ExplorerOverview? _overview;
    private ExplorerPage? _page;
    private ExplorerRunDetail? _unified;
    private ExplorerComparison? _compareResult;
    private IReadOnlyList<ExplorerRun>? _compareCandidates;
    private ExecutionStorageUsage? _overviewUsage;
    private string? _explorerError;

    private ExplorerSource? ExplorerSourceQuery => Enum.TryParse<ExplorerSource>(SourceQueryRaw, true, out var s) ? s : null;

    /// <summary>The area: explicit, else the legacy history links (?hq=…) open Storage &amp; Retention where run management lives, else Overview.</summary>
    internal string Area => Areas.Any(a => a.Key == AreaQuery) ? AreaQuery!
        : !string.IsNullOrEmpty(HistorySearch) || !string.IsNullOrEmpty(HistoryState) || !string.IsNullOrEmpty(HistoryFrom) || !string.IsNullOrEmpty(HistoryTo) || HistoryProtected ? "storage"
        : "overview";

    private string DetailTab => DetailTabQuery is "items" or "timeline" or "trace" or "technical" ? DetailTabQuery : "summary";

    private string ScopeNote =>
        (string.IsNullOrEmpty(ProjectQuery) ? "All projects in this local workspace" : $"Project {ProjectQuery}{(UnattributedQuery ? " (and runs without a recorded project)" : " — runs without a recorded project are not attributed to it")}")
        + " · " + (string.IsNullOrEmpty(EnvQuery) ? "every Target Environment" : $"Target Environment {EnvQuery}")
        + " · " + (RangeQuery switch { "1" => "last 24 hours", "7" => "last 7 days", "30" => "last 30 days", "90" => "last 90 days", _ => "any time" })
        + ". The environment a run executed against is recorded with the run; it is not the environment the source came from.";

    private DateTimeOffset? RangeFrom => int.TryParse(RangeQuery, out var days) && days > 0 ? DateTimeOffset.UtcNow.AddDays(-days) : null;

    private static List<T> Csv<T>(string? csv) where T : struct, Enum =>
        [.. (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => Enum.TryParse<T>(v, true, out var e) ? (T?)e : null).OfType<T>().Distinct()];

    private List<ExplorerSource> RunSources => Csv<ExplorerSource>(RunSourcesQuery);
    private List<ExplorerOutcome> RunOutcomes => Csv<ExplorerOutcome>(RunOutcomesQuery);
    private int RunPage => Math.Max(1, RunPageQuery ?? 1);

    private ExplorerFilter Scope => new()
    {
        Project = ProjectQuery, EnvironmentId = EnvQuery, Unattributed = UnattributedQuery, From = RangeFrom is { } f ? DateOnly.FromDateTime(f.LocalDateTime) : null,
    };

    private async Task LoadExplorerAsync()
    {
        _explorerError = null; _unified = null; _compareResult = null; _compareCandidates = null; _page = null;
        if (Explorer is not { } ex) { _explorerError = "Test Execution & Evidence is not available in this host."; return; }
        if (ExplorerSourceQuery is { } source && ExplorerIdQuery is { Length: > 0 } id)
        {
            (_unified, _explorerError) = await ex.RunAsync(source, id);
            if (_unified is { } d && d.Run.Capabilities.Contains(ExplorerCapability.Compare))
            {
                // Candidates: other runs of the same owner and definition (never unrelated runs).
                var (page, _) = await ex.RunsAsync(new ExplorerFilter { Sources = [source], Search = d.Run.SuiteRef ?? d.Run.ContractRefs.FirstOrDefault() ?? d.Run.Name, Take = 15 });
                _compareCandidates = page?.Runs.Where(r => r.Source == d.Run.Source && r.SourceRunId != d.Run.SourceRunId && r.EnvironmentId == d.Run.EnvironmentId).Take(10).ToList();
                if (CompareQuery is { Length: > 0 } baseline) (_compareResult, _explorerError) = await ex.CompareAsync(source, baseline, id);
            }
            return;
        }
        (_overview, var overviewError) = await ex.OverviewAsync(Scope);
        if (Area == "overview" && Api is { } api) (_overviewUsage, _) = await api.StorageAsync(EnvQuery);
        if (Area is "runs" or "evidence")
        {
            var filter = Area == "runs"
                ? Scope with
                {
                    Search = RunSearchQuery, Sources = RunSources, Outcomes = RunOutcomes, ProtectedOnly = RunProtectedQuery, IncludeStatic = StaticQuery,
                    Evidence = Enum.TryParse<EvidenceCompleteness>(RunEvidenceQuery, out var ev) ? ev : null, Skip = (RunPage - 1) * RunPageSize, Take = RunPageSize,
                }
                : Scope with { IncludeStatic = true, Take = 10 };
            (_page, var pageError) = await ex.RunsAsync(filter);
            _explorerError = pageError;
        }
        _explorerError ??= overviewError;
    }

    private string Query(string? area = null, string? project = null, string? env = null, string? range = null, bool? unattributed = null, bool? includeStatic = null,
        string? search = null, string? sources = null, string? outcomes = null, bool? prot = null, string? evidence = null, int? page = null)
    {
        var q = new List<string>();
        void Add(string key, string? value) { if (!string.IsNullOrEmpty(value)) q.Add($"{key}={Uri.EscapeDataString(value)}"); }
        Add("area", area ?? Area);
        Add("project", project ?? ProjectQuery); Add("env", env ?? EnvQuery); Add("range", range ?? RangeQuery);
        if ((unattributed ?? UnattributedQuery) && !string.IsNullOrEmpty(project ?? ProjectQuery)) q.Add("unattributed=true");
        if ((area ?? Area) == "runs")
        {
            if (includeStatic ?? StaticQuery) q.Add("static=true");
            Add("rq", search ?? RunSearchQuery); Add("rsrc", sources ?? RunSourcesQuery); Add("rout", outcomes ?? RunOutcomesQuery);
            if (prot ?? RunProtectedQuery) q.Add("rprot=true");
            Add("rev", evidence ?? RunEvidenceQuery);
            if ((page ?? RunPageQuery) is { } p and > 1) q.Add($"rpage={p}");
        }
        return "execution-results?" + string.Join('&', q);
    }

    private string AreaHref(string key) => Query(area: key);

    /// <summary>A scope change resets paging (filters stay); the area is kept.</summary>
    private void GoArea(string? project = null, string? env = null, string? range = null, bool? unattributed = null) =>
        Nav.NavigateTo(Query(project: project, env: env, range: range, unattributed: unattributed, page: 1));

    private void GoRuns(string? search = null, string? sources = null, string? outcomes = null, bool? prot = null, string? evidence = null, bool? includeStatic = null, int? page = null) =>
        Nav.NavigateTo(Query(area: "runs", search: search, sources: sources, outcomes: outcomes, prot: prot, evidence: evidence, includeStatic: includeStatic,
            page: page ?? 1));

    private string ToggleCsv<T>(List<T> current, T value) where T : struct, Enum =>
        string.Join(',', current.Contains(value) ? current.Where(v => !v.Equals(value)) : current.Append(value));

    private string DetailHref(ExplorerRun r, string? tab = null, string? compare = null)
    {
        var q = $"execution-results?source={r.Source}&id={Uri.EscapeDataString(r.SourceRunId)}";
        if (tab is { Length: > 0 } && tab != "summary") q += $"&dtab={tab}";
        if (compare is { Length: > 0 }) q += $"&cmp={Uri.EscapeDataString(compare)}";
        return q;
    }

    private string UnifiedDetailHref(string tab) => _unified is { } d ? DetailHref(d.Run, tab, CompareQuery) : AreaHref("runs");

    internal static string OutcomeLabel(ExplorerOutcome o) => o switch
    {
        ExplorerOutcome.NotAssessed => "Not assessed",
        ExplorerOutcome.TimedOut => "Timed out",
        ExplorerOutcome.UnknownOutcome => "Unknown outcome",
        ExplorerOutcome.NotSupported => "Not supported",
        ExplorerOutcome.Partial => "Partial",
        _ => o.ToString(),
    };

    /// <summary>A glyph beside every outcome so it never depends on colour alone.</summary>
    internal static string OutcomeGlyph(ExplorerOutcome o) => o switch
    {
        ExplorerOutcome.Passed => "✓",
        ExplorerOutcome.Failed or ExplorerOutcome.TimedOut or ExplorerOutcome.Interrupted => "✕",
        ExplorerOutcome.Partial => "◐",
        ExplorerOutcome.Blocked => "⊘",
        ExplorerOutcome.UnknownOutcome => "?",
        ExplorerOutcome.Running => "…",
        _ => "–",
    };

    internal static string OutcomeTone(ExplorerOutcome o) => o switch
    {
        ExplorerOutcome.Passed => "ok",
        ExplorerOutcome.Failed or ExplorerOutcome.TimedOut or ExplorerOutcome.Interrupted or ExplorerOutcome.UnknownOutcome => "bad",
        ExplorerOutcome.Partial or ExplorerOutcome.Blocked => "warn",
        ExplorerOutcome.Running => "info",
        _ => "neutral",
    };

    internal static string SourceLabel(ExplorerSource s) => s switch
    {
        ExplorerSource.ContractTests => "Contract tests",
        ExplorerSource.ContractChain => "Contract chain",
        ExplorerSource.IntegrationSuite => "Integration suite",
        ExplorerSource.CompositeE2E => "Composite E2E",
        ExplorerSource.IntegrationJourney => "Integration journey",
        ExplorerSource.BrowserE2E => "Browser E2E",
        ExplorerSource.Performance => "Performance",
        ExplorerSource.ParquetValidation => "Parquet validation",
        _ => "Security review",
    };

    internal static string CategoryLabel(ExplorerCategory c) => c switch
    {
        ExplorerCategory.FunctionalTest => "Functional tests",
        ExplorerCategory.IntegrationTest => "Integration tests",
        ExplorerCategory.BrowserE2E => "Browser E2E",
        ExplorerCategory.Performance => "Performance",
        ExplorerCategory.DataValidation => "Data-file validation",
        ExplorerCategory.SecurityRuntime => "Security (runtime)",
        _ => "Static assessment",
    };

    internal static string Duration(double? ms) => ms switch
    {
        null => "–",
        < 1000 => $"{ms:0} ms",
        < 120_000 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ms / 1000:0.0} s"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ms / 60000:0.0} min"),
    };

    internal static string Ago(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
