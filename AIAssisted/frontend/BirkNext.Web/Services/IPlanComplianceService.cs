using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

// Future: Plan vs Constitution/Spec/Tasks compliance analysis and readiness scoring.
// Wire up with AddSingleton<IPlanComplianceService, PlanComplianceService>() when implemented.
public interface IPlanComplianceService
{
    /// <summary>Evaluate how well a plan satisfies a parsed constitution document.</summary>
    Task<PlanComplianceReport> EvaluateAsync(PlanDocument plan, ConstitutionDocument constitution, CancellationToken ct = default);

    /// <summary>Compute a readiness score (0–100) based on gate status, completeness, and risk coverage.</summary>
    int ComputeReadinessScore(PlanDocument plan);

    /// <summary>Identify constitution rules that are not addressed in the plan's constitution check section.</summary>
    IEnumerable<string> FindUncheckedRules(PlanDocument plan, ConstitutionDocument constitution);
}

// ── Result model (stub) ───────────────────────────────────────────────────────

public sealed class PlanComplianceReport
{
    public int ReadinessScore { get; init; }           // 0–100
    public int TotalRules { get; init; }
    public int CoveredRules { get; init; }
    public int UncoveredRules { get; init; }
    public List<string> Gaps { get; init; } = [];      // rule IDs with no plan reference
    public List<string> Warnings { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}
