using BirkNext.Interpretation;

namespace BirkNext.Web.Services;

/// <summary>What one decision would change, in words: the outcome, its consequences and whether release readiness can change by it.</summary>
public sealed record CorrelationOutcomePreview(ReviewDecisionOutcome Outcome, string Label, IReadOnlyList<string> Consequences, bool ChangesGovernance);

/// <summary>
/// The decision-impact preview of a review question, before anyone decides: Confirm, Reject and Defer each with their consequences for the
/// relation, the consumers that read it, the impact graph (from the shared impact walk of both entities, when loaded) and governance. Nothing here
/// decides or changes anything; Defer is always "nothing changes" and never clears a blocker.
/// </summary>
public static class CorrelationDecisionImpact
{
    public static IReadOnlyList<CorrelationOutcomePreview> Preview(ReviewItemView item, CanonicalImpactView? sourceImpact, CanonicalImpactView? targetImpact)
    {
        var consumers = item.AffectedConsumers.Count == 0 ? "no consumer" : string.Join(", ", item.AffectedConsumers);
        var r = item.Relation;
        var identity = item.Type == ReviewItemType.IdentityConflict;
        var verb = r is null ? "relation" : CorrelationPresentation.RelationLabel(r.Type);
        var (source, target) = (r?.Source.DisplayName ?? item.Subject?.DisplayName ?? "the entity", r?.Target.DisplayName ?? "the other entity");
        string Reach(CanonicalImpactView? impact, string name) => impact is null ? $"Impact of {name} not loaded."
            : $"{name}: {impact.Entities.Count(e => e.Level == ImpactLevel.Direct)} direct, {impact.Entities.Count(e => e.Level == ImpactLevel.Downstream)} downstream, "
              + $"{impact.Entities.Count(e => e.Level == ImpactLevel.Potential)} potential dependents · {impact.Tests.Count} test{(impact.Tests.Count == 1 ? "" : "s")}{(impact.Unknown > 0 ? $" · {impact.Unknown} unknown" : "")}.";

        if (identity)
            return
            [
                new(ReviewDecisionOutcome.Confirmed, "Same entity",
                [
                    $"{source} and {target} are treated as one entity by {consumers}; the alias applies to both names.",
                    $"Relations of either side are read as one — {Reach(sourceImpact, source)} {Reach(targetImpact, target)}",
                    "Release readiness: this identity-conflict blocker is cleared when the governance gate is evaluated again; other blockers stay.",
                    "Needs a written rationale; recorded with your name, the time, the evidence fingerprint and the rule version.",
                ], true),
                new(ReviewDecisionOutcome.Rejected, "Separate entities",
                [
                    $"{source} and {target} stay separate; the conflicting alias declaration is not applied to either.",
                    $"{consumers} keep reading them apart; no relation moves.",
                    "Release readiness: this identity-conflict blocker is cleared when the governance gate is evaluated again; other blockers stay.",
                    "Needs a written rationale; recorded and auditable.",
                ], true),
                new(ReviewDecisionOutcome.Deferred, "Defer",
                [
                    "Nothing changes: the question stays open and both declarations stay unapplied.",
                    "Release readiness stays Blocked by this conflict.",
                ], false),
            ];

        var counts = r is not null && CorrelationPrecedence.Counts(r.Effective);
        return
        [
            new(ReviewDecisionOutcome.Confirmed, "Confirm",
            [
                counts ? $"The {verb} relation is already counted; confirming records a human decision on it." : $"The {verb} relation becomes established and counts for {consumers}.",
                r?.Type == CanonicalRelationType.Verifies ? $"{target} gains a confirmed test link — test design, not execution or a pass." : $"{Reach(sourceImpact, source)}",
                "Recorded with the evidence fingerprint: if the evidence changes, the decision is flagged for revalidation, never silently kept.",
            ], item.Kind == ReviewItemKind.Decision),
            new(ReviewDecisionOutcome.Rejected, "Reject",
            [
                $"The {verb} relation is not counted by {consumers}.",
                "It is not proposed again for the same evidence; new evidence can raise it again.",
            ], item.Kind == ReviewItemKind.Decision),
            new(ReviewDecisionOutcome.Deferred, "Defer",
            [
                "Nothing changes: the relation stays a candidate and counts nowhere.",
                item.Kind == ReviewItemKind.Decision ? "A required decision stays open, so anything it blocks stays blocked." : "The suggestion stays in the queue.",
            ], false),
        ];
    }
}
