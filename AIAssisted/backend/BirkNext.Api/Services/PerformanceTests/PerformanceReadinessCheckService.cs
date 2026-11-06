using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// The coordinated "Check readiness" (explicit action only): evaluate the saved definition, then — when eligible — run the existing bounded
/// container-network check (one GET of the target origin from the k6 container, same network/proxy/CA as a run; never a load test, never a
/// business request, never an authenticated endpoint) and evaluate again, so the result includes target connectivity.
/// Not eligible: the provider/runtime is unavailable, or the environment/target is refused by the safety rules. Recent evidence made under the same
/// network/TLS configuration is reused (bounded by PerformanceTests:Container:NetworkCheckValidMinutes); concurrent checks share one probe.
/// </summary>
public sealed class PerformanceReadinessCheckService(PerformanceTestReadinessService readiness, PerformanceTestProviderRegistry providers, PerformanceTestOptions options)
{
    /// <summary>Upper bound a caller waits for the probe (the container itself is bounded at 90 s; TLS diagnosis adds at most a few seconds).</summary>
    public static readonly TimeSpan ProbeBound = TimeSpan.FromSeconds(150);

    public Task<PerformanceReadinessCheck> CheckAsync(PerformanceTestDefinition d, PerformanceTestDataProfile? data, bool hasActiveBaseline,
        BirkNext.LocalHttpsProxy.AuthenticatedReviewIdentity? identity, CancellationToken ct) =>
        CoordinateAsync(
            token => readiness.EvaluateAsync(d, data, hasActiveBaseline, token, identity),
            token => providers.CheckReachabilityOnceAsync(d, token),
            SafetyRefusal(d, options),
            providers.LastReachability(d.ProviderId, d.TargetOrigin),
            ProbeBound, ct);

    /// <summary>The environment/target refusal a network check would get (the same rules as the explicit network-check endpoint), or null.</summary>
    public static string? SafetyRefusal(PerformanceTestDefinition d, PerformanceTestOptions options) =>
        PerformanceTestSafety.EnvironmentBlock(d.EnvironmentType) ?? PerformanceTestSafety.Origin(d.TargetOrigin, d.EnvironmentType, options).Error;

    /// <summary>The coordination itself, over its inputs (tested without containers or network).</summary>
    internal static async Task<PerformanceReadinessCheck> CoordinateAsync(Func<CancellationToken, Task<PerformanceTestReadiness>> evaluate,
        Func<CancellationToken, Task<PerformanceReachability>> probe, string? safetyRefusal, PerformanceReachability? previous, TimeSpan bound, CancellationToken ct)
    {
        var first = await evaluate(ct);
        var steps = new List<PerformanceReadinessCheckStep>
        {
            new("definition", "Checking definition", "Done", "The saved version was evaluated (scenario, workload, thresholds, test data, authentication)."),
            EnvironmentStep(first),
        };
        var network = first.Items.FirstOrDefault(i => i.Key == "network");
        string outcome, detail;
        PerformanceReachability? evidence = previous;
        var result = first;
        if (network is null)
            (outcome, detail) = (PerformanceConnectivityOutcomes.NotRun, "Target connectivity is not part of this definition's readiness.");
        else if (safetyRefusal is not null)
            (outcome, detail) = (PerformanceConnectivityOutcomes.SkippedSafety, $"Not checked — safety restriction: {safetyRefusal}");
        else if (network.Action != PerformanceReadinessActions.CheckNetwork && network.State == PerformanceReadinessState.Optional && network.Status is null)
            (outcome, detail) = (PerformanceConnectivityOutcomes.SkippedRuntimeUnavailable,
                "Not checked — the container runtime, k6 image or provider is not available. Connectivity is checked from the load-generator container once it is.");
        else if (network.Status is "Not checked" or "Stale")
        {
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(bound);
                evidence = await probe(bounded.Token).WaitAsync(bounded.Token);
                (outcome, detail) = (PerformanceConnectivityOutcomes.Checked, evidence.Reachable
                    ? $"Checked now from the k6 container: {(evidence.HttpStatus is { } s ? $"the base target answered HTTP {s}" : "the target answered")}. One request; not a load test."
                    : $"Checked now from the k6 container: {PerformanceTestReadinessService.ProbeHeadline(evidence.State)}.");
                result = await evaluate(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                (outcome, detail) = (PerformanceConnectivityOutcomes.TimedOut,
                    $"The container network check did not finish within {bound.TotalSeconds:0} s. The target is not verified; use Check container network to try again.");
            }
        }
        else
            (outcome, detail) = (PerformanceConnectivityOutcomes.ReusedRecent, previous is { } p
                ? $"Reused the check from {p.CheckedAt.ToUniversalTime():HH:mm} UTC (same network and TLS configuration; still valid). Use Check container network to re-check."
                : "Reused the most recent container network check.");
        steps.Add(new("connectivity", "Checking target connectivity", outcome switch
        {
            PerformanceConnectivityOutcomes.Checked or PerformanceConnectivityOutcomes.ReusedRecent => evidence?.Reachable == true ? "Done" : "Attention",
            PerformanceConnectivityOutcomes.TimedOut => "Attention",
            _ => "Skipped",
        }, detail));
        steps.Add(new("blockers", "Evaluating blockers", result.Ready ? "Done" : "Attention",
            result.Ready ? "No blocker: the run can be started explicitly." : $"{result.Blockers.Count} blocker(s) — see the readiness items."));
        return new PerformanceReadinessCheck { Readiness = result, Connectivity = outcome, ConnectivityDetail = detail, Reachability = evidence, Steps = steps };
    }

    private static PerformanceReadinessCheckStep EnvironmentStep(PerformanceTestReadiness r)
    {
        var setup = r.Items.Where(i => i.Key is "runtime" or "image" or "provider").ToList();
        var blocking = setup.Where(i => i.Blocking).ToList();
        return new("environment", "Checking execution environment", blocking.Count == 0 ? "Done" : "Attention",
            blocking.Count == 0 ? (setup.Count == 0 ? "No execution-setup item was reported." : "Container runtime, k6 image and provider are available.")
                : string.Join(" ", blocking.Select(i => i.Headline.Length > 0 ? i.Headline : i.Label)));
    }
}
