using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// M01 Kode 6/7 as guided steps with two independent review capabilities: the static security review (imported source evidence, no target
/// contact) and runtime security verification (approved synthetic DEV/QA context). A blocked runtime verification never blocks a supported
/// static review, and a completed static review never unblocks runtime execution. Derived from the page's existing state (source scope,
/// prerequisites, live readiness, stored result); runtime that cannot be exercised is RUNTIME UNVERIFIED, never done.
/// </summary>
public static class SecurityReviewWorkflow
{
    /// <summary>Static review readiness from the selected source and the source-evidence prerequisites.</summary>
    public static (string Label, GuidedStepState State, string Detail) StaticReadiness(bool hasSource, ClassificationPresentation.MissingView needed)
    {
        if (!hasSource) return ("Blocked — insufficient applicable source evidence", GuidedStepState.Blocked, "Select a Source Analysis snapshot with the M2LB source; nothing is reviewed statically without it.");
        var missing = needed.Rows.Where(r => r.Group == ClassificationMissingGroup.SourceEvidence && r.Counts).Select(r => r.Title).ToList();
        return missing.Count > 0
            ? ("Ready with evidence limitations", GuidedStepState.Done, $"The static review can run; source evidence is missing for: {string.Join(", ", missing)}.")
            : ("Ready for static review", GuidedStepState.Done, "The selected source covers the model, CDC and authorization evidence the static review reads.");
    }

    public static IReadOnlyList<GuidedStep> Derive(bool hasEnvironment, bool hasSource, ClassificationPresentation.MissingView needed, bool liveCanRun, string liveReason,
        ClassificationReviewResult? result)
    {
        var (staticLabel, staticState, staticDetail) = StaticReadiness(hasSource, needed);
        var runtimeItems = needed.Rows.Count(r => r.Group is ClassificationMissingGroup.RequiredForLiveChecks or ClassificationMissingGroup.RuntimeEvidence && r.Counts);
        var executedRuntime = result?.Checks.Count(c => c.Provenance == IntegrationEvidenceSource.NetworkProbe) ?? 0;
        var steps = new List<GuidedStep>
        {
            new("scope", "Select scope and evidence", hasEnvironment ? GuidedStepState.Done : GuidedStepState.Current, hasEnvironment ? (hasSource ? "Environment · source selected" : "Environment · no source") : "No Target Environment",
                hasEnvironment ? "Reviews are stored per Target Environment; the source snapshot is chosen under Source evidence (available is not bound)." : "Select an active Target Environment: reviews are recorded per environment."),
            new("requirements", "Review security requirements", GuidedStepState.Done, $"{SecurityControlMatrix.Families.Count} control families",
                "SEC-01 classification · SEC-02 propagation · SEC-03 authorization · SEC-04 disclosure · SEC-05 downstream · SEC-06 audit · SEC-07 failure & recovery — grouped from the existing checks; each row lists the checks behind it after a run."),
            new("static", "Run static security review", hasEnvironment && staticState != GuidedStepState.Blocked && result is null ? GuidedStepState.Current : staticState, staticLabel,
                $"{staticDetail} Finding a guard in source is evidence that a mechanism exists, not that Kode 6/7 access control is correct; no static finding is not Passed."),
            new("runtime-context", "Prepare runtime test context", liveCanRun ? GuidedStepState.Done : GuidedStepState.Blocked, liveCanRun ? "Approved DEV/QA context" : "Blocked — missing prerequisites",
                liveCanRun ? "Synthetic test children and identities are configured for an approved DEV/QA environment (values are never shown)." : $"{liveReason} {runtimeItems} runtime item(s) missing. This never blocks the static review.",
                DisabledReason: liveCanRun ? null : "Runtime verification stays unverified until an approved synthetic context exists."),
            new("runtime", "Run runtime verification", executedRuntime > 0 ? GuidedStepState.Done : liveCanRun ? GuidedStepState.Waiting : GuidedStepState.Blocked,
                executedRuntime > 0 ? $"{executedRuntime} runtime check(s) executed" : liveCanRun ? "Not run — start it explicitly" : "RUNTIME UNVERIFIED",
                executedRuntime > 0 ? "Each runtime check compares the expected and observed result for a synthetic identity; blocked or skipped is neither Passed nor Failed."
                    : "Runtime verification runs only on an explicit Run with an approved context; it never runs from opening the page or checking readiness."),
        };
        steps.Add(result is null
            ? new("matrix", "Review security matrix and risks", GuidedStepState.Waiting, "After a review",
                "Static evidence and runtime verification side by side per control family, with residual risk and the next action. An absent finding does not establish Kode 6/7 protection.")
            : new("matrix", "Review security matrix and risks", GuidedStepState.Done, MatrixSummary(SecurityControlMatrix.Build(result)),
                "See the Kode 6/7 security matrix below; residual risks stay visible until runtime evidence or a manual decision closes them."));
        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }

    public static string MatrixSummary(IReadOnlyList<SecurityControlMatrix.Row> rows) =>
        $"{rows.Count(r => r.Result == "Passed")} passed · {rows.Count(r => r.Result == "Failed")} failed · {rows.Count(r => r.Result == "Partial")} partial · {rows.Count(r => r.Result == "Not assessed")} not assessed";
}
