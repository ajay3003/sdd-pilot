using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// The coordinated Check readiness: target connectivity from the load-generator container is part of it when eligible (one bounded probe, never a
/// load test), reused while recent, skipped with the reason when the runtime is unavailable or the target is refused, and never sent twice at once.
/// </summary>
public sealed class PerformanceReadinessCheckTests
{
    private static PerformanceReadinessItem Network(string? status, PerformanceReadinessState state = PerformanceReadinessState.NeedsConfiguration, bool blocking = true) =>
        new("network", "Target reachability", state, "detail", blocking) { Status = status, Action = status is null && state == PerformanceReadinessState.Optional ? null : PerformanceReadinessActions.CheckNetwork };

    private static PerformanceTestReadiness Readiness(bool ready, params PerformanceReadinessItem[] items) =>
        new() { Ready = ready, Items = [.. items], Blockers = ready ? [] : [.. items.Where(i => i.Blocking).Select(i => i.Label)] };

    private static PerformanceReachability Reach(string state, bool reachable, int? http = null) =>
        new() { State = state, Reachable = reachable, HttpStatus = http, Detail = state, CheckedAt = DateTimeOffset.UtcNow, TargetOrigin = "http://qa.example.test" };

    /// <summary>Evaluations in order (before / after the probe) and a counting probe.</summary>
    private sealed class Script(PerformanceTestReadiness before, PerformanceTestReadiness after, Func<CancellationToken, Task<PerformanceReachability>>? probe = null)
    {
        public int Evaluations, Probes;
        public Task<PerformanceTestReadiness> Evaluate(CancellationToken ct) => Task.FromResult(Interlocked.Increment(ref Evaluations) == 1 ? before : after);
        public Task<PerformanceReachability> Probe(CancellationToken ct) { Interlocked.Increment(ref Probes); return probe?.Invoke(ct) ?? Task.FromResult(Reach("Reachable", true, 200)); }
    }

    private static Task<PerformanceReadinessCheck> Run(Script s, string? safety = null, PerformanceReachability? previous = null, TimeSpan? bound = null, CancellationToken ct = default) =>
        PerformanceReadinessCheckService.CoordinateAsync(s.Evaluate, s.Probe, safety, previous, bound ?? TimeSpan.FromSeconds(5), ct);

    [Fact]
    public async Task Healthy_runtime_and_reachable_target_checks_connectivity_once_and_re_evaluates()
    {
        var s = new Script(Readiness(false, Network("Not checked")), Readiness(true, Network("Reachable", PerformanceReadinessState.Ready, false)));
        var check = await Run(s);
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.Checked);
        s.Probes.Should().Be(1);
        s.Evaluations.Should().Be(2, "readiness is recalculated with the new evidence");
        check.Readiness.Ready.Should().BeTrue();
        check.ConnectivityDetail.Should().Contain("HTTP 200").And.Contain("not a load test");
        check.Steps.Select(x => x.Key).Should().Equal("definition", "environment", "connectivity", "blockers");
        check.Steps.Single(x => x.Key == "connectivity").State.Should().Be("Done");
    }

    [Fact]
    public async Task Unavailable_runtime_skips_the_probe_with_the_reason()
    {
        var s = new Script(Readiness(false, Network(null, PerformanceReadinessState.Optional, false)), Readiness(false));
        var check = await Run(s);
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.SkippedRuntimeUnavailable);
        s.Probes.Should().Be(0);
        check.ConnectivityDetail.Should().Contain("not available");
        check.Steps.Single(x => x.Key == "connectivity").State.Should().Be("Skipped");
    }

    [Fact]
    public async Task A_refused_target_is_never_probed()
    {
        var s = new Script(Readiness(false, Network("Not checked")), Readiness(false));
        var check = await Run(s, safety: "Production targets are never load tested.");
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.SkippedSafety);
        check.ConnectivityDetail.Should().Contain("safety restriction").And.Contain("Production");
        s.Probes.Should().Be(0);
        check.Readiness.Ready.Should().BeFalse();
    }

    [Theory]
    [InlineData("Reachable", true)]
    [InlineData("DnsFailure", false)]
    public async Task Fresh_evidence_under_the_same_configuration_is_reused_without_a_request(string state, bool reachable)
    {
        var item = reachable ? Network(state, PerformanceReadinessState.Ready, false) : Network(state, PerformanceReadinessState.NetworkUnavailable);
        var s = new Script(Readiness(reachable, item), Readiness(reachable, item));
        var check = await Run(s, previous: Reach(state, reachable));
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.ReusedRecent);
        s.Probes.Should().Be(0, "a recent result (success or failure) is reused; Check container network re-checks on purpose");
        s.Evaluations.Should().Be(1);
        check.Steps.Single(x => x.Key == "connectivity").State.Should().Be(reachable ? "Done" : "Attention");
    }

    [Fact]
    public async Task Stale_evidence_after_a_configuration_change_is_checked_again()
    {
        var s = new Script(Readiness(false, Network("Stale")), Readiness(true, Network("Reachable", PerformanceReadinessState.Ready, false)));
        var check = await Run(s, previous: Reach("Reachable", true));
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.Checked);
        s.Probes.Should().Be(1);
    }

    [Theory]
    [InlineData("DnsFailure", "DNS lookup failed")]
    [InlineData("TlsTrustFailure", "TLS trust failed")]
    [InlineData("ConnectionRefused", "refused the connection")]
    [InlineData("Timeout", "timed out")]
    public async Task Probe_failures_are_reported_by_cause_and_never_as_ready(string state, string headline)
    {
        var failed = Readiness(false, Network(state, PerformanceReadinessState.NetworkUnavailable));
        var s = new Script(Readiness(false, Network("Not checked")), failed, _ => Task.FromResult(Reach(state, false)));
        var check = await Run(s);
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.Checked);
        check.ConnectivityDetail.Should().Contain(headline);
        check.Steps.Single(x => x.Key == "connectivity").State.Should().Be("Attention");
        check.Readiness.Ready.Should().BeFalse();
    }

    [Fact]
    public async Task A_probe_that_does_not_finish_within_its_bound_is_reported_as_timed_out()
    {
        var s = new Script(Readiness(false, Network("Not checked")), Readiness(true), _ => new TaskCompletionSource<PerformanceReachability>().Task);
        var check = await Run(s, bound: TimeSpan.FromMilliseconds(50));
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.TimedOut);
        check.Readiness.Ready.Should().BeFalse("the earlier evaluation stands: the target is not verified");
        s.Evaluations.Should().Be(1);
    }

    [Fact]
    public async Task Caller_cancellation_stops_the_check()
    {
        using var cts = new CancellationTokenSource();
        var s = new Script(Readiness(false, Network("Not checked")), Readiness(true), _ => { cts.Cancel(); return new TaskCompletionSource<PerformanceReachability>().Task; });
        var act = () => Run(s, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Reachable_target_does_not_clear_a_missing_credential_or_parameter()
    {
        var credential = new PerformanceReadinessItem("credential", "Execution credential", PerformanceReadinessState.NeedsAuthentication, "Authentication unresolved", true);
        var parameter = new PerformanceReadinessItem("testdata", "Test data", PerformanceReadinessState.NeedsConfiguration, "brukerId is not mapped", true);
        var s = new Script(Readiness(false, Network("Not checked"), credential, parameter),
            Readiness(false, Network("Reachable", PerformanceReadinessState.Ready, false), credential, parameter));
        var check = await Run(s);
        check.Connectivity.Should().Be(PerformanceConnectivityOutcomes.Checked);
        check.Readiness.Ready.Should().BeFalse("network reachability is not authentication or a resolved runtime parameter");
        check.Steps.Single(x => x.Key == "blockers").Detail.Should().Contain("2 blocker(s)");
    }

    // ── Registry: concurrent duplicates share one probe ────────────────────────────────────────────────────────────────────

    private sealed class SlowProvider : IPerformanceTestProvider
    {
        public int Checks;
        public TaskCompletionSource<PerformanceReachability> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "performance.fake";
        public string DisplayName => "Fake";
        public PerformanceProviderCapabilities Capabilities { get; } = new();
        public Task<PerformanceProviderStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(new PerformanceProviderStatus { ProviderId = ProviderId, Availability = ProviderAvailability.Available });
        public IReadOnlyList<string> Validate(PerformanceTestDefinition definition) => [];
        public Task<PerformanceProviderResult> ExecuteAsync(PerformanceProviderInput input, IProgress<string>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Check readiness never starts a load test.");
        public Task<PerformanceReachability> CheckReachabilityAsync(PerformanceTestDefinition definition, CancellationToken ct = default) { Interlocked.Increment(ref Checks); return Gate.Task; }
        public Task<string?> PrepareAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> CleanupOrphansAsync(IReadOnlySet<Guid> activeRunIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public async Task Concurrent_checks_for_the_same_target_share_one_probe_and_a_cancelled_caller_does_not_cancel_it()
    {
        var provider = new SlowProvider();
        var registry = new PerformanceTestProviderRegistry([provider]);
        var d = new PerformanceTestDefinition { Id = "def-1", ProviderId = provider.ProviderId, TargetOrigin = "http://qa.example.test" };
        using var impatient = new CancellationTokenSource();
        var first = registry.CheckReachabilityOnceAsync(d, impatient.Token);
        var second = registry.CheckReachabilityOnceAsync(d);
        registry.ReachabilityInFlight(d.ProviderId, d.TargetOrigin).Should().BeTrue();
        impatient.Cancel();
        await ((Func<Task>)(() => first)).Should().ThrowAsync<OperationCanceledException>();
        provider.Gate.SetResult(Reach("Reachable", true, 200));
        (await second).Reachable.Should().BeTrue();
        provider.Checks.Should().Be(1);
        registry.LastReachability(d.ProviderId, d.TargetOrigin)!.State.Should().Be("Reachable");
        await Task.Delay(50);
        registry.ReachabilityInFlight(d.ProviderId, d.TargetOrigin).Should().BeFalse("a finished probe is not reused as in-flight");
    }
}
