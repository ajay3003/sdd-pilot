using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using BirkNext.Api.Services.FrontendQualityEngines;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>
/// The engine status endpoint probes expensive Layer 3 readiness only for the engines a review declares active.
/// Inactive engines are never contacted (no delay) and are reported as "not active for review".
/// </summary>
public sealed class FrontendQualityEngineReadinessFilterTests
{
    private sealed class CountingProvider(FrontendQualityEngineId id, TimeSpan delay = default) : IFrontendQualityEngineReadinessProvider
    {
        public FrontendQualityEngineId EngineId => id;
        public int Calls { get; private set; }
        public async Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct)
        {
            Calls++;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            return new(id, true, null, DateTime.UtcNow);
        }
    }

    private static (FrontendQualityEngineReadinessAggregator Aggregator, Dictionary<FrontendQualityEngineId, CountingProvider> Providers) Aggregator(TimeSpan slow = default)
    {
        var providers = new Dictionary<FrontendQualityEngineId, CountingProvider>
        {
            [FrontendQualityEngineId.BrowserRuntime] = new(FrontendQualityEngineId.BrowserRuntime),
            [FrontendQualityEngineId.Accessibility] = new(FrontendQualityEngineId.Accessibility),
            [FrontendQualityEngineId.Lighthouse] = new(FrontendQualityEngineId.Lighthouse, slow),
            [FrontendQualityEngineId.PassiveSecurity] = new(FrontendQualityEngineId.PassiveSecurity),
        };
        return (new FrontendQualityEngineReadinessAggregator(providers.Values, NullLogger<FrontendQualityEngineReadinessAggregator>.Instance), providers);
    }

    private static FrontendQualityEngineStatusService Service(IFrontendQualityEngineReadinessAggregator aggregator)
    {
        var interpreter = new FrontendQualityEngineLegacyConfigInterpreter(
            NullLogger<FrontendQualityEngineLegacyConfigInterpreter>.Instance,
            new ConfigurationBuilder().Build(),
            Options.Create(new FrontendBrowserRuntimeOptions()),
            Options.Create(new FrontendAccessibilityOptions()),
            Options.Create(new FrontendLighthouseOptions()));
        return new FrontendQualityEngineStatusService(aggregator, interpreter, NullLogger<FrontendQualityEngineStatusService>.Instance);
    }

    [Fact]
    public async Task CheckAsync_ProbesOnlyRequestedEngines_AndMarksOthersNotChecked()
    {
        var (aggregator, providers) = Aggregator();

        var results = await aggregator.CheckAsync([FrontendQualityEngineId.BrowserRuntime], CancellationToken.None);

        providers[FrontendQualityEngineId.BrowserRuntime].Calls.Should().Be(1);
        providers[FrontendQualityEngineId.Accessibility].Calls.Should().Be(0);
        providers[FrontendQualityEngineId.Lighthouse].Calls.Should().Be(0);
        providers[FrontendQualityEngineId.PassiveSecurity].Calls.Should().Be(0);
        results.Should().HaveCount(4, "every engine still gets a readiness record");
        results[FrontendQualityEngineId.BrowserRuntime].IsAvailable.Should().BeTrue();
        results[FrontendQualityEngineId.Lighthouse].Reason.Should().Be(FrontendQualityEngineReadinessReason.NotChecked);
        results[FrontendQualityEngineId.Lighthouse].IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_EmptyList_ProbesNothing()
    {
        var (aggregator, providers) = Aggregator();

        var results = await aggregator.CheckAsync([], CancellationToken.None);

        providers.Values.Sum(p => p.Calls).Should().Be(0);
        results.Values.Should().OnlyContain(r => r.Reason == FrontendQualityEngineReadinessReason.NotChecked);
    }

    [Fact]
    public async Task CheckAllAsync_LegacyBehaviour_ProbesEveryEngine()
    {
        var (aggregator, providers) = Aggregator();

        await aggregator.CheckAllAsync(CancellationToken.None);

        providers.Values.Should().OnlyContain(p => p.Calls == 1);
    }

    [Fact]
    public async Task InactiveSlowEngine_DoesNotDelayActiveEngineStatus()
    {
        var (aggregator, _) = Aggregator(slow: TimeSpan.FromSeconds(5));
        var started = DateTime.UtcNow;

        await aggregator.CheckAsync([FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineId.PassiveSecurity], CancellationToken.None);

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2), "the slow Lighthouse provider was not active and must not be awaited");
    }

    [Fact]
    public async Task StatusService_WithReadinessEngines_ReportsInactiveEnginesAsNotActiveForReview()
    {
        var (aggregator, providers) = Aggregator();
        var service = Service(aggregator);
        var query = new FrontendQualityEngineStatusQuery(ReviewAuthenticationMode.Anonymous,
            new FrontendQualityEngineSelectionContext(
                new Dictionary<FrontendQualityEngineId, bool> { [FrontendQualityEngineId.BrowserRuntime] = true },
                [FrontendQualityEngineId.BrowserRuntime]));

        var report = await service.GetStatusAsync(query);

        report.Engines.Should().HaveCount(4);
        providers[FrontendQualityEngineId.BrowserRuntime].Calls.Should().Be(1);
        providers.Values.Where(p => p.EngineId != FrontendQualityEngineId.BrowserRuntime).Should().OnlyContain(p => p.Calls == 0);
        var lighthouse = report.Engines.Single(e => e.EngineId == FrontendQualityEngineId.Lighthouse);
        lighthouse.Reasons.Should().Contain(FrontendQualityEngineUnavailableReason.NotActiveForReview);
        lighthouse.Reasons.Should().NotContain(FrontendQualityEngineUnavailableReason.RuntimeUnavailable);
        lighthouse.Available.Should().BeFalse();
        lighthouse.Layer3Readiness.Reason.Should().Be(FrontendQualityEngineReadinessReason.NotChecked);
    }

    [Fact]
    public async Task StatusService_WithoutReadinessEngines_KeepsLegacyProbeAll()
    {
        var (aggregator, providers) = Aggregator();
        var service = Service(aggregator);

        await service.GetStatusAsync(new FrontendQualityEngineStatusQuery(ReviewAuthenticationMode.Anonymous,
            new FrontendQualityEngineSelectionContext(new Dictionary<FrontendQualityEngineId, bool>())));

        providers.Values.Should().OnlyContain(p => p.Calls == 1);
    }
}
