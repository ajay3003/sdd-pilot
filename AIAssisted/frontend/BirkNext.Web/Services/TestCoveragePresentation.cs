using System.Net;
using System.Text;
using BirkNext.TestCoverage;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Plain-language mapping for the Test Coverage &amp; Overlap Review and the browser-owned workspace evidence it sends (requirements, acceptance
/// scenarios as QA test candidates, imported executions). Internal enums never reach the default view; technical names stay in details.
/// </summary>
public static class TestCoveragePresentation
{
    public static CoverageWorkspaceEvidence WorkspaceEvidence(IWorkspaceSessionService workspace, ReviewContext? context, string? projectName)
    {
        var spec = context?.Specification;
        var requirements = (spec?.Requirements ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Id)).GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First())
            .Select(r => new CoverageRequirement(r.Id, r.Text, null)).ToList();
        var scenarios = (spec?.AcceptanceScenarios ?? []).Select((s, i) => new QaTestCandidate
        {
            Id = string.IsNullOrWhiteSpace(s.Id) ? $"scenario-{i + 1}" : s.Id!, Title = string.IsNullOrWhiteSpace(s.Title) ? s.When ?? $"Scenario {i + 1}" : s.Title,
            Source = "Acceptance scenario in the specification", Ownership = TestOwnership.QaManual,
            RequirementIds = s.LinkedRequirements.Select(r => r.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList(),
            Given = s.Given, When = s.When, Then = s.Then,
        }).ToList();
        var executions = workspace.SddLifecycle.TestExecutions.Select(e => new CoverageExecution(e.TestId, e.TestName, e.Result)).ToList();
        return new CoverageWorkspaceEvidence
        {
            ProjectName = projectName, SpecificationAvailable = workspace.Specification is not null, Requirements = requirements, AcceptanceScenarios = scenarios, Executions = executions,
        };
    }

    public static string Step(StepCoverage coverage) => coverage switch
    {
        StepCoverage.Covered => "Covered", StepCoverage.PartlyCovered => "Partly covered", StepCoverage.MockOnly => "Covered by mocks only",
        StepCoverage.LowerLevelOnly => "Covered at a lower level only", _ => "Not verified",
    };

    public static string StepTone(StepCoverage coverage) => coverage switch
    {
        StepCoverage.Covered => "clear", StepCoverage.PartlyCovered or StepCoverage.LowerLevelOnly => "partial", StepCoverage.MockOnly => "attention", _ => "neutral",
    };

    public static string Connection(ConnectionEvidence evidence, string? reviewer) => reviewer switch
    {
        "Confirmed" => $"Confirmed by reviewer (source: {ConnectionSource(evidence)})",
        "Rejected" => $"Rejected by reviewer (source: {ConnectionSource(evidence)})",
        _ => ConnectionSource(evidence),
    };

    private static string ConnectionSource(ConnectionEvidence evidence) => evidence switch
    {
        ConnectionEvidence.Confirmed => "Confirmed in source", ConnectionEvidence.StronglySupported => "Strongly supported by source",
        ConnectionEvidence.Suggested => "Suggested — needs confirmation", ConnectionEvidence.NeedsConfirmation => "Needs confirmation", _ => "Unsupported",
    };

    public static string Dimension(DimensionStatus status) => status switch
    {
        DimensionStatus.Strong => "Covered", DimensionStatus.Some => "Some evidence", DimensionStatus.Partial => "Partly covered",
        DimensionStatus.MissingEvidence => "Not verified", _ => "Not assessed",
    };

    public static string DimensionTone(DimensionStatus status) => status switch
    {
        DimensionStatus.Strong => "clear", DimensionStatus.Some => "partial", DimensionStatus.Partial => "attention", DimensionStatus.MissingEvidence => "neutral", _ => "muted",
    };

    public static string Overlap(OverlapKind kind) => kind switch
    {
        OverlapKind.HighConfidenceOverlap => "Possible duplicate test", OverlapKind.PartialOverlap => "Partly overlapping", OverlapKind.ComplementaryCoverage => "Adds coverage",
        OverlapKind.DifferentBoundary => "Different level — not a duplicate", OverlapKind.NeedsTesterReview => "Needs tester review", _ => "Not enough information",
    };

    public static string Strength(EvidenceStrength strength) => strength switch
    {
        EvidenceStrength.Strong => "Strong evidence", EvidenceStrength.Some => "Some evidence", EvidenceStrength.Weak => "Weak evidence", _ => "Not enough information",
    };

    public static string Execution(TestExecutionState state) => state switch
    {
        TestExecutionState.Passed => "Executed — passed", TestExecutionState.Failed => "Executed — failed", TestExecutionState.Skipped => "Skipped when run",
        TestExecutionState.NotExecuted => "Not executed", _ => "Execution not verified",
    };

    public static string Mode(BoundaryMode mode) => mode switch
    {
        BoundaryMode.Real => "real", BoundaryMode.RealContainer => "real engine in a container", BoundaryMode.InProcess => "in-process host", BoundaryMode.InMemory => "in-memory substitute",
        BoundaryMode.Mocked => "mocked", BoundaryMode.Fake => "fake", _ => "unknown",
    };

    public static string Level(TestLevel level) => level switch
    {
        TestLevel.Unit => "Unit", TestLevel.Component => "Component", TestLevel.Repository => "Database / repository", TestLevel.Contract => "Contract", TestLevel.Integration => "Integration",
        TestLevel.Api => "API", TestLevel.Ui => "UI component", TestLevel.E2E => "End-to-end", TestLevel.Security => "Security", TestLevel.Performance => "Performance",
        TestLevel.Runtime => "Runtime", TestLevel.NeedsReview => "Needs review", _ => "Unknown",
    };

    public static string? RunBlockedReason(Guid? currentId, bool busy) =>
        busy ? "A review is running." : currentId is null ? TestCoverageText.SourceRequired : null;

    // ── documentation (Markdown + HTML) ───────────────────────────────────────────────────────────────────────────

    public static string FileName(TestCoverageReviewResult r, string kind, string extension) =>
        $"{kind}-{Safe(r.ProjectName ?? r.Current.Repository)}-{r.CompletedAt:yyyyMMdd-HHmm}.{extension}";

    private static string Safe(string text) => new(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    /// <summary>Covered Tests &amp; Remaining QA Scope as Markdown (wiki, Azure DevOps, Loop). Simple language first; technical details at the end.</summary>
    public static string Markdown(TestCoverageReviewResult r, bool coveredOnly = false, bool scopeOnly = false)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {(scopeOnly ? "Remaining QA Scope" : "Covered Tests & Remaining QA Scope")} — {r.ProjectName ?? r.Current.Repository}");
        md.AppendLine();
        md.AppendLine($"Source snapshot: {r.Current.ArchiveName} ({Short(r.Current.Fingerprint)}), analyzed {r.Current.AnalyzedAt:yyyy-MM-dd HH:mm} UTC. Generated {r.CompletedAt:yyyy-MM-dd HH:mm} UTC.");
        if (r.Baseline is { } b) md.AppendLine($"Baseline snapshot: {b.ArchiveName} ({Short(b.Fingerprint)}).");
        md.AppendLine();
        md.AppendLine($"> {TestCoverageText.Subtext} {TestCoverageText.NoCertification}");
        md.AppendLine();
        if (!scopeOnly)
        {
            md.AppendLine("## Summary");
            md.AppendLine($"- Tests found: {r.Inventory.TestsDiscovered} ({r.Inventory.TestsAnalyzed} analyzed, {r.Inventory.TestsWithExecutionEvidence} with execution results)");
            md.AppendLine($"- {r.Inventory.ExecutionEvidence}");
            md.AppendLine($"- {r.Inventory.CodeCoverage}");
            md.AppendLine($"- Journeys found: {r.Journeys.Count}; possible duplicate QA tests: {r.Overlaps.Count(o => o.Kind == OverlapKind.HighConfidenceOverlap)}; remaining gaps: {r.Gaps.Count}");
            md.AppendLine();
            md.AppendLine("## By journey");
            foreach (var j in r.Journeys.Take(20))
            {
                md.AppendLine($"### {j.Title}");
                foreach (var s in j.Steps)
                    md.AppendLine($"- {s.FromName} → {s.ToName} ({TestCoverageReviewText.Boundary(s.Boundary)}): developer tests **{Step(s.Developer)}**; QA {Step(s.Qa).ToLowerInvariant()}; runtime {s.Runtime.ToLowerInvariant()}. Connection: {Connection(s.Evidence, s.ReviewerDecision)}.");
                if (j.FocusForTesters.Count > 0) md.AppendLine($"- What testers should focus on: {string.Join("; ", j.FocusForTesters)}");
                md.AppendLine();
            }
            md.AppendLine("## By component");
            foreach (var c in r.Components)
            {
                md.AppendLine($"### {c.Name}");
                md.AppendLine(string.Join("  \n", c.Dimensions.Where(d => d.Status != DimensionStatus.NotAssessed).Select(d => $"- {d.Dimension}: **{Dimension(d.Status)}** — {d.Explanation}")));
                md.AppendLine();
            }
            md.AppendLine("## By requirement or behaviour");
            foreach (var bh in r.Behaviors.Take(coveredOnly ? 200 : 120))
            {
                md.AppendLine($"### {bh.Title} — {bh.Status}");
                if (bh.AlreadyTested.Count > 0) { md.AppendLine("What is already tested"); foreach (var t in bh.AlreadyTested) md.AppendLine($"- {t}"); }
                if (bh.WhereTested.Count > 0) { md.AppendLine("Where it is tested"); foreach (var t in bh.WhereTested) md.AppendLine($"- {t}"); }
                if (bh.NotVerifiedYet.Count > 0) { md.AppendLine("What is not verified yet"); foreach (var t in bh.NotVerifiedYet) md.AppendLine($"- {t}"); }
                md.AppendLine($"Recommendation for testers: {bh.ForTesters}");
                md.AppendLine();
            }
        }
        if (!coveredOnly)
        {
            md.AppendLine("## Remaining QA scope");
            md.AppendLine("### What testers should focus on");
            foreach (var k in r.QaScope.Keep) md.AppendLine($"- {k}");
            md.AppendLine("### Tests testers probably do not need to repeat");
            foreach (var k in r.QaScope.ProbablyUnnecessary) md.AppendLine($"- {k}");
            if (r.QaScope.ProbablyUnnecessary.Count == 0) md.AppendLine("- None identified from the current evidence.");
            md.AppendLine("### Needs tester review");
            foreach (var k in r.QaScope.NeedsReview) md.AppendLine($"- {k}");
            md.AppendLine();
            md.AppendLine("## Possible duplicate tests");
            foreach (var o in r.Overlaps.Where(o => o.Kind is OverlapKind.HighConfidenceOverlap or OverlapKind.PartialOverlap or OverlapKind.DifferentBoundary))
                md.AppendLine($"- {o.QaTitle}: **{Overlap(o.Kind)}** ({Strength(o.Strength)}). {o.Recommendation} {o.RemainingRisk}");
            md.AppendLine();
            md.AppendLine("## Remaining gaps");
            foreach (var g in r.Gaps.Take(80)) md.AppendLine($"- {g.Attention}: {g.Title}. {g.Explanation}");
        }
        md.AppendLine();
        md.AppendLine("## Technical details");
        md.AppendLine("| Test | Level | Execution | Boundaries | Location |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var t in r.Tests.Take(400))
            md.AppendLine($"| {Cell(t.Name)} | {Level(t.Level)} | {Execution(t.Execution)} | {Cell(string.Join(", ", t.Boundaries.Select(b => $"{TestCoverageReviewText.Boundary(b.Kind)} ({Mode(b.Mode)})")))} | {Cell(t.FilePath)}:{t.Line} |");
        md.AppendLine();
        md.AppendLine("## Limitations");
        foreach (var l in r.Limitations.Concat(TestCoverageReviewText.Limitations)) md.AppendLine($"- {l}");
        return md.ToString();
    }

    public static string Html(TestCoverageReviewResult r, bool coveredOnly = false)
    {
        string E(string? t) => WebUtility.HtmlEncode(t ?? "");
        var h = new StringBuilder();
        h.Append($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{E(coveredOnly ? "Covered Tests" : "Test Coverage & Overlap Review")} — {E(r.ProjectName ?? r.Current.Repository)}</title>");
        h.Append("<style>body{font-family:system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#1f2937;line-height:1.5}h1{font-size:1.6rem}h2{border-bottom:1px solid #e5e7eb;padding-bottom:.25rem;margin-top:2rem}")
            .Append(".s{display:inline-block;padding:0 .4rem;border-radius:4px;font-size:.85rem;background:#f3f4f6;color:#374151}.clear{background:#dcfce7;color:#14532d}.partial{background:#fef9c3;color:#713f12}.attention{background:#fee2e2;color:#7f1d1d}")
            .Append("table{border-collapse:collapse;width:100%;font-size:.85rem}td,th{border:1px solid #e5e7eb;padding:.25rem .4rem;text-align:left;vertical-align:top;overflow-wrap:anywhere}details{margin:.5rem 0}.note{background:#f9fafb;border-left:3px solid #9ca3af;padding:.5rem .75rem}</style></head><body>");
        h.Append($"<h1>{E(coveredOnly ? "Covered Tests" : "Covered Tests & Remaining QA Scope")} — {E(r.ProjectName ?? r.Current.Repository)}</h1>");
        h.Append($"<p>Source snapshot <strong>{E(r.Current.ArchiveName)}</strong> ({E(Short(r.Current.Fingerprint))}), analyzed {r.Current.AnalyzedAt:yyyy-MM-dd HH:mm} UTC · generated {r.CompletedAt:yyyy-MM-dd HH:mm} UTC");
        if (r.Baseline is { } b) h.Append($" · baseline {E(b.ArchiveName)} ({E(Short(b.Fingerprint))})");
        h.Append($"</p><p class=\"note\">{E(TestCoverageText.Subtext)} {E(TestCoverageText.NoCertification)}</p>");
        h.Append($"<h2>Summary</h2><ul><li>Tests found: {r.Inventory.TestsDiscovered} ({r.Inventory.TestsAnalyzed} analyzed, {r.Inventory.TestsWithExecutionEvidence} with execution results)</li><li>{E(r.Inventory.ExecutionEvidence)}</li><li>{E(r.Inventory.CodeCoverage)}</li></ul>");
        h.Append("<h2>By journey</h2>");
        foreach (var j in r.Journeys.Take(20))
        {
            h.Append($"<h3>{E(j.Title)}</h3><ol>");
            foreach (var s in j.Steps)
                h.Append($"<li>{E(s.FromName)} → {E(s.ToName)} ({E(TestCoverageReviewText.Boundary(s.Boundary))}): <span class=\"s {StepTone(s.Developer)}\">{E(Step(s.Developer))}</span> — {E(s.DeveloperExplanation)}<details><summary>Technical details</summary><p>Connection: {E(Connection(s.Evidence, s.ReviewerDecision))}. QA: {E(s.QaExplanation)} Runtime: {E(s.Runtime)}.</p><ul>{string.Concat(s.Provenance.Select(p => $"<li>{E(p)}</li>"))}</ul></details></li>");
            h.Append("</ol>");
            if (j.FocusForTesters.Count > 0) h.Append($"<p><strong>What testers should focus on:</strong> {E(string.Join("; ", j.FocusForTesters))}</p>");
        }
        h.Append("<h2>By component</h2><table><tr><th>Component</th><th>Coverage</th></tr>");
        foreach (var c in r.Components)
            h.Append($"<tr><td>{E(c.Name)} ({c.Tests} tests)</td><td>{string.Join(" ", c.Dimensions.Where(d => d.Status != DimensionStatus.NotAssessed).Select(d => $"<span class=\"s {DimensionTone(d.Status)}\" title=\"{E(d.Explanation)}\">{E(d.Dimension)}: {E(Dimension(d.Status))}</span>"))}</td></tr>");
        h.Append("</table><h2>By requirement or behaviour</h2>");
        foreach (var bh in r.Behaviors.Take(150))
        {
            h.Append($"<h3>{E(bh.Title)} <span class=\"s\">{E(bh.Status)}</span></h3>");
            if (bh.AlreadyTested.Count > 0) h.Append($"<p><strong>What is already tested</strong></p><ul>{string.Concat(bh.AlreadyTested.Select(t => $"<li>{E(t)}</li>"))}</ul>");
            if (bh.WhereTested.Count > 0) h.Append($"<p><strong>Where it is tested</strong></p><ul>{string.Concat(bh.WhereTested.Select(t => $"<li>{E(t)}</li>"))}</ul>");
            if (bh.NotVerifiedYet.Count > 0) h.Append($"<p><strong>What is not verified yet</strong></p><ul>{string.Concat(bh.NotVerifiedYet.Select(t => $"<li>{E(t)}</li>"))}</ul>");
            h.Append($"<p><strong>Recommendation for testers:</strong> {E(bh.ForTesters)}</p><details><summary>Why BirkNext says this</summary><ul>{string.Concat(bh.Why.Select(t => $"<li>{E(t)}</li>"))}</ul></details>");
        }
        if (!coveredOnly)
        {
            h.Append("<h2>Remaining QA scope</h2><h3>What testers should focus on</h3><ul>").Append(string.Concat(r.QaScope.Keep.Select(k => $"<li>{E(k)}</li>"))).Append("</ul>");
            h.Append("<h3>Tests testers probably do not need to repeat</h3><ul>").Append(r.QaScope.ProbablyUnnecessary.Count == 0 ? "<li>None identified from the current evidence.</li>" : string.Concat(r.QaScope.ProbablyUnnecessary.Select(k => $"<li>{E(k)}</li>"))).Append("</ul>");
            h.Append("<h3>Needs tester review</h3><ul>").Append(string.Concat(r.QaScope.NeedsReview.Select(k => $"<li>{E(k)}</li>"))).Append("</ul>");
            h.Append("<h2>Possible duplicate tests</h2><ul>");
            foreach (var o in r.Overlaps) h.Append($"<li><strong>{E(o.QaTitle)}</strong>: {E(Overlap(o.Kind))} ({E(Strength(o.Strength))}). {E(o.Recommendation)} {E(o.RemainingRisk)}</li>");
            h.Append("</ul><h2>Remaining gaps</h2><ul>");
            foreach (var g in r.Gaps.Take(100)) h.Append($"<li>{E(g.Attention)}: {E(g.Title)}. {E(g.Explanation)}</li>");
            h.Append("</ul>");
        }
        h.Append("<h2>Technical details</h2><details><summary>All tests</summary><table><tr><th>Test</th><th>Level</th><th>Execution</th><th>Boundaries</th><th>Location</th></tr>");
        foreach (var t in r.Tests.Take(600))
            h.Append($"<tr><td>{E(t.Name)}</td><td>{E(Level(t.Level))}</td><td>{E(Execution(t.Execution))}</td><td>{E(string.Join(", ", t.Boundaries.Select(x => $"{TestCoverageReviewText.Boundary(x.Kind)} ({Mode(x.Mode)})")))}</td><td>{E(t.FilePath)}:{t.Line}</td></tr>");
        h.Append("</table></details><h2>Limitations</h2><ul>").Append(string.Concat(r.Limitations.Concat(TestCoverageReviewText.Limitations).Select(l => $"<li>{E(l)}</li>"))).Append("</ul></body></html>");
        return h.ToString();
    }

    private static string Short(string fingerprint) => fingerprint.Length > 12 ? fingerprint[..12] : fingerprint;
    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}

public static class TestCoverageReviewText
{
    public static readonly IReadOnlyList<string> Limitations =
    [
        "What a test intends cannot always be proven automatically; descriptions say what a test appears to check.",
        "Mocks and fakes are recognised by type names in C# test code only; other languages are counted, not analyzed.",
        "Runtime coverage needs runtime evidence; without imported execution results, test execution is not verified.",
        "Journey connections may need confirmation; a name match is never treated as a confirmed connection.",
        "Manual tests that are not in the specification or another machine-readable form are not seen.",
        "A code coverage percentage needs a real coverage report; none is computed from test counts.",
    ];

    public static string Boundary(BoundaryKind kind) => kind switch
    {
        BoundaryKind.Http => "HTTP", BoundaryKind.GraphQl => "GraphQL", BoundaryKind.Database => "database", BoundaryKind.EventHub => "Event Hub", BoundaryKind.ServiceBus => "Service Bus",
        BoundaryKind.Messaging => "messaging", BoundaryKind.Browser => "browser", BoundaryKind.FileSystem => "file system", BoundaryKind.ExternalService => "external system",
        BoundaryKind.Cdc => "change capture", BoundaryKind.Storage => "storage", _ => "other",
    };
}
