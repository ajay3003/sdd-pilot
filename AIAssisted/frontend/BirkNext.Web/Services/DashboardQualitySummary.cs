namespace BirkNext.Web.Services;

/// <summary>One Quality Summary card: a value only when evidence states it (else Not assessed / Not verified), why, and where to look.</summary>
public sealed record DashboardKpi(string Key, string Label, string Value, string Detail, string Href, DashboardStatus Status);

/// <summary>
/// SDD Quality &amp; Readiness summary. Re-presents what <see cref="DashboardPresentation"/> already derives from the current workspace,
/// Recommended Workflow readiness and this session's review results — no new calculation, no score: SDD evidence (document roles),
/// specification quality (Document Quality Review), traceability (Requirements Traceability), analysis readiness (review inputs), QA
/// verification (runtime/QA reviews) and attention. A card without evidence says Not assessed / Not verified, never 0 or a percentage.
/// </summary>
public static class DashboardQualitySummary
{
    public const string ProjectEvidenceHref = "/admin/system-settings?section=target-environments&tab=project";
    private static readonly string[] DocumentRoles = ["Specification", "Plan", "Tasks", "Constitution"];

    public static IReadOnlyList<DashboardKpi> Cards(IReadOnlyList<DashboardReadinessRow> readiness, IReadOnlyList<DashboardAttentionItem> attention, DashboardReviewFacts r)
    {
        // An optional document type that does not apply (for example no Constitution) is not part of the denominator.
        var roles = readiness.Where(x => DocumentRoles.Contains(x.Key) && x.Status != DashboardStatus.NotApplicable).ToList();
        var available = roles.Count(x => x.Status == DashboardStatus.Available);
        var partial = roles.Count(x => x.Status == DashboardStatus.Partial);
        var missingRequired = roles.Count(x => x.Status == DashboardStatus.NeedsSetup);
        var inputs = readiness.Where(x => !DocumentRoles.Contains(x.Key) && x.Key != "dqr").ToList();
        var open = attention.Count(a => !a.Informational);
        return
        [
            new("evidence", "SDD evidence", roles.Count == 0 ? "No project" : $"{available} of {roles.Count} core document types",
                roles.Count == 0 ? "Import a project to see its evidence." : $"{(partial > 0 ? $"{partial} partial · " : "")}{(missingRequired > 0 ? $"{missingRequired} required missing · " : "")}presence only, not reviewed",
                ProjectEvidenceHref, roles.Count == 0 ? DashboardStatus.NeedsSetup : missingRequired > 0 ? DashboardStatus.NeedsSetup : partial > 0 ? DashboardStatus.Partial : DashboardStatus.Available),
            r.DocumentReview == AnalysisSessionStatus.Completed
                ? new("specification", "Specification quality", $"{r.DocumentReviewFindings} finding{S(r.DocumentReviewFindings)}",
                    $"{r.DocumentReviewSevere} severe · {(r.DocumentReviewAt is { } at ? $"reviewed {at.ToLocalTime():d MMM HH:mm}" : "reviewed this session")}", DashboardPresentation.DocumentReviewRoute,
                    r.DocumentReviewSevere > 0 ? DashboardStatus.AttentionNeeded : r.DocumentReviewFindings > 0 ? DashboardStatus.Partial : DashboardStatus.Available)
                : new("specification", "Specification quality", r.DocumentReview == AnalysisSessionStatus.Failed ? "Review failed" : "Not assessed",
                    "Run Document Quality Review to assess specifications, plans and tasks.", DashboardPresentation.DocumentReviewRoute,
                    r.DocumentReview == AnalysisSessionStatus.Failed ? DashboardStatus.Failed : DashboardStatus.NotAssessed),
            r.TraceabilityRun
                ? new("traceability", "Traceability", $"{r.TraceabilityTransitionsAssessed} of {r.TraceabilityTransitionsTotal} transitions assessed",
                    $"{r.TraceabilityReferenceIssues} reference issue{S(r.TraceabilityReferenceIssues)}", DashboardPresentation.TraceabilityRoute,
                    r.TraceabilityReferenceIssues > 0 ? DashboardStatus.AttentionNeeded : DashboardStatus.Available)
                : new("traceability", "Traceability", "Not assessed", "Run Requirements Traceability to check document transitions.", DashboardPresentation.TraceabilityRoute, DashboardStatus.NotAssessed),
            new("readiness", "Analysis readiness", $"{inputs.Count(x => x.Status == DashboardStatus.Available)} of {inputs.Count} inputs ready",
                inputs.Where(x => x.Status != DashboardStatus.Available).Select(x => $"{x.Label}: {x.StatusLabel}").DefaultIfEmpty("Source, Target Environment and runtime inputs ready").First(),
                DashboardPresentation.WorkflowRoute, inputs.All(x => x.Status == DashboardStatus.Available) && inputs.Count > 0 ? DashboardStatus.Available : DashboardStatus.Partial),
            ExecutionCard(r),
            new("attention", "Needs attention", open.ToString(), open == 0 ? "No open item from the evidence and completed reviews." : $"{Groups(attention).Count} area{S(Groups(attention).Count)} affected",
                "#db-attention-heading", open > 0 ? DashboardStatus.AttentionNeeded : DashboardStatus.Available),
        ];
    }

    /// <summary>
    /// Actual recorded test runs (Test Execution &amp; Evidence) for the active Target Environment. Document and source analysis are never
    /// executions; a generated test is not an executed test; unreadable evidence is reported as unavailable, never as zero.
    /// </summary>
    private static DashboardKpi ExecutionCard(DashboardReviewFacts r)
    {
        const string href = "/execution-results";
        var e = r.Execution;
        if (e is null)
            return new("execution", "Execution evidence", "Not read", r.RuntimeEvidence ? "A runtime review produced results this session; recorded test runs were not read." : "Recorded test runs were not read for this view.", href, DashboardStatus.NotAssessed);
        if (!e.Readable)
            return new("execution", "Execution evidence", "Unavailable", "Test Execution & Evidence could not be read; this is not the same as no runs.", href, DashboardStatus.Unavailable);
        if (e.Runs == 0)
            return new("execution", "Execution evidence", "No recorded runs",
                $"No test has been executed{(e.EnvironmentName is { } env ? $" in {env}" : "")}. Generated tests and analyses are not executions.", href, DashboardStatus.NotAssessed);
        var other = e.Runs - e.Passed - e.Failed;
        return new("execution", "Execution evidence", $"{e.Runs} recorded run{S(e.Runs)}",
            $"{e.Passed} passed · {e.Failed} failed or error{(other > 0 ? $" · {other} blocked, not assessed or other" : "")}{(e.Latest is { } at ? $" · latest {at.ToLocalTime():d MMM HH:mm}" : "")}{(e.EnvironmentName is { } name ? $" · {name}" : "")}",
            href, e.Failed > 0 ? DashboardStatus.AttentionNeeded : other > 0 ? DashboardStatus.Partial : DashboardStatus.Available);
    }

    /// <summary>The group a finding belongs to, from its stable key. Unknown keys stay "Other" — never merged by similar text.</summary>
    public static string Group(string key) => key switch
    {
        "workspace-error" => "Configuration blockers",
        "source" or "source-binding" => "Missing required evidence",
        "severe-findings" or "needs-changes" or "dqr-failed" => "Specification quality",
        "stale" => "Outdated results",
        _ when key.StartsWith("trace", StringComparison.OrdinalIgnoreCase) => "Traceability gaps",
        // An ambiguity is evidence that exists more than once (a choice to make), never missing evidence.
        _ when key.StartsWith("ambiguous", StringComparison.OrdinalIgnoreCase) => "Ambiguous evidence",
        _ when key.StartsWith("missing", StringComparison.OrdinalIgnoreCase) => "Missing required evidence",
        _ => "Other",
    };

    public static IReadOnlyList<string> Groups(IReadOnlyList<DashboardAttentionItem> attention) =>
        [.. attention.Where(a => !a.Informational).Select(a => Group(a.Key)).Distinct()];

    private static string S(int n) => n == 1 ? "" : "s";
}
