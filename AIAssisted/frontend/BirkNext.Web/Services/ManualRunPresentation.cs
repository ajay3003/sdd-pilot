using System.Globalization;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// «Testgjennomføring i BirkNext» (restored 2026-11-25 as an optional capability; Azure DevOps Test Plans stays the default): labels, counts and
/// stakeholder reports of manual runs, in plain Norwegian. Results only — a prepared case that was never run is not a run, and a stopped run never
/// counts as a result. (These helpers were removed with the execution page on 2026-11-19 and are restored here, apart from the workspace's own
/// presentation.)
/// </summary>
public static class ManualRunPresentation
{
    public const string ExecuteRoute = "manual-testing/run";

    public static string RunTone(ManualRunState state, ManualRunOutcome outcome) => state switch
    {
        ManualRunState.InProgress => "info",
        ManualRunState.Abandoned => "muted",
        _ => outcome switch { ManualRunOutcome.Passed => "ok", ManualRunOutcome.Failed => "bad", _ => "warn" },
    };

    public static string RunLabel(ManualRunState state, ManualRunOutcome outcome) => state switch
    {
        ManualRunState.InProgress => "Pågår",
        ManualRunState.Abandoned => "Avbrutt",
        _ => OutcomeLabel(outcome),
    };

    public static string OutcomeLabel(ManualRunOutcome outcome) => outcome switch
    {
        ManualRunOutcome.Passed => "Bestått", ManualRunOutcome.Failed => "Feilet", ManualRunOutcome.Blocked => "Blokkert", ManualRunOutcome.NotRun => "Ikke kjørt",
        _ => "Ikke avgjort",
    };

    /// <summary>One step's result in plain Norwegian.</summary>
    public static string StepLabel(ManualStepOutcome? outcome) => outcome switch
    {
        ManualStepOutcome.Pass => "Bestått", ManualStepOutcome.Fail => "Feilet", ManualStepOutcome.Blocked => "Blokkert", ManualStepOutcome.NotRun => "Ikke kjørt", _ => "Ikke avgjort",
    };

    /// <summary>A run's step results in one short line: only the outcomes that occurred.</summary>
    public static string StepCounts(ManualRunSummary r)
    {
        var parts = new List<string>();
        if (r.Passed > 0) parts.Add($"{r.Passed} bestått");
        if (r.Failed > 0) parts.Add($"{r.Failed} feilet");
        if (r.Blocked > 0) parts.Add($"{r.Blocked} blokkert");
        if (r.NotRun > 0) parts.Add($"{r.NotRun} ikke kjørt");
        if (r.Undecided > 0) parts.Add($"{r.Undecided} ikke avgjort");
        return parts.Count == 0 ? "Ingen stegresultat" : string.Join(" · ", parts);
    }

    public static string DefectStatusLabel(ManualDefectStatus status) => status switch
    {
        ManualDefectStatus.ReadyForRetest => "Klar for ny test", ManualDefectStatus.Closed => "Lukket", _ => "Åpen",
    };

    public static string DefectStatusTone(ManualDefectStatus status) => status switch
    {
        ManualDefectStatus.ReadyForRetest => "warn", ManualDefectStatus.Closed => "muted", _ => "bad",
    };

    public static string FileSize(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} byte",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    // ── Reports (CSV for spreadsheet tools: UTF-8 with BOM, quoted; formula-looking text is neutralised) ──

    public static readonly IReadOnlyList<string> RunReportColumns =
        ["Testtilfelle", "Plan", "Planrevisjon", "Resultat", "Tester", "Miljø", "Startet", "Fullført", "Steg", "Bestått", "Feilet", "Blokkert", "Ikke kjørt",
         "Feil", "Åpne feil", "Dokumentasjon", "Ny test etter feilet kjøring", "Gjennomført i"];

    public static string RunResultsCsv(IEnumerable<ManualRunSummary> runs, IReadOnlyDictionary<Guid, string> planNames)
    {
        var sb = new System.Text.StringBuilder("﻿");
        sb.Append(string.Join(",", RunReportColumns.Select(Cell))).Append("\r\n");
        foreach (var r in runs.OrderByDescending(r => r.StartedAt))
            sb.Append(string.Join(",", new[]
            {
                r.Title, planNames.GetValueOrDefault(r.DraftId) ?? "", N(r.PlanRevision), RunLabel(r.State, r.Outcome), r.Tester, r.Environment ?? "", D(r.StartedAt),
                r.CompletedAt is { } done ? D(done) : "", N(r.Steps), N(r.Passed), N(r.Failed), N(r.Blocked), N(r.NotRun), N(r.Defects), N(r.OpenDefects), N(r.Evidence),
                r.RetestOf is null ? "" : "Ja", "BirkNext",
            }.Select(Cell))).Append("\r\n");
        return sb.ToString();
    }

    public static readonly IReadOnlyList<string> DefectReportColumns =
        ["Feil", "Status", "Alvorlighet", "Steg", "Forventet", "Faktisk", "Arbeidselement", "Registrert av", "Registrert", "Siste oppfølging", "Testtilfelle", "Kjøringens resultat", "Kjøring startet", "Tester"];

    public static string DefectsCsv(IEnumerable<ManualDefectListItem> defects)
    {
        var sb = new System.Text.StringBuilder("﻿");
        sb.Append(string.Join(",", DefectReportColumns.Select(Cell))).Append("\r\n");
        foreach (var i in defects.OrderByDescending(i => i.RunStartedAt))
        {
            var d = i.Defect;
            var last = d.History is { Count: > 0 } h ? h[^1] : null;
            sb.Append(string.Join(",", new[]
            {
                d.Title, DefectStatusLabel(d.Status), d.Severity ?? "", d.Step?.ToString(CultureInfo.InvariantCulture) ?? "", d.Expected ?? "", d.Actual ?? "",
                d.ExternalReference ?? "", d.RecordedBy, d.RecordedAt == default ? "" : D(d.RecordedAt),
                last is null ? "" : $"{D(last.At)} {last.By}: {DefectStatusLabel(last.From)} → {DefectStatusLabel(last.To)}{(string.IsNullOrWhiteSpace(last.Note) ? "" : " — " + last.Note)}",
                i.RunTitle, RunLabel(i.RunState, i.RunOutcome), D(i.RunStartedAt), i.Tester,
            }.Select(Cell))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string D(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A spreadsheet-safe quoted CSV cell (formula-looking text is prefixed with an apostrophe).</summary>
    private static string Cell(string? v)
    {
        var t = v ?? "";
        if (t.Length > 0 && t[0] is '=' or '+' or '-' or '@' or '\t' or '\r') t = "'" + t;
        return "\"" + t.Replace("\"", "\"\"") + "\"";
    }
}
