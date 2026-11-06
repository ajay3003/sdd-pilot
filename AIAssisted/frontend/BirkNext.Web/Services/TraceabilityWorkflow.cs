using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>
/// Requirements Traceability as guided steps: evidence scope → Constitution → Specification → Specification → Plan → Plan → Tasks → Tasks →
/// Requirements/SC → gaps &amp; evidence. Derived from the report's own transitions and observations — no new analysis. A transition is "done" when
/// it was assessed (its own x / y is stated), never because coverage is high; a missing reference is informational unless a configured policy
/// requires it; Plan → Tasks stays Not assessed (never 0 %) while plan entries carry no stable task identifiers. Step actions only switch tabs.
/// </summary>
public static class TraceabilityWorkflow
{
    public static IReadOnlyList<GuidedStep> Derive(ArtifactTraceabilityReport report, TraceabilityInputs? inputs)
    {
        GuidedStep Transition(string key, string tab, string title, string notAssessedWhy)
        {
            var t = report.Transitions.First(x => x.Key == key);
            return t.Assessed
                ? new(key, title, GuidedStepState.Done, t.Detail.Split(" · ")[0], "Assessed. An explicit document reference is evidence of intent, not proof that work was implemented or tested.",
                    Evidence: t.Detail, ActionLabel: "Open", ActionId: tab)
                : new(key, title, GuidedStepState.NotApplicable, "Not assessed", $"{t.Detail}. {notAssessedWhy}", ActionLabel: "Open", ActionId: tab);
        }

        var steps = new List<GuidedStep>
        {
            new("scope", "Select evidence scope", inputs?.Status == TraceabilityInputStatus.Ready ? GuidedStepState.Done : GuidedStepState.Current,
                inputs is null ? "Loading" : $"{inputs.LoadedCount} of {TraceabilityInputs.TraceabilityRoles.Count} documents",
                inputs is null ? "Reading the selected module and artifact pack." : $"{inputs.SourceLabel}{(inputs.ProjectDisplay is { } p ? $" · {p}" : "")}: Constitution, Specification, Plan and Tasks of the selected module and pack."),
            Transition("const-spec", "const-spec", "Constitution → Specification", "Needs both documents."),
            Transition("spec-plan", "spec-plan", "Specification → Plan", "A requirement without a plan reference is not thereby unimplemented."),
            Transition("plan-task", "plan-task", "Plan → Tasks", "Plan entries carry action text, not stable task identifiers; assessing this needs task IDs in the plan. Plan → Tasks is kept separate from Tasks → Requirements."),
            Transition("task-ref", "overview", "Tasks → Requirements / SC", "Needs Tasks and Specification."),
        };

        var policy = report.PolicyRequiredGapCount;
        var informational = report.NoExplicitReferenceCount;
        steps.Add(policy > 0
            ? new("gaps", "Review gaps and evidence", GuidedStepState.Current, $"{policy} policy-required trace{(policy == 1 ? "" : "s")} not established",
                $"The configured policy requires these traces. {informational} further observation{(informational == 1 ? " is" : "s are")} informational only.",
                ActionLabel: "Open observations", ActionId: "gaps")
            : new("gaps", "Review gaps and evidence", GuidedStepState.Done, informational == 0 ? "No observations" : $"{informational} informational",
                informational == 0 ? "Every assessed item has an explicit reference."
                    : "No explicit reference is an observation, not a failure, while no traceability policy requires it. Links are never created to raise coverage.",
                Evidence: report.NoExplicitReferenceBreakdown.Count == 0 ? null : string.Join(" · ", report.NoExplicitReferenceBreakdown.Select(b => $"{b.Count} {b.Label}")),
                ActionLabel: informational > 0 ? "Open observations" : null, ActionId: informational > 0 ? "gaps" : null));
        return steps;
    }
}
