using BirkNext.ApiReview;
using BirkNext.Integrations;
using BirkNext.PerformanceTests;
using BirkNext.ProjectActivity;

namespace BirkNext.Web.Services.ProjectActivity;

/// <summary>
/// Activity events for completed reviews and runs. The review's own outcome is preserved ("2 findings · 3 of 4 targets reviewed", "Blocked",
/// "Execution failed: TLS trust failed") — a completed operation is never normalized to success or "passed". Keyed by the run's own identity,
/// so reopening or re-reporting the same run is the same event.
/// </summary>
public static class ReviewActivity
{
    public static ProjectActivityRecordRequest ApiQuality(ApiReviewReport report, string? environmentId, string? environmentName)
    {
        var completed = report.Targets.Count(t => t.Status is ApiReviewTargetStatus.Completed or ApiReviewTargetStatus.PartiallyCompleted);
        var outcome = $"{report.Findings.Count} finding{(report.Findings.Count == 1 ? "" : "s")} · {completed} of {report.Targets.Count} target{(report.Targets.Count == 1 ? "" : "s")} reviewed";
        return new()
        {
            Type = ProjectActivityType.ApiQualityReviewCompleted, Status = ProjectActivityStatus.Completed, SourceModule = "API Quality Review",
            Title = "API Quality Review completed", Summary = "Read-only review of the selected API targets.", Outcome = outcome,
            EnvironmentId = environmentId, EnvironmentName = environmentName, RelatedEntityType = "ApiQualityReview",
            RelatedEntityId = $"{environmentId}:{report.GeneratedAt.UtcTicks}", Route = "api-quality-review",
            IdempotencyKey = $"aqr:{environmentId}:{report.GeneratedAt.UtcTicks}",
        };
    }

    public static ProjectActivityRecordRequest IntegrationQuality(IntegrationReviewResult result, string? environmentId, string? environmentName) => new()
    {
        Type = ProjectActivityType.IntegrationQualityReviewCompleted, Status = ProjectActivityStatus.Completed, SourceModule = "Integration Quality Review",
        Title = "Integration Quality Review completed", Outcome = $"{result.Outcome} · {result.Findings.Count} finding{(result.Findings.Count == 1 ? "" : "s")}",
        EnvironmentId = environmentId, EnvironmentName = environmentName, RelatedEntityType = "IntegrationReviewRun", RelatedEntityId = result.RunId.ToString("D"),
        Route = "integration-quality-review", IdempotencyKey = $"iqr:{result.RunId:N}",
    };

    /// <summary>A finished performance run: Completed keeps its verdict; a failed, timed-out or blocked run is a Failed event with its reason.
    /// A cancelled run is the tester's own decision and is not recorded. Null for a run that is still active or cancelled.</summary>
    public static ProjectActivityRecordRequest? PerformanceRun(PerformanceTestRun run, string? environmentId, string? environmentName)
    {
        if (run.IsActive || run.State == PerformanceRunState.Cancelled) return null;
        var failed = run.State != PerformanceRunState.Completed;
        return new()
        {
            Type = failed ? ProjectActivityType.PerformanceRunFailed : ProjectActivityType.PerformanceRunCompleted,
            Status = failed ? ProjectActivityStatus.Failed : ProjectActivityStatus.Completed, SourceModule = "Performance Test Review",
            Title = failed ? "Performance Test failed" : "Performance Test completed",
            Summary = failed ? run.StateReason : null,
            Outcome = failed ? PerformanceTestPresentation.Label(run.State) : $"Verdict: {run.Verdict}",
            EnvironmentId = environmentId, EnvironmentName = environmentName, RelatedEntityType = "PerformanceRun", RelatedEntityId = run.RunId.ToString("D"),
            Route = "performance-test-review", IdempotencyKey = $"performance-run:{run.RunId:N}",
        };
    }

    /// <summary>
    /// A manually run container network check: one event when it failed (keyed by target + failure + day, so repeated checks are not noise) or
    /// when a local TLS trust repair was applied (keyed by the managed bundle). A passing check without a repair is not activity.
    /// </summary>
    public static ProjectActivityRecordRequest? NetworkValidation(PerformanceReachability reach, string? environmentId, string? environmentName)
    {
        var host = PerformanceTlsPresentation.Host(reach.TargetOrigin);
        if (reach is { Reachable: true, TlsRepair: { State: TlsRepairStates.Applied } repair })
            return new()
            {
                Type = ProjectActivityType.PerformanceTlsTrustRepaired, Status = ProjectActivityStatus.Configured, SourceModule = "Performance Test Review",
                Title = "Performance TLS trust repaired automatically", Summary = $"{host} · missing intermediate validated against a trusted root · server chain still incomplete",
                EnvironmentId = environmentId, EnvironmentName = environmentName, RelatedEntityType = "TlsRepair", RelatedEntityId = repair.BundleSha256,
                Route = "admin/system-settings?section=performance-test-engines&focus=container-tls-trust", IdempotencyKey = $"tls-repair:{host}:{repair.BundleSha256}",
            };
        if (reach.Reachable) return null;
        var cause = reach.Chain is { } chain ? TlsFailureClasses.Label(chain.FailureClass) : PerformanceNetworkStages.Classification(reach.State);
        return new()
        {
            Type = ProjectActivityType.PerformanceValidationFailed, Status = ProjectActivityStatus.Failed, SourceModule = "Performance Test Review",
            Title = "Performance runtime validation failed", Summary = $"{host} · {cause}", Outcome = PerformanceNetworkStages.Classification(reach.State),
            EnvironmentId = environmentId, EnvironmentName = environmentName, RelatedEntityType = "NetworkCheck", Route = "performance-test-review",
            IdempotencyKey = $"network-failed:{host}:{reach.Chain?.FailureClass ?? reach.State}:{reach.CheckedAt.UtcDateTime:yyyyMMdd}",
        };
    }
}
