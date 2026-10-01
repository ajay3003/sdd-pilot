using System.Text;
using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of a Dependency Review run from its stored snapshot: coverage, config files and hashes, managers, findings, rules, the synthetic
/// simulation matrix and the limitations. Secrets were redacted by the backend before storage; no source file content is included.
/// </summary>
public static class DependencyReviewExport
{
    public static string Build(DependencyReviewResult result, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"block\"><h2>Summary</h2>");
        sb.Append($"<p>{esc(DependencyReviewPresentation.Headline(result))}. Completed {result.CompletedAt:u}.</p>");
        sb.Append(table(["Category", "State", "Detail"], result.Categories.Select(c => new[] { esc(c.Name), badge(DependencyLabels.Category(c.State)), esc(c.Detail) })));
        sb.Append($"<p><strong>Evaluation mechanism:</strong> {esc(result.EvaluationMechanism)}</p><ul>");
        foreach (var limitation in result.UnsupportedSemantics) sb.Append($"<li>{esc(limitation)}</li>");
        sb.Append("</ul><p>Simulated versions are synthetic candidates for testing policy only — not observed, available or published versions. The source review uses no vulnerability source: nothing here is a vulnerability status (advisories are checked in the dependency health review).</p></section>\n");

        // The exact source scope (every snapshot, not only the primary) and cross-source observations.
        sb.Append("<section class=\"block\"><h2>Source scope</h2>");
        if (result.SourceScope is { } scope)
        {
            sb.Append(table(["Role", "Repository", "Archive", "Fingerprint", "Source Analysis", "Analyzed"], new[] { ("Primary", scope.Primary) }.Concat(scope.Related.Select(r => ("Related", r)))
                .Select(x => new[] { esc(x.Item1), esc(x.Item2.Repository), esc(x.Item2.ArchiveName), esc(x.Item2.Fingerprint), esc(x.Item2.SourceStatus), x.Item2.AnalyzedAt.ToString("u") })));
            foreach (var limitation in scope.Limitations) sb.Append($"<p>{esc(limitation)}</p>");
        }
        else sb.Append("<p>Legacy source input: repository archives uploaded directly to Dependency Review. No Source Analysis snapshot is linked.</p>");
        if (result.CrossSource.Count > 0)
        {
            sb.Append("<h3>Cross-source differences</h3><p>Observations to review — not failures, vulnerabilities or incompatibilities.</p>");
            sb.Append(table(["Dependency", "Manager", "Declared per source", "State"], result.CrossSource.Select(o => new[] { esc(o.PackageName), esc(o.Manager), esc(string.Join("; ", o.Values.Select(v => $"{v.Repository}: {v.Value}"))), badge(o.Kind) })));
        }
        foreach (var r in result.SourceRelationships) sb.Append($"<p>{esc(r.FromRepository)} references {esc(r.PackageName)} {esc(string.Join(", ", r.ReferencedValues))} — produced by {esc(r.ToRepository)}{(r.PublishedVersion is { } v ? esc($" (declares {v})") : "")}.</p>");
        sb.Append("</section>\n");

        foreach (var repo in result.Repositories)
        {
            sb.Append($"<section class=\"block\"><h2>{esc(repo.Repository)}</h2>");
            sb.Append($"<p>Renovate coverage {badge(DependencyReviewPresentation.Coverage(repo.Coverage))} {esc(repo.CoverageDetail)} Archive sha256 {esc(DependencyReviewPresentation.Short(repo.ArchiveSha256))}; config hash {esc(DependencyReviewPresentation.Short(repo.ConfigHash))}.</p>");
            if (repo.DriftSincePrevious is { } drift) sb.Append($"<p><strong>Drift:</strong> {esc(drift)}</p>");
            if (repo.ConfigFiles.Count > 0)
                sb.Append(table(["Config file", "Read by Renovate", "Syntax", "Note"], repo.ConfigFiles.Select(f => new[] { esc(f.Path), f.Used ? "Yes" : "No", f.SyntaxValid ? "Valid" : esc(f.SyntaxError ?? "Invalid"), esc(f.Note) })));
            sb.Append("<h3>Managers</h3>");
            sb.Append(table(["Manager", "Files", "Declared dependencies", "State", "Detail"], repo.Managers.Select(m => new[] { esc(m.Manager), m.Files.ToString(), m.Dependencies.ToString(), badge(DependencyLabels.Manager(m.State)), esc(m.Detail) })));
            sb.Append("<h3>Findings</h3>");
            sb.Append(repo.Findings.Count == 0 ? "<p>No findings.</p>" : table(["Severity", "Finding", "Detail", "Evidence"],
                DependencyReviewPresentation.OrderedFindings(repo).Select(f => new[] { badge(DependencyLabels.Severity(f.Severity)), esc(f.Title), esc(f.Detail), esc(string.Join(" · ", f.Evidence)) })));
            if (repo.Rules.Count > 0)
            {
                sb.Append("<h3>packageRules</h3>");
                sb.Append(table(["#", "Description", "Matchers", "Settings", "Matches", "Location"], repo.Rules.Select(r => new[]
                {
                    r.Index.ToString(), esc(r.Description), esc(string.Join("; ", r.Matchers.Select(m => $"{m.Key} [{string.Join(", ", m.Value)}]"))),
                    esc(string.Join("; ", r.Settings.Select(s => $"{s.Key}: {s.Value}"))), r.MatchedDependencies.ToString(), esc(r.Location is { } l ? $"{l.File}:{l.Line}" : ""),
                })));
            }
            if (repo.Simulations.Count > 0)
            {
                sb.Append("<h3>Policy simulation (synthetic candidates)</h3>");
                sb.Append(table(["Package", "File", "Current", "Scenario", DependencyLabels.SyntheticCandidate, "Update type", "Result", "Rules", "Why"], repo.Simulations.Select(s => new[]
                {
                    esc(s.PackageName), esc(s.OwnerFile), esc(s.CurrentValue), esc(s.Scenario), esc(s.CandidateVersion ?? "—"), esc(DependencyReviewPresentation.UpdateType(s.UpdateType)),
                    badge(DependencyReviewPresentation.Outcome(s)), esc(DependencyReviewPresentation.Rules(s)), esc(s.Explanation),
                })));
            }
            sb.Append($"<h3>Security-update policy</h3><p>{badge(DependencyLabels.Category(repo.SecurityUpdatePolicy.State))} {esc(repo.SecurityUpdatePolicy.Detail)}</p>");
            sb.Append($"<h3>Runtime automation</h3><p>{badge(DependencyLabels.Category(repo.RuntimeAutomation.State))} {esc(repo.RuntimeAutomation.Detail)}</p>");
            sb.Append("</section>\n");
        }
        return buildHtml("Dependency Review", null, $"{result.Label} · run {result.RunId}", sb.ToString());
    }
}
