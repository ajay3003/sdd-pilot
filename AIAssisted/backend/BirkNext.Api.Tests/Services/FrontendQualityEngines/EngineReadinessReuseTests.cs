using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Services.FrontendQualityEngines.Readiness;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>
/// Roadmap R13: Passive Web Security readiness (a real ZAP container start) took ~13 s on every status request. Status displays now reuse a recent
/// probe for the same configuration; "Check again" and configuration changes always probe; a timed-out check is never reused; concurrent requests
/// share one probe; a reused result keeps the time it was checked.
/// </summary>
public sealed class EngineReadinessReuseTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static FrontendQualityEngineReadiness Ready(DateTime at) => new(FrontendQualityEngineId.PassiveSecurity, true, null, at);
    private static FrontendQualityEngineReadiness NotReady(DateTime at, FrontendQualityEngineReadinessReason reason = FrontendQualityEngineReadinessReason.ContainerRuntimeUnavailable) =>
        new(FrontendQualityEngineId.PassiveSecurity, false, "Container runtime is unavailable.", at, reason);

    [Fact]
    public async Task A_status_request_within_five_minutes_reuses_the_probe_and_says_when_it_was_checked()
    {
        var clock = new Clock(); var reuse = new EngineReadinessReuse(clock); var probes = 0;
        var checkedAt = clock.Now.UtcDateTime;
        Task<FrontendQualityEngineReadiness> Probe() { probes++; return Task.FromResult(Ready(checkedAt)); }

        var first = await reuse.GetAsync("k", Probe, fresh: false, CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(4);
        var second = await reuse.GetAsync("k", Probe, fresh: false, CancellationToken.None);

        probes.Should().Be(1);
        second.CheckedAtUtc.Should().Be(first.CheckedAtUtc, "a reused result keeps the time of its probe, never 'now'");
        clock.Now += TimeSpan.FromMinutes(2);
        await reuse.GetAsync("k", Probe, fresh: false, CancellationToken.None);
        probes.Should().Be(2, "after five minutes a ready result is probed again");
    }

    [Fact]
    public async Task Check_again_and_a_changed_configuration_always_probe()
    {
        var clock = new Clock(); var reuse = new EngineReadinessReuse(clock); var probes = 0;
        Task<FrontendQualityEngineReadiness> Probe() { probes++; return Task.FromResult(Ready(clock.Now.UtcDateTime)); }

        await reuse.GetAsync("enabled|podman", Probe, fresh: false, CancellationToken.None);
        await reuse.GetAsync("enabled|podman", Probe, fresh: true, CancellationToken.None);
        await reuse.GetAsync("disabled|podman", Probe, fresh: false, CancellationToken.None);

        probes.Should().Be(3);
    }

    [Fact]
    public async Task A_not_ready_result_is_reused_briefly_and_a_timed_out_check_never()
    {
        var clock = new Clock(); var reuse = new EngineReadinessReuse(clock); var probes = 0;
        var answer = NotReady(clock.Now.UtcDateTime);
        Task<FrontendQualityEngineReadiness> Probe() { probes++; return Task.FromResult(answer); }

        await reuse.GetAsync("k", Probe, false, CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(20);
        await reuse.GetAsync("k", Probe, false, CancellationToken.None);
        probes.Should().Be(1);
        clock.Now += TimeSpan.FromSeconds(15);
        await reuse.GetAsync("k", Probe, false, CancellationToken.None);
        probes.Should().Be(2, "a fixed runtime shows within 30 seconds");

        answer = NotReady(clock.Now.UtcDateTime, FrontendQualityEngineReadinessReason.CheckTimedOut);
        await reuse.GetAsync("t", Probe, true, CancellationToken.None);
        await reuse.GetAsync("t", Probe, false, CancellationToken.None);
        probes.Should().Be(4, "a timed-out check is not an answer and is never reused");
    }

    [Fact]
    public async Task Concurrent_requests_share_one_probe_and_leaving_cancels_only_the_callers_wait()
    {
        var clock = new Clock(); var reuse = new EngineReadinessReuse(clock); var probes = 0;
        var gate = new TaskCompletionSource<FrontendQualityEngineReadiness>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FrontendQualityEngineReadiness> Probe() { Interlocked.Increment(ref probes); return gate.Task; }

        using var leaving = new CancellationTokenSource();
        var left = reuse.GetAsync("k", Probe, false, leaving.Token);
        var staying = reuse.GetAsync("k", Probe, false, CancellationToken.None);
        leaving.Cancel();

        await left.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        gate.SetResult(Ready(clock.Now.UtcDateTime));
        (await staying).IsAvailable.Should().BeTrue("the shared probe kept running for the caller that stayed");
        probes.Should().Be(1);
    }

    [Fact]
    public void The_reuse_key_follows_every_setting_the_probe_depends_on()
    {
        static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();
        var baseline = PassiveSecurityReadinessProvider.ConfigurationKey(Config(("FrontendPassiveSecurity:Enabled", "true"), ("FrontendQualityCapabilities:PassiveSecurityAllowed", "true")));

        PassiveSecurityReadinessProvider.ConfigurationKey(Config(("FrontendPassiveSecurity:Enabled", "false"), ("FrontendQualityCapabilities:PassiveSecurityAllowed", "true"))).Should().NotBe(baseline);
        PassiveSecurityReadinessProvider.ConfigurationKey(Config(("FrontendPassiveSecurity:Enabled", "true"), ("FrontendQualityCapabilities:PassiveSecurityAllowed", "false"))).Should().NotBe(baseline);
        PassiveSecurityReadinessProvider.ConfigurationKey(Config(("FrontendPassiveSecurity:Enabled", "true"), ("FrontendQualityCapabilities:PassiveSecurityAllowed", "true"), ("FrontendPassiveSecurity:ContainerRuntime", "docker"))).Should().NotBe(baseline);
        PassiveSecurityReadinessProvider.ConfigurationKey(Config(("FrontendPassiveSecurity:Enabled", "true"), ("FrontendQualityCapabilities:PassiveSecurityAllowed", "true"), ("FrontendPassiveSecurity:ContainerNetwork", "zapnet"))).Should().NotBe(baseline);
    }

    private sealed class FreshCountingProvider : IFrontendQualityEngineReadinessProvider, IFreshReadinessProvider
    {
        public int Status, Fresh;
        public FrontendQualityEngineId EngineId => FrontendQualityEngineId.PassiveSecurity;
        public Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct) { Status++; return Task.FromResult(Ready(DateTime.UtcNow)); }
        public Task<FrontendQualityEngineReadiness> CheckFreshAsync(CancellationToken ct) { Fresh++; return Task.FromResult(Ready(DateTime.UtcNow)); }
    }

    [Fact]
    public async Task Check_again_through_the_readiness_endpoint_probes_fresh_while_status_may_reuse()
    {
        var provider = new FreshCountingProvider();
        var aggregator = new FrontendQualityEngineReadinessAggregator([provider], NullLogger<FrontendQualityEngineReadinessAggregator>.Instance);

        await aggregator.CheckAsync([FrontendQualityEngineId.PassiveSecurity], CancellationToken.None);
        await aggregator.RevalidateAsync(FrontendQualityEngineId.PassiveSecurity, CancellationToken.None);

        (provider.Status, provider.Fresh).Should().Be((1, 1));
    }
}
