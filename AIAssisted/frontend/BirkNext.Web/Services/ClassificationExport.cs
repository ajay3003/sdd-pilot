using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>HTML export of a Security Classification run from its stored snapshot. Ids and labels only — no PII, token or payload exists in it.</summary>
public static class ClassificationExport
{
    public static string Build(ClassificationReviewResult r, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        sb.Append($"<section class=\"block\"><h2>Security classification</h2><p>{badge(ClassificationLabels.Overall(r.Overall))} {esc(ClassificationPresentation.Headline(r))}. Completed {r.CompletedAt:u}.</p>");
        sb.Append("<h3>Source scope</h3>");
        if (r.SourceScope is { } scope)
        {
            sb.Append(table(["Role", "Source", "Archive", "Fingerprint (sha256)", "Analyzed", "Source Analysis"], new[] { ("Primary", scope.Primary) }.Concat(scope.Related.Select(e => ("Related", e))).Select(x => new[]
            {
                x.Item1, esc(x.Item2.Repository), esc(x.Item2.ArchiveName), $"<code>{esc(x.Item2.Fingerprint)}</code>", $"{x.Item2.AnalyzedAt:u}", esc(x.Item2.SourceStatus),
            })));
            if (scope.Related.Count == 0) sb.Append("<p>No related source included.</p>");
            foreach (var l in scope.Limitations) sb.Append($"<p>{esc(l)}</p>");
        }
        else sb.Append($"<p>{esc(ClassificationPresentation.ScopeLine(r))}</p>");
        sb.Append($"<p>{esc(ClassificationSourceCoverage.RuntimeNote)}</p>");
        sb.Append(table(["Area", "State", "Evidence"], r.Summary.Select(s => new[] { esc(s.Title), badge(ClassificationLabels.State(s.State)),
            esc(s.Evidence) + string.Concat(s.Parts.Select(p => $"<br>{esc(p.Name)}: {badge(ClassificationLabels.State(p.State))} {esc(p.Detail)}")) })));
        sb.Append("</section>\n<section class=\"block\"><h2>Classification model (from source)</h2>");
        sb.Append(table(["Level", "Value", "BiRK / Elements", "Graded access"], r.Levels.Select(l => new[] { l.Nivaa.ToString(), esc(l.Verdi), esc($"{l.BiRKKode ?? "—"} / {l.ElementsKode ?? "—"}"), l.KreverGradertTilgang ? "Required" : "No" })));
        sb.Append("</section>\n<section class=\"block\"><h2>CDC / change-capture pipeline (from source)</h2>");
        sb.Append(table(["Stage", "Source / configuration", "Runtime"], r.Pipeline.Select(p => new[] { esc(p.Title), $"{badge(ClassificationLabels.State(p.Source))} {esc(p.SourceDetail)}", $"{badge(ClassificationLabels.State(p.Runtime))} {esc(p.RuntimeDetail)}" })));
        sb.Append("</section>\n<section class=\"block\"><h2>Authorization (safe live checks)</h2>");
        sb.Append($"<p>{esc(IntegrationReviewLabels.EvidenceState(r.Live.State))}: {esc(r.Live.Reason)}</p>");
        if (r.Live.Observations.Count > 0)
            sb.Append(table(["Level", "Identity", "Surface", "Test type", "Result", "Expected", "Observed"], r.Live.Observations.Select(o => new[]
            {
                o.Nivaa.ToString(), esc(o.Identity.ToString()), esc(ClassificationLabels.Surface(o.Surface)), esc(ClassificationLabels.TestType(o.TestType)), badge(ClassificationPresentation.Cell(o)), esc(o.Expected), esc(o.Observed),
            })));
        var summary = r.ContextSummary ?? ClassificationContextSummary.From(r.Context); // older runs: only what was configured, never the values
        sb.Append($"<p>Temporary test context: {esc(summary.Environment ?? "not configured")} · unauthorized identity {(summary.UnauthorizedIdentityConfigured ? "configured" : "missing")} · authorized graded identity {(summary.AuthorizedIdentityConfigured ? "configured" : "missing")} · test children for level(s) {(summary.ConfiguredLevels.Count == 0 ? "none" : string.Join(", ", summary.ConfiguredLevels))} · live mutation tests disabled. Context values are not recorded.</p></section>\n");
        sb.Append("<section class=\"block\"><h2>Checks</h2>");
        sb.Append(table(["Area", "Test type", "Check", "State", "Detail", "Source"], r.Checks.Select(c => new[]
        {
            esc(ClassificationLabels.Area(c.Area)), esc(ClassificationLabels.TestType(c.TestType)), esc(c.Title), badge(ClassificationLabels.State(c.State)), esc(c.Detail),
            esc(IntegrationReviewLabels.Source(c.Provenance) + (c.Sources.Count > 0 ? $" · {ClassificationPresentation.Sources(c.Sources)}" : "") + (c.Locations.Count > 0 ? $" · {ClassificationPresentation.Locations(c.Locations)}" : "")),
        })));
        sb.Append("</section>\n<section class=\"block\"><h2>Findings</h2>");
        sb.Append(r.Findings.Count == 0 ? "<p>No findings.</p>" : table(["Severity", "Area", "Finding", "Detail", "Source", "Recommendation"], r.Findings.Select(f => new[]
        {
            badge(f.Severity.ToString()), esc(ClassificationLabels.Area(f.Area)), esc(f.Title), esc(f.Detail + (f.Evidence.Count > 0 ? $" ({string.Join(", ", f.Evidence)})" : "")),
            esc(f.Sources.Count > 0 ? ClassificationPresentation.Sources(f.Sources) : f.RuleId.StartsWith("live-") ? "Runtime (safe live check)" : "—"), esc(f.Recommendation),
        })));
        sb.Append("<p>A source finding describes the selected source snapshot(s); it is not an observed runtime breach.</p>");
        sb.Append("</section>\n<section class=\"block\"><h2>Classification distribution</h2>");
        if (r.SourceCounts is { } sc) sb.Append($"<p>Source {esc(sc.System)} captured {sc.CapturedAt:u} ({esc(sc.Provenance)}).</p>");
        if (r.TargetCounts is { } tc) sb.Append($"<p>Target {esc(tc.System)} captured {tc.CapturedAt:u} ({esc(tc.Provenance)}).</p>");
        sb.Append(table(["Level", "Source", "Target", "Comparison", "Detail"], r.CountComparisons.Select(c => new[]
        {
            c.Nivaa < 0 ? "—" : c.Nivaa.ToString(), c.Source?.ToString() ?? "—", c.Target?.ToString() ?? "—", badge(ClassificationLabels.Comparison(c.State)), esc(c.Detail),
        })));
        sb.Append("<p>Counts are observed evidence at the stated times — never an expected value — and a match says nothing about authorization.</p></section>\n");
        sb.Append("<section class=\"block\"><h2>Repository tests</h2>");
        sb.Append(table(["Behaviour", "Coverage", "Tests", "Repository", "Note"], r.TestCoverage.Select(t => new[] { esc(t.Scenario), badge(ClassificationLabels.Coverage(t.State)), esc(string.Join(", ", t.Tests.Take(3))), esc(string.Join(", ", t.Repositories)), esc(t.Note) })));
        sb.Append("<p>Developer tests are source evidence; a passing repository test is not a runtime result.</p>");
        foreach (var p in r.ProposedTests) sb.Append($"<h3>Proposed regression: {esc(p.Name)}</h3><p>{esc(p.Purpose)}</p><pre>{esc(p.Code)}</pre>");
        sb.Append("</section>\n<section class=\"block\"><h2>Missing evidence</h2><ul>");
        if (r.Readiness.Count > 0)
            foreach (var m in ClassificationPresentation.GroupOrder.SelectMany(g => r.Readiness.Where(x => x.Group == g))) sb.Append($"<li>{esc(ClassificationLabels.MissingGroup(m.Group))}: <strong>{esc(m.Title)}</strong> — {esc(ClassificationLabels.Readiness(m.Status))}. {esc(m.Detail)}</li>");
        else if (r.MissingItems.Count > 0)
            foreach (var m in r.MissingItems) sb.Append($"<li>{esc(ClassificationLabels.MissingGroup(m.Group))}: <strong>{esc(m.Title)}</strong> — {esc(m.Detail)}</li>");
        else foreach (var m in r.Missing) sb.Append($"<li>{esc(m)}</li>");
        sb.Append("</ul><h2>Limitations</h2><ul>");
        foreach (var l in r.Limitations) sb.Append($"<li>{esc(l)}</li>");
        sb.Append("</ul></section>\n");
        return buildHtml("Security Classification Review", null, $"Environment {r.EnvironmentId} · run {r.RunId}", sb.ToString());
    }
}
