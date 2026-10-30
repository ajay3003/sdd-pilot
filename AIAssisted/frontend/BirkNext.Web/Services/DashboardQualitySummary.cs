using BirkNext.Web.Services.Operations;

namespace BirkNext.Web.Services;

/// <summary>One Quality Summary card: a value only when evidence states it (else Not assessed / Not verified), why, and where to look.</summary>
public sealed record DashboardKpi(string Key, string Label, string Value, string Detail, string Href, DashboardStatus Status);

/// <summary>One recent operation of the current project: what ran, its operation status, and whether a result was produced.</summary>
public sealed record DashboardActivity(Guid Id, string Title, string Status, string When, string? Revision, string? ResultLabel, string Href, bool Failed);

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
        var roles = readiness.Where(x => DocumentRoles.Contains(x.Key)).ToList();
        var available = roles.Count(x => x.Status == DashboardStatus.Available);
        var partial = roles.Count(x => x.Status == DashboardStatus.Partial);
        var missingRequired = roles.Count(x => x.Status == DashboardStatus.NeedsSetup);
        var inputs = readiness.Where(x => !DocumentRoles.Contains(x.Key) && x.Key != "dqr").ToList();
        var open = attention.Count(a => !a.Informational);
        return
        [
            new("evidence", "SDD evidence", roles.Count == 0 ? "No project" : $"{available} of {roles.Count} document types",
                roles.Count == 0 ? "Import a project to see its evidence." : $"{(partial > 0 ? $"{partial} partial · " : "")}{(missingRequired > 0 ? $"{missingRequired} required missing · " : "")}presence only, not reviewed",
                ProjectEvidenceHref, roles.Count == 0 ? DashboardStatus.NeedsSetup : missingRequired > 0 ? DashboardStatus.NeedsSetup : partial > 0 ? DashboardStatus.Partial : DashboardStatus.Available),
            r.DocumentReview == AnalysisSessionStatus.Completed
                ? new("specification", "Specification quality", $"{r.DocumentReviewFindings} finding{S(r.DocumentReviewFindings)}",
                    $"{r.DocumentReviewSevere} severe · {(r.DocumentReviewAt is { } at ? $"reviewed {at.ToLocalTime():d MMM HH:mm}" : "reviewed this session")}", DashboardPresentation.DocumentReviewRoute,
                    r.DocumentReviewSevere > 0 ? DashboardStatus.AttentionNeeded : DashboardStatus.Passed)
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
            r.RuntimeEvidence || r.QaAssessed
                ? new("qa", "QA verification", "Results this session", "A runtime or QA review produced results; open the review for what was verified.", "quality-review", DashboardStatus.Available)
                : new("qa", "QA verification", "Not verified", "No runtime or QA review has produced results for this project yet.", "quality-review", DashboardStatus.NotAssessed),
            new("attention", "Needs attention", open.ToString(), open == 0 ? "No open item from the evidence and completed reviews." : $"{Groups(attention).Count} area{S(Groups(attention).Count)} affected",
                "#db-attention-heading", open > 0 ? DashboardStatus.AttentionNeeded : DashboardStatus.Available),
        ];
    }

    /// <summary>The group a finding belongs to, from its stable key. Unknown keys stay "Other" — never merged by similar text.</summary>
    public static string Group(string key) => key switch
    {
        "workspace-error" => "Configuration blockers",
        "source" or "source-binding" => "Missing required evidence",
        "severe-findings" or "needs-changes" or "dqr-failed" => "Specification quality",
        "stale" => "Outdated results",
        _ when key.StartsWith("trace", StringComparison.OrdinalIgnoreCase) => "Traceability gaps",
        _ when key.StartsWith("ambiguous", StringComparison.OrdinalIgnoreCase) || key.StartsWith("missing", StringComparison.OrdinalIgnoreCase) => "Missing required evidence",
        _ => "Other",
    };

    public static IReadOnlyList<string> Groups(IReadOnlyList<DashboardAttentionItem> attention) =>
        [.. attention.Where(a => !a.Informational).Select(a => Group(a.Key)).Distinct()];

    /// <summary>
    /// The newest operations of the selected project — matched by its name as the operation recorded it, plus the project-independent upload
    /// check — so another project's (or a delivery project's) operations never appear. Operation status is not a review result.
    /// </summary>
    public static IReadOnlyList<DashboardActivity> Activity(IEnumerable<OperationSnapshot> operations, string? projectName, int take = 5) =>
        [.. operations.Where(o => o.Context.ProjectKey == "project-import:upload"
                || projectName is { Length: > 0 } && string.Equals(o.Context.ProjectLabel, projectName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.CompletedAt ?? o.StartedAt).Take(take)
            .Select(o => new DashboardActivity(o.Id, o.Title, o.Status switch
            {
                OperationStatus.Completed => "Completed", OperationStatus.Failed => "Failed", OperationStatus.Cancelled => "Cancelled",
                OperationStatus.Queued => "Queued", _ => "Running",
            }, (o.CompletedAt ?? o.StartedAt).ToLocalTime().ToString("d MMM HH:mm"), o.Context.Revision, o.ResultLabel,
            o.ResultRoute ?? o.DetailsRoute, o.Status == OperationStatus.Failed))];

    private static string S(int n) => n == 1 ? "" : "s";
}
