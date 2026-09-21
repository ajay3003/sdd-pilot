using BirkNext.CriticalE2E;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.CriticalE2E;

public sealed record CriticalE2ERunRequest
{
    public required CriticalE2EFlowDefinition Flow { get; init; }
    public AuthenticatedReviewIdentity? ApiIdentity { get; init; }
    /// <summary>The approved application origin browser steps act on. Comes from the paired companion session, never from the flow.</summary>
    public string? TargetOrigin { get; init; }
    /// <summary>The live page the whole run is bound to. A run never follows the user to another tab.</summary>
    public string? PageId { get; init; }
    public string? BuildId { get; init; }
    public string? ReleaseId { get; init; }
    public string? CommitSha { get; init; }
}

/// <summary>
/// Runs one critical flow, whatever its transport. Browser steps and integration steps differ entirely in how they
/// reach the system and not at all in what a result means, so there is one runner and several executors rather than one
/// runner per module.
/// </summary>
public sealed class CriticalE2ERunner(IEnumerable<ICriticalE2EStepExecutor> executors, TimeProvider time, ILogger<CriticalE2ERunner> logger)
{
    public async Task<CriticalE2ERunResult> RunAsync(CriticalE2ERunRequest request, CancellationToken cancellationToken)
    {
        var flow = request.Flow;
        var startedAt = time.GetUtcNow();
        var runId = $"{flow.Id}-{startedAt:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var context = new CriticalE2ERunContext
        {
            Flow = flow, RunId = runId, CorrelationId = CriticalE2ECorrelation.New(startedAt), StartedAt = startedAt,
            ApiIdentity = request.ApiIdentity, TargetOrigin = request.TargetOrigin, PageId = request.PageId,
        };

        CriticalE2ERunResult Finish(CriticalE2EStatus status, List<CriticalE2EStepResult> steps, string? reason)
        {
            var completedAt = time.GetUtcNow();
            return new CriticalE2ERunResult
            {
                RunId = runId, FlowId = flow.Id, FlowName = flow.Name, Module = flow.Module, Mode = flow.Mode,
                ProfileId = flow.ProfileId, EnvironmentId = flow.EnvironmentId,
                StartedAt = startedAt, CompletedAt = completedAt, DurationMs = (completedAt - startedAt).TotalMilliseconds,
                Status = status, CorrelationId = context.CorrelationId, StepResults = steps, FailureReason = reason,
                AutomationBoundary = flow.AutomationBoundary, RequiredForRelease = flow.RequiredForRelease,
                BuildId = request.BuildId, ReleaseId = request.ReleaseId, CommitSha = request.CommitSha,
                EvidenceReferences = steps.Select(s => s.EvidenceReference).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().Cast<string>().ToList(),
                SafeDiagnosticMetadata = new Dictionary<string, string>
                {
                    ["stepsExecuted"] = steps.Count(s => s.Status != CriticalE2EStatus.NotRun).ToString(),
                    ["stepsDefined"] = flow.Steps.Count.ToString(),
                },
            };
        }

        // A flow that cannot state what it proves is a configuration problem, not a failing system. Both of these are
        // Blocked so that a release gate never reads "the application is broken" when the test was never written.
        if (flow.Steps.Count == 0) return Finish(CriticalE2EStatus.Blocked, [], "The flow has no steps.");
        if (!flow.HasFinalAssertion)
            return Finish(CriticalE2EStatus.Blocked, [], "The flow has no final business assertion, so a pass would mean nothing more than that the steps ran.");

        logger.LogInformation("Critical E2E run {RunId} started for flow {FlowId} ({Mode}, correlation {CorrelationId})",
            runId, flow.Id, flow.Mode, context.CorrelationId);

        var results = new List<CriticalE2EStepResult>();
        foreach (var step in flow.Steps)
        {
            if (executors.FirstOrDefault(e => e.CanExecute(step)) is not { } executor)
            {
                results.Add(CriticalE2EStepOutcome.From(step, time.GetUtcNow(), time.GetUtcNow(), CriticalE2EStatus.Blocked,
                    error: "No executor handles this step; the flow definition is incomplete."));
                break;
            }

            CriticalE2EStepResult result;
            try
            {
                result = await executor.ExecuteAsync(step, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var now = time.GetUtcNow();
                results.Add(CriticalE2EStepOutcome.From(step, now, now, CriticalE2EStatus.Cancelled, error: "Cancelled."));
                break;
            }
            catch (Exception ex)
            {
                // The message, never the exception: a stack trace from a target environment is diagnostic noise at best
                // and a leak at worst.
                logger.LogError(ex, "Critical E2E step {StepId} of run {RunId} threw", step.StepId, runId);
                var now = time.GetUtcNow();
                results.Add(CriticalE2EStepOutcome.From(step, now, now, CriticalE2EStatus.Blocked, error: "The step could not be executed."));
                break;
            }

            results.Add(result);
            // Later steps assume the earlier ones happened. Running them anyway produces failures that describe nothing.
            if (result.Status != CriticalE2EStatus.Passed) break;
        }

        // Steps that never ran are recorded as NotRun rather than omitted, so the UI can show where the flow stopped.
        foreach (var skipped in flow.Steps.Skip(results.Count))
            results.Add(CriticalE2EStepOutcome.From(skipped, time.GetUtcNow(), time.GetUtcNow(), CriticalE2EStatus.NotRun));

        var stopped = results.FirstOrDefault(r => r.Status is CriticalE2EStatus.Failed or CriticalE2EStatus.Blocked or CriticalE2EStatus.Cancelled);
        if (stopped is not null)
            return Finish(stopped.Status, results, $"{stopped.Description}: {stopped.SanitizedError ?? stopped.Status.ToString()}");

        // Every step passed — but a pass is the final assertion's to give, not the step count's.
        var final = results.Where(r => r.IsFinalAssertion).ToList();
        if (final.Count == 0 || final.Any(r => r.Status != CriticalE2EStatus.Passed))
            return Finish(CriticalE2EStatus.Blocked, results, "The final business assertion did not run, so the flow proves nothing.");

        logger.LogInformation("Critical E2E run {RunId} passed ({Steps} steps)", runId, results.Count);
        return Finish(CriticalE2EStatus.Passed, results, null);
    }
}
