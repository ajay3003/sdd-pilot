using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.DataQuality;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.DataQuality;

public sealed class DataQualityRunConflictException(string message) : Exception(message);

/// <summary>
/// The diagnostics store of Data Quality runs (2026-11-27): one JSON file per finished run under App_Data/diagnostics/data-quality, the newest
/// <see cref="MaxRuns"/> kept. It is SEPARATE from the business domain (no database table, no migration) and holds only run metadata and results.
/// </summary>
public sealed class DataQualityRunStore
{
    public const int MaxRuns = 20;
    public const long MaxReportBytes = 25 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };
    private readonly string folder;
    private readonly object gate = new();

    public DataQualityRunStore(IHostEnvironment environment) : this(Path.Combine(AppDataLocation.Root(environment), "diagnostics", "data-quality")) { }
    public DataQualityRunStore(string folder) { this.folder = folder; }

    public void Save(DataQualityRun run)
    {
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            var json = JsonSerializer.Serialize(run, Json);
            if (Encoding.UTF8.GetByteCount(json) > MaxReportBytes && run.Report is { } r)
                json = JsonSerializer.Serialize(run with
                {
                    Report = r with
                    {
                        Checks = [.. r.Checks.Select(c => c with { Records = [.. c.Records.Take(20)], RecordsOmitted = c.RecordsOmitted + Math.Max(0, c.Records.Count - 20) })],
                        Identities = [.. r.Identities.Take(2000)], Skipped = [.. r.Skipped, "Rapporten var større enn 25 MB; detaljlistene er avkortet."],
                    },
                }, Json);
            var path = Path.Combine(folder, Safe(run.RunId) + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
            // Newest first; run ids start with their start time, so they break ties between runs saved within the same timer tick.
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(MaxRuns)) old.Delete();
        }
    }

    public DataQualityRun? Load(string runId)
    {
        var path = Path.Combine(folder, Safe(runId) + ".json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<DataQualityRun>(File.ReadAllText(path), Json) : null; }
        catch (JsonException) { return null; }
    }

    public List<DataQualityRun> Recent()
    {
        if (!Directory.Exists(folder)) return [];
        var list = new List<DataQualityRun>();
        foreach (var f in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Take(MaxRuns))
            try { if (JsonSerializer.Deserialize<DataQualityRun>(File.ReadAllText(f.FullName), Json) is { } run) list.Add(run); }
            catch (JsonException) { }
        return list;
    }

    /// <summary>Local Data Reset removes the diagnostics with the rest of the local data.</summary>
    public void ClearAll() { lock (gate) { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } }

    private static string Safe(string runId) => new([.. runId.Where(c => char.IsLetterOrDigit(c) || c == '-')]);
}

/// <summary>
/// Runs at most one Data Quality audit at a time in the background (user-initiated only), with progress and cancellation; finished runs go to
/// the <see cref="DataQualityRunStore"/>. The pattern of <c>ApiFuzzingRunCoordinator</c>.
/// </summary>
public sealed class DataQualityRunCoordinator(IServiceScopeFactory scopes, DataQualityRunStore store, ILogger<DataQualityRunCoordinator> logger)
{
    private sealed class Live
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public DataQualityRun Run { get; set; } = new();
        public Task? Execution { get; set; }
    }

    private readonly ConcurrentDictionary<string, Live> live = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public DataQualityRun Start(DataQualityRunRequest request, string startedBy)
    {
        Live run;
        lock (gate)
        {
            if (live.Values.Any(r => r.Run.State == DataQualityRunState.Running))
                throw new DataQualityRunConflictException("En datakvalitetskontroll kjører allerede. Vent til den er ferdig, eller avbryt den.");
            var runId = $"dq-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..28];
            var categories = request.Categories.Distinct().ToList();
            run = new Live
            {
                Cancellation = new CancellationTokenSource(),
                Run = new DataQualityRun { RunId = runId, ProjectKey = request.ProjectKey, Categories = categories, State = DataQualityRunState.Running, StartedAt = DateTimeOffset.UtcNow,
                    StartedBy = startedBy, Progress = new DataQualityProgress { Phase = "Starter" } },
            };
            live[runId] = run;
            foreach (var old in live.Where(r => r.Value.Run.State != DataQualityRunState.Running).OrderByDescending(r => r.Value.Run.StartedAt).Skip(5).Select(r => r.Key).ToList())
                if (live.TryRemove(old, out var removed)) removed.Cancellation.Dispose();
        }
        var id = run.Run.RunId;
        run.Execution = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<DataQualityEngine>();
                var report = await engine.RunAsync(id, request.ProjectKey, run.Run.Categories, p => run.Run = run.Run with { Progress = p }, run.Cancellation.Token);
                run.Run = run.Run with { State = DataQualityRunState.Completed, FinishedAt = DateTimeOffset.UtcNow, Report = report,
                    Progress = run.Run.Progress with { Phase = "Ferdig", ChecksCompleted = report.Checks.Count, ChecksFailed = report.Count(DataQualityStatus.Fail) } };
            }
            catch (OperationCanceledException)
            {
                run.Run = run.Run with { State = DataQualityRunState.Cancelled, FinishedAt = DateTimeOffset.UtcNow, Error = "Avbrutt av brukeren. Ingen resultater er regnet som bestått." };
            }
            catch (Exception ex)
            {
                logger.LogError("Data Quality run {RunId} failed with {ExceptionType}.", id, ex.GetType().Name);
                run.Run = run.Run with { State = DataQualityRunState.Failed, FinishedAt = DateTimeOffset.UtcNow, Error = $"Kontrollen kunne ikke fullføres ({ex.GetType().Name})." };
            }
            try { store.Save(run.Run); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("Data Quality run {RunId} could not be stored ({ExceptionType}).", id, ex.GetType().Name); }
        });
        return run.Run;
    }

    public DataQualityRun? Get(string runId) => live.TryGetValue(runId, out var run) ? run.Run : store.Load(runId);

    public DataQualityRun? Cancel(string runId)
    {
        if (!live.TryGetValue(runId, out var run)) return null;
        if (run.Run.State == DataQualityRunState.Running) run.Cancellation.Cancel();
        return run.Run;
    }

    /// <summary>The browser stage's production values, kept with the stored report (diagnostics store only).</summary>
    public DataQualityRun? AttachUiValues(string runId, List<DataQualityUiValue> values)
    {
        var run = Get(runId);
        if (run?.Report is not { } report || run.State != DataQualityRunState.Completed) return null;
        var known = report.Kpis.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
        var updated = run with { Report = report with { UiValues = [.. values.Where(v => known.Contains(v.Id)).DistinctBy(v => v.Id)] } };
        if (live.TryGetValue(runId, out var l)) l.Run = updated;
        store.Save(updated);
        return updated;
    }

    public List<DataQualityRunSummary> Recent()
    {
        var runs = live.Values.Select(l => l.Run).Concat(store.Recent()).DistinctBy(r => r.RunId).OrderByDescending(r => r.StartedAt).Take(DataQualityRunStore.MaxRuns);
        return [.. runs.Select(r => new DataQualityRunSummary(r.RunId, r.ProjectKey, r.State, r.StartedAt, r.FinishedAt,
            r.Report?.Count(DataQualityStatus.Pass) ?? 0, r.Report?.Count(DataQualityStatus.Fail) ?? 0, r.Report?.Count(DataQualityStatus.Warning) ?? 0,
            r.Report?.Count(DataQualityStatus.Inconclusive) ?? 0, r.Report?.Count(DataQualityStatus.NotVerified) ?? 0, r.Report?.Baseline.Label ?? ""))];
    }

    internal Task WaitAsync(string runId) => live.TryGetValue(runId, out var run) && run.Execution is { } t ? t : Task.CompletedTask;
}

/// <summary>The Markdown form of a report (the same content as the JSON form).</summary>
public static class DataQualityMarkdown
{
    public static string Write(DataQualityReport r)
    {
        var sb = new StringBuilder();
        string Status(DataQualityStatus s) => s switch
        {
            DataQualityStatus.Pass => "PASS", DataQualityStatus.Fail => "FAIL", DataQualityStatus.Warning => "WARNING", DataQualityStatus.Inconclusive => "INCONCLUSIVE", _ => "NOT VERIFIED",
        };
        static string Cell(string? s) => (s ?? "—").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        sb.AppendLine($"# BirkNext Data Quality — {r.ProjectKey}").AppendLine();
        sb.AppendLine($"- Kjøring: `{r.RunId}` · {r.StartedAt:yyyy-MM-dd HH:mm:ss} → {r.FinishedAt:HH:mm:ss} UTC ({(r.FinishedAt - r.StartedAt).TotalSeconds:0.0} s)");
        sb.AppendLine($"- Applikasjon: {r.Baseline.Application ?? "ikke registrert"}");
        sb.AppendLine($"- Kildegrunnlag: {r.Baseline.Label}");
        sb.AppendLine($"- Øyeblikksbilde: `{r.SnapshotMarker[..Math.Min(16, r.SnapshotMarker.Length)]}` · {(r.Consistent ? "konsistent" : "**INCONCLUSIVE: dataene ble endret under kjøringen**")}");
        sb.AppendLine($"- Kategorier: {string.Join(", ", r.Categories)}");
        sb.AppendLine($"- Resultat: {r.Count(DataQualityStatus.Pass)} PASS · {r.Count(DataQualityStatus.Fail)} FAIL · {r.Count(DataQualityStatus.Warning)} WARNING · {r.Count(DataQualityStatus.Inconclusive)} INCONCLUSIVE · {r.Count(DataQualityStatus.NotVerified)} NOT VERIFIED").AppendLine();
        sb.AppendLine("## Kontroller").AppendLine().AppendLine("| Status | Kontroll | Steg | Omfang | Forventet | Faktisk | Diff | Årsak |").AppendLine("|---|---|---|---|---:|---:|---:|---|");
        foreach (var c in r.Checks) sb.AppendLine($"| {Status(c.Status)} | {Cell(c.Metric)} | {c.Stage} | {Cell(c.Scope)} | {Cell(c.Expected)} | {Cell(c.Actual)} | {Cell(c.Difference)} | {c.Cause} |");
        sb.AppendLine();
        foreach (var c in r.Checks.Where(c => c.Status != DataQualityStatus.Pass || c.Records.Count > 0))
        {
            sb.AppendLine($"### {Status(c.Status)} — {c.Metric}").AppendLine().AppendLine(c.Explanation).AppendLine().AppendLine($"*Telleregel:* {c.Contract}").AppendLine();
            foreach (var rec in c.Records) sb.AppendLine($"- {rec.Kind}: `{rec.Key}`{(rec.Path is { } p && p != rec.Key ? $" ({p})" : "")}{(rec.Text is { } t ? $" — {t}" : "")}");
            if (c.RecordsOmitted > 0) sb.AppendLine($"- … {c.RecordsOmitted} flere i JSON-rapporten er utelatt her (grense {DataQualityEngine.MaxRecordsPerCheck}).");
            sb.AppendLine();
        }
        if (r.Kpis.Count > 0)
        {
            sb.AppendLine("## Dashbord-tall (API → UI)").AppendLine().AppendLine("| Tall | Forventet (referanse) | Vist i dashbordet | Status |").AppendLine("|---|---:|---:|---|");
            foreach (var k in r.Kpis)
            {
                var ui = r.UiValues.FirstOrDefault(v => v.Id == k.Id);
                var st = ui is null ? "NOT VERIFIED (nettleseren har ikke sendt verdien)" : ui.Actual == k.Expected ? "PASS" : "FAIL";
                sb.AppendLine($"| {Cell(k.Metric)} | {k.Expected?.ToString() ?? "—"} | {ui?.Actual?.ToString() ?? "—"} | {st} |");
            }
            sb.AppendLine();
        }
        if (r.Identities.Count > 0)
        {
            sb.AppendLine("## Kravidentiteter (tørrkjøring — ingenting er endret)").AppendLine();
            sb.AppendLine("| Status | Krav | Spesifikasjon | v1 | v2 (foreslått) | Dok. med samme v1 | Testtilfeller | Planposter | Vurderinger | Godkjenninger | Gjennomføringer |").AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---:|---:|");
            foreach (var i in r.Identities.Where(i => i.Resolution != IdentityResolution.NotAffected))
                sb.AppendLine($"| {i.Resolution} | {Cell(i.RequirementId)} | {Cell(i.SpecificationPath)} | `{i.V1}` | `{i.V2}` | {i.DocumentsWithSameV1} | {i.TestCases.Count} | {i.PlanItems.Count} | {i.ReadinessDecisions.Count} | {i.Approvals.Count} | {i.Executions.Count} |");
            sb.AppendLine().AppendLine($"{r.Identities.Count(i => i.Resolution == IdentityResolution.NotAffected)} krav er ikke berørt (bare ett dokument har v1-identiteten).").AppendLine();
        }
        if (r.Skipped.Count > 0) { sb.AppendLine("## Hoppet over / ikke kontrollert").AppendLine(); foreach (var s in r.Skipped) sb.AppendLine($"- {s}"); sb.AppendLine(); }
        if (r.FollowUps.Count > 0) { sb.AppendLine("## Anbefalt oppfølging").AppendLine(); foreach (var s in r.FollowUps) sb.AppendLine($"- {s}"); }
        return sb.ToString();
    }
}
