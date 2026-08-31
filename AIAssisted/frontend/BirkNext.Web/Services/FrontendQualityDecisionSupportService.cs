using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class FrontendQualityDecisionSupportService
{
    private static readonly FrontendQualityEngineExecutionState[] ReviewFailureStates =
    [
        FrontendQualityEngineExecutionState.Disabled,
        FrontendQualityEngineExecutionState.Unavailable,
        FrontendQualityEngineExecutionState.SafetyBlocked,
        FrontendQualityEngineExecutionState.TimedOut,
        FrontendQualityEngineExecutionState.Cancelled,
        FrontendQualityEngineExecutionState.EngineError,
    ];

    public static List<FrontendQualityManualReviewItem> BuildManualReviewItems(
        IReadOnlyList<FrontendQualityLogicalIssue> issues,
        IReadOnlyList<FrontendQualityEngineOutcome> outcomes)
    {
        var items = issues
            .Where(issue => issue.ReviewDisposition == FrontendQualityReviewDisposition.ManualVerificationRequired)
            .Select(issue => new FrontendQualityManualReviewItem
            {
                Title = issue.CanonicalTitle,
                Reason = "Automated evidence cannot determine the final result for this issue.",
                Source = string.Join(", ", issue.Sources.Select(SourceLabel)),
                RelatedLogicalId = issue.LogicalId,
                Severity = issue.PrimarySeverity,
            }).ToList();

        foreach (var outcome in outcomes)
        {
            items.AddRange(outcome.ManualTestingObligations.Select(obligation => new FrontendQualityManualReviewItem
            {
                Title = $"{outcome.DisplayName} manual verification",
                Reason = obligation,
                Source = outcome.DisplayName,
            }));
        }

        return items
            .DistinctBy(item => (item.Title, item.Reason, item.Source, item.RelatedLogicalId))
            .OrderBy(item => item.Severity ?? FrontendQualitySeverity.Info)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();
    }

    public static FrontendQualityReleaseDisposition EvaluateReleaseDisposition(
        FrontendQualityCoverage coverage,
        IReadOnlyList<FrontendQualityEngineOutcome> outcomes,
        IReadOnlyList<FrontendQualityLogicalIssue> issues,
        IReadOnlyList<FrontendQualityManualReviewItem> manualItems,
        FrontendQualityReleasePolicySettings policy)
    {
        if (coverage.RequiredCoverageState == FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment)
            return FrontendQualityReleaseDisposition.Blocked;

        var blockingIds = policy.BlockingLogicalIssueIds.ToHashSet(StringComparer.Ordinal);
        if (issues.Any(issue => blockingIds.Contains(issue.LogicalId)))
            return FrontendQualityReleaseDisposition.Blocked;

        if (coverage.RequiredCoverageState == FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed)
            return FrontendQualityReleaseDisposition.ReviewRequired;

        if (manualItems.Count > 0)
            return FrontendQualityReleaseDisposition.ReviewRequired;

        if (policy.ReviewOptionalEngineFailures && outcomes.Any(outcome =>
                outcome.Requirement == FrontendQualityEngineRequirement.Optional &&
                outcome.Enabled && ReviewFailureStates.Contains(outcome.ExecutionState)))
            return FrontendQualityReleaseDisposition.ReviewRequired;

        return FrontendQualityReleaseDisposition.NoAutomatedBlockDetected;
    }

    public static string SourceLabel(FrontendQualityEngineId id) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => "Static Security",
        FrontendQualityEngineId.PassivePerformance => "Passive Performance",
        FrontendQualityEngineId.BrowserRuntime => "Browser Runtime",
        FrontendQualityEngineId.Accessibility => "Accessibility / axe-core",
        FrontendQualityEngineId.Lighthouse => "Lighthouse",
        FrontendQualityEngineId.PassiveSecurity => "Passive Security / ZAP",
        _ => id.ToString(),
    };

    public static string ExecutionStateLabel(FrontendQualityEngineExecutionState state) => state switch
    {
        FrontendQualityEngineExecutionState.SafetyBlocked => "Safety blocked",
        FrontendQualityEngineExecutionState.TimedOut => "Timed out",
        FrontendQualityEngineExecutionState.EngineError => "Engine error",
        FrontendQualityEngineExecutionState.NotApplicable => "Not applicable",
        _ => state.ToString(),
    };
}
