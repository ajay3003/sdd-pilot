using System.Text;
using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of a stored dependency-health run, from the snapshot only (no lookup): review sources, inventory, health summary, registry,
/// advisories, licenses, supply-chain identifiers, Renovate policy, automation runtime, deployment and baseline comparisons, limitations and
/// findings. Provider texts were redacted by the backend before storage; no credential is part of a run.
/// </summary>
public static class DependencyHealthExport
{
    public static string Build(DependencyHealthRun run, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        var items = run.Items.OrderBy(i => i.Dependency.PackageName, StringComparer.OrdinalIgnoreCase).ToList();
        string Section(string title) => $"<section class=\"block\"><h2>{esc(title)}</h2>";

        sb.Append(Section("Review sources"));
        sb.Append($"<p>{esc(DependencyHealthPresentation.Headline(run))}. Completed {esc(DependencyHealthPresentation.Utc(run.CompletedAt))}{(run.RefreshOf is { } r ? $"; refresh of run {r} (unchanged)" : "")}. Stored snapshot — not live.</p>");
        sb.Append(table(["Check", "Source", "State", "Retrieved", "Detail"], run.Sources.Select(s => new[] { esc(DependencyHealthLabels.Category(s.Category)), esc(s.Name), badge(s.State), esc(DependencyHealthPresentation.Utc(s.RetrievedAt)), esc(s.Detail) })));
        sb.Append("</section>\n");

        sb.Append(Section("Inventory summary"));
        var inv = run.Inventory;
        sb.Append(table(["Inventory", "Source", "Stage", "Captured", "Freshness", "Environment / build", "Provenance"], [[
            esc(inv.Name), esc(DependencyHealthLabels.Source(inv.SourceType)), esc(DependencyHealthLabels.Stage(inv.Stage)), esc(DependencyHealthPresentation.Utc(inv.CapturedAt)),
            badge(run.Freshness.ToString()) + " " + esc(run.FreshnessDetail), esc(string.Join(" / ", new[] { inv.Environment, inv.BuildId, inv.ArtifactId }.OfType<string>())), esc(inv.Provenance)]]));
        sb.Append("</section>\n");

        sb.Append(Section("Dependency health"));
        sb.Append(table(["Category", "State", "Detail"], run.Categories.Select(c => new[] { esc(c.Name), badge(DependencyHealthPresentation.CategoryLabel(c.State)), esc(c.Detail) })));
        sb.Append("</section>\n");

        sb.Append(Section("Findings"));
        sb.Append(run.Observations.Count == 0 ? "<p>No findings, limitations or notable observations.</p>" : table(["Kind", "Category", "Severity", "Title", "Detail", "Evidence"],
            DependencyHealthPresentation.OrderedObservations(run).Select(o => new[]
            {
                badge(DependencyHealthLabels.Kind(o.Kind)), esc(DependencyHealthLabels.Category(o.Category)),
                esc(o.Kind is ObservationKind.Finding or ObservationKind.EvidenceConflict ? DependencyLabels.Severity(o.Severity) + (o.SourceSeverity is { } s ? $" (source: {s})" : "") : "—"),
                esc(o.Title), esc(o.Detail), esc(string.Join(" · ", o.Evidence.Where(e => e.Length > 0))),
            })));
        sb.Append("</section>\n");

        sb.Append(Section("Registry metadata"));
        sb.Append($"<p>{esc(DependencyHealthLabels.LatestStable)} is what the registry lists — not a recommended version. Age is evidence; no age threshold is applied.</p>");
        sb.Append(table(["Package", "Observed", "Source", DependencyHealthLabels.LatestStable, "Version status", "Observed published", "Age", "Registry", "Deprecated", "Retrieved"], items.Select(i => new[]
        {
            esc(i.Dependency.PackageName), esc(DependencyHealthPresentation.ObservedVersion(i.Dependency)), esc(DependencyHealthPresentation.SourceText(i.Dependency)), esc(i.Registry.LatestStable ?? "—"),
            esc(DependencyHealthLabels.Version(i.VersionStatus)), esc(DependencyHealthPresentation.Date(i.Registry.ObservedPublished)), esc(DependencyHealthPresentation.Age(i.ObservedAgeDays)),
            esc(DependencyHealthLabels.Registry(i.Registry.State)), esc(i.Registry.Deprecation is { } d ? string.Join(", ", d.Reasons) : "—"),
            esc($"{DependencyHealthPresentation.Utc(i.Registry.RetrievedAt)}{(i.Registry.FromCache ? " (cached)" : "")}"),
        })));
        sb.Append("</section>\n");

        sb.Append(Section("Security advisories"));
        sb.Append("<p>No matched advisory is not a safety claim; an unavailable advisory source is not zero vulnerabilities; exploitability is not assessed. A fixed version is evidence, not a tested upgrade.</p>");
        sb.Append(table(["Package", "Observed", "Advisory status", "Advisories", "Fixed in", "Source", "Retrieved"], items.Where(i => i.Security.State != AdvisoryState.NotAssessed).Select(i => new[]
        {
            esc(i.Dependency.PackageName), esc(DependencyHealthPresentation.ObservedVersion(i.Dependency)), badge(DependencyHealthLabels.Advisory(i.Security.State)),
            esc(string.Join("; ", i.Security.Advisories.Select(a => $"{a.Id}{(a.Aliases.Count > 0 ? $" ({string.Join(", ", a.Aliases)})" : "")}: {(a.AffectsObservedVersion switch { true => "affects observed", false => "does not affect observed", null => "not comparable" })}, severity {a.SourceSeverity ?? "not stated"}"))),
            esc(i.Security.FixedIn ?? "—"), esc(i.Security.Source), esc(DependencyHealthPresentation.Utc(i.Security.RetrievedAt)),
        })));
        sb.Append("</section>\n");

        sb.Append(Section("License metadata"));
        sb.Append(table(["Package", "License state", "License", "Sources", "Policy"], items.Select(i => new[]
        {
            esc(i.Dependency.PackageName), esc(DependencyHealthLabels.License(i.License.State)), esc(string.Join(" / ", i.License.Licenses)), esc(string.Join(", ", i.License.Sources)),
            esc(DependencyHealthLabels.LicensePolicy(i.License.Policy)),
        })));
        sb.Append("</section>\n");

        var identified = items.Where(i => i.Dependency.Purl is not null || i.Dependency.Hashes.Count > 0 || i.Dependency.Registry is not null).ToList();
        sb.Append(Section("Supply-chain metadata"));
        sb.Append(identified.Count == 0 ? "<p>The inventory carries no package URLs, hashes or package-source metadata.</p>" : table(["Package", "purl", "CPE", "Hashes", "Package source", "Publisher"], identified.Select(i => new[]
        {
            esc(i.Dependency.PackageName), esc(i.Dependency.Purl ?? "—"), esc(i.Dependency.Cpe ?? "—"), esc(string.Join(" · ", i.Dependency.Hashes.Select(h => $"{h.Algorithm} {h.Value}"))),
            esc(i.Dependency.Registry ?? "—"), esc(i.Dependency.Publisher ?? "—"),
        })));
        sb.Append("</section>\n");

        sb.Append(Section("Renovate policy"));
        var remediations = items.Where(i => i.Remediation is not null).ToList();
        sb.Append($"<p>{esc(run.PolicySource ?? "Not assessed — no source/config selected.")} The configuration is never changed.</p>");
        if (remediations.Count > 0)
            sb.Append(table(["Package", "Fixed version", "Result", "Explanation"], remediations.Select(i => new[]
                { esc(i.Dependency.PackageName), esc(i.Remediation!.FixedVersion ?? "—"), badge(DependencyHealthLabels.Remediation(i.Remediation.State)), esc(i.Remediation.Explanation) })));
        sb.Append("</section>\n");

        var a = run.Automation;
        sb.Append(Section("Automation runtime"));
        sb.Append($"<p>{badge(DependencyHealthLabels.Automation(a.State))} {esc(a.Provider)} — {esc(a.Detail)}</p><p>{esc(a.StaticConfiguration ?? "Static Renovate configuration: not assessed in this run.")}</p>");
        if (a.State == AutomationState.Observed)
        {
            sb.Append($"<p>Last run: {esc(a.LastRun is { } lr ? $"{lr.Id} {lr.Result} {DependencyHealthPresentation.Utc(lr.FinishedAt ?? lr.StartedAt)}" : "none observed")}; last successful: {esc(a.LastSuccessfulRun is { } ls ? $"{ls.Id} {DependencyHealthPresentation.Utc(ls.FinishedAt ?? ls.StartedAt)}" : "none observed")}; Dependency Dashboard: {esc(a.DependencyDashboard)}.</p>");
            sb.Append(table(["Package", "Current → proposed", "Update type", "Age (days)", "State", "Security-marked", "Merge status"], a.OpenPullRequests.Select(p => new[]
            {
                esc(p.PackageName ?? p.Title), esc(DependencyHealthPresentation.CurrentToProposed(p)), esc(p.UpdateType?.ToString() ?? "Not assessed"), esc(p.AgeDays?.ToString() ?? "Unknown"),
                esc($"Observed · {p.Status}"), p.SecurityMarked ? "Yes (PR says so)" : "No", esc(p.MergeStatus ?? "Not provided"),
            })));
        }
        sb.Append("</section>\n");

        foreach (var kind in new[] { ComparisonKind.ExpectedVsDeployed, ComparisonKind.SourceVsSbom, ComparisonKind.Baseline })
        {
            var title = kind switch { ComparisonKind.ExpectedVsDeployed => "Deployment evidence", ComparisonKind.SourceVsSbom => "Source ↔ SBOM", _ => "Drift / baseline" };
            sb.Append(Section(title));
            var comparison = run.Comparisons.FirstOrDefault(c => c.Kind == kind);
            if (comparison is null) { sb.Append("<p>Not assessed.</p></section>\n"); continue; }
            sb.Append($"<p>{esc(comparison.LeftName)} vs {esc(comparison.RightName)} · {comparison.Unchanged} unchanged/matched.</p>");
            foreach (var limitation in comparison.Limitations) sb.Append($"<p>{esc(limitation)}</p>");
            sb.Append(table(["Package", "State", comparison.LeftName, comparison.RightName, "Hash", "Detail"], comparison.Entries.Select(e => new[]
                { esc(e.PackageName), badge(DependencyHealthLabels.Comparison(e.State)), esc(e.LeftValue ?? "—"), esc(e.RightValue ?? "—"), esc(e.Hash == HashComparison.NotAvailable ? "Not available" : e.Hash.ToString()), esc(e.Detail) })));
            sb.Append("</section>\n");
        }

        sb.Append(Section("Limitations"));
        sb.Append("<ul>");
        foreach (var limitation in run.Limitations) sb.Append($"<li>{esc(limitation)}</li>");
        sb.Append("</ul></section>\n");
        return buildHtml("Dependency health review", null, $"{run.Label} · run {run.RunId}", sb.ToString());
    }
}
