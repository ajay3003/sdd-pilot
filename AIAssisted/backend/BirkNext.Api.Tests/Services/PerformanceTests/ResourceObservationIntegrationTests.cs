using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.PerformanceTests.Resources;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using P = BirkNext.Api.Tests.Services.PerformanceTests.PerformanceTestReviewTests;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>Resource observation in the run lifecycle: started with the workload, stopped on completion/cancel/timeout/shutdown, partial evidence kept,
/// provider failure never failing performance, immutable history, approved targets only.</summary>
public sealed class ResourceObservationIntegrationTests
{
    private const string Api = "pay-api";
    private const double MiB = 1024 * 1024;

    private static PerformanceTestOptions Options(params ResourceTargetSpec[] extra) => new()
    {
        Resources = new PerformanceResourceOptions
        {
            MinSampleIntervalSeconds = 1, DefaultSampleIntervalSeconds = 1,
            Targets = [new ResourceTargetSpec(new ResourceObservationTarget { Id = Api, DisplayName = "Payments API", ProviderId = ResourceProviderIds.Podman }, "pay-api-1"), .. extra],
        },
    };

    private static PerformanceTestDefinition Definition(Func<ResourceObservationConfiguration, ResourceObservationConfiguration>? change = null) =>
        P.Definition(d => d with { ResourceObservation = (change ?? (c => c))(new ResourceObservationConfiguration
        {
            Enabled = true, TargetComponentIds = [Api, PerformanceResourceOptions.SelfTargetId], SampleIntervalSeconds = 1,
        }) });

    /// <summary>A provider whose samples a test scripts (or makes fail).</summary>
    private sealed class ScriptedProvider : IResourceObservationProvider
    {
        public string ProviderId => "resource.scripted";
        public string DisplayName => "Scripted";
        public int Calls;
        public Func<int, ResourceSampleResult>? Next { get; set; }
        public Task<ResourceProviderCapability> StatusAsync(CancellationToken ct = default) => Task.FromResult(new ResourceProviderCapability(ProviderId, DisplayName, "Available", "", []));
        public Task<ResourceSampleResult> ProbeAsync(ResourceTargetSpec target, CancellationToken ct = default) => Task.FromResult(new ResourceSampleResult(null, ResourceCollectionState.Collected, "found"));
        public Task<ResourceSampleResult> SampleAsync(ResourceTargetSpec target, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref Calls);
            return Task.FromResult(Next?.Invoke(n) ?? throw new InvalidOperationException("collector crashed"));
        }
    }

    /// <summary>A k6 runtime whose load runs until cancelled (or for a fixed time).</summary>
    private sealed class HeldRuntime(P.FakeRuntime inner, TimeSpan hold) : IContainerExecutionRuntime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public P.FakeRuntime Inner => inner;
        public string RuntimeId => inner.RuntimeId;
        public string DisplayName => inner.DisplayName;
        public string HostGatewayAlias => inner.HostGatewayAlias;
        public Task<PerformanceRuntimeStatus> StatusAsync(CancellationToken ct = default) => inner.StatusAsync(ct);
        public Task<ContainerImageStatus> ImageAsync(string image, CancellationToken ct = default) => inner.ImageAsync(image, ct);
        public Task<ContainerImageStatus> PullAsync(string image, CancellationToken ct = default) => inner.PullAsync(image, ct);
        public Task<bool> RemoveAsync(string name, CancellationToken ct = default) => inner.RemoveAsync(name, ct);
        public Task<IReadOnlyList<string>> ListManagedAsync(string component, CancellationToken ct = default) => inner.ListManagedAsync(component, ct);
        public Task<IReadOnlySet<string>> ControllersAsync(CancellationToken ct = default) => inner.ControllersAsync(ct);
        public Task<ContainerInstanceInfo?> InspectAsync(string name, CancellationToken ct = default) => inner.InspectAsync(name, ct);
        public Task<ContainerStatsSnapshot> StatsAsync(string name, CancellationToken ct = default) => inner.StatsAsync(name, ct);
        public async Task<ContainerRunOutcome> RunAsync(ContainerRunSpec spec, CancellationToken ct)
        {
            if (spec.Command[0] == "version" || spec.Command[^1].EndsWith("probe.js", StringComparison.Ordinal)) return await inner.RunAsync(spec, ct);
            inner.Containers[spec.Name] = new ContainerInstanceInfo("ab12cd34ef56", "2026-10-02T10:00:00Z", 0, true, "docker.io/grafana/k6:1.0.0", null, null);
            Started.TrySetResult();
            try { await Task.Delay(hold, ct); return await inner.RunAsync(spec, CancellationToken.None); }
            catch (OperationCanceledException) { return new ContainerRunOutcome(null, "", "", false, true, null, true); }
            finally { inner.Containers.Remove(spec.Name); inner.Removed.Add(spec.Name); }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public P.FakeRuntime Runtime { get; }
        public ScriptedProvider Scripted { get; } = new();
        public ServiceProvider Services { get; }
        public PerformanceTestExecutionService Execution => Services.GetRequiredService<PerformanceTestExecutionService>();
        public PerformanceTestProviderRegistry Registry => Services.GetRequiredService<PerformanceTestProviderRegistry>();

        public Harness(PerformanceTestOptions? options = null, IContainerExecutionRuntime? custom = null, P.FakeRuntime? runtime = null)
        {
            Runtime = runtime ?? new P.FakeRuntime();
            Runtime.Containers["pay-api-1"] = new ContainerInstanceInfo("0a1b2c3d4e5f", "2026-10-02T09:00:00Z", 0, true, "registry.example.test/pay-api:2.4.1", (long)(1024 * MiB), 2_000_000_000);
            Runtime.OnStats = name => name == "pay-api-1" ? new ContainerStatsSnapshot(18.5, 420 * MiB, 1024 * MiB, 40, null) : new ContainerStatsSnapshot(35, 60 * MiB, null, 8, null);
            var services = new ServiceCollection();
            services.AddLogging();
            var db = Guid.NewGuid().ToString();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
            services.AddSingleton(options ?? Options());
            services.AddSingleton(custom ?? Runtime);
            services.AddSingleton<IPerformanceTestProvider, K6PerformanceTestProvider>();
            services.AddSingleton<PerformanceTestProviderRegistry>();
            services.AddSingleton<IResourceObservationProvider, PodmanResourceObservationProvider>();
            services.AddSingleton<IResourceObservationProvider, DotNetRuntimeSelfObservationProvider>();
            services.AddSingleton<IResourceObservationProvider>(Scripted);
            services.AddSingleton<ResourceObservationRegistry>();
            services.AddSingleton<PerformanceTestReadinessService>();
            services.AddSingleton<PerformanceTestExecutionService>();
            services.AddScoped<PerformanceTestStore>();
            services.AddScoped<IqrSourceStore>();
            Services = services.BuildServiceProvider();
        }

        public PerformanceTestStore Store() => Services.CreateScope().ServiceProvider.GetRequiredService<PerformanceTestStore>();

        public async Task<PerformanceTestDefinition> SeedAsync(PerformanceTestDefinition d)
        {
            await Store().SaveDataProfileAsync("pay-qa", P.Data(), DateTimeOffset.UtcNow);
            return await Store().SaveDefinitionAsync("pay-qa", d, DateTimeOffset.UtcNow);
        }

        public async Task<PerformanceRunStartResult> StartAsync(PerformanceTestDefinition d)
        {
            await Registry.CheckReachabilityAsync(d);
            var started = await Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
            started.Run.Should().NotBeNull(string.Join(" ", started.Blockers) + started.Conflict);
            return started;
        }

        public async Task<PerformanceTestRun> RunToEndAsync(PerformanceTestDefinition d)
        {
            var started = await StartAsync(d);
            await Execution.LastExecution!;
            return (await Store().RunAsync(started.Run!.RunId))!;
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    [Fact]
    public async Task CompletedRun_CarriesPerComponentEvidence_WithTheLoadGeneratorSeparate()
    {
        var runtime = new HeldRuntime(new P.FakeRuntime(), TimeSpan.FromSeconds(2.5));
        await using var h = new Harness(custom: runtime, runtime: runtime.Inner);
        var run = await h.RunToEndAsync(await h.SeedAsync(Definition()));

        run.State.Should().Be(PerformanceRunState.Completed);
        run.Verdict.Should().Be(PerformanceQualityVerdict.Pass, "resource evidence without policies changes nothing about the threshold verdict");
        var r = run.Resources!;
        (r.Configured, r.InProgress, r.SampleIntervalSeconds).Should().Be((true, false, 1));
        var api = r.Components.Single(c => c.TargetId == Api);
        (api.Role, api.ProviderId, api.CollectionState, api.Image, api.MemoryLimitBytes).Should().Be((ResourceComponentRole.Target, ResourceProviderIds.Podman, ResourceCollectionState.Collected,
            "registry.example.test/pay-api:2.4.1", 1024 * MiB));
        api.Samples.Should().NotBeEmpty().And.OnlyContain(s => s.Memory!.ContainerMemoryBytes == 420 * MiB && s.InstanceId!.StartsWith("0a1b2c3d4e5f@"));
        var self = r.Components.Single(c => c.TargetId == PerformanceResourceOptions.SelfTargetId);
        (self.ProviderId, self.Role).Should().Be((ResourceProviderIds.DotNetRuntime, ResourceComponentRole.Target));
        self.Samples.Should().OnlyContain(s => s.Memory!.ManagedHeapBytes > 0 && s.Memory.WorkingSetBytes > 0);
        var generator = r.Components.Single(c => c.Role == ResourceComponentRole.LoadGenerator);
        generator.Samples.Should().NotBeEmpty("the k6 container is observed while it runs");
        generator.Summaries.Should().OnlyContain(s => s.AssessmentState != ResourceAssessmentState.Regression);
        api.Summaries.Should().Contain(s => s.Metric == ResourceMetric.ContainerMemoryBytes);
        runtime.Inner.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(run.RunId));
        r.Limitations.Should().Contain(l => l.Contains("Growth is not proof of a memory leak"));
    }

    [Fact]
    public async Task ProviderFailure_LeavesThePerformanceRunValid_AndEvidenceUnavailable()
    {
        var options = Options(new ResourceTargetSpec(new ResourceObservationTarget { Id = "worker", DisplayName = "Worker", ProviderId = "resource.scripted" }, "worker-1"));
        await using var h = new Harness(options);
        var run = await h.RunToEndAsync(await h.SeedAsync(Definition(c => c with { TargetComponentIds = ["worker"], ObserveLoadGenerator = false })));
        run.State.Should().Be(PerformanceRunState.Completed);
        run.Verdict.Should().Be(PerformanceQualityVerdict.Pass);
        run.Metrics.Should().NotBeNull();
        var worker = run.Resources!.Components.Single();
        worker.CollectionState.Should().Be(ResourceCollectionState.CollectionFailed);
        worker.CollectionDetail.Should().Contain("Sampling failed (InvalidOperationException)");
        run.Resources.EvidenceState.Should().Be(ResourceCollectionState.CollectionFailed);
        run.Resources.Findings.Should().NotContain(f => f.Severity == "Regression");
    }

    [Fact]
    public async Task PartialEvidence_IsPersistedWhileRunning()
    {
        var runtime = new HeldRuntime(new P.FakeRuntime(), TimeSpan.FromMinutes(5));
        await using var h = new Harness(custom: runtime, runtime: runtime.Inner);
        var started = await h.StartAsync(await h.SeedAsync(Definition()));
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        PerformanceTestRun? live = null;
        for (var i = 0; i < 50 && live?.Resources is not { Components.Count: > 0 }; i++) { await Task.Delay(100); live = await h.Store().RunAsync(started.Run!.RunId); }
        live!.State.Should().Be(PerformanceRunState.Running);
        live.Resources!.InProgress.Should().BeTrue();
        live.Resources.Components.Single(c => c.TargetId == Api).Samples.Should().NotBeEmpty();
        await h.Execution.CancelAsync(started.Run!.RunId);
        await h.Execution.LastExecution!;
    }

    [Fact]
    public async Task Cancel_StopsTheCollectors_RemovesK6_AndKeepsPartialEvidence()
    {
        var runtime = new HeldRuntime(new P.FakeRuntime(), TimeSpan.FromMinutes(5));
        await using var h = new Harness(custom: runtime, runtime: runtime.Inner);
        var started = await h.StartAsync(await h.SeedAsync(Definition()));
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 50 && runtime.Inner.StatsCalls.Count(n => n == "pay-api-1") < 2; i++) await Task.Delay(100);
        await h.Execution.CancelAsync(started.Run!.RunId);
        await h.Execution.LastExecution!;
        var calls = runtime.Inner.StatsCalls.Count;
        await Task.Delay(2500);
        runtime.Inner.StatsCalls.Count.Should().Be(calls, "collectors stopped with the run");
        var run = (await h.Store().RunAsync(started.Run.RunId))!;
        run.State.Should().Be(PerformanceRunState.Cancelled);
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        run.Resources!.InProgress.Should().BeFalse();
        run.Resources.Components.Single(c => c.TargetId == Api).Samples.Count.Should().BeGreaterThanOrEqualTo(2);
        runtime.Inner.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(run.RunId));
    }

    [Fact]
    public async Task Shutdown_CancelsActiveRuns_AndCleansUpOwnedResources()
    {
        var runtime = new HeldRuntime(new P.FakeRuntime(), TimeSpan.FromMinutes(5));
        await using var h = new Harness(custom: runtime, runtime: runtime.Inner);
        var started = await h.StartAsync(await h.SeedAsync(Definition()));
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await new PerformanceTestShutdownService(h.Execution).StopAsync(CancellationToken.None);
        var run = (await h.Store().RunAsync(started.Run!.RunId))!;
        run.State.Should().Be(PerformanceRunState.Cancelled);
        runtime.Inner.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(run.RunId));
        var calls = runtime.Inner.StatsCalls.Count;
        await Task.Delay(1500);
        runtime.Inner.StatsCalls.Count.Should().Be(calls);
    }

    [Fact]
    public async Task RepeatedRuns_LeaveNoK6Containers_AndNoRunningCollectors()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition());
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add((await h.RunToEndAsync(d)).RunId);
        foreach (var id in ids) h.Runtime.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(id));
        h.Runtime.Containers.Keys.Should().Equal("pay-api-1");
        (await h.Execution.CleanupOrphansAsync(force: true)).Should().BeEmpty();
        var calls = h.Runtime.StatsCalls.Count;
        await Task.Delay(1500);
        h.Runtime.StatsCalls.Count.Should().Be(calls, "no collector outlives its run");
        Directory.Exists(Path.Combine(Path.GetTempPath(), "birknext-performance", ids[^1].ToString("N"))).Should().BeFalse();
    }

    [Fact]
    public async Task History_IsImmutable_AcrossNewPoliciesRunsAndBaselines()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition());
        var run1 = await h.RunToEndAsync(d);
        var json1 = JsonSerializer.Serialize(run1.Resources);
        var v1 = (await h.Store().PromoteAsync("pay-qa", new PerformanceBaselinePromotion { RunId = run1.RunId, Reason = "reference" }, DateTimeOffset.UtcNow)).Baseline!;
        var stricter = await h.Store().SaveDefinitionAsync("pay-qa", d with { ResourceObservation = d.ResourceObservation! with
        {
            Policies = [new ResourceStabilityPolicy { Metric = ResourceMetric.ContainerMemoryBytes, AllowedRelativeGrowthPercent = 1 }],
            DriftPolicies = [new ResourceDriftPolicy { Metric = ResourceMetric.ContainerMemoryBytes, AllowedRelativeChangePercent = 5 }],
        } }, DateTimeOffset.UtcNow);
        var run2 = await h.RunToEndAsync(stricter);
        run2.BaselineIdAtRun.Should().Be(v1.BaselineId);
        run2.ResourceDrift!.BaselineRunId.Should().Be(run1.RunId);
        run2.Resources!.PolicyFingerprint.Should().NotBe(run1.Resources!.PolicyFingerprint);
        await h.Store().PromoteAsync("pay-qa", new PerformanceBaselinePromotion { RunId = run2.RunId, Reason = "v2" }, DateTimeOffset.UtcNow);
        var reread1 = (await h.Store().RunAsync(run1.RunId))!;
        JsonSerializer.Serialize(reread1.Resources).Should().Be(json1, "a finished run's resource evidence never changes");
        reread1.ResourceDrift.Should().BeNull();
        (await h.Store().RunAsync(run2.RunId))!.ResourceDrift!.BaselineId.Should().Be(v1.BaselineId, "run 2 stays bound to the baseline active when it ran");
    }

    [Fact]
    public async Task RequiredResourcePolicy_CountsInTheSharedQuality_EvidenceGapsDoNot()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition(c => c with { Policies = [new ResourceStabilityPolicy { Metric = ResourceMetric.ContainerMemoryBytes, AllowedRelativeGrowthPercent = 10 }] }));
        var store = h.Store();
        var queued = await h.StartAsync(d);
        await h.Execution.LastExecution!;
        var baseRun = (await store.RunAsync(queued.Run!.RunId))! with { State = PerformanceRunState.Running, Resources = null, ResourceDrift = null };
        var growing = ResourceStabilityAnalyzerTests.Component(ResourceStabilityAnalyzerTests.Series(60, i => 400 + i * 5), Api);
        var config = d.ResourceObservation!;
        var failing = ResourceStabilityAnalyzer.Assess(new ResourceStabilityAssessment { Configured = true, Components = [growing] }, config, ResourceStabilityAnalyzerTests.Windows(590));
        var result = new PerformanceProviderResult { State = PerformanceRunState.Completed, Metrics = new PerformanceMetrics { RequestCount = 10, Latency = new PerformanceLatency { P95Ms = 100 }, ErrorRatePercent = 0, RequestsPerSecond = 30 }, MetricsSource = "k6" };
        var rescored = await h.Execution.FinishAsync(store, baseRun with { RunId = Guid.NewGuid() }, result, failing);
        rescored.Verdict.Should().Be(PerformanceQualityVerdict.Fail, "a violated required resource policy is genuine evidence in the shared score");
        var insufficient = ResourceStabilityAnalyzer.Assess(new ResourceStabilityAssessment { Configured = true, Components = [ResourceStabilityAnalyzerTests.Component(ResourceStabilityAnalyzerTests.Series(4, i => 400 + i * 50), Api)] },
            config, ResourceStabilityAnalyzerTests.Windows(30, warmup: 0));
        (await h.Execution.FinishAsync(store, baseRun with { RunId = Guid.NewGuid() }, result, insufficient)).Verdict.Should().NotBe(PerformanceQualityVerdict.Fail);
    }

    [Fact]
    public async Task Readiness_ShowsResourceItems_WithoutBlockingTheLoadTest()
    {
        await using var h = new Harness();
        var readiness = h.Services.GetRequiredService<PerformanceTestReadinessService>();
        var shortRun = await readiness.EvaluateAsync(Definition(c => c with { SampleIntervalSeconds = 10 }), P.Data(), false);
        shortRun.Items.Single(i => i.Key == "resources").Blocking.Should().BeFalse();
        shortRun.Items.Single(i => i.Key == "resource-evidence").Detail.Should().Contain("Longer controlled runs (soak) give stronger resource-stability evidence");
        shortRun.Items.Single(i => i.Key == "resource-policy").Detail.Should().Contain("descriptively");
        var notConfigured = await readiness.EvaluateAsync(P.Definition(), P.Data(), false);
        notConfigured.Items.Single(i => i.Key == "resources").Detail.Should().StartWith("Not configured");

        h.Runtime.Containers.Clear();
        var required = await readiness.EvaluateAsync(Definition(c => c with { TargetComponentIds = [Api], RequireResourceEvidence = true }), P.Data(), false);
        required.Items.Single(i => i.Key == "resources").Should().Match<PerformanceReadinessItem>(i => i.Blocking && i.State == PerformanceReadinessState.ProviderUnavailable);
    }

    [Fact]
    public async Task OnlyApprovedTargets_CanBeObserved_AndNothingIsEnumerated()
    {
        await using var h = new Harness();
        var readiness = h.Services.GetRequiredService<PerformanceTestReadinessService>();
        var r = await readiness.EvaluateAsync(Definition(c => c with { TargetComponentIds = ["postgres-prod-db"] }), P.Data(), false);
        r.Ready.Should().BeFalse();
        r.Blockers.Should().Contain(b => b.Contains("'postgres-prod-db' is not an approved resource target"));
        r.Blockers.Should().NotContain(b => b.Contains("pay-api-1"), "container names are never listed");
        var controller = new BirkNext.Api.Controllers.PerformanceTestsController(h.Store(), readiness, h.Execution, h.Registry, h.Services.GetRequiredService<PerformanceTestOptions>(),
            null, h.Services.GetRequiredService<ResourceObservationRegistry>());
        var targets = (await controller.ResourceTargets("pay-qa", "QA", default)).Result.As<Microsoft.AspNetCore.Mvc.OkObjectResult>().Value.As<IReadOnlyList<ResourceTargetStatus>>();
        targets.Select(t => t.Target.Id).Should().BeEquivalentTo(PerformanceResourceOptions.SelfTargetId, Api);
        JsonSerializer.Serialize(targets).Should().NotContain("pay-api-1", "internal container names stay internal");
        var providers = (await controller.ResourceProviders(default)).Result.As<Microsoft.AspNetCore.Mvc.OkObjectResult>().Value.As<IReadOnlyList<ResourceProviderCapability>>();
        providers.Single(p => p.ProviderId == ResourceProviderIds.Browser).Availability.Should().Be("Unsupported");
        providers.Single(p => p.ProviderId == ResourceProviderIds.Podman).Availability.Should().Be("Available");
    }

    [Fact]
    public async Task PodmanProvider_WithoutMemoryController_IsPartial_AndNeverReportsZeroMemory()
    {
        var runtime = new P.FakeRuntime { Controllers = ["cpu"] };
        runtime.Containers["pay-api-1"] = new ContainerInstanceInfo("0a1b2c3d4e5f", "t0", 0, true, null, null, null);
        runtime.OnStats = _ => PodmanContainerExecutionRuntime.ParseStats("""{"CPU":3.5,"MemUsage":0,"MemLimit":0,"PIDs":0}""", new HashSet<string> { "cpu" });
        var provider = new PodmanResourceObservationProvider(runtime);
        var status = await provider.StatusAsync();
        status.Availability.Should().Be("Partial");
        status.Metrics.Should().Equal(ResourceMetric.CpuPercent);
        status.UnavailableMetrics.Should().Equal([ResourceMetric.ContainerMemoryBytes], "memory is unavailable on this host — never reported as 0");
        status.Scope.Should().Be("Approved Podman containers");
        status.Summary.Should().NotBeNullOrWhiteSpace();
        var spec = new ResourceTargetSpec(new ResourceObservationTarget { Id = Api }, "pay-api-1");
        var sample = await provider.SampleAsync(spec);
        sample.State.Should().Be(ResourceCollectionState.PartialEvidence);
        sample.Sample!.Memory!.ContainerMemoryBytes.Should().BeNull();
        sample.Sample.Cpu!.CpuPercent.Should().Be(3.5);
        (await provider.SampleAsync(spec with { ContainerName = "missing" })).State.Should().Be(ResourceCollectionState.TargetNotFound);
        (await provider.SampleAsync(spec with { ContainerName = "bad name;rm" })).State.Should().Be(ResourceCollectionState.TargetNotFound);
    }

    [Fact]
    public async Task DotNetSelfProvider_DeclaresItsBirkNextOnlyScope_AndExternalDotNetTargetsAsUnsupported()
    {
        var status = await new DotNetRuntimeSelfObservationProvider().StatusAsync();
        status.DisplayName.Should().Be("BirkNext API runtime");
        status.Scope.Should().Be("BirkNext API process only");
        status.UnavailableMetrics.Should().BeEmpty();
        status.Limits.Should().ContainSingle().Which.Should().Be(new ResourceScopeLimit("External .NET targets", "Unsupported",
            "No process attachment or exported runtime telemetry provider is configured."));
    }

    [Fact]
    public async Task DotNetSelfProvider_ReportsRealRuntimeCounters_OnlyForBirkNext()
    {
        var provider = new DotNetRuntimeSelfObservationProvider();
        var self = new ResourceTargetSpec(ResourceObservationRegistry.Self, null);
        var first = (await provider.SampleAsync(self)).Sample!;
        first.Memory!.ManagedHeapBytes.Should().BeGreaterThan(0);
        first.Memory.WorkingSetBytes.Should().BeGreaterThan(0);
        first.Memory.Gen0Collections.Should().NotBeNull();
        GC.Collect(); GC.WaitForPendingFinalizers();
        await Task.Delay(50);
        var second = (await provider.SampleAsync(self)).Sample!;
        second.Memory!.GcHeapAfterGcBytes.Should().BeGreaterThan(0, "a new GC records its post-GC heap (the floor)");
        second.Cpu!.CpuPercent.Should().NotBeNull("CPU is a delta between two readings");
        second.Memory.AllocationRateBytesPerSecond.Should().NotBeNull();
        second.InstanceId.Should().Be(first.InstanceId);
        (await provider.SampleAsync(new ResourceTargetSpec(new ResourceObservationTarget { Id = Api }, "pay-api-1"))).State.Should().Be(ResourceCollectionState.ProviderUnavailable);
    }

    /// <summary>
    /// Live soak diagnostic (gated): real k6 in Podman against a throw-away busybox container, observing that container (podman stats), the k6 container
    /// (generator health) and BirkNext's own runtime. Memory is asserted according to what this host can actually measure.
    /// </summary>
    [PodmanK6LiveFact]
    public async Task LivePodman_ShortSoakDiagnostic_ObservesTargetAndGenerator_AndCleansUp()
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var network = $"birknext-rs-live-{id}";
        var target = $"birknext-rs-target-{id}";
        var runner = new SystemProcessRunner();
        async Task<ProcessOutcome> Podman(params string[] args) => await runner.RunAsync(new ProcessSpec("podman", args, Path.GetTempPath()), TimeSpan.FromMinutes(2), 64 * 1024, CancellationToken.None);
        try
        {
            (await Podman("network", "create", network)).ExitCode.Should().Be(0);
            (await Podman("run", "--detach", "--rm", "--name", target, "--network", network, "docker.io/library/busybox:latest", "httpd", "-f", "-p", "8080", "-h", "/etc")).ExitCode.Should().Be(0);
            var options = new PerformanceTestOptions
            {
                Container = new PerformanceContainerOptions { TargetNetworks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [target] = network } },
                Resources = new PerformanceResourceOptions { MinSampleIntervalSeconds = 1, Targets = [new ResourceTargetSpec(new ResourceObservationTarget { Id = "live-target", DisplayName = "Live busybox target", ProviderId = ResourceProviderIds.Podman }, target)] },
            };
            var runtime = new PodmanContainerExecutionRuntime(runner, options, NullLogger<PodmanContainerExecutionRuntime>.Instance);
            await using var h = new Harness(options, runtime);
            var d = await h.SeedAsync(P.Definition(x => x with
            {
                TargetOrigin = $"http://{target}:8080", EnvironmentType = "Test",
                Scenario = x.Scenario with { TestDataProfileId = null, Steps = [new HttpPerformanceStep { Name = "hostname", RelativePath = "/hostname", ExpectedStatusCodes = [200], ThinkTimeMs = 50 }] },
                Workload = new PerformanceWorkload { Purpose = WorkloadPurpose.Soak, VirtualUsers = 3, WarmupSeconds = 4, RampUpSeconds = 2, SteadyStateSeconds = 24, RampDownSeconds = 0 },
                ResourceObservation = new ResourceObservationConfiguration
                {
                    Enabled = true, TargetComponentIds = ["live-target", PerformanceResourceOptions.SelfTargetId], SampleIntervalSeconds = 2, CooldownSeconds = 4,
                    Policies = [new ResourceStabilityPolicy { Metric = ResourceMetric.ContainerMemoryBytes, AllowedRelativeGrowthPercent = 50 }],
                },
            }));
            var run = await h.RunToEndAsync(d);
            run.State.Should().Be(PerformanceRunState.Completed, run.StateReason);
            run.Metrics!.RequestCount.Should().BeGreaterThan(0);
            var r = run.Resources!;
            var tgt = r.Components.Single(c => c.TargetId == "live-target");
            tgt.Samples.Count.Should().BeGreaterThanOrEqualTo(8);
            tgt.Samples.Should().OnlyContain(s => s.Cpu!.CpuPercent != null);
            var memoryAccounted = (await runtime.ControllersAsync()).Contains("memory");
            var memory = tgt.Summaries.Single(s => s.Metric == ResourceMetric.ContainerMemoryBytes);
            if (memoryAccounted) memory.AssessmentState.Should().NotBe(ResourceAssessmentState.Unavailable);
            else
            {
                tgt.Samples.Should().OnlyContain(s => s.Memory!.ContainerMemoryBytes == null, "rootless Podman without a memory controller cannot measure container memory");
                memory.AssessmentState.Should().Be(ResourceAssessmentState.Unavailable);
                tgt.CollectionState.Should().Be(ResourceCollectionState.PartialEvidence);
            }
            r.Components.Single(c => c.Role == ResourceComponentRole.LoadGenerator).Samples.Should().NotBeEmpty("the k6 container is observed while it runs");
            r.Components.Single(c => c.TargetId == PerformanceResourceOptions.SelfTargetId).Summaries.Should().Contain(s => s.Metric == ResourceMetric.ManagedHeapBytes && s.SteadySampleCount > 0);
            (await runtime.ListManagedAsync("performance-test")).Should().BeEmpty("no k6 container outlives the run");
            var stored = JsonSerializer.Serialize((await h.Store().RunAsync(run.RunId))!.Resources);
            await Task.Delay(3000);
            JsonSerializer.Serialize((await h.Store().RunAsync(run.RunId))!.Resources).Should().Be(stored, "collectors stopped; the finished run is immutable");
        }
        finally
        {
            await Podman("rm", "--force", "--ignore", target);
            await Podman("network", "rm", "--force", network);
        }
    }

    [Fact]
    public void ResourceSamples_AreBoundedPerComponent()
    {
        var session = new ResourceObservationSession(Guid.NewGuid(), [], TimeSpan.FromSeconds(1), 60, NullLogger.Instance);
        var samples = ResourceStabilityAnalyzerTests.Series(61, i => 100 + i);
        ResourceObservationSession.Downsample(samples).Count.Should().Be(31);
        var options = PerformanceTestOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PerformanceTests:Resources:MaxSamplesPerComponent"] = "999999", ["PerformanceTests:Resources:Targets:0:Id"] = "api", ["PerformanceTests:Resources:Targets:0:Container"] = "bad name",
            ["PerformanceTests:Resources:Targets:1:Id"] = "worker", ["PerformanceTests:Resources:Targets:1:Container"] = "worker-1",
        }).Build());
        options.Resources.MaxSamplesPerComponent.Should().Be(5000, "the cap is clamped");
        options.Resources.Targets.Select(t => t.Target.Id).Should().Equal("worker");
        session.Snapshot(false).Components.Should().BeEmpty();
    }
}
