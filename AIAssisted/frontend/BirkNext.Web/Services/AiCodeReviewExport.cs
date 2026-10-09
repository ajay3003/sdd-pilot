using System.Globalization;
using System.Net;
using System.Text;
using BirkNext.AiCodeReview;

namespace BirkNext.Web.Services;

/// <summary>
/// Self-contained HTML report of one AI-Generated Code Review run: project, exact snapshots, categories, findings with provenance, rule
/// execution (including skipped and unsupported rules) and limitations. Findings hold no source text or values, so nothing raw is exported.
/// </summary>
public static class AiCodeReviewExport
{
    public static string FileName(AiCodeReviewResult r) => $"ai-generated-code-review-{r.CompletedAt.UtcDateTime:yyyy-MM-dd-HHmm}.html";

    public static string Html(AiCodeReviewResult r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string D(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(AiCodeReviewText.Title)).Append("</title><style>")
          .Append("body{font-family:system-ui,sans-serif;margin:2rem;color:#1f2937;max-width:1100px}table{border-collapse:collapse;width:100%;margin:.5rem 0 1.5rem}")
          .Append("th,td{border:1px solid #d1d5db;padding:.35rem .5rem;text-align:left;vertical-align:top;font-size:.85rem}th{background:#f3f4f6}")
          .Append(".note{background:#eff6ff;border:1px solid #bfdbfe;padding:.6rem .8rem;border-radius:6px}code{font-size:.8rem}</style></head><body>");
        sb.Append("<h1>").Append(E(AiCodeReviewText.Title)).Append("</h1>");
        sb.Append("<p class=\"note\">").Append(E(r.Disclaimer)).Append("</p>");
        sb.Append("<table><tbody>")
          .Append("<tr><th>Project</th><td>").Append(E(r.ProjectName ?? r.Current.Repository)).Append("</td></tr>")
          .Append("<tr><th>Review date</th><td>").Append(D(r.CompletedAt)).Append("</td></tr>")
          .Append("<tr><th>Mode</th><td>").Append(r.Mode == AiReviewMode.SnapshotChange ? "Snapshot change review" : "Current snapshot review")
          .Append(" · scope: ").Append(r.Scope == AiReviewScope.ChangedFiles ? "changed files only" : "entire source").Append("</td></tr>")
          .Append("<tr><th>Current source snapshot</th><td>").Append(E($"{r.Current.ArchiveName} · {r.Current.Fingerprint} · analyzed {D(r.Current.AnalyzedAt)} · id {r.Current.SnapshotId}")).Append("</td></tr>")
          .Append("<tr><th>Baseline snapshot</th><td>").Append(r.Baseline is { } b ? E($"{b.ArchiveName} · {b.Fingerprint} · analyzed {D(b.AnalyzedAt)} · id {b.SnapshotId}") : "Not selected").Append("</td></tr>");
        if (r.Attention is { } attention) sb.Append("<tr><th>Review attention</th><td>").Append(E($"{attention} — {r.AttentionBasis}")).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Categories</h2><table><thead><tr><th>Category</th><th>Status</th><th>Findings</th><th>Assessed scope</th><th>Explanation</th></tr></thead><tbody>");
        foreach (var c in r.Categories)
            sb.Append("<tr><td>").Append(E(AiCodeReviewText.Label(c.Category))).Append("</td><td>").Append(E(AiCodeReviewText.Label(c.Status))).Append("</td><td>")
              .Append(c.Findings).Append("</td><td>").Append(E(c.AssessedScope)).Append("</td><td>").Append(E(c.Explanation)).Append("</td></tr>");
        sb.Append("</tbody></table>");

        if (r.Changes.Count > 0)
        {
            sb.Append("<h2>Changes between snapshots</h2><table><thead><tr><th>Area</th><th>Introduced</th><th>Removed</th><th>Changed</th><th>Unchanged</th><th>Note</th></tr></thead><tbody>");
            foreach (var c in r.Changes)
                sb.Append("<tr><td>").Append(E(c.Area)).Append("</td><td>").Append(c.Introduced).Append("</td><td>").Append(c.Removed).Append("</td><td>").Append(c.Changed)
                  .Append("</td><td>").Append(c.Unchanged).Append("</td><td>").Append(E(c.Note)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<h2>Findings (").Append(r.Findings.Count).Append(")</h2>");
        if (r.Findings.Count == 0) sb.Append("<p>").Append(E(AiCodeReviewText.NoIndicators)).Append(" This does not mean the code is correct.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Rule</th><th>Severity</th><th>Finding</th><th>Evidence</th><th>Change</th><th>Limitation / review action</th></tr></thead><tbody>");
            foreach (var f in r.Findings)
                sb.Append("<tr><td><code>").Append(E(f.RuleId)).Append("</code><br>").Append(E(AiCodeReviewText.Label(f.Category))).Append("</td><td>").Append(f.Severity)
                  .Append("</td><td><strong>").Append(E(f.Title)).Append("</strong><br>").Append(E(f.Description)).Append("<br><em>Why flagged:</em> ").Append(E(f.Rationale))
                  .Append("</td><td>").Append(E(string.Join(", ", f.EvidenceSources.Select(AiCodeReviewPresentation.Label))))
                  .Append(f.Locations.Count > 0 ? "<br>" + string.Join("<br>", f.Locations.Take(10).Select(l => $"<code>{E(AiCodeReviewPresentation.Location(l))}</code>")) : "")
                  .Append(f.Locations.Count > 10 ? $"<br>…and {f.Locations.Count - 10} more" : "")
                  .Append(f.RelatedIds.Count > 0 ? "<br>Related: " + E(string.Join(", ", f.RelatedIds.Take(20))) : "")
                  .Append(f.EvidenceReferences > 1 ? $"<br>{f.EvidenceReferences} evidence references" : "")
                  .Append("</td><td>").Append(E(AiCodeReviewPresentation.Label(f.Change)))
                  .Append(f.Baseline is null && f.Current is null ? "" : $"<br>{E(f.Baseline ?? "—")} → {E(f.Current ?? "—")}")
                  .Append("</td><td>").Append(E(f.Limitation)).Append("<br><em>Action:</em> ").Append(E(f.Recommendation)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<h2>Rule execution</h2><table><thead><tr><th>Rule</th><th>Title</th><th>State</th><th>Findings</th><th>Reason</th></tr></thead><tbody>");
        foreach (var rule in r.Rules)
            sb.Append("<tr><td><code>").Append(E(rule.RuleId)).Append("</code></td><td>").Append(E(rule.Title)).Append("</td><td>").Append(E(AiCodeReviewText.Label(rule.State)))
              .Append("</td><td>").Append(rule.Findings).Append("</td><td>").Append(E(rule.Reason)).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Limitations</h2><ul>");
        foreach (var l in r.Limitations.DefaultIfEmpty("None recorded beyond the per-rule limitations.")) sb.Append("<li>").Append(E(l)).Append("</li>");
        sb.Append("<li>Deterministic rules only; no language model was used. Runtime behaviour is verified by the runtime reviews, not here.</li></ul>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
