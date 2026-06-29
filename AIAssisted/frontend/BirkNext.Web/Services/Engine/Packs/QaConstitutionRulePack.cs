using BirkNext.Web.Models;

namespace BirkNext.Web.Services.Engine.Packs;

/// <summary>
/// QA Auditor rule pack: Constitution coverage gaps and violations.
/// Requires <see cref="RuleContext.ComplianceReport"/> to be pre-populated
/// (by <see cref="IConstitutionComplianceService"/> before the engine runs).
///
/// Rules:
///   CONST-001 — Constitution rule not covered by any artifact
///   CONST-002 — Constitution rule only partially covered
///   CONST-003 — Constitution violation detected in plan
/// </summary>
public sealed class QaConstitutionRulePack : IRulePack
{
    public string RulePackId   => "qa-constitution";
    public string RulePackName => "Constitution Coverage";

    public RulePackResult Execute(RuleContext context)
    {
        var findings = new List<RuleFinding>();
        var gaps     = new List<RuleGap>();

        if (context.Constitution is null || context.ComplianceReport is null)
            return Result(findings, gaps);

        var report = context.ComplianceReport;

        // CONST-001: Rule not covered by any loaded artifact
        foreach (var r in report.Results.Where(r => r.Status == ComplianceStatus.Missing))
        {
            var sev = r.RuleType switch
            {
                ConstitutionRuleType.Principle  => "Critical",
                ConstitutionRuleType.Standard   => "High",
                ConstitutionRuleType.Constraint => "High",
                _                               => "Medium",
            };

            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "CONST-001",
                Category       = "Constitution",
                Title          = $"Constitution rule {r.RuleId} not covered by any artifact",
                Description    = $"Rule '{r.RuleTitle}' ({r.RuleType}) has no coverage in the Specification, Plan, or Tasks.",
                Severity       = sev,
                Status         = "Failed",
                AffectedItem   = r.RuleId,
                Recommendation = $"Add coverage for {r.RuleId} to the specification, plan, and task list.",
            });

            gaps.Add(new RuleGap
            {
                GapArea     = "Missing Constitution Coverage",
                Description = $"{r.RuleId}: {r.RuleTitle}",
                ItemId      = r.RuleId,
                ItemTitle   = r.RuleTitle,
                Severity    = sev,
            });
        }

        // CONST-002: Rule partially covered (some artifacts mention it, others do not)
        foreach (var r in report.Results.Where(r => r.Status == ComplianceStatus.Partial))
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "CONST-002",
                Category       = "Constitution",
                Title          = $"Constitution rule {r.RuleId} only partially covered",
                Description    = $"Rule '{r.RuleTitle}' is not consistently referenced across all loaded artifacts.",
                Severity       = "Medium",
                Status         = "Failed",
                AffectedItem   = r.RuleId,
                Recommendation = $"Extend coverage for {r.RuleId} across all loaded artifacts.",
            });
        }

        // CONST-003: Explicit violation in plan (failed gate or non-compliant check item)
        foreach (var v in report.Violations)
        {
            var sev = v.Severity switch
            {
                ViolationSeverity.Critical => "Critical",
                ViolationSeverity.High     => "High",
                _                          => "Medium",
            };

            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "CONST-003",
                Category       = "Compliance",
                Title          = $"Constitution violation: {v.RuleId} in {v.Artifact}",
                Description    = v.Issue,
                Severity       = sev,
                Status         = "Failed",
                AffectedItem   = $"{v.Artifact}: {v.RuleId}",
                Recommendation = $"Resolve the violation for {v.RuleId} in the plan.",
            });
        }

        return Result(findings, gaps);
    }

    private RulePackResult Result(List<RuleFinding> f, List<RuleGap> g) =>
        new() { RulePackId = RulePackId, RulePackName = RulePackName, Findings = f, Gaps = g };
}
