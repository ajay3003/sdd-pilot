using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// M01 – Gradert tilgang (Kode 6/7): one per-control-family security matrix over a stored review, with static and runtime evidence kept apart.
/// Families are a documented review rule over the EXISTING check ids and areas (no new control records, no new analyzer): a check with
/// provenance SourceCode is static evidence; NetworkProbe is runtime verification. A guard found in source is a mechanism, not verified protection;
/// a CDC mapping is not runtime propagation; no static finding is not Passed; Passed requires runtime observations that all passed.
/// </summary>
public static class SecurityControlMatrix
{
    public sealed record Family(string Id, string Title, string Requirement, IReadOnlySet<ClassificationArea> Areas, IReadOnlySet<string> CheckIds, string Unknown, string Evidence);

    public sealed record Row(Family Family, string Static, string Runtime, string Result, string ResidualRisk, string NextAction, int StaticChecks, int RuntimeChecks,
        IReadOnlyList<string> CheckIds);

    private static IReadOnlySet<ClassificationArea> A(params ClassificationArea[] areas) => areas.ToHashSet();
    private static IReadOnlySet<string> C(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    public static readonly IReadOnlyList<Family> Families =
    [
        new("SEC-01", "Code 6/7 classification", "Protected classifications are recognized and represented.", A(ClassificationArea.Model), C(),
            "whether the classification is applied to real records", "runtime responses for synthetic children of each level"),
        new("SEC-02", "Classification propagation", "The classification survives CDC/event propagation and integration transformations.", A(ClassificationArea.Pipeline, ClassificationArea.Guard), C("level-change"),
            "whether the classification arrives intact at runtime (a mapping in source is not propagation)", "an observed message or record carrying the expected level"),
        new("SEC-03", "Authorization enforcement", "Protected operations are guarded by authorization.",
            A(ClassificationArea.DirectAccess, ClassificationArea.Search, ClassificationArea.GraphQL, ClassificationArea.ChildAccess, ClassificationArea.Grants, ClassificationArea.EmergencyAccess), C("parallel-authorization"),
            "whether the guard makes the correct decision for each identity (a guard in source is a mechanism, not a verified decision)", "authorized and unauthorized runtime requests with the expected allow/deny"),
        new("SEC-04", "Protected data disclosure", "Protected fields and responses are withheld from unauthorized consumers.",
            A(ClassificationArea.Privacy, ClassificationArea.Caching), C("access-profile-antidisclosure", "search-count", "browser-storage"),
            "whether protected data can leak through responses, counts, caches or the browser", "runtime responses and counts for an unauthorized identity"),
        new("SEC-05", "Downstream protection", "Downstream services and integrations preserve the protection.", A(ClassificationArea.Consistency), C(),
            "whether consumers of the data apply the same protection (no shared control exists; consuming reviews assess their own requirements)", "consumer-side evidence in the consuming review (e.g. Skolenærvær domain requirements)"),
        new("SEC-06", "Audit and revision evidence", "Access and classification events are traceable without exposing protected data.",
            A(ClassificationArea.AuditAccess, ClassificationArea.ReadLogging, ClassificationArea.Observability), C(),
            "whether events are actually emitted and captured (logging configured is not an event recorded)", "captured audit records and telemetry for test requests"),
        new("SEC-07", "Failure and recovery", "Protection holds during errors, missing data, retries and delayed events.", A(),
            C("access-authz-fail-closed", "access-policy-handler", "readlog-fail-closed", "cdc-unknown-level"),
            "whether failure paths fail closed at runtime", "runtime behaviour under missing or unknown classification (no safe runtime check exists yet)"),
    ];

    private static bool In(Family f, ClassificationCheck c) => f.CheckIds.Contains(c.CheckId) || f.Areas.Contains(c.Area);

    public static IReadOnlyList<Row> Build(ClassificationReviewResult result)
    {
        var sourceRan = result.Checks.Any(c => c.Provenance == IntegrationEvidenceSource.SourceCode);
        return [.. Families.Select(f =>
        {
            var mine = result.Checks.Where(c => In(f, c)).ToList();
            var stat = mine.Where(c => c.Provenance == IntegrationEvidenceSource.SourceCode).ToList();
            var live = mine.Where(c => c.Provenance == IntegrationEvidenceSource.NetworkProbe).ToList();
            var staticLabel = !sourceRan ? "Not assessed"
                : stat.Count == 0 ? "No supporting evidence found"
                : stat.Any(c => c.State is ClassificationState.IssueDetected or ClassificationState.Fail) ? "Potential issue"
                : stat.Any(c => c.State is ClassificationState.Warning or ClassificationState.NeedsDecision or ClassificationState.DocumentedOnly or ClassificationState.NotFound) ? "Partial evidence"
                : "Evidence found";
            var runtimeLabel = live.Count == 0 ? "Not assessed"
                : live.Any(c => c.State is ClassificationState.Fail or ClassificationState.IssueDetected) ? "Failed"
                : live.All(c => c.State == ClassificationState.Pass) ? "Passed"
                : "Partial";
            var result_ = runtimeLabel switch
            {
                "Failed" => "Failed",
                "Passed" when staticLabel != "Potential issue" => "Passed",
                "Passed" or "Partial" => "Partial",
                _ => "Not assessed",
            };
            var residual = runtimeLabel switch
            {
                "Failed" => "Confirmed runtime failure: protection did not hold for a tested request.",
                "Passed" => staticLabel == "Potential issue" ? "Runtime checks passed, but a potential source issue remains to be decided." : "Verified for the tested identities and surfaces only.",
                _ => staticLabel switch
                {
                    "Potential issue" => $"Potential static concern (not a confirmed defect); unknown: {f.Unknown}.",
                    "Evidence found" or "Partial evidence" => $"Mechanism found in source; unknown: {f.Unknown}.",
                    "No supporting evidence found" => $"Missing source evidence; unknown: {f.Unknown}.",
                    _ => $"Not assessed; unknown: {f.Unknown}.",
                },
            };
            var next = runtimeLabel == "Failed" ? "Investigate the failed runtime check and record a decision."
                : staticLabel is "Not assessed" or "No supporting evidence found" && f.Id != "SEC-05" ? "Bind source evidence that covers this family (Source evidence)."
                : runtimeLabel == "Not assessed" ? $"Runtime verification needed: {f.Evidence}."
                : "Record the manual security assessment for what runtime checks cannot cover.";
            return new Row(f, staticLabel, runtimeLabel, result_, residual, next, stat.Count, live.Count, [.. mine.Select(c => c.CheckId).Distinct()]);
        })];
    }

    public static string Tone(string label) => label switch
    {
        "Passed" or "Evidence found" => "ready",
        "Failed" => "fail",
        "Potential issue" => "attention",
        "Partial" or "Partial evidence" => "warning",
        _ => "muted",
    };
}
