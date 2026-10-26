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

        // CONST-001: Rule not referenced by ID in any reviewed document — a documentation traceability gap, not a product failure.
        foreach (var r in report.Results.Where(r => r.Status == ComplianceStatus.Missing))
        {
            var sev = r.RuleType is ConstitutionRuleType.Principle or ConstitutionRuleType.Standard or ConstitutionRuleType.Constraint ? "Medium" : "Low";

            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "CONST-001",
                Category       = "Constitution",
                Title          = $"Documentation gap — constitution rule {r.RuleId} is not referenced in the reviewed documents",
                Description    = $"Rule '{r.RuleTitle}' ({r.RuleType}) is not cited by its ID in the Specification, Plan or Tasks of this Artifact Pack. This checks references, not whether the product follows the rule.",
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

        // CONST-004: rules without a citable ID — reference coverage cannot be determined (no placeholder ID is ever "missing").
        var unmapped = report.Results.Where(r => r.Status == ComplianceStatus.Unknown).ToList();
        if (unmapped.Count > 0)
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "CONST-004",
                Category       = "Constitution",
                Title          = $"No deterministic mapping for {unmapped.Count} constitution rule(s) without an ID",
                Description    = $"These rules have no rule ID in the constitution ({string.Join(", ", unmapped.Take(6).Select(r => r.RuleTitle))}{(unmapped.Count > 6 ? ", …" : "")}), so references to them cannot be checked deterministically. Not assessed.",
                Severity       = "Info",
                Status         = "NotAssessed",
                Recommendation = "Give constitution rules IDs (for example PP-01) if documents should cite them.",
            });

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
