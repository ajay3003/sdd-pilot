using System.Text;
using BirkNext.PipelineReview;

namespace BirkNext.Web.Services;

/// <summary>HTML export of a Pipeline Review through the shared export engine: delivery flow, gaps, tests, environments, artifact lineage, evidence/limitations.</summary>
public static class PipelineReviewExport
{
    public static string Build(PipelineReviewResult r, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        string Text(string s) => esc(s).Replace("`", "");
        var sb = new StringBuilder();
        sb.Append($"<section class=\"block\"><h2>Summary</h2><p><strong>{esc(PipelineReviewPresentation.Headline(r))}</strong> — {esc(PipelineReviewPresentation.Counts(r))}.</p>");
        sb.Append($"<p>Source snapshot {esc(r.ArchiveName)} ({esc(r.SourceFingerprint[..Math.Min(12, r.SourceFingerprint.Length)])}), analyzed {r.SourceAnalyzedAt:u}; CI/CD evidence v{r.CiCdAnalyzerVersion}, review rules v{r.RulesVersion}.</p>");
        if (r.DeliveryPath.Count > 0) sb.Append($"<p><strong>Delivery path:</strong> {esc(string.Join(" → ", r.DeliveryPath))}</p>");
        sb.Append($"<p>{esc(r.Boundary)}</p></section>\n");

        sb.Append("<section class=\"block\"><h2>Delivery flow</h2><ol>");
        foreach (var s in r.Story) sb.Append($"<li>{Text(s.Text)} <em>({esc(PipelineReviewText.Label(s.State))})</em></li>");
        sb.Append("</ol></section>\n");

        sb.Append("<section class=\"block\"><h2>Gaps</h2>");
        sb.Append(table(["Severity", "Evidence", "Gap", "Why it matters", "Suggested review action"],
            r.Findings.Select(f => new[] { badge(f.Severity.ToString()), esc(PipelineReviewText.Label(f.EvidenceState)), $"<strong>{Text(f.Title)}</strong><br>{Text(f.Explanation)}", esc(f.WhyItMatters), Text(f.SuggestedAction) })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Tests</h2>");
        sb.Append(table(["Validation", .. r.TestMatrix.Columns], r.TestMatrix.Rows.Select(row =>
            new[] { esc(PipelineReviewText.Label(row.Category) + (row.Note is null ? "" : $" — {row.Note}")) }.Concat(r.TestMatrix.Columns.Select(c =>
            { var (symbol, text) = PipelineReviewPresentation.Cell(row.Cells.GetValueOrDefault(c)); return esc($"{symbol} {text}"); })).ToArray())));
        sb.Append(table(["Pipeline", "Test", "When it runs", "Gates"], r.Tests.Select(t => new[] { esc(t.PipelineName), Text($"{PipelineReviewText.Label(t.Category)}: {t.Name}"), Text(t.WhenItRuns), esc(t.Gates.Count.ToString()) })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Environment progression</h2>");
        sb.Append(table(["Environment", "Deployment", "Before", "After", "Artifact", "Approval", "Rollback"], r.Deployments.Select(d => new[]
        {
            esc(PipelineReviewBuilderDisplay(d)), Text($"{d.PipelineName} · {d.Stage ?? d.Job}"),
            esc(string.Join(", ", d.Before.Select(g => $"{PipelineReviewText.Label(g.Category)} ({PipelineReviewText.Label(g.State)})").Distinct())),
            esc(string.Join(", ", d.After.Select(g => PipelineReviewText.Label(g.Category)).Distinct())),
            Text(string.Join("; ", d.Artifacts.Select(a => $"{a.Artifact} from {a.Source} ({PipelineReviewText.Label(a.State)})"))), Text(d.Approval), Text(d.Rollback),
        })));
        sb.Append(table(["From", "To", "Evidence", "Basis"], r.Progression.Select(p => new[] { esc(p.From), esc(p.To), esc(PipelineReviewText.Label(p.State)), Text(p.Basis) })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Artifact lineage and dependencies</h2>");
        sb.Append(table(["From", "Relationship", "To", "Evidence", "Basis"], r.Dependencies.Select(d => new[] { esc(d.FromPipeline), esc(PipelineReviewText.Label(d.Kind)), esc(d.ToPipeline), esc(PipelineReviewText.Label(d.State)), Text(d.Basis) })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Evidence and limitations</h2><ul>");
        foreach (var l in r.Limitations) sb.Append($"<li>{esc(l)}</li>");
        foreach (var u in r.UnresolvedTemplates) sb.Append($"<li>Template {Text(u.Template)} ({esc(u.Level)}): {esc(u.Reason)}</li>");
        sb.Append("</ul></section>\n");
        return buildHtml("Pipeline Review", null, $"{r.ArchiveName} · {r.Pipelines.Count(p => !p.IsTemplate)} pipeline(s)", sb.ToString());
    }

    private static string PipelineReviewBuilderDisplay(DeploymentReview d) =>
        d.EnvironmentKind is BirkNext.SourceDomains.SourceEnvironmentKind.Custom or BirkNext.SourceDomains.SourceEnvironmentKind.Default ? d.Environment : $"{d.Environment} ({d.EnvironmentKind})";
}
