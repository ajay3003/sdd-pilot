using System.Globalization;
using System.Net;
using System.Text;
using BirkNext.DeliveryReporting;

namespace BirkNext.Api.Services.DeliveryReporting;

public sealed record DeliveryExportOptions(bool IncludeEvidenceAppendix = false, bool IncludeUnchangedStories = false);

/// <summary>
/// Renders a frozen <see cref="WeeklyDeliveryReport"/> as Markdown or standalone HTML. Reads nothing but the document, so an old report always
/// exports the same way. The default output is management-first: no ids, fingerprints, commits or file paths — those appear only in the
/// optional evidence appendix. Status is always written as text, never as colour alone.
/// </summary>
public static class DeliveryReportExporter
{
    public static DeliveryReportExport Export(WeeklyDeliveryReport report, DeliveryExportFormat format, DeliveryExportOptions options) => format switch
    {
        DeliveryExportFormat.Html => new(WeeklyReportBuilder.FileName(report, "html"), "text/html", Html(report, options)),
        _ => new(WeeklyReportBuilder.FileName(report, "md"), "text/markdown", Markdown(report, options)),
    };

    // ── Markdown ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string Markdown(WeeklyDeliveryReport report, DeliveryExportOptions options)
    {
        var zone = Zone(report);
        var md = new StringBuilder();
        md.AppendLine($"# {Md(report.Title)} — {report.WeekLabel}").AppendLine();
        md.AppendLine($"**Project:** {Md(report.ProjectName)}  ");
        md.AppendLine($"**Period:** {Period(report, zone)}  ");
        md.AppendLine($"**Baseline:** {SnapshotLine(report.Baseline, zone)}  ");
        md.AppendLine($"**Current:** {SnapshotLine(report.Current, zone)}  ");
        md.AppendLine($"**Coverage:** {report.Coverage}  ");
        md.AppendLine($"**Generated:** {Time(report.GeneratedAt, zone)}").AppendLine();
        foreach (var note in report.CoverageNotes) md.AppendLine($"> **Coverage note:** {Md(note)}").AppendLine();

        md.AppendLine("## Executive summary").AppendLine();
        foreach (var line in report.ExecutiveSummary) md.AppendLine($"- {Md(line)}");
        md.AppendLine();

        md.AppendLine("## Movement summary").AppendLine();
        md.AppendLine("| Measure | Count |").AppendLine("|---|---:|");
        foreach (var metric in report.Metrics) md.AppendLine($"| {Cell(metric.Label)} | {metric.Value} |");
        md.AppendLine();

        var stories = DeliveryReportStories.Select(report, options.IncludeUnchangedStories);
        md.AppendLine(options.IncludeUnchangedStories ? "## All stories" : "## Changed stories").AppendLine();
        if (stories.Count == 0) md.AppendLine("No stories changed in this period.").AppendLine();
        else
        {
            md.AppendLine("| Story / feature | Module | Baseline | Current | Change this week | Task progress | Attention |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var s in stories)
                md.AppendLine($"| {Cell(s.Title)} | {Cell(s.Module)} | {DeliveryReportStories.StatusOrDash(s.BaselineStatus)} | {DeliveryReportStories.StatusOrDash(s.CurrentStatus)} | {DeliveryReportingText.Movement(s.Movement)} | {DeliveryReportStories.ProgressChange(s)} | {(s.Attention.Count == 0 ? "–" : Cell(string.Join(", ", s.Attention)))} |");
            md.AppendLine();
            if (!options.IncludeUnchangedStories && report.Stories.Count > stories.Count)
                md.AppendLine($"_{report.Stories.Count - stories.Count} unchanged stor{(report.Stories.Count - stories.Count == 1 ? "y is" : "ies are")} not listed._").AppendLine();
        }

        var moved = report.Stories.Where(s => s.Movement is DeliveryMovement.Progressed or DeliveryMovement.Completed).ToList();
        md.AppendLine("## Completed and progressed").AppendLine();
        if (moved.Count == 0) md.AppendLine("No story progressed in this period.").AppendLine();
        else
        {
            foreach (var s in moved) md.AppendLine($"- **{Md(s.Title)}** — {DeliveryReportingText.Movement(s.Movement)}: {Md(string.Join("; ", s.Reasons))}");
            md.AppendLine();
        }

        md.AppendLine("## Needs attention").AppendLine();
        if (report.Attention.Count == 0) md.AppendLine("Nothing needs attention.").AppendLine();
        else
        {
            foreach (var a in report.Attention)
                md.AppendLine($"- **{(a.Severity == DeliveryAttentionSeverity.Blocker ? "Blocker" : "Review")} · {DeliveryReportingText.Attention(a.Kind)}** — {Md(a.Title)}: {Md(a.Detail)}");
            md.AppendLine();
        }

        md.AppendLine("## Next-week focus").AppendLine();
        if (report.NextWeekFocus.Count == 0) md.AppendLine("No open work was found in the current snapshot.").AppendLine();
        else
        {
            foreach (var f in report.NextWeekFocus)
            {
                md.AppendLine($"- {Md(f.Text)}");
                foreach (var feature in f.Features) md.AppendLine($"  - {Md(feature)}");
            }
            md.AppendLine();
        }

        if (options.IncludeEvidenceAppendix)
        {
            md.AppendLine("## Appendix: evidence").AppendLine();
            md.AppendLine($"- Baseline source: {Md(report.Baseline.SourceFileName)}{(report.Baseline.GitCommit is { } bc ? $", commit `{bc}`" : "")}, fingerprint `{report.Baseline.Fingerprint}`, imported {Time(report.Baseline.ImportedAt, zone)}");
            md.AppendLine($"- Current source: {Md(report.Current.SourceFileName)}{(report.Current.GitCommit is { } cc ? $", commit `{cc}`" : "")}, fingerprint `{report.Current.Fingerprint}`, imported {Time(report.Current.ImportedAt, zone)}");
            md.AppendLine();
            foreach (var s in stories)
            {
                md.AppendLine($"### {Md(s.Title)}").AppendLine();
                foreach (var line in EvidenceLines(s)) md.AppendLine($"- {Md(line)}");
                md.AppendLine();
            }
        }
        return md.ToString().TrimEnd() + "\n";
    }

    // ── HTML ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string Html(WeeklyDeliveryReport report, DeliveryExportOptions options)
    {
        var zone = Zone(report);
        var h = new StringBuilder();
        h.AppendLine("<!doctype html>").AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        h.AppendLine($"<title>{E(report.Title)} — {E(report.WeekLabel)}</title>");
        h.AppendLine("<style>").AppendLine(Css).AppendLine("</style></head><body><main>");
        h.AppendLine($"<header><h1>{E(report.Title)} — {E(report.WeekLabel)}</h1><dl class=\"meta\">");
        Meta(h, "Project", report.ProjectName);
        Meta(h, "Period", Period(report, zone));
        Meta(h, "Baseline", SnapshotLine(report.Baseline, zone));
        Meta(h, "Current", SnapshotLine(report.Current, zone));
        Meta(h, "Coverage", report.Coverage.ToString());
        Meta(h, "Generated", Time(report.GeneratedAt, zone));
        h.AppendLine("</dl></header>");
        foreach (var note in report.CoverageNotes) h.AppendLine($"<p class=\"note\" role=\"note\"><strong>Coverage note:</strong> {E(note)}</p>");

        h.AppendLine("<section><h2>Executive summary</h2><ul>");
        foreach (var line in report.ExecutiveSummary) h.AppendLine($"<li>{E(line)}</li>");
        h.AppendLine("</ul></section>");

        h.AppendLine("<section><h2>Movement summary</h2><div class=\"cards\">");
        foreach (var m in report.Metrics) h.AppendLine($"<div class=\"card\"><span class=\"value\">{m.Value}</span><span class=\"label\">{E(m.Label)}</span></div>");
        h.AppendLine("</div></section>");

        var stories = DeliveryReportStories.Select(report, options.IncludeUnchangedStories);
        h.AppendLine($"<section><h2>{(options.IncludeUnchangedStories ? "All stories" : "Changed stories")}</h2>");
        if (stories.Count == 0) h.AppendLine("<p>No stories changed in this period.</p>");
        else
        {
            h.AppendLine("<table><thead><tr><th scope=\"col\">Story / feature</th><th scope=\"col\">Module</th><th scope=\"col\">Baseline</th><th scope=\"col\">Current</th><th scope=\"col\">Change this week</th><th scope=\"col\">Task progress</th><th scope=\"col\">Attention</th></tr></thead><tbody>");
            foreach (var s in stories)
                h.AppendLine($"<tr><th scope=\"row\">{E(s.Title)}</th><td>{E(s.Module)}</td><td>{E(DeliveryReportStories.StatusOrDash(s.BaselineStatus))}</td><td>{E(DeliveryReportStories.StatusOrDash(s.CurrentStatus))}</td><td><span class=\"badge {MovementClass(s.Movement)}\">{E(DeliveryReportingText.Movement(s.Movement))}</span></td><td>{E(DeliveryReportStories.ProgressChange(s))}</td><td>{(s.Attention.Count == 0 ? "–" : E(string.Join(", ", s.Attention)))}</td></tr>");
            h.AppendLine("</tbody></table>");
            if (!options.IncludeUnchangedStories && report.Stories.Count > stories.Count)
                h.AppendLine($"<p class=\"muted\">{report.Stories.Count - stories.Count} unchanged stor{(report.Stories.Count - stories.Count == 1 ? "y is" : "ies are")} not listed.</p>");
        }
        h.AppendLine("</section>");

        var moved = report.Stories.Where(s => s.Movement is DeliveryMovement.Progressed or DeliveryMovement.Completed).ToList();
        h.AppendLine("<section><h2>Completed and progressed</h2>");
        if (moved.Count == 0) h.AppendLine("<p>No story progressed in this period.</p>");
        else
        {
            h.AppendLine("<ul>");
            foreach (var s in moved) h.AppendLine($"<li><strong>{E(s.Title)}</strong> — {E(DeliveryReportingText.Movement(s.Movement))}: {E(string.Join("; ", s.Reasons))}</li>");
            h.AppendLine("</ul>");
        }
        h.AppendLine("</section>");

        h.AppendLine("<section><h2>Needs attention</h2>");
        if (report.Attention.Count == 0) h.AppendLine("<p>Nothing needs attention.</p>");
        else
        {
            h.AppendLine("<ul class=\"attention\">");
            foreach (var a in report.Attention)
                h.AppendLine($"<li><span class=\"badge {(a.Severity == DeliveryAttentionSeverity.Blocker ? "blocker" : "review")}\">{(a.Severity == DeliveryAttentionSeverity.Blocker ? "Blocker" : "Review")}</span> <strong>{E(DeliveryReportingText.Attention(a.Kind))}</strong> — {E(a.Title)}: {E(a.Detail)}</li>");
            h.AppendLine("</ul>");
        }
        h.AppendLine("</section>");

        h.AppendLine("<section><h2>Next-week focus</h2>");
        if (report.NextWeekFocus.Count == 0) h.AppendLine("<p>No open work was found in the current snapshot.</p>");
        else
        {
            h.AppendLine("<ul>");
            foreach (var f in report.NextWeekFocus)
            {
                h.Append($"<li>{E(f.Text)}");
                if (f.Features.Count > 0) h.Append("<ul>").Append(string.Concat(f.Features.Select(x => $"<li>{E(x)}</li>"))).Append("</ul>");
                h.AppendLine("</li>");
            }
            h.AppendLine("</ul>");
        }
        h.AppendLine("</section>");

        if (options.IncludeEvidenceAppendix)
        {
            h.AppendLine("<section class=\"appendix\"><h2>Appendix: evidence</h2><ul>");
            h.AppendLine($"<li>Baseline source: {E(report.Baseline.SourceFileName)}{(report.Baseline.GitCommit is { } bc ? $", commit <code>{E(bc)}</code>" : "")}, fingerprint <code>{E(report.Baseline.Fingerprint)}</code>, imported {E(Time(report.Baseline.ImportedAt, zone))}</li>");
            h.AppendLine($"<li>Current source: {E(report.Current.SourceFileName)}{(report.Current.GitCommit is { } cc ? $", commit <code>{E(cc)}</code>" : "")}, fingerprint <code>{E(report.Current.Fingerprint)}</code>, imported {E(Time(report.Current.ImportedAt, zone))}</li>");
            h.AppendLine("</ul>");
            foreach (var s in stories)
            {
                h.AppendLine($"<h3>{E(s.Title)}</h3><ul>");
                foreach (var line in EvidenceLines(s)) h.AppendLine($"<li>{E(line)}</li>");
                h.AppendLine("</ul>");
            }
            h.AppendLine("</section>");
        }
        h.AppendLine("</main></body></html>");
        return h.ToString();
    }

    private const string Css = """
        :root { color-scheme: light; --ink:#1f2328; --muted:#57606a; --line:#d0d7de; --soft:#f6f8fa; }
        body { margin:0; font:15px/1.5 -apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; color:var(--ink); background:#fff; }
        main { max-width:1100px; margin:0 auto; padding:24px 16px 48px; }
        h1 { font-size:1.6rem; margin:0 0 12px; } h2 { font-size:1.2rem; margin:28px 0 10px; border-bottom:1px solid var(--line); padding-bottom:4px; }
        .meta { display:grid; grid-template-columns:max-content 1fr; gap:4px 16px; margin:0; } .meta dt { font-weight:600; color:var(--muted); } .meta dd { margin:0; }
        .note { background:#fff8c5; border:1px solid #d4a72c; border-radius:6px; padding:8px 12px; }
        .cards { display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:10px; }
        .card { border:1px solid var(--line); border-radius:8px; padding:10px 12px; background:var(--soft); display:flex; flex-direction:column; }
        .card .value { font-size:1.6rem; font-weight:700; } .card .label { color:var(--muted); }
        table { width:100%; border-collapse:collapse; } th, td { border:1px solid var(--line); padding:6px 8px; text-align:left; vertical-align:top; }
        thead th { background:var(--soft); } tbody th { font-weight:600; }
        .badge { display:inline-block; border:1px solid currentColor; border-radius:999px; padding:0 8px; font-size:.85em; white-space:nowrap; }
        .progressed, .completed { color:#1a7f37; } .regressed, .blocker { color:#b42318; } .new { color:#0550ae; } .review, .removed { color:#7d4e00; }
        .muted { color:var(--muted); } code { font-size:.85em; word-break:break-all; }
        @media print {
          main { max-width:none; padding:0; } .card { break-inside:avoid; } tr, li { break-inside:avoid; } thead { display:table-header-group; }
          h2 { break-after:avoid; } table { font-size:12px; } .badge { border-color:#000; color:#000; }
        }
        """;

    private static IEnumerable<string> EvidenceLines(DeliveryStoryRow s)
    {
        var e = s.Evidence;
        if (e.SpecPath is not null) yield return $"Specification: {e.SpecPath}";
        if (e.TasksPath is not null) yield return $"Tasks: {e.TasksPath}";
        if (e.PlanPath is not null) yield return $"Plan (context): {e.PlanPath}";
        if (e.BaselineStatusReason is not null) yield return $"Baseline status reason: {e.BaselineStatusReason}";
        if (e.CurrentStatusReason is not null) yield return $"Current status reason: {e.CurrentStatusReason}";
        foreach (var r in s.Reasons) yield return $"Movement: {r}";
        foreach (var t in e.TasksCompleted) yield return $"Task completed: {t}";
        foreach (var t in e.TasksReopened) yield return $"Task reopened: {t}";
        foreach (var t in e.TasksAdded) yield return $"Task added: {t}";
        foreach (var t in e.TasksRemoved) yield return $"Task removed: {t}";
        foreach (var a in e.AcceptanceAdded) yield return $"Acceptance criterion added: {a}";
        foreach (var a in e.AcceptanceRemoved) yield return $"Acceptance criterion removed: {a}";
        if (e.SourceSummary is not null) yield return e.SourceSummary;
    }

    private static string MovementClass(DeliveryMovement movement) => movement switch
    {
        DeliveryMovement.Progressed => "progressed",
        DeliveryMovement.Completed => "completed",
        DeliveryMovement.Regressed => "regressed",
        DeliveryMovement.NewThisPeriod => "new",
        DeliveryMovement.Removed => "removed",
        DeliveryMovement.NeedsReview => "review",
        _ => "unchanged",
    };

    private static void Meta(StringBuilder h, string label, string value) => h.AppendLine($"<dt>{E(label)}</dt><dd>{E(value)}</dd>");

    private static string Period(WeeklyDeliveryReport report, TimeZoneInfo zone) =>
        $"{WeeklyReportBuilder.Day(TimeZoneInfo.ConvertTime(report.PeriodStart, zone))} – {WeeklyReportBuilder.Day(TimeZoneInfo.ConvertTime(report.PeriodEnd, zone))} (week {report.WeekLabel})";

    private static string SnapshotLine(DeliveryReportSnapshotRef snapshot, TimeZoneInfo zone) =>
        $"{snapshot.Label} · {WeeklyReportBuilder.Day(TimeZoneInfo.ConvertTime(snapshot.EffectiveAt, zone))} · {DeliveryReportingText.Source(snapshot.SourceType)}";

    private static string Time(DateTimeOffset value, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo Zone(WeeklyDeliveryReport report)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(report.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Markdown text: escapes characters that would start formatting or HTML.</summary>
    private static string Md(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r", " ").Replace("\n", " "))
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#' or '|') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Cell(string text) => Md(text);
}
