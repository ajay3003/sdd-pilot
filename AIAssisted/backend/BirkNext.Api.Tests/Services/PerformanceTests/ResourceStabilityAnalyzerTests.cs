using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.Api.Services.PerformanceTests.Resources;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>Resource Stability analysis on synthetic series: never "memory leak", policies required for Regression, warm-up excluded, robust statistics.</summary>
public sealed class ResourceStabilityAnalyzerTests
{
    internal static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
    private const double MiB = 1024 * 1024;

    /// <summary>A sample every <paramref name="interval"/> seconds; <paramref name="memory"/> maps sample index → MiB.</summary>
    internal static List<ResourceSample> Series(int count, Func<int, double?> memory, int interval = 10, string instance = "a", Func<int, double?>? heap = null,
        Func<int, double?>? floor = null, Func<int, double?>? cpu = null, string target = "api")
        => Enumerable.Range(0, count).Select(i => new ResourceSample
        {
            At = T0.AddSeconds(i * interval), ProviderId = ResourceProviderIds.Podman, TargetId = target, InstanceId = instance,
            Memory = new MemoryResourceSample { ContainerMemoryBytes = memory(i) * MiB, ManagedHeapBytes = heap?.Invoke(i) * MiB, GcHeapAfterGcBytes = floor?.Invoke(i) * MiB },
            Cpu = new CpuResourceSample { CpuPercent = cpu?.Invoke(i) },
        }).ToList();

    internal static ResourceComponentObservation Component(List<ResourceSample> samples, string id = "api", ResourceComponentRole role = ResourceComponentRole.Target,
        List<ResourceDiscontinuity>? discontinuities = null, ResourceCollectionState state = ResourceCollectionState.Collected, double? limit = null) => new()
    {
        TargetId = id, DisplayName = id.ToUpperInvariant(), Role = role, ProviderId = ResourceProviderIds.Podman, CollectionState = state, Samples = samples,
        RawSampleCount = samples.Count, Discontinuities = discontinuities ?? [], MemoryLimitBytes = limit,
    };

    /// <summary>Workload of <paramref name="seconds"/> starting at T0 with a warm-up exclusion.</summary>
    internal static ResourceWindows Windows(int seconds, int warmup = 60, int cooldown = 0) => new(T0, T0.AddSeconds(seconds), warmup, cooldown);

    internal static ResourceStabilityPolicy Growth(double relative, ResourceMetric metric = ResourceMetric.ContainerMemoryBytes, int? minSeconds = null, ThresholdSeverity severity = ThresholdSeverity.Required, string? target = null) =>
        new() { Metric = metric, AllowedRelativeGrowthPercent = relative, MinimumObservationSeconds = minSeconds, Severity = severity, TargetComponentId = target };

    private static ResourceStabilitySummary Summary(ResourceComponentObservation c, ResourceWindows w, params ResourceStabilityPolicy[] policies) =>
        ResourceStabilityAnalyzer.Summarize(c, ResourceMetric.ContainerMemoryBytes, w, policies);

    private static ResourceStabilityAssessment Assess(ResourceObservationConfiguration config, ResourceWindows w, params ResourceComponentObservation[] components) =>
        ResourceStabilityAnalyzer.Assess(new ResourceStabilityAssessment { Configured = true, Components = components.ToList() }, config, w);

    // ── Stable, warm-up, growth ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stable_FluctuatingAroundOneLevel_IsStableWithinPolicy()
    {
        var c = Component(Series(60, i => 500 + (i % 3 - 1) * 6));
        var s = Summary(c, Windows(590), Growth(10));
        s.AssessmentState.Should().Be(ResourceAssessmentState.StableWithinPolicy);
        s.SteadySampleCount.Should().Be(54, "the first 60 s are the warm-up exclusion");
        s.EarlySteadyValue.Should().BeApproximately(500 * MiB, MiB * 7);
        s.PolicyResults.Single().Outcome.Should().Be(CheckOutcome.Pass);
    }

    [Fact]
    public void WarmupRiseThenPlateau_IsNotARegression()
    {
        // Memory triples while caches/JIT/pools warm up, then holds: the warm-up is excluded, start-vs-end is never compared.
        var c = Component(Series(60, i => i < 6 ? 150 + i * 60 : 520 + (i % 2) * 4));
        var s = Summary(c, Windows(590, warmup: 60), Growth(10));
        s.AssessmentState.Should().Be(ResourceAssessmentState.StableWithinPolicy);
        (s.EndValue!.Value / s.StartValue!.Value).Should().BeGreaterThan(3, "start → end tripled, yet that is warm-up, not a trend");
    }

    [Fact]
    public void SustainedGrowth_BeyondExplicitPolicy_IsRegression_NeverMemoryLeak()
    {
        var c = Component(Series(60, i => 400 + i * 5));
        var config = new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(20)] };
        var a = Assess(config, Windows(590), c);
        var s = a.Components.Single().Summaries.Single(x => x.Metric == ResourceMetric.ContainerMemoryBytes);
        s.AssessmentState.Should().Be(ResourceAssessmentState.Regression);
        s.TrendConfidence.Should().Be("High");
        s.AssessmentReason.Should().Contain("not a proven leak");
        var finding = a.Findings.Single(f => f.Code == "ResourceGrowthExceededPolicy");
        finding.Severity.Should().Be("Regression");
        finding.Message.Should().Contain("exceeded steady-state growth ≤ 20 %");
        a.Findings.Select(f => f.Code).Should().NotContain(code => code.Contains("Leak", StringComparison.OrdinalIgnoreCase));
        a.Findings.Select(f => f.Message).Should().NotContain(m => m.Contains("memory leak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GrowthWithinTolerance_IsIncreasingWithinTolerance()
    {
        var c = Component(Series(60, i => 500 + i * 0.5));
        Summary(c, Windows(590), Growth(25)).AssessmentState.Should().Be(ResourceAssessmentState.IncreasingWithinTolerance);
    }

    [Fact]
    public void TooShort_StrongGrowth_IsPotentialRegression_NotRegression()
    {
        var c = Component(Series(20, i => 400 + i * 20));
        var s = Summary(c, Windows(190, warmup: 30), Growth(10, minSeconds: 1800));
        s.AssessmentState.Should().Be(ResourceAssessmentState.PotentialRegression);
        s.AssessmentReason.Should().Contain("shorter or sparser than that policy requires");
        s.PolicyResults.Single().Outcome.Should().Be(CheckOutcome.NotAssessed);
    }

    [Fact]
    public void TooShort_WithoutGrowth_IsInsufficientEvidence()
    {
        var c = Component(Series(20, i => 400));
        Summary(c, Windows(190, warmup: 30), Growth(10, minSeconds: 1800)).AssessmentState.Should().Be(ResourceAssessmentState.InsufficientEvidence);
        Summary(Component(Series(5, i => 400 + i * 50)), Windows(40, warmup: 30), Growth(10)).AssessmentState.Should().Be(ResourceAssessmentState.InsufficientEvidence,
            "two samples after the warm-up cannot form a trend");
    }

    [Fact]
    public void NoPolicy_IsDescriptiveOnly_NeverPass()
    {
        var c = Component(Series(60, i => 400 + i * 5));
        var a = Assess(new ResourceObservationConfiguration { Enabled = true }, Windows(590), c);
        var s = a.Components.Single().Summaries.Single(x => x.Metric == ResourceMetric.ContainerMemoryBytes);
        s.AssessmentState.Should().Be(ResourceAssessmentState.NotAssessed);
        s.AbsoluteGrowth.Should().BeGreaterThan(0);
        ResourceFormat.State(s.AssessmentState).Should().Be("Descriptive only — no policy");
        ResourceStabilityAnalyzer.QualityOutcomes(a).Should().BeEmpty("no policy, nothing passed or failed");
    }

    [Fact]
    public void AdvisoryPolicy_ExceededIsPotentialRegression()
    {
        var c = Component(Series(60, i => 400 + i * 5));
        Summary(c, Windows(590), Growth(20, severity: ThresholdSeverity.Advisory)).AssessmentState.Should().Be(ResourceAssessmentState.PotentialRegression);
    }

    [Fact]
    public void SingleGcSpike_DoesNotMakeARegression()
    {
        var c = Component(Series(60, i => i == 40 ? 2000 : 500));
        var s = Summary(c, Windows(590), Growth(10), new ResourceStabilityPolicy { Metric = ResourceMetric.ContainerMemoryBytes, MaxValue = 900 * MiB });
        s.PeakValue.Should().Be(2000 * MiB);
        s.AssessmentState.Should().Be(ResourceAssessmentState.StableWithinPolicy, "medians and the 95th percentile ignore one spike");
    }

    // ── GC-aware ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ManagedHeapSawtooth_WithStableFloor_IsNotARegression()
    {
        // allocate → grow → GC → drop, repeatedly; the post-GC floor stays at ~200 MiB.
        var c = Component(Series(60, i => 600, heap: i => 200 + (i % 6) * 60, floor: i => i % 6 == 0 ? 200 + (i % 12 == 0 ? 2 : 0) : null));
        var config = new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(15, ResourceMetric.ManagedHeapBytes), Growth(10, ResourceMetric.GcHeapAfterGcBytes)] };
        var a = Assess(config, Windows(590), c with { AvailableMetrics = [ResourceMetric.ManagedHeapBytes] });
        var summaries = a.Components.Single().Summaries;
        summaries.Single(s => s.Metric == ResourceMetric.ManagedHeapBytes).AssessmentState.Should().Be(ResourceAssessmentState.StableWithinPolicy);
        summaries.Single(s => s.Metric == ResourceMetric.GcHeapAfterGcBytes).AssessmentState.Should().Be(ResourceAssessmentState.StableWithinPolicy);
        a.Findings.Should().BeEmpty();
    }

    [Fact]
    public void RisingGcFloor_BeyondPolicy_IsManagedHeapFloorRegression()
    {
        var c = Component(Series(60, i => 600, heap: i => 200 + i * 3 + (i % 6) * 60, floor: i => 200 + i * 3));
        var config = new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(10, ResourceMetric.GcHeapAfterGcBytes)] };
        var a = Assess(config, Windows(590), c);
        a.Components.Single().Summaries.Single(s => s.Metric == ResourceMetric.GcHeapAfterGcBytes).AssessmentState.Should().Be(ResourceAssessmentState.Regression);
        a.Findings.Single().Code.Should().Be("ManagedHeapFloorRegression");
    }

    [Fact]
    public void ContainerMemoryOnly_StatesTheManagedHeapLimitation()
    {
        var c = Component(Series(60, i => 400 + i * 5));
        var s = Summary(c, Windows(590), Growth(20));
        s.AssessmentState.Should().Be(ResourceAssessmentState.Regression, "a violated container policy is a container resource regression");
        s.Limitations.Should().Contain(l => l.StartsWith("Only container-level memory was available. Managed .NET heap metrics were not available"));
    }

    // ── Absolute vs drift, baselines ─────────────────────────────────────────────────────────────────────────────────

    internal static PerformanceTestRun Run(ResourceStabilityAssessment? resources, Guid? id = null) => new()
    {
        RunId = id ?? Guid.NewGuid(), EnvironmentId = "pay-qa", TargetOrigin = "https://pay-qa.example.test", ComparisonFingerprint = "cmp", State = PerformanceRunState.Completed,
        Metrics = new PerformanceMetrics { RequestCount = 100 }, Resources = resources,
    };

    [Fact]
    public void AbsoluteThresholdPasses_WhileDriftPolicyFails_BothAreVisible()
    {
        var policy = new ResourceStabilityPolicy { Metric = ResourceMetric.ContainerMemoryBytes, MaxValue = 1024 * MiB };
        var config = new ResourceObservationConfiguration { Enabled = true, Policies = [policy],
            DriftPolicies = [new ResourceDriftPolicy { Metric = ResourceMetric.ContainerMemoryBytes, Statistic = ResourceStatistic.LateSteady, AllowedRelativeChangePercent = 25 }] };
        var baseline = Run(Assess(config, Windows(590), Component(Series(60, i => 400))));
        var current = Run(Assess(config, Windows(590), Component(Series(60, i => 700))));
        current.Resources!.Components.Single().Summaries.Single(s => s.Metric == ResourceMetric.ContainerMemoryBytes).AssessmentState.Should().Be(ResourceAssessmentState.StableWithinPolicy, "700 MiB < 1 GiB");
        var drift = ResourceStabilityAnalyzer.Drift(current, baseline, "b1", config.DriftPolicies)!;
        drift.Compatible.Should().BeTrue();
        drift.State.Should().Be(PerformanceDriftState.Regression);
        var late = drift.Deltas.Single(d => d.Metric == ResourceMetric.ContainerMemoryBytes && d.Statistic == ResourceStatistic.LateSteady);
        late.RelativePercent.Should().Be(75);
        drift.Findings.Single().Code.Should().Be("SteadyStateMemoryRegression");
        drift.Findings.Single().Message.Should().Contain("changed 75 % from the baseline and exceeded the accepted drift (+25 %)");
    }

    [Fact]
    public void Drift_WithoutPolicy_IsDescriptive_AndZeroBaselineHasNoRelativeChange()
    {
        var config = new ResourceObservationConfiguration { Enabled = true };
        var baseline = Run(Assess(config, Windows(590), Component(Series(60, i => 400, cpu: i => 0))));
        var current = Run(Assess(config, Windows(590), Component(Series(60, i => 450, cpu: i => 12))));
        var drift = ResourceStabilityAnalyzer.Drift(current, baseline, "b1", [])!;
        drift.State.Should().Be(PerformanceDriftState.NotAssessed);
        var cpu = drift.Deltas.Single(d => d.Metric == ResourceMetric.CpuPercent && d.Statistic == ResourceStatistic.LateSteady);
        cpu.Reference.Should().Be(0);
        cpu.RelativePercent.Should().BeNull("a zero baseline has no relative change");
        cpu.AbsoluteDelta.Should().Be(12);
    }

    [Fact]
    public void Drift_DifferentMemoryLimit_IsNotComparable()
    {
        var config = new ResourceObservationConfiguration { Enabled = true, DriftPolicies = [new ResourceDriftPolicy { AllowedRelativeChangePercent = 10 }] };
        var baseline = Run(Assess(config, Windows(590), Component(Series(60, i => 400), limit: 1024 * MiB)));
        var current = Run(Assess(config, Windows(590), Component(Series(60, i => 800), limit: 2048 * MiB)));
        var drift = ResourceStabilityAnalyzer.Drift(current, baseline, "b1", config.DriftPolicies)!;
        drift.State.Should().Be(PerformanceDriftState.NotComparable);
        drift.CompatibilityNotes.Should().Contain(n => n.Contains("memory limit changed"));
        drift.Findings.Should().BeEmpty();
    }

    // ── Restart, multi-component, load generator, availability ───────────────────────────────────────────────────────

    [Fact]
    public void TargetRestart_IsADiscontinuity_NotOneTrend()
    {
        var samples = Series(30, i => 400, instance: "a").Concat(Series(30, i => 150, instance: "b").Select(s => s with { At = s.At.AddSeconds(300) })).ToList();
        var c = Component(samples, discontinuities: [new ResourceDiscontinuity(T0.AddSeconds(300), "instance changed (restart or replacement)", "a", "b")]);
        var a = Assess(new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(10)] }, Windows(590), c);
        a.Components.Single().Summaries.Single(s => s.Metric == ResourceMetric.ContainerMemoryBytes).AssessmentState.Should().Be(ResourceAssessmentState.NotComparable);
        a.Findings.Should().Contain(f => f.Code == "TargetRestartedDuringTest" && f.Evidence == "a → b");
    }

    [Fact]
    public void MultipleComponents_AreAssessedSeparately_NeverSummed()
    {
        var api = Component(Series(60, i => 500), "api");
        var worker = Component(Series(60, i => 300 + i * 6, target: "worker"), "worker");
        var a = Assess(new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(20)] }, Windows(590), api, worker);
        State(a, "api").Should().Be(ResourceAssessmentState.StableWithinPolicy);
        State(a, "worker").Should().Be(ResourceAssessmentState.Regression);
        a.Findings.Single(f => f.Code == "ResourceGrowthExceededPolicy").TargetId.Should().Be("worker");
        static ResourceAssessmentState State(ResourceStabilityAssessment a, string id) => a.Components.Single(c => c.TargetId == id).Summaries.Single(s => s.Metric == ResourceMetric.ContainerMemoryBytes).AssessmentState;
    }

    [Fact]
    public void LoadGenerator_IsHealthOnly_AndSaturationIsFlagged()
    {
        var generator = Component(Series(60, i => 900 + i * 10, cpu: i => 95, target: "load-generator"), "load-generator", ResourceComponentRole.LoadGenerator);
        var a = Assess(new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(5)] }, Windows(590), generator);
        a.Components.Single().Summaries.Should().OnlyContain(s => s.AssessmentState == ResourceAssessmentState.NotAssessed, "the generator's growth is never application stability");
        a.Findings.Should().ContainSingle(f => f.Code == "LoadGeneratorSaturationPossible");
        a.EvidenceState.Should().Be(ResourceCollectionState.NotConfigured, "no application target was observed");
    }

    [Fact]
    public void UnavailableMetric_IsUnavailable_NotZero()
    {
        var c = Component(Series(60, i => null, cpu: i => 20));
        var a = Assess(new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(10)] }, Windows(590), c with { CollectionState = ResourceCollectionState.PartialEvidence });
        var memory = a.Components.Single().Summaries.Single(s => s.Metric == ResourceMetric.ContainerMemoryBytes);
        memory.AssessmentState.Should().Be(ResourceAssessmentState.Unavailable);
        memory.LateSteadyValue.Should().BeNull();
        memory.AssessmentReason.Should().Contain("unavailable, not zero");
        a.Components.Single().UnavailableMetrics.Should().Contain(ResourceMetric.ContainerMemoryBytes);
        a.EvidenceState.Should().Be(ResourceCollectionState.PartialEvidence);
        ResourceFormat.Format(null, ResourceMetric.ContainerMemoryBytes).Should().Be("Unavailable");
    }

    [Fact]
    public void QualityOutcomes_CountOnlyEvaluablePolicies()
    {
        var a = Assess(new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(20)] }, Windows(590), Component(Series(60, i => 400 + i * 5)));
        ResourceStabilityAnalyzer.QualityOutcomes(a).Should().Equal(CheckOutcome.Fail);
        var shortRun = Assess(new ResourceObservationConfiguration { Enabled = true, Policies = [Growth(20, minSeconds: 3600)] }, Windows(590), Component(Series(60, i => 400 + i * 5)));
        ResourceStabilityAnalyzer.QualityOutcomes(shortRun).Should().BeEmpty("insufficient evidence is a coverage gap, never a failure");
    }

    [Fact]
    public void RateMetrics_AreDeltasOfTheSameInstance()
    {
        var a = new ResourceSample { At = T0, InstanceId = "x", Memory = new MemoryResourceSample { Gen2Collections = 10, GcPauseMs = 100 } };
        var b = new ResourceSample { At = T0.AddMinutes(2), InstanceId = "x", Memory = new MemoryResourceSample { Gen2Collections = 14, GcPauseMs = 160 } };
        ResourceStabilityAnalyzer.Value(b, ResourceMetric.Gen2CollectionsPerMinute, a).Should().Be(2);
        ResourceStabilityAnalyzer.Value(b, ResourceMetric.GcPauseMsPerMinute, a).Should().Be(30);
        ResourceStabilityAnalyzer.Value(b with { InstanceId = "y" }, ResourceMetric.Gen2CollectionsPerMinute, a).Should().BeNull("a new process restarts its counters");
        ResourceStabilityAnalyzer.Value(a, ResourceMetric.Gen2CollectionsPerMinute).Should().BeNull();
    }

    // ── Bounded storage ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Downsampling_HalvesDeterministically_AndNeverMergesAcrossInstances()
    {
        var samples = Series(9, i => 100 + i * 10, instance: "a").Concat(Series(3, i => 50, instance: "b").Select(s => s with { At = s.At.AddSeconds(90) })).ToList();
        var once = ResourceObservationSession.Downsample(samples);
        once.Should().HaveCount(7);
        once.Sum(s => s.Aggregated).Should().Be(12);
        once[0].Memory!.ContainerMemoryBytes.Should().Be(105 * MiB);
        once.Select(s => s.InstanceId).Should().Equal("a", "a", "a", "a", "a", "b", "b");
        ResourceObservationSession.Downsample(samples).Should().BeEquivalentTo(once, "deterministic");
    }

    // ── Podman parsing ───────────────────────────────────────────────────────────────────────────────────────────────

    private const string StatsJson = """{"AvgCPU":0.25,"ContainerID":"5cc92597f50df281dac34125e23b3596a617415ac79373ce50f59ef21dd0006b","Name":"api","CPU":12.3456,"MemUsage":524288000,"MemLimit":1073741824,"MemPerc":48.8,"PIDs":31}""";

    [Fact]
    public void PodmanStats_WithMemoryController_ReportsBytes()
    {
        var s = PodmanContainerExecutionRuntime.ParseStats(StatsJson, new HashSet<string> { "cpu", "memory", "pids" });
        (s.CpuPercent, s.MemoryBytes, s.MemoryLimitBytes, s.Pids, s.Error).Should().Be((12.35, 524288000d, 1073741824d, 31, null));
    }

    [Fact]
    public void PodmanStats_WithoutMemoryController_MemoryIsUnavailable_NotZero()
    {
        // Real rootless output on a host without delegated controllers: MemUsage 0, PIDs 0, CPU measured.
        var rootless = """{"AvgCPU":0.25,"ContainerID":"5cc9","Name":"api","CPU":0.25168439093855105,"MemUsage":0,"MemLimit":16560869376,"MemPerc":0,"PIDs":0}""";
        var s = PodmanContainerExecutionRuntime.ParseStats(rootless, new HashSet<string>());
        s.CpuPercent.Should().Be(0.25);
        s.MemoryBytes.Should().BeNull();
        s.MemoryLimitBytes.Should().BeNull();
        s.Pids.Should().BeNull();
        s.Error.Should().Contain("memory is unavailable, not 0");
        PodmanContainerExecutionRuntime.ParseStats("{not json", new HashSet<string> { "memory" }).Error.Should().Contain("could not be parsed");
        PodmanContainerExecutionRuntime.ParseStats("", new HashSet<string> { "memory" }).Error.Should().Contain("no statistics");
    }

    [Fact]
    public void PodmanInspect_ParsesIdentity_AndDetectsRestart()
    {
        var a = PodmanContainerExecutionRuntime.ParseInspect("5cc92597f50df281|2026-10-02 10:00:00.1 +0000 UTC|0|true|docker.io/library/api:1.2|1073741824|2000000000")!;
        (a.Running, a.Image, a.MemoryLimitBytes, a.NanoCpus).Should().Be((true, "docker.io/library/api:1.2", 1073741824L, 2000000000L));
        var restarted = PodmanContainerExecutionRuntime.ParseInspect("5cc92597f50df281|2026-10-02 10:05:00.3 +0000 UTC|1|true|docker.io/library/api:1.2|0|0")!;
        restarted.InstanceKey.Should().NotBe(a.InstanceKey);
        restarted.MemoryLimitBytes.Should().BeNull("0 = no limit configured");
        PodmanContainerExecutionRuntime.ParseInspect("Error: no such container").Should().BeNull();
        PodmanContainerExecutionRuntime.ParseInspect("").Should().BeNull();
    }
}
