using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Generic, ordered journey execution: for each step the first registered executor that accepts it acts, otherwise the first observer that
/// can see it observes, otherwise the step is Not assessed. Every step keeps its own state — an accepted submission is not a processed report,
/// a published message is not a consumed one. When an executed (active) step does not progress, or any step observes an unexpected result,
/// the remaining steps are not reached — nothing is retried or resent. A missing observer leaves only that step Not assessed. The runner knows no project, system or transport.
/// </summary>
public sealed class IntegrationJourneyRunner(IEnumerable<IJourneyStepExecutor> executors, IEnumerable<IJourneyStepObserver> observers, TimeProvider clock)
{
    private static readonly JourneyStepState[] Progressed =
        [JourneyStepState.Configured, JourneyStepState.Generated, JourneyStepState.Sent, JourneyStepState.Accepted, JourneyStepState.Observed, JourneyStepState.Verified];

    /// <summary>Whether a step has something that can produce evidence for it (readiness: a journey with an uncovered mandatory step cannot run).</summary>
    public (bool Executable, bool Observable, string Reason) Coverage(JourneyStepContext probe)
    {
        var executable = executors.Any(executor => executor.CanExecute(probe, out _));
        var observable = observers.Any(observer => observer.CanObserve(probe, out _));
        var reason = executable ? "A registered executor performs this step."
            : observable ? "A registered observer can observe this step (it does not cause it)."
            : "No executor or observer is registered for this step.";
        return (executable, observable, reason);
    }

    public async Task<IntegrationJourneyRun> ExecuteAsync(IntegrationJourneyRun run, TrustedExecutionEnvironment environment, IntegrationJourneyDefinition journey,
        JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, IntegrationDefinition> integrations, CancellationToken ct,
        Func<IntegrationJourneyRun, CancellationToken, Task>? persistProgress = null)
    {
        var results = new List<JourneyStepResult>();
        string? stopReason = null;
        foreach (var step in journey.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var integration = step.IntegrationKey is { } key ? integrations.GetValueOrDefault(key) : null;
            if (stopReason is not null)
            {
                results.Add(Result(step, integration, JourneyStepState.NotAssessed, "", "", $"Not reached: {stopReason}", "Journey runner"));
                continue;
            }

            var context = new JourneyStepContext(run.RunId, run.EnvironmentId, environment, journey, scenario, step, integration, results.ToArray());
            JourneyStepResult result;
            var executed = false;
            try
            {
                var executor = executors.FirstOrDefault(item => item.CanExecute(context, out _));
                var observer = executor is null ? observers.FirstOrDefault(item => item.CanObserve(context, out _)) : null;
                executed = executor is not null;
                result = executor is not null ? await executor.ExecuteAsync(context, ct)
                    : observer is not null ? await observer.ObserveAsync(context, ct)
                    : Result(step, integration, JourneyStepState.NotAssessed, "", "", "No executor or observer is registered for this step.", "Journey runner");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                result = Result(step, integration, JourneyStepState.Unavailable, "", "", $"The step produced no evidence ({ex.GetType().Name}); it was not retried.", "Journey runner");
            }

            result = result with
            {
                StepId = step.StepId, Label = step.Label, IntegrationId = result.IntegrationId ?? integration?.Id,
                Correlation = result.Correlation ?? run.RunId.ToString("N"), CapturedAt = result.CapturedAt ?? clock.GetUtcNow(),
            };
            results.Add(result);
            if (result.State == JourneyStepState.UnexpectedResult || (executed && !Progressed.Contains(result.State)))
                stopReason = $"step \"{step.Label}\" ended {result.State}.";
            if (persistProgress is not null) await persistProgress(run with { Steps = results.ToArray() }, ct);
        }

        var (state, reason) = Overall(journey, results);
        return run with { Steps = results, OverallState = state, StateReason = reason, CompletedAt = clock.GetUtcNow() };
    }

    /// <summary>Completed only when every mandatory step is Verified. Accepted/observed evidence without verification is Partial, never Completed.</summary>
    public static (JourneyRunState State, string Reason) Overall(IntegrationJourneyDefinition journey, IReadOnlyList<JourneyStepResult> results)
    {
        var mandatory = journey.Steps.Where(step => step.Mandatory).Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
        var relevant = results.Where(result => mandatory.Contains(result.StepId)).ToArray();
        if (results.Any(result => result.State == JourneyStepState.UnexpectedResult))
            return (JourneyRunState.Failed, "At least one step observed an unexpected result.");
        if (relevant.Length == mandatory.Count && relevant.All(result => result.State == JourneyStepState.Verified))
            return (JourneyRunState.Completed, "Every mandatory step was verified.");
        var verified = relevant.Count(result => result.State == JourneyStepState.Verified);
        if (results.Any(result => result.State is JourneyStepState.Sent or JourneyStepState.Accepted or JourneyStepState.Observed or JourneyStepState.Verified))
            return (JourneyRunState.Partial, $"{verified} of {mandatory.Count} mandatory steps verified; the rest were accepted/observed only or not verified.");
        return (JourneyRunState.NotVerified, "No step produced accepted, observed or verified evidence.");
    }

    private JourneyStepResult Result(JourneyStepDefinition step, IntegrationDefinition? integration, JourneyStepState state, string evidence, string verification, string reason, string source) =>
        new()
        {
            StepId = step.StepId, Label = step.Label, IntegrationId = integration?.Id, State = state, Evidence = evidence, Verification = verification,
            Reason = reason, EvidenceSource = source, CapturedAt = clock.GetUtcNow(),
        };
}
