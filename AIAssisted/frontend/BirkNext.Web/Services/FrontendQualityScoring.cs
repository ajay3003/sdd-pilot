using BirkNext.Applicability;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend Quality Review on the shared scoring semantics. An engine that did not assess the target (unavailable, blocked, timed out, engine
/// error) is a coverage gap — <see cref="CheckOutcome.Unavailable"/> — never a failed project; a disabled or not-applicable engine (e.g. the WASM
/// engines on a non-WASM site) is excluded. Category scores are null when not assessed and the overall score averages assessed categories only
/// (<see cref="ScoreSemantics.AverageAssessed"/>, applied in <see cref="FrontendQualityReviewService"/>).
/// </summary>
public static class FrontendQualityScoring
{
    public static CheckOutcome EngineOutcome(FrontendQualityEngineExecutionState state) => state switch
    {
        FrontendQualityEngineExecutionState.Assessed => CheckOutcome.Informational,
        FrontendQualityEngineExecutionState.NotApplicable or FrontendQualityEngineExecutionState.Disabled => CheckOutcome.NotApplicable,
        _ => CheckOutcome.Unavailable,
    };

    /// <summary>Tool coverage of a run: which engines assessed the target. Never a quality figure.</summary>
    public static ReviewCoverage EngineCoverage(IEnumerable<FrontendQualityEngineOutcome> outcomes) =>
        ScoreSemantics.Coverage(outcomes.Select(o => EngineOutcome(o.ExecutionState)).ToList());
}
