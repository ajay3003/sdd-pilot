using System.Globalization;
using System.Net;
using System.Text;
using BirkNext.IntegrationSuites;

namespace BirkNext.Web.Services;

/// <summary>
/// The documented result of an integration suite (one stored revision) for people who were not at the screen: which journey it was prepared for,
/// the sources, environment, every stage with its coverage and blockers, the test data, the runs and the outcome — from the stored suite only.
/// Self-contained HTML (no scripts, no external resources); every value is HTML-encoded.
/// </summary>
public static class IntegrationSuiteReport
{
    public static string FileName(IntegrationSuite s) =>
        $"integration-suite-{Slug(s.Scope.DisplayName.Length > 0 ? s.Scope.DisplayName : s.Scope.ScopeId)}-r{s.Revision}-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.html";

    public static string Html(IntegrationSuite s, string? projectName, string? environmentName)
    {
        static string E(string? v) => WebUtility.HtmlEncode(v ?? "");
        static string T(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E($"Integration test result — {s.Scope.DisplayName} r{s.Revision}")).Append("</title>")
          .Append("<style>body{font-family:system-ui,Segoe UI,sans-serif;margin:2rem;max-width:72rem;color:#1f2328}table{border-collapse:collapse;width:100%;margin:.5rem 0 1.2rem}")
          .Append("th,td{border:1px solid #d0d7de;padding:.35rem .5rem;text-align:left;vertical-align:top;font-size:.9rem}th{background:#f6f8fa}dt{font-weight:600}dd{margin:0 0 .4rem}")
          .Append(".muted{color:#57606a}.k{display:grid;grid-template-columns:14rem 1fr;gap:.2rem 1rem}</style></head><body>");
        sb.Append("<h1>Integration test result</h1><p class=\"muted\">Produced by BirkNext from the stored suite revision. Nothing was executed to produce this report; a result is only what a run recorded.</p>");

        sb.Append("<h2>Identity</h2><dl class=\"k\">");
        void Row(string k, string? v) => sb.Append("<dt>").Append(E(k)).Append("</dt><dd>").Append(E(string.IsNullOrWhiteSpace(v) ? "not recorded" : v)).Append("</dd>");
        Row("Project", projectName);
        Row("Suite", $"{s.Scope.DisplayName} — revision {s.Revision}");
        Row("Suite key", s.SuiteKey);
        Row("Generated", $"{T(s.GeneratedAt)} by {s.GeneratedBy}");
        Row("Prepared for journey", s.PreparedForJourneys.Count == 0 ? null
            : string.Join("; ", s.PreparedForJourneys.GroupBy(j => j.JourneyId).Select(g => g.OrderByDescending(j => j.Revision).First()).Select(j => $"{j.Name} (revision {j.Revision})")));
        Row("Source", $"{s.Snapshot.ArchiveFileName} · SHA-256 {(s.Snapshot.ArchiveSha256.Length >= 16 ? s.Snapshot.ArchiveSha256[..16] + "…" : s.Snapshot.ArchiveSha256)} · analysed {T(s.Snapshot.AnalyzedAt)}");
        if (s.Composite is not null) Row("Sources", "Composite source context (several imported sources; see the suite in BirkNext for each stage's source)");
        Row("Target environment", environmentName ?? s.EnvironmentId);
        sb.Append("</dl>");

        sb.Append("<h2>Result</h2><dl class=\"k\">");
        Row("Outcome", s.Outcome.ToString());
        Row("Status", $"{s.Status} — {s.StatusReason}");
        sb.Append("</dl>");

        if (s.Stages.Count > 0)
        {
            sb.Append("<h2>Stages of the flow</h2><table><thead><tr><th>#</th><th>Stage</th><th>Component</th><th>Coverage</th><th>Readiness</th><th>Tests</th><th>Blockers</th></tr></thead><tbody>");
            foreach (var st in s.Stages.OrderBy(x => x.Order))
                sb.Append("<tr><td>").Append(st.Order).Append("</td><td>").Append(E(st.Label)).Append("</td><td>").Append(E(st.Component)).Append("</td><td>").Append(E(st.Coverage.ToString()))
                  .Append("</td><td>").Append(E(st.Readiness.ToString())).Append("</td><td>").Append(st.ContractTests.Count).Append("</td><td>").Append(E(string.Join("; ", st.Blockers))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<h2>Runs</h2>");
        if (s.Runs.Count == 0) sb.Append("<p>No run has been recorded for this suite.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Started</th><th>Kind</th><th>By</th><th>Result</th><th>Tests</th></tr></thead><tbody>");
            foreach (var r in s.Runs.OrderByDescending(r => r.StartedAt))
                sb.Append("<tr><td>").Append(E(T(r.StartedAt))).Append("</td><td>").Append(E(r.Kind)).Append("</td><td>").Append(E(r.Initiator)).Append("</td><td>").Append(E(r.State))
                  .Append("</td><td>").Append(r.TestIds.Count).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        if (s.TestData.Count > 0)
        {
            sb.Append("<h2>Test data</h2><table><thead><tr><th>Role</th><th>Purpose</th><th>Asset</th><th>State</th></tr></thead><tbody>");
            foreach (var d in s.TestData)
                sb.Append("<tr><td>").Append(E(d.Role)).Append("</td><td>").Append(E(d.Purpose)).Append("</td><td>").Append(E(d.Asset ?? "none"))
                  .Append(d.Generated ? " (generated draft)" : "").Append("</td><td>").Append(E(d.Approved ? "Approved" : d.State)).Append("</td></tr>");
            sb.Append("</tbody></table><p class=\"muted\">Test data states are as recorded when this revision was generated.</p>");
        }

        if (s.Readiness.Count > 0)
        {
            sb.Append("<h2>Readiness</h2><table><thead><tr><th>Dimension</th><th>State</th><th>Detail</th></tr></thead><tbody>");
            foreach (var d in s.Readiness) sb.Append("<tr><td>").Append(E(d.Name)).Append("</td><td>").Append(E(d.State.ToString())).Append("</td><td>").Append(E(d.Detail)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        if (s.Limitations.Count > 0)
        {
            sb.Append("<h2>Limitations</h2><ul>");
            foreach (var l in s.Limitations) sb.Append("<li>").Append(E(l)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Slug(string v)
    {
        var chars = v.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) && ch < 128 ? ch : '-').ToArray();
        var slug = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "suite" : slug[..Math.Min(slug.Length, 40)];
    }
}
