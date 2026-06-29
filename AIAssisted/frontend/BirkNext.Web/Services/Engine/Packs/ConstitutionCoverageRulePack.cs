using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Services.Engine.Packs;

/// <summary>
/// Exposes <see cref="IConstitutionComplianceService"/> as an <see cref="IRulePack"/>,
/// allowing constitution analysis to participate in the shared rule engine pipeline.
///
/// The underlying analysis logic (regex-based rule ID mention detection, violation
/// detection from plan gates) remains in <see cref="IConstitutionComplianceService"/>.
/// This pack translates its rich output into the engine's shared <see cref="RuleFinding"/>
/// format, making constitution coverage composable with other rule packs.
///
/// Score uses the coverage formula: (Compliant × 1.0 + Partial × 0.5) / total × 100.
/// </summary>
public sealed class ConstitutionCoverageRulePack : IRulePack
{
    private readonly IConstitutionComplianceService _service;

    public string RulePackId   => "constitution-coverage";
    public string RulePackName => "Constitution Coverage";

    public ConstitutionCoverageRulePack(IConstitutionComplianceService service)
    {
        _service = service;
    }

    public RulePackResult Execute(RuleContext context)
    {
        var report = _service.Analyze(
            context.Constitution,
            context.Spec,
            context.Plan,
            context.Tasks);

        var findings = report.Results.Select(r => new RuleFinding
        {
            RulePackId   = RulePackId,
            RuleId       = r.RuleId,
            Category     = r.RuleType.ToString(),
            Title        = r.RuleTitle,
            Description  = BuildDescription(r),
            Severity     = r.RuleType switch
            {
                ConstitutionRuleType.Principle  => "High",
                ConstitutionRuleType.Standard   => "High",
                ConstitutionRuleType.Constraint => "Medium",
                _                               => "Low",
            },
            Status = r.Status switch
            {
                ComplianceStatus.Compliant => "Passed",
                ComplianceStatus.Partial   => "Warning",
                _                          => "Failed",
            },
            Recommendation = r.Status is ComplianceStatus.Missing or ComplianceStatus.Partial
                ? $"Add coverage for {r.RuleId} ({r.RuleTitle}) in the missing artifacts."
                : string.Empty,
        }).ToList();

        return new RulePackResult
        {
            RulePackId   = RulePackId,
            RulePackName = RulePackName,
            Findings     = findings,
            Score        = RuleEngine.ComputeCoverageScore(findings),
        };
    }

    private static string BuildDescription(ComplianceResult r)
    {
        var covered = new List<string>(3);
        if (r.HasSpecCoverage)  covered.Add("Spec");
        if (r.HasPlanCoverage)  covered.Add("Plan");
        if (r.HasTaskCoverage)  covered.Add("Tasks");
        return covered.Count > 0
            ? $"Covered in: {string.Join(", ", covered)}."
            : "Not covered in any loaded artifact.";
    }
}
