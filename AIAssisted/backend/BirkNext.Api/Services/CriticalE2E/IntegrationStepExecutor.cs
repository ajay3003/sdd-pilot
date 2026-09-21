using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.CriticalE2E;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// Executes REST and GraphQL steps through the authenticated review gateway — the same path API Quality Review already
/// uses, so a flow never re-enters a URL, never holds a credential and never gets a second way to be authenticated.
///
/// The gateway does not return response bodies, and this executor does not ask it to. A flow asserts on what crosses
/// that boundary safely: the status code, whether a GraphQL response carried data, and how many GraphQL errors came
/// back. Asserting "the record arrived" is then a matter of writing a query that returns data only when it did — the
/// assertion happens where the data already lives.
/// </summary>
public sealed class IntegrationStepExecutor(IAuthenticatedReviewGateway gateway, TimeProvider time, ILogger<IntegrationStepExecutor> logger)
    : ICriticalE2EStepExecutor
{
    public bool CanExecute(CriticalE2EStepDefinition step) =>
        step.IntegrationAction is CriticalE2EIntegrationKind.Http or CriticalE2EIntegrationKind.GraphQl or CriticalE2EIntegrationKind.Poll;

    public async Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        CriticalE2EStepResult Blocked(string reason) => CriticalE2EStepOutcome.From(step, startedAt, time.GetUtcNow(), CriticalE2EStatus.Blocked, error: reason);

        if (context.ApiIdentity is not { } identity) return Blocked("No authenticated API context is configured for this environment.");
        var capabilities = gateway.Resolve(identity);
        if (!capabilities.AuthenticatedApi)
            // Silently falling back to an anonymous request would turn "the API is protected" into a passing flow.
            return Blocked(string.IsNullOrWhiteSpace(capabilities.Reason) ? "Authenticated API access is unavailable for this environment." : capabilities.Reason);

        var target = CriticalE2EVariables.Resolve(step.PathOrOperation, context, startedAt);
        if (string.IsNullOrWhiteSpace(target)) return Blocked("The step has no endpoint or operation configured.");
        var body = CriticalE2EVariables.Resolve(step.Body, context, startedAt) ?? "";
        var expectation = step.Expect ?? new CriticalE2EExpectation();
        var deadline = startedAt + TimeSpan.FromMilliseconds(Math.Clamp(step.TimeoutMs, 500, 600_000));
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(step.PollIntervalMs ?? 1_000, 200, 30_000));
        var polls = step.IntegrationAction == CriticalE2EIntegrationKind.Poll;

        AuthenticatedReviewExecutionOutcome outcome;
        var attempts = 0;
        while (true)
        {
            attempts++;
            outcome = step.IntegrationAction == CriticalE2EIntegrationKind.GraphQl || (polls && !string.IsNullOrWhiteSpace(body))
                ? await gateway.ExecuteGraphQlQueryAsync(identity, target, body, cancellationToken).ConfigureAwait(false)
                : await gateway.ExecuteRestAsync(identity, string.IsNullOrWhiteSpace(step.Method) ? "GET" : step.Method!, target, cancellationToken).ConfigureAwait(false);

            if (!outcome.Executed)
                return Blocked(string.IsNullOrWhiteSpace(outcome.Message) ? $"The request did not execute ({outcome.Status})." : outcome.Message);

            if (Holds(outcome.Result!, expectation) || !polls || time.GetUtcNow() + interval >= deadline) break;
            // Poll on a condition, never on a stopwatch: an event pipeline arrives when it arrives.
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        var result = outcome.Result!;
        var observed = Observe(result, expectation.Observable);
        context.Outputs[step.StepId] = observed;
        var held = Holds(result, expectation);
        logger.LogInformation("Critical E2E integration step {StepId} of run {RunId}: {Observable}={Observed} after {Attempts} attempt(s)",
            step.StepId, context.RunId, expectation.Observable, observed, attempts);

        return CriticalE2EStepOutcome.From(step, startedAt, time.GetUtcNow(),
            held ? CriticalE2EStatus.Passed : CriticalE2EStatus.Failed,
            summary: held ? $"{expectation.Observable} = {observed}{(attempts > 1 ? $" after {attempts} polls" : "")}" : null,
            error: held ? null : $"Expected {expectation.Describe()}; observed {observed}{(attempts > 1 ? $" after {attempts} polls" : "")}.",
            value: observed);
    }

    private static string Observe(AuthenticatedApiExecutionResult result, CriticalE2EObservable observable) => observable switch
    {
        CriticalE2EObservable.GraphQlHasData => (result.GraphQlHasData ?? false).ToString(),
        CriticalE2EObservable.GraphQlErrorCount => (result.GraphQlErrorCount ?? 0).ToString(),
        _ => result.StatusCode.ToString(),
    };

    private static bool Holds(AuthenticatedApiExecutionResult result, CriticalE2EExpectation expectation)
    {
        var observed = Observe(result, expectation.Observable);
        return expectation.Comparison switch
        {
            CriticalE2EComparison.Equals => string.Equals(observed, expectation.ExpectedValue.Trim(), StringComparison.OrdinalIgnoreCase),
            CriticalE2EComparison.Contains => observed.Contains(expectation.ExpectedValue.Trim(), StringComparison.OrdinalIgnoreCase),
            CriticalE2EComparison.NotExists => observed is "0" or "False",
            _ => observed is not ("0" or "False" or ""),
        };
    }
}

/// <summary>
/// Event Hub publication is not implemented in V1.
///
/// It is refused rather than approximated: publishing needs a namespace, an entity and a managed identity that come from
/// the Target Environment's integration configuration, and none of those may be guessed. A step that cannot prove where
/// it would publish is a step that must not publish, so it reports Blocked with the reason and the flow says so.
/// </summary>
public sealed class EventHubStepExecutor(TimeProvider time) : ICriticalE2EStepExecutor
{
    public bool CanExecute(CriticalE2EStepDefinition step) => step.IntegrationAction == CriticalE2EIntegrationKind.EventHub;

    public Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return Task.FromResult(CriticalE2EStepOutcome.From(step, now, now, CriticalE2EStatus.Blocked,
            error: "Event Hub publication is not available in this version. Drive the flow from an API step, or run the publish step manually and record the result."));
    }
}
