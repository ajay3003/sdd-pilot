using BirkNext.Web.Models;

namespace BirkNext.Web.Services.Engine.Packs;

/// <summary>
/// QA Auditor rule pack: End-to-end traceability chain checks.
/// Requires <see cref="RuleContext.Trace"/> to be pre-populated.
///
/// Rules:
///   TRACE-001 — Constitution rules not referenced in specification
///   TRACE-002 — Specification requirements not referenced in plan
///   TRACE-003 — Plan items with no covering tasks
/// </summary>
public sealed class QaTraceabilityRulePack : IRulePack
{
    public string RulePackId   => "qa-traceability";
    public string RulePackName => "Traceability";

    public RulePackResult Execute(RuleContext context)
    {
        var findings = new List<RuleFinding>();

        if (context.Trace is null)
            return new RulePackResult { RulePackId = RulePackId, RulePackName = RulePackName };

        var trace = context.Trace;

        // TRACE-001: Constitution→Spec gaps (only when both artifacts are loaded)
        if (context.Constitution is not null && context.Spec is not null
            && trace.ConstitutionCoverage.TotalItems > 0)
        {
            int missing = trace.ConstitutionCoverage.MissingItems;
            if (missing > 0)
                findings.Add(new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = "TRACE-001",
                    Category       = "Traceability",
                    Title          = $"{missing} constitution rule(s) not referenced in specification",
                    Description    = $"{missing} constitution rule(s) have no corresponding requirements in the specification.",
                    Severity       = missing > 3 ? "High" : "Medium",
                    Status         = "Failed",
                    Recommendation = "Add references to the missing constitution rules in specification requirements.",
                });
        }

        // TRACE-002: Spec→Plan gaps (only when both artifacts are loaded)
        if (context.Spec is not null && context.Plan is not null
            && trace.SpecificationCoverage.TotalItems > 0)
        {
            int missing = trace.SpecificationCoverage.MissingItems;
            if (missing > 0)
                findings.Add(new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = "TRACE-002",
                    Category       = "Traceability",
                    Title          = $"{missing} requirement(s) not referenced in the plan",
                    Description    = $"{missing} specification requirement(s) are not mentioned in the implementation plan.",
                    Severity       = missing > 3 ? "High" : "Medium",
                    Status         = "Failed",
                    Recommendation = "Ensure the plan explicitly addresses all specification requirements.",
                });
        }

        // TRACE-003: Plan→Task gaps (only when both artifacts are loaded)
        if (context.Plan is not null && context.Tasks is not null
            && trace.PlanCoverage.TotalItems > 0)
        {
            int missing = trace.PlanCoverage.MissingItems;
            if (missing > 0)
                findings.Add(new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = "TRACE-003",
                    Category       = "Traceability",
                    Title          = $"{missing} plan item(s) with no covering tasks",
                    Description    = $"{missing} plan item(s) are not referenced in the task list — implementation coverage is incomplete.",
                    Severity       = missing > 3 ? "High" : "Medium",
                    Status         = "Failed",
                    Recommendation = "Create tasks for all plan items that lack task coverage.",
                });
        }

        return new RulePackResult
        {
            RulePackId   = RulePackId,
            RulePackName = RulePackName,
            Findings     = findings,
        };
    }
}
