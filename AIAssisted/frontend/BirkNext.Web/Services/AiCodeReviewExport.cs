using System.Globalization;
using System.Net;
using System.Text;
using BirkNext.AiCodeReview;

namespace BirkNext.Web.Services;

/// <summary>
/// Self-contained HTML report of one Code Risk Review run, in the page's order: summary, review coverage, recommended actions, findings grouped
/// by category and rule, change risk, rule execution (including skipped and unsupported rules) and limitations. Rule ids are shown as CRR-*;
/// findings hold no source text or values, so nothing raw is exported.
/// </summary>
public static class AiCodeReviewExport
{
    public static string FileName(AiCodeReviewResult r) => $"code-risk-review-{r.CompletedAt.UtcDateTime:yyyy-MM-dd-HHmm}.html";

    public static string Html(AiCodeReviewResult r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string D(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        static string Id(string ruleId) => AiCodeReviewText.DisplayId(ruleId);
        var summary = CodeRiskTriage.Summary(r);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(AiCodeReviewText.Title)).Append("</title><style>")
          .Append("body{font-family:system-ui,sans-serif;margin:2rem;color:#1f2937;max-width:1100px}table{border-collapse:collapse;width:100%;margin:.5rem 0 1.5rem}")
          .Append("th,td{border:1px solid #d1d5db;padding:.35rem .5rem;text-align:left;vertical-align:top;font-size:.85rem}th{background:#f3f4f6}")
          .Append(".note{background:#eff6ff;border:1px solid #bfdbfe;padding:.6rem .8rem;border-radius:6px}code{font-size:.8rem}</style></head><body>");
        sb.Append("<h1>").Append(E(AiCodeReviewText.Title)).Append("</h1>");
        sb.Append("<p class=\"note\">").Append(E(AiCodeReviewText.Disclaimer)).Append("</p>");
        sb.Append("<table><tbody>")
          .Append("<tr><th>Project</th><td>").Append(E(r.ProjectName ?? r.Current.Repository)).Append("</td></tr>")
          .Append("<tr><th>Review date</th><td>").Append(D(r.CompletedAt)).Append("</td></tr>")
          .Append("<tr><th>Mode</th><td>").Append(r.Mode == AiReviewMode.SnapshotChange ? "Snapshot change review" : "Current snapshot review")
          .Append(" · scope: ").Append(r.Scope == AiReviewScope.ChangedFiles ? "changed files only" : "entire source").Append("</td></tr>")
          .Append("<tr><th>Current source snapshot</th><td>").Append(E($"{r.Current.ArchiveName} · {r.Current.Fingerprint} · analyzed {D(r.Current.AnalyzedAt)} · id {r.Current.SnapshotId}")).Append("</td></tr>")
          .Append("<tr><th>Baseline snapshot</th><td>").Append(r.Baseline is { } b ? E($"{b.ArchiveName} · {b.Fingerprint} · analyzed {D(b.AnalyzedAt)} · id {b.SnapshotId}") : "Not selected").Append("</td></tr>")
          .Append("<tr><th>Rule set</th><td>").Append(E($"v{r.EngineVersion} · rule ids shown as {AiCodeReviewText.DisplayPrefix}* (stored as {AiCodeReviewText.StoredPrefix}*)")).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Summary</h2><p>").Append(E(summary.Headline)).Append("</p><table><tbody>")
          .Append($"<tr><th>Risk findings</th><td>{summary.Findings} (High {summary.High} · Medium {summary.Medium} · Low {summary.Low} · Info {summary.Info})</td></tr>")
          .Append($"<tr><th>Rules</th><td>{summary.Executed} executed · {summary.EvidenceGaps} evidence gap(s) · {summary.NotAssessed} not assessed · {summary.Unsupported} unsupported · {summary.NotApplicable} not applicable</td></tr>")
          .Append("<tr><th>Current-state review</th><td>").Append(E(summary.CurrentState)).Append("</td></tr>")
          .Append("<tr><th>Change-risk review</th><td>").Append(E(summary.ChangeRisk)).Append("</td></tr>");
        if (r.Attention is { } attention) sb.Append("<tr><th>Review attention</th><td>").Append(E($"{attention} — {r.AttentionBasis}")).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Review coverage</h2><p>Coverage limitations are not code defects.</p><table><thead><tr><th>Evidence</th><th>Status</th><th>Detail</th><th>Impact</th></tr></thead><tbody>");
        foreach (var row in CodeRiskTriage.Coverage(r))
            sb.Append("<tr><td>").Append(E(row.Evidence)).Append("</td><td>").Append(E(CodeRiskTriage.Label(row.Status))).Append("</td><td>").Append(E(row.Detail))
              .Append(row.Provenance is null ? "" : " · " + E(row.Provenance)).Append("</td><td>").Append(E(row.Impact)).Append("</td></tr>");
        sb.Append("</tbody></table>");

        var actions = CodeRiskTriage.Actions(r);
        sb.Append("<h2>Recommended actions</h2>");
        if (actions.Count == 0) sb.Append("<p>None.</p>");
        else
        {
            sb.Append("<ol>");
            foreach (var a in actions) sb.Append("<li>").Append(E(a.Text)).Append("</li>");
            sb.Append("</ol>");
        }

        sb.Append("<h2>Findings by category (").Append(r.Findings.Count).Append(")</h2>");
        if (r.Findings.Count == 0) sb.Append("<p>").Append(E(AiCodeReviewText.NoIndicators)).Append(" This does not mean the code is correct.</p>");
        foreach (var category in CodeRiskTriage.Groups(r.Findings))
        {
            sb.Append("<h3>").Append(E(category.Label)).Append(" · ").Append(category.Findings).Append("</h3>");
            foreach (var group in category.Rules)
            {
                sb.Append("<h4><code>").Append(E(group.DisplayId)).Append("</code> ").Append(E(group.Title)).Append(" · ").Append(group.Findings.Count)
                  .Append(" · ").Append(group.MaxSeverity).Append("</h4>");
                sb.Append("<table><thead><tr><th>Severity</th><th>Finding</th><th>Evidence</th><th>Change</th><th>Limitation / review action</th></tr></thead><tbody>");
                foreach (var f in group.Findings)
                    sb.Append("<tr><td>").Append(f.Severity).Append("</td><td><strong>").Append(E(f.Title)).Append("</strong><br>").Append(E(f.Description))
                      .Append("<br><em>Why it matters:</em> ").Append(E(f.Rationale))
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
        }

        if (r.Changes.Count > 0)
        {
            sb.Append("<h2>Change risk</h2><table><thead><tr><th>Area</th><th>Introduced</th><th>Removed</th><th>Changed</th><th>Unchanged</th><th>Note</th></tr></thead><tbody>");
            foreach (var c in r.Changes)
                sb.Append("<tr><td>").Append(E(c.Area)).Append("</td><td>").Append(c.Introduced).Append("</td><td>").Append(c.Removed).Append("</td><td>").Append(c.Changed)
                  .Append("</td><td>").Append(c.Unchanged).Append("</td><td>").Append(E(c.Note)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        else if (r.Baseline is null) sb.Append("<h2>Change risk</h2><p>Limited — no baseline snapshot selected; change and drift rules were not assessed.</p>");

        sb.Append("<h2>Rule execution</h2><table><thead><tr><th>Rule ID</th><th>Rule</th><th>State</th><th>Findings</th><th>Reason</th></tr></thead><tbody>");
        foreach (var rule in r.Rules)
            sb.Append("<tr><td><code>").Append(E(Id(rule.RuleId))).Append("</code></td><td>").Append(E(rule.Title)).Append("</td><td>").Append(E(CodeRiskTriage.Label(CodeRiskTriage.Kind(rule, r))))
              .Append("</td><td>").Append(rule.Findings).Append("</td><td>").Append(E(rule.Reason)).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Limitations</h2><ul>");
        foreach (var l in r.Limitations.DefaultIfEmpty("None recorded beyond the per-rule limitations.")) sb.Append("<li>").Append(E(l)).Append("</li>");
        sb.Append("<li>Deterministic rules only; no language model was used. Runtime behaviour is verified by the runtime reviews, not here.</li></ul>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
