using BirkNext.Web.Models;

namespace BirkNext.Web.Services.Engine.Packs;

/// <summary>
/// QA Auditor rule pack: Specification quality checks.
/// Requires <see cref="RuleContext.Spec"/> and optionally <see cref="RuleContext.Trace"/>.
///
/// Rules:
///   SPEC-001 — No acceptance criteria defined
///   SPEC-002 — Requirements without plan coverage
///   SPEC-003 — Plan items without task coverage (untasked requirements proxy)
///   SPEC-004 — High clarification count (ambiguity signal)
///   SPEC-005 — No edge cases documented
/// </summary>
public sealed class QaSpecificationRulePack : IRulePack
{
    public string RulePackId   => "qa-specification";
    public string RulePackName => "Specification Quality";

    public RulePackResult Execute(RuleContext context)
    {
        var findings = new List<RuleFinding>();
        var gaps     = new List<RuleGap>();

        if (context.Spec is null)
        {
            gaps.Add(new RuleGap
            {
                GapArea     = "Missing Specification Coverage",
                Description = "Specification not loaded — specification audit unavailable",
                Severity    = "High",
            });
            return Result(findings, gaps);
        }

        var h     = context.Spec.Health;
        var trace = context.Trace;
        bool hasContent = context.Spec.Roots.Count > 0 && h.TotalHeadings > 1;

        // SPEC-001: No acceptance criteria
        if (hasContent && h.Tests + h.BddScenarios + h.SuccessCriteria == 0)
        {
            int reqCount = h.Requirements > 0 ? h.Requirements : h.TotalHeadings - 1;
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "SPEC-001",
                Category       = "Specification",
                Title          = "No acceptance criteria defined across requirements",
                Description    = $"Specification has {reqCount} requirement(s) but no acceptance criteria, BDD scenarios, or success criteria.",
                Severity       = "High",
                Status         = "Failed",
                Recommendation = "Add acceptance criteria (tests, BDD scenarios, or success criteria) to each requirement.",
            });
        }

        if (trace is not null)
        {
            // SPEC-002: Requirements not referenced in the plan
            int unplanned = trace.SpecificationCoverage.MissingItems;
            if (unplanned > 0)
            {
                findings.Add(new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = "SPEC-002",
                    Category       = "Specification",
                    Title          = $"{unplanned} requirement(s) without plan coverage",
                    Description    = $"{unplanned} specification requirement(s) are not referenced in the implementation plan.",
                    Severity       = unplanned > 3 ? "High" : "Medium",
                    Status         = "Failed",
                    Recommendation = "Reference all specification requirements in the implementation plan.",
                });

                gaps.Add(new RuleGap
                {
                    GapArea     = "Missing Plan Coverage",
                    Description = $"{unplanned} requirement(s) not covered by the plan",
                    Severity    = unplanned > 3 ? "High" : "Medium",
                });
            }

            // SPEC-003: Plan items without task coverage (indicator of untasked requirements)
            int orphanPlan = trace.PlanCoverage.MissingItems;
            if (orphanPlan > 0 && trace.PlanCoverage.TotalItems > 0)
            {
                findings.Add(new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = "SPEC-003",
                    Category       = "Specification",
                    Title          = $"{orphanPlan} plan item(s) without task coverage, indicating untasked requirements",
                    Description    = $"{orphanPlan} plan item(s) have no associated tasks — requirements they address may not be implemented.",
                    Severity       = orphanPlan > 3 ? "High" : "Medium",
                    Status         = "Failed",
                    Recommendation = "Create tasks for all plan items that cover specification requirements.",
                });
            }
        }

        // SPEC-004: High clarification count
        if (h.Clarifications > 5)
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "SPEC-004",
                Category       = "Specification",
                Title          = "High clarification count indicates specification ambiguity",
                Description    = $"{h.Clarifications} open clarification(s) detected. Resolve them before implementation.",
                Severity       = "Medium",
                Status         = "Failed",
                Recommendation = "Resolve open specification clarifications to eliminate ambiguity before implementation.",
            });
        }

        // SPEC-005: No edge cases
        if (hasContent && h.EdgeCases == 0)
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "SPEC-005",
                Category       = "Specification",
                Title          = "No edge cases documented",
                Description    = "Specification has requirements but no edge cases. Document boundary conditions and failure scenarios.",
                Severity       = "Low",
                Status         = "Failed",
                Recommendation = "Add edge case documentation to the specification.",
            });
        }

        return Result(findings, gaps);
    }

    private RulePackResult Result(List<RuleFinding> f, List<RuleGap> g) =>
        new() { RulePackId = RulePackId, RulePackName = RulePackName, Findings = f, Gaps = g };
}
