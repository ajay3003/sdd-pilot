using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeAuthentication;

namespace BirkNext.Web.Services;

/// <summary>
/// API Quality Review before a run, as guided steps: select API targets → configure authentication → contracts &amp; evidence → readiness →
/// run → findings &amp; coverage. Derived from the page's own state (targets, selection, authenticated-review capabilities, the resolved runtime
/// authentication mode and <see cref="ApiReviewRunEligibility"/>) — no second readiness model. Discovered ≠ executed; Authentication None never
/// reaches a protected endpoint; a verified browser session is used only when Attended is selected; a missing contract limits, never blocks.
/// </summary>
public static class ApiReviewWorkflow
{
    public static IReadOnlyList<GuidedStep> Derive(IReadOnlyList<ApiReviewTarget> targets, IReadOnlyCollection<string> selectedIds, AuthenticatedReviewCapabilities? capabilities,
        RuntimeAuthenticationResolution? authentication, ApiReviewRunEligibility eligibility, string? profileId)
    {
        var selected = targets.Where(t => selectedIds.Contains(t.TargetId)).ToList();
        var protectedCount = selected.Count(t => t.AuthRequired);
        var withContract = selected.Count(t => !string.IsNullOrWhiteSpace(t.ContractSource));
        var mode = authentication?.Effective;
        var modeReaches = mode is null or RuntimeAuthenticationMode.Auto or RuntimeAuthenticationMode.Attended;
        var steps = new List<GuidedStep>
        {
            targets.Count == 0
                ? new("targets", "Select API targets", GuidedStepState.Current, "No API target",
                    "No REST or GraphQL target is discovered or configured for this environment yet.",
                    ActionLabel: "Open Endpoint Discovery", Href: ApiReviewRunEligibility.TargetEnvironmentsHref + "&tab=endpoint" + (profileId is { Length: > 0 } id ? "&profile=" + Uri.EscapeDataString(id) : ""))
                : selected.Count == 0
                    ? new("targets", "Select API targets", GuidedStepState.Current, $"{targets.Count} available, none selected", "Select the services to review below. Discovered operations are not executed until a run.")
                    : new("targets", "Select API targets", GuidedStepState.Done, $"{selected.Count} of {targets.Count} selected",
                        $"{selected.Count(t => t.ApiType == ApiReviewTargetType.Rest)} REST · {selected.Count(t => t.ApiType == ApiReviewTargetType.GraphQl)} GraphQL. Discovered operations are not executed until a run."),
        };

        steps.Add(selected.Count == 0
            ? new("authentication", "Configure authentication", GuidedStepState.Waiting, "Not assessed", "Depends on the selected targets.")
            : protectedCount == 0
                ? new("authentication", "Configure authentication", GuidedStepState.Done, "Not required", "No selected target requires authentication.")
                : !modeReaches
                    ? new("authentication", "Configure authentication", GuidedStepState.Current, $"{mode} selected",
                        $"{protectedCount} selected target{(protectedCount == 1 ? " requires" : "s require")} authentication; with {mode} {(protectedCount == 1 ? "it is" : "they are")} blocked before execution, never passed."
                        + (capabilities?.AuthenticatedApi == true ? " A verified attended session is available: select Attended to use it." : ""))
                    : capabilities?.AuthenticatedApi == true
                        ? new("authentication", "Configure authentication", GuidedStepState.Done, $"{mode?.ToString() ?? "Auto"} · session available",
                            "The authenticated API context captured by the Local HTTPS proxy is used for protected targets; its value is never shown or copied.")
                        : new("authentication", "Configure authentication", GuidedStepState.Current, "No authenticated context",
                            $"{protectedCount} selected target{(protectedCount == 1 ? " requires" : "s require")} authentication and no authenticated API context exists; the readiness card below links to Authentication setup."));

        steps.Add(selected.Count == 0
            ? new("contracts", "Review contracts and evidence", GuidedStepState.Waiting, "Not assessed", "Depends on the selected targets.")
            : new("contracts", "Review contracts and evidence", withContract == selected.Count ? GuidedStepState.Done : GuidedStepState.Waiting,
                $"{withContract} of {selected.Count} with a contract",
                withContract == selected.Count ? "Each selected target has a contract source for the read-only contract checks."
                    : "Targets without a contract are reviewed from runtime responses only; contract checks are reported as not assessed (a limitation, not a blocker)."));

        // The readiness card below is the one action surface for blockers; the step states the reason only.
        steps.Add(new("readiness", "Check review readiness", eligibility.Enabled ? GuidedStepState.Done : selected.Count == 0 ? GuidedStepState.Waiting : GuidedStepState.Blocked,
            eligibility.Enabled ? "Can run" : "Cannot run", eligibility.Reason));

        steps.Add(new("run", "Run automated review", eligibility.Enabled ? GuidedStepState.Current : GuidedStepState.Waiting, eligibility.Enabled ? "Ready to start" : "Not available",
            "Read-only: safe requests only, never a mutation; nothing starts until you select Run below."));

        steps.Add(new("findings", "Review findings and coverage", GuidedStepState.Waiting, "After a run",
            $"Findings with evidence, what was actually executed, unassessed checks and {ApiReviewPresentation.ManualReviewAreas.Count} manual review areas. No findings is not a passed review."));

        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }
}
