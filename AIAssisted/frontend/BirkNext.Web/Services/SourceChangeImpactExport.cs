using System.Net;
using BirkNext.SourceImpact;

namespace BirkNext.Web.Services;

public static class SourceChangeImpactExport
{
    public static string UnifiedHtml(ImpactAnalysisRunReport report)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var findings = string.Join("", report.Findings.Select(x => $"<li><strong>{E(x.DisplayName)}</strong> — {E(x.Classification.ToString())} · {E(x.VerificationState)}<p>{E(x.Reason)}</p><ul>{string.Join("", x.Evidence.Select(e => $"<li>{E(e.Kind)}: {E(e.Description)}{(e.SourcePath is null ? "" : $" ({E(e.SourcePath)})")}</li>"))}</ul></li>"));
        var domains = string.Join("", report.DomainAssessments.Select(x => $"<li><strong>{E(x.Domain)}</strong>: {E(x.Status.ToString())} — {E(x.Reason)}</li>"));
        var limitations = string.Join("", report.Limitations.Select(x => $"<li>{E(x)}</li>"));
        var baseline = report.ChangeSet.BaselineSnapshotId?.ToString() ?? "Not selected";
        var current = report.ChangeSet.CurrentSnapshotId?.ToString() ?? "Not selected";
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Impact Analysis</title><style>body{{font:16px system-ui;max-width:1000px;margin:2rem auto;padding:0 1rem;color:#172033}}li{{margin:.5rem 0}}code{{overflow-wrap:anywhere}}</style></head><body><h1>Impact Analysis</h1><h2>{E(report.ProjectDisplayName)}</h2><p>Project identity: {E(report.ProjectId)} · Import: {E(report.ProjectImportId ?? "None")}</p><p>Change source: {E(report.ChangeSet.ChangeOrigin)}</p><p>Baseline: {E(baseline)} · fingerprint {E(report.ChangeSet.BaselineFingerprint ?? "Not available")}</p><p>Current: {E(current)} · fingerprint {E(report.ChangeSet.CurrentFingerprint ?? "Not available")}</p><h2>What may be affected and why</h2><ul>{findings}</ul><h2>Evidence domains</h2><ul>{domains}</ul><h2>What is not known</h2><ul>{limitations}</ul><p>Linked evidence does not prove test execution, runtime behavior, or a failure.</p></body></html>";
    }

    public static string UnifiedMarkdown(ImpactAnalysisRunReport report)
    {
        var findings = report.Findings.Count == 0 ? "- No impact findings were established from the selected evidence." : string.Join("\n", report.Findings.Select(x => $"- **{x.DisplayName}** — {x.Classification} ({x.VerificationState}): {x.Reason}\n  {string.Join("; ", x.Evidence.Select(e => $"{e.Kind}: {e.Description}{(e.SourcePath is null ? "" : $" ({e.SourcePath})")}"))}"));
        var domains = string.Join("\n", report.DomainAssessments.Select(x => $"- **{x.Domain}: {x.Status}** — {x.Reason}"));
        var limitations = report.Limitations.Count == 0 ? "- None recorded." : string.Join("\n", report.Limitations.Select(x => $"- {x}"));
        return $"# Impact Analysis\n\nProject: {report.ProjectDisplayName} (`{report.ProjectId}`)\n\nImport: {report.ProjectImportId ?? "None"}\n\nChange source: {report.ChangeSet.ChangeOrigin}\n\nBaseline snapshot: {report.ChangeSet.BaselineSnapshotId?.ToString() ?? "Not selected"} ({report.ChangeSet.BaselineFingerprint ?? "Not available"})\n\nCurrent snapshot: {report.ChangeSet.CurrentSnapshotId?.ToString() ?? "Not selected"} ({report.ChangeSet.CurrentFingerprint ?? "Not available"})\n\n## What may be affected and why\n\n{findings}\n\n## Evidence domains\n\n{domains}\n\n## What is not known\n\n{limitations}\n\nLinked evidence does not prove test execution, runtime behavior, or a failure.\n";
    }

    public static string Html(SourceChangeImpactReport report)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var changes = string.Join("", report.Changes.Select(x => $"<li>{E(x.Domain.ToString())} · {E(x.Kind.ToString())} · {E(x.Name)}: {E(x.Detail)}</li>"));
        var impacts = string.Join("", report.TechnicalImpacts.Select(x => $"<li><strong>{E(x.DisplayName)}</strong> — {E(x.Level.ToString())}: {E(x.Reason)}</li>"));
        var tests = string.Join("", report.RecommendedTests.Select(x => $"<li>{E(x.Title)}: {E(x.Reason)}</li>"));
        var journeys = string.Join("", report.Journeys.Select(x => $"<li>{E(x.DisplayName)}: {E(x.ImpactedSection)}</li>"));
        var reviews = string.Join("", report.SecurityAndConfigurationReviews.Select(x => $"<li>{E(x)}</li>"));
        var limitations = string.Join("", report.Limitations.Select(x => $"<li>{E(x)}</li>"));
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Impact Analysis</title><style>body{{font:16px system-ui;max-width:1000px;margin:2rem auto;padding:0 1rem;color:#172033}}li{{margin:.5rem 0}}code{{overflow-wrap:anywhere}}</style></head><body><h1>Impact Analysis</h1><h2>{E(report.ProjectDisplayName ?? report.TargetRepository)}</h2><p>Change: source snapshot comparison</p><p>Baseline {E(report.BaselineSnapshotId.ToString())} · {E(report.BaselineFingerprint)}</p><p>Current {E(report.TargetSnapshotId.ToString())} · {E(report.TargetFingerprint)}</p><h2>Changes</h2><ul>{changes}</ul><h2>What may be affected</h2><ul>{impacts}</ul><h2>Tests to review</h2><ul>{tests}</ul><h2>Journeys to review</h2><ul>{journeys}</ul><h2>Security and configuration review</h2><ul>{reviews}</ul><h2>What is not known</h2><ul>{limitations}</ul><p>Source relationships do not verify deployed or runtime behavior.</p></body></html>";
    }

    public static string Markdown(SourceChangeImpactReport report) => $"# Impact Analysis\n\nProject: {report.ProjectDisplayName ?? report.TargetRepository}\n\nChange: Source snapshot comparison\n\nBaseline: {report.BaselineSnapshotId} ({report.BaselineFingerprint})\n\nCurrent: {report.TargetSnapshotId} ({report.TargetFingerprint})\n\n## Changes\n{string.Join("\n", report.Changes.Select(x => $"- {x.Domain} · {x.Kind} · {x.Name}: {x.Detail}"))}\n\n## What may be affected\n{string.Join("\n", report.TechnicalImpacts.Select(x => $"- **{x.DisplayName}** — {x.Level}: {x.Reason}"))}\n\n## Tests to review\n{string.Join("\n", report.RecommendedTests.Select(x => $"- {x.Title}: {x.Reason}"))}\n\n## Journeys to review\n{string.Join("\n", report.Journeys.Select(x => $"- {x.DisplayName}: {x.ImpactedSection}"))}\n\n## Security and configuration\n{string.Join("\n", report.SecurityAndConfigurationReviews.Select(x => $"- {x}"))}\n\n## What is not known\n{string.Join("\n", report.Limitations.Select(x => $"- {x}"))}\n\nSource relationships do not verify deployed or runtime behavior.\n";
}
