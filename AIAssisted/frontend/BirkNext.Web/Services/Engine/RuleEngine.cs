namespace BirkNext.Web.Services.Engine;

/// <summary>
/// Orchestrates execution of pluggable rule packs against a shared rule context.
/// Packs are independent — an error in one does not prevent others from running.
///
/// Usage:
///   var engine = new RuleEngine();
///   var context = new RuleContext { ... };
///   var results = engine.Run(context, new IRulePack[] { packA, packB });
/// </summary>
public sealed class RuleEngine
{
    /// <summary>
    /// Execute all packs and return one <see cref="RulePackResult"/> per pack.
    /// Order is preserved; failed packs return an error-flagged result with empty findings.
    /// </summary>
    public List<RulePackResult> Run(RuleContext context, IEnumerable<IRulePack> packs)
    {
        var results = new List<RulePackResult>();
        foreach (var pack in packs)
        {
            try
            {
                results.Add(pack.Execute(context));
            }
            catch (Exception ex)
            {
                results.Add(new RulePackResult
                {
                    RulePackId   = pack.RulePackId,
                    RulePackName = pack.RulePackName,
                    Error        = $"Rule pack '{pack.RulePackName}' failed: {ex.Message}",
                });
            }
        }
        return results;
    }

    /// <summary>
    /// Coverage-based score: (Passed × 1.0 + Warning × 0.5) / applicable_checks × 100.
    /// Used by keyword-based rule packs (Standards: WCAG, OWASP, GDPR, ISO 25010).
    /// QA Auditor uses a deduction-based score instead.
    /// </summary>
    public static double ComputeCoverageScore(IEnumerable<RuleFinding> findings)
    {
        var applicable = findings
            .Where(f => f.Status != "NotApplicable")
            .ToList();
        if (applicable.Count == 0) return 100.0;
        return Math.Round(
            applicable.Sum(f => f.Status switch
            {
                "Passed"  => 1.0,
                "Warning" => 0.5,
                _         => 0.0,
            }) / applicable.Count * 100.0, 1);
    }
}
