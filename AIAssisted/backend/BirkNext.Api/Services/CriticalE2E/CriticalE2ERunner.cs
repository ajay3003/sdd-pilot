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
    /// <summary>"Manual" or "Delivery evidence".</summary>
    public string? BuildSource { get; init; }
    /// <summary>The definition's release readiness when the run started (computed by the service: modules, Test Data, outcome …).</summary>
    public CriticalE2EFlowReadiness? Readiness { get; init; }
    /// <summary>Central Test Data values for this run (variable → value). Transient; never part of a result.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
    /// <summary>Why the run's central Test Data cannot be used (missing / needs review). The run is then Blocked before any step.</summary>
    public string? TestDataProblem { get; init; }
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
            Data = request.Data ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
        var boundPage = request.PageId;

        CriticalE2ERunResult Finish(CriticalE2EStatus status, List<CriticalE2EStepResult> steps, string? reason)
        {
            var completedAt = time.GetUtcNow();
            var (evidence, note) = ReleaseEvidence(flow, request);
            var coversDownstream = flow.Boundary?.CoversDownstream == true;
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
                FlowVersion = flow.Version,
                ReleaseEvidence = evidence,
                ReleaseEvidenceNote = note,
                DownstreamVerified = coversDownstream && status == CriticalE2EStatus.Passed,
                DownstreamOutcome = coversDownstream
                    ? status == CriticalE2EStatus.Passed ? "Verified by this flow" : "Not verified"
                    : "Not verified by this flow" + (flow.Boundary?.Downstream is { Length: > 0 } downstream ? $" · {downstream}" : ""),
                OutcomeText = CriticalE2EReleaseRules.OutcomeText(flow),
                BuildSource = string.IsNullOrWhiteSpace(request.BuildId) ? null : request.BuildSource ?? "Manual",
                Boundary = flow.Boundary,
                TestDataAliases = CriticalE2ETestData.Aliases(flow) is { Count: > 0 } aliases ? [.. aliases] : null,
                CompanionPageId = flow.Mode == CriticalE2EExecutionMode.CompanionBrowser ? boundPage : null,
            };
        }

        // A flow that cannot state what it proves is a configuration problem, not a failing system. Both of these are
        // Blocked so that a release gate never reads "the application is broken" when the test was never written.
        if (flow.Steps.Count == 0) return Finish(CriticalE2EStatus.Blocked, [], "The flow has no steps.");
        if (!flow.HasFinalAssertion)
            return Finish(CriticalE2EStatus.Blocked, [], "The flow has no final business assertion, so a pass would mean nothing more than that the steps ran.");

        if (request.TestDataProblem is { } dataProblem) return Finish(CriticalE2EStatus.Blocked, [], dataProblem);

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

        // Every step passed — but a pass is the final outcome's to give, not the step count's.
        if (flow.FinalOutcome is { } outcome)
        {
            var outcomeResult = (await OutcomeAsync(outcome, flow, context, cancellationToken).ConfigureAwait(false)) with { Kind = "Outcome" };
            results.Add(outcomeResult);
            if (outcomeResult.Status != CriticalE2EStatus.Passed)
                return Finish(outcomeResult.Status, results, $"{outcomeResult.Description}: {outcomeResult.SanitizedError ?? outcomeResult.Status.ToString()}");
        }
        else if (!CriticalE2EReleaseRules.HasConcreteOutcome(flow))
            // A final-marked click proves that the click happened. The flow ran; nothing concrete was asserted.
            return Finish(CriticalE2EStatus.Inconclusive, results, "Legacy assertion marker · concrete outcome required. Every action executed, but no assertion proved the business outcome.");
        else
        {
            var final = results.Where(r => r.IsFinalAssertion).ToList();
            if (final.Count == 0 || final.Any(r => r.Status != CriticalE2EStatus.Passed))
                return Finish(CriticalE2EStatus.Blocked, results, "The final business assertion did not run, so the flow proves nothing.");
        }

        logger.LogInformation("Critical E2E run {RunId} passed ({Steps} steps)", runId, results.Count);
        return Finish(CriticalE2EStatus.Passed, results, null);
    }

    /// <summary>Whether this run can count toward a release, and if not, why. Status does not matter here: a failed release run is evidence too.</summary>
    private static (bool Evidence, string? Note) ReleaseEvidence(CriticalE2EFlowDefinition flow, CriticalE2ERunRequest request)
    {
        if (flow.Kind != CriticalE2EFlowKind.Critical) return (false, "Diagnostic flow · never release evidence");
        if (string.IsNullOrWhiteSpace(request.BuildId)) return (false, "Diagnostic result · does not count as release evidence (no build identity)");
        if (request.Readiness is not { ReleaseReady: true } readiness)
            return (false, $"Diagnostic result · does not count as release evidence: {request.Readiness?.Blockers.FirstOrDefault()?.Text ?? "release readiness was not evaluated."}");
        return (true, null);
    }

    /// <summary>The step the final outcome becomes. Browser outcomes are assertions through the same executor; a provider outcome needs a provider.</summary>
    public static CriticalE2EStepDefinition? OutcomeStep(CriticalE2EFinalOutcome outcome)
    {
        var description = CriticalE2EReleaseRules.Describe(outcome);
        CriticalE2EStepDefinition Assert(CompanionActionKind action, CompanionSelector? selector, CompanionTextMatch match = CompanionTextMatch.Equals) => new()
        {
            StepId = "final-outcome", Description = $"Final outcome: {description}", BrowserAction = action, Selector = selector,
            Expected = outcome.Expected, Match = match, IsFinalAssertion = true,
        };
        return outcome.Kind switch
        {
            CriticalE2EOutcomeKind.UrlEquals => Assert(CompanionActionKind.AssertRoute, null),
            CriticalE2EOutcomeKind.UrlContains => Assert(CompanionActionKind.AssertRoute, null, CompanionTextMatch.Contains),
            CriticalE2EOutcomeKind.ElementVisible => Assert(CompanionActionKind.AssertVisible, outcome.Selector),
            CriticalE2EOutcomeKind.ElementHidden => Assert(CompanionActionKind.AssertHidden, outcome.Selector),
            CriticalE2EOutcomeKind.TextEquals => Assert(CompanionActionKind.AssertText, outcome.Selector),
            CriticalE2EOutcomeKind.TextContains or CriticalE2EOutcomeKind.ListContains => Assert(CompanionActionKind.AssertText, outcome.Selector, CompanionTextMatch.Contains),
            CriticalE2EOutcomeKind.ValueEquals => Assert(CompanionActionKind.AssertValue, outcome.Selector),
            CriticalE2EOutcomeKind.SuccessMessageVisible => outcome.Selector is { } region
                ? Assert(CompanionActionKind.AssertText, region, CompanionTextMatch.Contains)
                : Assert(CompanionActionKind.AssertVisible, new CompanionSelector { Kind = CompanionSelectorKind.Text, Value = outcome.Expected ?? "" }),
            _ => null,
        };
    }

    private async Task<CriticalE2EStepResult> OutcomeAsync(CriticalE2EFinalOutcome outcome, CriticalE2EFlowDefinition flow, CriticalE2ERunContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var step = OutcomeStep(outcome);
        if (step is null)
            // No approved verification provider is registered for Critical E2E: the state is unverified, never assumed.
            return CriticalE2EStepOutcome.From(new CriticalE2EStepDefinition { StepId = "final-outcome", Description = $"Final outcome: {CriticalE2EReleaseRules.Describe(outcome)}", IsFinalAssertion = true },
                now, now, CriticalE2EStatus.Inconclusive, error: $"No verification provider {(outcome.ProviderId is { Length: > 0 } id ? $"'{id}' " : "")}is available, so the business state is not verified.");
        if (flow.Mode != CriticalE2EExecutionMode.CompanionBrowser || executors.FirstOrDefault(e => e.CanExecute(step)) is not { } executor)
            return CriticalE2EStepOutcome.From(step, now, now, CriticalE2EStatus.Blocked, error: "No executor can check this outcome.");
        try { return await executor.ExecuteAsync(step, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CriticalE2EStepOutcome.From(step, now, time.GetUtcNow(), CriticalE2EStatus.Cancelled, error: "Cancelled."); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Critical E2E final outcome of run {RunId} threw", context.RunId);
            return CriticalE2EStepOutcome.From(step, now, time.GetUtcNow(), CriticalE2EStatus.Blocked, error: "The outcome could not be checked.");
        }
    }
}
