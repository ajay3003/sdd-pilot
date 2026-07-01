using BirkNext.Web.Models;

namespace BirkNext.Web.Services.Engine.Packs;

/// <summary>
/// QA Auditor rule pack: Implementation plan quality checks.
/// Requires <see cref="RuleContext.Plan"/> and optionally <see cref="RuleContext.Trace"/>.
///
/// Rules:
///   PLAN-001 — Missing implementation phases
///   PLAN-002 — Architecture decision without rationale
///   PLAN-003 — Missing risk analysis
///   PLAN-004 — Missing testing strategy
///   PLAN-005 — Plan items without task coverage
/// </summary>
public sealed class QaPlanRulePack : IRulePack
{
    public string RulePackId   => "qa-plan";
    public string RulePackName => "Plan Quality";

    public RulePackResult Execute(RuleContext context)
    {
        var findings = new List<RuleFinding>();
        var gaps     = new List<RuleGap>();

        if (context.Plan is null)
        {
            gaps.Add(new RuleGap
            {
                GapArea     = "Missing Plan Coverage",
                Description = "Plan not loaded — plan audit unavailable",
                Severity    = "High",
            });
            return Result(findings, gaps);
        }

        var h     = context.Plan.Health;
        var trace = context.Trace;

        // PLAN-001: No implementation phases
        if (!h.HasImplementationPhases)
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "PLAN-001",
                Category       = "Plan",
                Title          = "Missing implementation phases",
                Description    = "Plan has no implementation phases. Add phased delivery sections with tasks and deliverables.",
                Severity       = "High",
                Status         = "Failed",
                Recommendation = "Add implementation phases with tasks and deliverables to the plan.",
            });
        }

        // PLAN-002: Architecture decisions without rationale
        foreach (var adr in context.Plan.ArchitectureDecisions.Where(a =>
            string.IsNullOrWhiteSpace(a.Rationale) &&
            !a.RawText.Contains("Rationale", StringComparison.OrdinalIgnoreCase)))
        {
            var adrLabel = string.IsNullOrEmpty(adr.Id) ? adr.Title : adr.Id;
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "PLAN-002",
                Category       = "Architecture",
                Title          = $"Architecture decision {adrLabel} missing rationale",
                Description    = $"ADR '{adr.Title}' has no documented rationale. Explain why this decision was made.",
                Severity       = "Medium",
                Status         = "Failed",
                AffectedItem   = adrLabel,
                Recommendation = $"Document the rationale for architecture decision {adrLabel}.",
            });
        }

        // PLAN-003: No risk analysis
        if (h.TotalRisks == 0)
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "PLAN-003",
                Category       = "Plan",
                Title          = "Missing risk analysis",
                Description    = "Plan has no risks documented. Identify delivery risks with probability, impact, and mitigation.",
                Severity       = "Medium",
                Status         = "Failed",
                Recommendation = "Add a risk section to the plan with probability, impact, and mitigation for each risk.",
            });
        }

        // PLAN-004: No testing strategy
        if (!h.HasTestingInfo)
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "PLAN-004",
                Category       = "Testing",
                Title          = "Missing testing strategy",
                Description    = "Plan has no testing section. Document test frameworks, coverage targets, and test approach.",
                Severity       = "High",
                Status         = "Failed",
                Recommendation = "Add a testing strategy section to the plan documenting test frameworks and coverage targets.",
            });

            gaps.Add(new RuleGap
            {
                GapArea     = "Missing Testing Coverage",
                Description = "No testing strategy documented in the plan",
                Severity    = "High",
            });
        }

        // PLAN-005: Plan items without task coverage
        if (trace is not null)
        {
            int uncovered = trace.PlanCoverage.MissingItems;
            if (uncovered > 0 && trace.PlanCoverage.TotalItems > 0)
            {
                findings.Add(new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = "PLAN-005",
                    Category       = "Plan",
                    Title          = $"{uncovered} plan item(s) without task coverage",
                    Description    = $"{uncovered} plan item(s) have no associated tasks — implementation cannot be verified.",
                    Severity       = uncovered > 3 ? "High" : "Medium",
                    Status         = "Failed",
                    Recommendation = "Create implementation tasks for all uncovered plan items.",
                });

                gaps.Add(new RuleGap
                {
                    GapArea     = "Missing Task Coverage",
                    Description = $"{uncovered} plan item(s) with no associated tasks",
                    Severity    = "Medium",
                });
            }
        }

        return Result(findings, gaps);
    }

    private RulePackResult Result(List<RuleFinding> f, List<RuleGap> g) =>
        new() { RulePackId = RulePackId, RulePackName = RulePackName, Findings = f, Gaps = g };
}
