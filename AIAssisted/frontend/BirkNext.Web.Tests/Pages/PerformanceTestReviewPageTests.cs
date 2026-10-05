using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PerformanceMetric = BirkNext.PerformanceTests.PerformanceMetric;
using PerformanceReadinessState = BirkNext.PerformanceTests.PerformanceReadinessState;
using PerformanceThreshold = BirkNext.PerformanceTests.PerformanceThreshold;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Performance Test Review page: landing, configuration, readiness, run/cancel, results (execution ≠ quality, no score ≠ 0), history, baselines, compare.</summary>
public sealed class PerformanceTestReviewPageTests : BunitContext
{
    private sealed class FakeApi : IPerformanceTestApiService
    {
        public PerformanceTestOverview? Overview { get; set; }
        public PerformanceTestReadiness? Readiness { get; set; }
        public List<PerformanceTestRun> Runs { get; set; } = [];
        public List<PerformanceBaseline> Baselines { get; set; } = [];
        public PerformanceApiResult<PerformanceTestRun>? StartResult { get; set; }
        public PerformanceApiResult<PerformanceTestDefinition>? SaveResult { get; set; }
        public List<string> Calls { get; } = [];
        public PerformanceReachability Reachability { get; set; } = new() { State = "Reachable", Reachable = true, Detail = "The k6 container reached https://paymenthub-qa.example.test (HTTP 200).", CheckedAt = DateTimeOffset.UtcNow };
        public Task<PerformanceApiResult<PerformanceReachability>> NetworkCheckAsync(string environmentId, string definitionId, CancellationToken ct = default)
        { Calls.Add("network-check"); return Task.FromResult(PerformanceApiResult<PerformanceReachability>.Ok(Reachability)); }
        public List<ResourceTargetStatus> ResourceTargets { get; set; } =
        [
            new(new ResourceObservationTarget { Id = "birknext-api", DisplayName = "BirkNext API (this process)", ProviderId = ResourceProviderIds.DotNetRuntime }, true, "Available", "BirkNext API process (this process).", null),
            new(new ResourceObservationTarget { Id = "pay-api", DisplayName = "Payments API", ProviderId = ResourceProviderIds.Podman }, true, "Partial", "Container found. CPU only — container memory accounting is not available.", null),
        ];
        public Task<List<ResourceTargetStatus>> ResourceTargetsAsync(string environmentId, string environmentType, CancellationToken ct = default) => Task.FromResult(ResourceTargets);
        public List<ResourceProviderCapability> ResourceProviderList { get; set; } =
        [
            new(ResourceProviderIds.Podman, "Podman container resources", "Partial", "Podman 5.4.2: CPU only — container memory accounting is not available.", [ResourceMetric.CpuPercent]),
            new(ResourceProviderIds.DotNetRuntime, ".NET runtime (BirkNext process)", "Available", "In-process runtime counters of the BirkNext API itself.", [ResourceMetric.ManagedHeapBytes]),
            new(ResourceProviderIds.Browser, "Browser memory", "Unsupported", "Not a resource provider: the JavaScript heap does not represent Blazor/.NET WASM managed memory.", []),
        ];
        public Task<List<ResourceProviderCapability>> ResourceProvidersAsync(CancellationToken ct = default) => Task.FromResult(ResourceProviderList);
        public List<PerformanceProviderStatus> ProviderList { get; set; } = [];
        public Task<List<PerformanceProviderStatus>> ProvidersAsync(CancellationToken ct = default) => Task.FromResult(ProviderList);
        public Task<PerformanceApiResult<PerformanceProviderStatus>> PrepareProviderAsync(string providerId, CancellationToken ct = default)
        { Calls.Add("prepare"); return Task.FromResult(PerformanceApiResult<PerformanceProviderStatus>.Ok(ProviderList[0] with { Availability = ProviderAvailability.Available, ImagePresent = true })); }

        public Task<PerformanceTestOverview?> OverviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(Overview);
        public Task<PerformanceApiResult<PerformanceTestDefinition>> SaveDefinitionAsync(string environmentId, PerformanceTestDefinition definition, bool create, CancellationToken ct = default)
        { Calls.Add(create ? "create" : "update"); return Task.FromResult(SaveResult ?? PerformanceApiResult<PerformanceTestDefinition>.Ok(definition with { Id = "def-1", Version = definition.Version + 1 })); }
        public Task<PerformanceApiResult<PerformanceTestDataProfile>> SaveDataProfileAsync(string environmentId, PerformanceTestDataProfile profile, CancellationToken ct = default) =>
            Task.FromResult(PerformanceApiResult<PerformanceTestDataProfile>.Ok(profile));
        public Task<PerformanceTestReadiness?> ReadinessAsync(string environmentId, string definitionId, CancellationToken ct = default) => Task.FromResult(Readiness);
        public Task<PerformanceApiResult<PerformanceTestRun>> StartAsync(string environmentId, PerformanceRunRequest request, CancellationToken ct = default)
        { Calls.Add("start"); return Task.FromResult(StartResult ?? PerformanceApiResult<PerformanceTestRun>.Ok(Run(PerformanceRunState.Running))); }
        public Task<PerformanceTestRun?> RunAsync(string environmentId, Guid runId, CancellationToken ct = default) => Task.FromResult(Runs.FirstOrDefault(r => r.RunId == runId));
        public Task<List<PerformanceTestRun>> RunsAsync(string environmentId, string? definitionId, CancellationToken ct = default) => Task.FromResult(Runs);
        public Task<PerformanceTestRun?> CancelAsync(string environmentId, Guid runId, CancellationToken ct = default)
        { Calls.Add("cancel"); return Task.FromResult<PerformanceTestRun?>(Runs.First(r => r.RunId == runId) with { State = PerformanceRunState.Cancelling }); }
        public Task<List<PerformanceBaseline>> BaselinesAsync(string environmentId, string? definitionId, CancellationToken ct = default) => Task.FromResult(Baselines);
        public Task<PerformanceBaselinePromotionResult> PromoteAsync(string environmentId, PerformanceBaselinePromotion request, CancellationToken ct = default)
        {
            Calls.Add("promote:" + request.Reason);
            var b = new PerformanceBaseline { RunId = request.RunId, Version = 1, DefinitionId = "def-1", Status = PerformanceBaselineStatus.Active, ComparisonFingerprint = "cf" };
            Baselines = [b];
            return Task.FromResult(new PerformanceBaselinePromotionResult(b, null, null));
        }
        public Task<PerformanceRunComparison?> CompareAsync(string environmentId, Guid current, Guid? reference, string? baselineId, CancellationToken ct = default) =>
            Task.FromResult<PerformanceRunComparison?>(new PerformanceRunComparison
            {
                Kind = PerformanceComparisonKind.RunVsRun, AdHoc = true, CurrentRunId = current, ReferenceRunId = reference ?? Guid.Empty, Compatible = false,
                CompatibilityNotes = ["Not comparable — workload changed."], Deltas = [PerformanceTestRules.Delta(PerformanceMetric.LatencyP95Ms, 340, 420)],
            });
    }

    private readonly FakeApi _api = new();
    private static readonly PerformanceTestDefinition Definition = new()
    {
        Id = "def-1", EnvironmentId = "pay", Name = "Lookup baseline", TargetOrigin = "https://paymenthub-qa.example.test", EnvironmentType = "QA", EnvironmentName = "PaymentHub QA",
        Version = 2, ComparisonFingerprint = "cf",
        Scenario = new PerformanceScenario { Name = "Lookup", Steps = [new HttpPerformanceStep { Name = "lookup", RelativePath = "/api/payments" }] },
        Thresholds = [new PerformanceThreshold { Metric = PerformanceMetric.LatencyP95Ms, Value = 500 }],
    };

    private static PerformanceTestRun Run(PerformanceRunState state, PerformanceQualityVerdict verdict = PerformanceQualityVerdict.NotAssessed, List<PerformanceThresholdResult>? thresholds = null,
        QualityResult? quality = null) => new()
    {
        RunId = Guid.NewGuid(), EnvironmentId = "pay", DefinitionId = "def-1", DefinitionVersion = 2, DefinitionSnapshot = Definition, ComparisonFingerprint = "cf", State = state, Verdict = verdict,
        CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow, ProviderId = PerformanceProviderIds.K6, ProviderVersion = "0.52.0", TargetOrigin = Definition.TargetOrigin,
        Metrics = state == PerformanceRunState.Completed ? new PerformanceMetrics { RequestCount = 1500, SuccessfulRequests = 1497, FailedRequests = 3, ErrorRatePercent = 0.2, RequestsPerSecond = 25.5,
            Latency = new PerformanceLatency { P50Ms = 180, P90Ms = 350, P95Ms = 610, P99Ms = 700, MeanMs = 210, MaxMs = 900 } } : null,
        ThresholdResults = thresholds ?? [], Quality = quality,
    };

    private static PerformanceProviderStatus Provider(ProviderAvailability availability) => new()
    {
        ProviderId = PerformanceProviderIds.K6, DisplayName = "k6", Availability = availability, Version = availability == ProviderAvailability.Available ? "0.52.0" : null,
        Detail = availability == ProviderAvailability.Available ? "k6 0.52.0 is available." : "k6 is not installed or configured on this BirkNext host.",
        Capabilities = new PerformanceProviderCapabilities { Http = true, GraphQl = true, Purposes = [.. Enum.GetValues<WorkloadPurpose>()], Modes = [WorkloadMode.VirtualUsers, WorkloadMode.ArrivalRate] },
        Runtime = new PerformanceRuntimeStatus { RuntimeId = PerformanceProviderIds.PodmanRuntime, DisplayName = "Podman", Version = "5.4.2",
            Availability = availability == ProviderAvailability.RuntimeUnavailable ? ProviderAvailability.Unavailable : ProviderAvailability.Available, Detail = "Podman 5.4.2 is available." },
        Image = "docker.io/grafana/k6:1.0.0", ImagePresent = availability != ProviderAvailability.ImageMissing, ImageDigest = "sha256:72f3", AllowImagePull = true,
    };

    private static PerformanceTestReadiness ReadyState() => new()
    {
        Ready = true, EstimatedDurationSeconds = 205, EstimatedMaxRequests = 2000,
        Items = [new("provider", "Provider", PerformanceReadinessState.Ready, "k6 0.52.0 is available.", false), new("thresholds", "Thresholds", PerformanceReadinessState.Ready, "1 required, 0 advisory.", false)],
    };

    private void Register(bool profile = true, ProviderAvailability availability = ProviderAvailability.Available, PerformanceTestReadiness? readiness = null, List<PerformanceTestDefinition>? definitions = null)
    {
        _api.Overview = new PerformanceTestOverview { Definitions = definitions ?? [Definition], Provider = Provider(availability), Limits = new PerformanceSafetyLimits(50, 50, 1800, 3600, 200_000, true, true) };
        _api.Readiness = readiness ?? ReadyState();
        Services.AddSingleton<IPerformanceTestApiService>(_api);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = profile ? new FrontendAnalysisProfile { Id = "pay", Name = "PaymentHub QA", EnvironmentType = FrontendEnvironmentType.QA, TargetUrl = "https://paymenthub-qa.example.test/app" } : null,
        });
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<PerformanceTestReview> Page()
    {
        var cut = Render<PerformanceTestReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-page]"));
        return cut;
    }

    private static void Tab(IRenderedComponent<PerformanceTestReview> cut, string tab) => cut.Find($"[data-testid=pt-tab-{tab}]").Click();

    // ── Resource Stability ───────────────────────────────────────────────────────────────────────────────────────────

    private const double MiB = 1024 * 1024;

    private static ResourceStabilityAssessment Resources(ResourceAssessmentState memoryState = ResourceAssessmentState.Regression) => new()
    {
        Configured = true, EvidenceState = ResourceCollectionState.PartialEvidence, SampleIntervalSeconds = 10, WarmupExcludedSeconds = 60, ObservationStart = DateTimeOffset.UtcNow.AddMinutes(-10),
        ObservationEnd = DateTimeOffset.UtcNow, WorkloadStart = DateTimeOffset.UtcNow.AddMinutes(-10),
        Components =
        [
            new ResourceComponentObservation
            {
                TargetId = "pay-api", DisplayName = "Payments API", Role = ResourceComponentRole.Target, ProviderId = ResourceProviderIds.Podman, CollectionState = ResourceCollectionState.Collected,
                RawSampleCount = 60, MemoryLimitBytes = 1024 * MiB,
                Samples = Enumerable.Range(0, 60).Select(i => new ResourceSample { At = DateTimeOffset.UtcNow.AddMinutes(-10).AddSeconds(i * 10), InstanceId = "a", Memory = new MemoryResourceSample { ContainerMemoryBytes = (400 + i * 5) * MiB } }).ToList(),
                Summaries =
                [
                    new ResourceStabilitySummary { TargetId = "pay-api", Metric = ResourceMetric.ContainerMemoryBytes, EarlySteadyValue = 460 * MiB, LateSteadyValue = 650 * MiB, PeakValue = 695 * MiB,
                        AbsoluteGrowth = 190 * MiB, RelativeGrowthPercent = 41.3, TrendSlopePerMinute = 30 * MiB, TrendConfidence = "High", AssessmentState = memoryState,
                        AssessmentReason = "A required resource policy was violated on sufficient evidence. This is a policy violation, not a proven leak or a known root cause.",
                        Limitations = ["Only container-level memory was available. Managed .NET heap metrics were not available, so BirkNext cannot determine whether observed growth came from the managed heap."] },
                    new ResourceStabilitySummary { TargetId = "pay-api", Metric = ResourceMetric.ManagedHeapBytes, AssessmentState = ResourceAssessmentState.Unavailable, AssessmentReason = "Managed heap was not reported by resource.podman; it is unavailable, not zero." },
                ],
            },
            new ResourceComponentObservation { TargetId = "load-generator", DisplayName = "k6 load generator", Role = ResourceComponentRole.LoadGenerator, ProviderId = ResourceProviderIds.Podman,
                CollectionState = ResourceCollectionState.PartialEvidence, CollectionDetail = "Container memory accounting is not available (no memory cgroup controller delegated to this Podman); memory is unavailable, not 0." },
        ],
        Findings = [new ResourceFinding("ResourceGrowthExceededPolicy", "Regression", "pay-api", ResourceMetric.ContainerMemoryBytes,
            "Payments API: Container memory exceeded steady-state growth ≤ 20 % (observed 41.3 %).", "Early steady 460 MiB, late steady 650 MiB")],
        Limitations = ["Resource Stability is evidence for this scenario, environment and duration. Growth is not proof of a memory leak, stability is not proof of its absence, and no root cause is inferred."],
    };

    [Fact]
    public void ResourcesTab_OffersOnlyApprovedTargets_AndMakesTheDraftDirty()
    {
        Register();
        var cut = Page();
        cut.Find("[data-testid=pt-overview-resources]").TextContent.Should().Be("Disabled");
        Tab(cut, "resources");
        cut.Find("[data-testid=prc-recommendation]").TextContent.Should().Contain("Longer controlled (soak) runs provide stronger resource-stability evidence");
        cut.Find("#prc-enabled").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=prc-target]").Select(e => e.GetAttribute("data-target")).Should().Equal("birknext-api", "pay-api"));
        cut.Find("label[for=prc-target-pay-api]").TextContent.Should().Contain("Partial — some metrics unavailable");
        cut.Find("[data-testid=prc-target][data-target=pay-api]").Change(true);
        cut.Find("[data-testid=prc-add-policy]").Click();
        cut.FindAll("[data-testid=prc-policy]").Should().HaveCount(1);
        cut.Find("[data-testid=prc-policy-relative]").Change("20");
        cut.Find("[data-testid=pt-save-state]").TextContent.Should().Contain("Unsaved changes");
        cut.Find("[data-testid=pt-tab-overview]").Click();
        cut.Find("[data-testid=pt-overview-resources]").TextContent.Should().StartWith("Enabled · every 10 s · 1 stability / 0 drift policy");
        cut.Find("[data-testid=pt-overview-resource-targets]").TextContent.Should().Contain("Payments API (resource.podman)");
    }

    [Fact]
    public void SoakWorkload_RecommendsLongerControlledRuns_WithoutLeakClaims()
    {
        Register(definitions: [Definition with { Workload = Definition.Workload with { Purpose = WorkloadPurpose.Soak } }]);
        var cut = Page();
        Tab(cut, "workload");
        var note = cut.Find("[data-testid=pt-soak-note]").TextContent;
        note.Should().Contain("Longer controlled runs provide stronger resource-stability evidence");
        note.Should().NotContain("detect leaks");
    }

    [Fact]
    public void Results_ShowResourceStabilityPerComponent_WithUnavailableNeverZero_AndNoLeakVerdict()
    {
        Register();
        var run = Run(PerformanceRunState.Completed, PerformanceQualityVerdict.Fail) with { Resources = Resources() };
        _api.Runs = [run];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=prr]").GetAttribute("data-evidence").Should().Be("PartialEvidence"));
        var api = cut.Find("[data-testid=prr-component][data-target=pay-api]");
        api.GetAttribute("data-role").Should().Be("Target");
        api.QuerySelector("[data-testid=prr-row][data-metric=ContainerMemoryBytes]")!.TextContent.Should().Contain("460 MiB").And.Contain("650 MiB").And.Contain("Resource regression (policy violated)");
        api.QuerySelector("[data-testid=prr-row][data-metric=ManagedHeapBytes]")!.TextContent.Should().Contain("Unavailable").And.NotContain("0 B");
        api.TextContent.Should().Contain("Only container-level memory was available. Managed .NET heap metrics were not available, so BirkNext cannot determine whether observed growth came from the managed heap.");
        api.QuerySelector("[data-testid=prr-chart] svg")!.GetAttribute("role").Should().Be("img");
        cut.Find("[data-testid=prr-component][data-target=load-generator]").TextContent.Should().Contain("Load generator health").And.Contain("memory is unavailable, not 0");
        cut.Find("[data-testid=prr-finding]").GetAttribute("data-code").Should().Be("ResourceGrowthExceededPolicy");
        cut.Find("[data-testid=prr]").TextContent.Should().NotContain("Memory leak detected").And.NotContain("Leak detected");
        cut.Find("[data-testid=prr-limitations]").TextContent.Should().Contain("Growth is not proof of a memory leak");
    }

    [Fact]
    public void Results_WithoutResourceObservation_SayNotConfigured()
    {
        Register();
        _api.Runs = [Run(PerformanceRunState.Completed)];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=prr-not-configured]").TextContent.Should().Be("Resource observation: Not configured."));
    }

    [Fact]
    public void Results_ShowResourceDrift_SeparateFromAbsolutePolicies()
    {
        Register();
        var drift = new ResourceDriftAssessment
        {
            BaselineRunId = Guid.NewGuid(), Compatible = true, State = PerformanceDriftState.Regression,
            Deltas = [new ResourceMetricDelta { TargetId = "pay-api", Metric = ResourceMetric.ContainerMemoryBytes, Statistic = ResourceStatistic.LateSteady, Reference = 400 * MiB, Current = 700 * MiB,
                AbsoluteDelta = 300 * MiB, RelativePercent = 75, Direction = MetricChangeDirection.Worse, PolicyState = PerformanceDriftState.Regression, PolicyText = "Accepted change +25 %" }],
            Findings = [new ResourceFinding("SteadyStateMemoryRegression", "Regression", "pay-api", ResourceMetric.ContainerMemoryBytes,
                "Payments API: late steady-state container memory changed 75 % from the baseline and exceeded the accepted drift (+25 %).", "Baseline 400 MiB, current 700 MiB.")],
        };
        _api.Runs = [Run(PerformanceRunState.Completed) with { Resources = Resources(ResourceAssessmentState.StableWithinPolicy), ResourceDrift = drift }];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=prr-drift]").GetAttribute("data-state").Should().Be("Regression"));
        cut.Find("[data-testid=prr-drift-row]").TextContent.Should().Contain("400 MiB").And.Contain("700 MiB").And.Contain("+75 %");
        cut.Find("[data-testid=prr-row][data-metric=ContainerMemoryBytes]").TextContent.Should().Contain("Stable within policy", "absolute policy and drift are both visible");
    }

    [Fact]
    public void History_ShowsResourceColumns_WithPreciseStates()
    {
        Register();
        _api.Runs = [Run(PerformanceRunState.Completed) with { Resources = Resources() }, Run(PerformanceRunState.Completed)];
        var cut = Page();
        Tab(cut, "history");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pt-history-resources]").Select(e => e.TextContent).Should().Equal("Resource regression (policy violated)", "Not configured"));
        cut.Find("[data-testid=pt-history-row]").TextContent.Should().Contain("650 MiB").And.Contain("695 MiB");
    }

    [Fact]
    public void Run_ShowsLiveResourceValues_WhileRunning()
    {
        Register();
        var running = Run(PerformanceRunState.Running) with { Resources = Resources() with { InProgress = true } };
        _api.Runs = [running];
        var cut = Page();
        Tab(cut, "run");
        cut.WaitForAssertion(() => cut.Find("[data-testid=prr-live-component][data-target=pay-api]").TextContent.Should().Contain("memory 695 MiB"));
        cut.Find("[data-testid=prr-live-component][data-target=load-generator]").TextContent.Should().Contain("Partial evidence");
    }

    [Fact]
    public void SystemSettings_ListResourceProviders_SeparateFromK6()
    {
        Register();
        _api.ProviderList = [Provider(ProviderAvailability.Available)];
        var cut = Render<BirkNext.Web.Components.PerformanceTestEngineStatus>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pte-resource]").Should().HaveCount(3));
        cut.Find("[data-testid=pte-resource][data-provider='resource.podman']").GetAttribute("data-availability").Should().Be("Partial");
        cut.Find("[data-testid=pte-resource][data-provider='resource.browser']").TextContent.Should().Contain("does not represent Blazor/.NET WASM managed memory");
        cut.Find("[data-testid=pte-availability] .sd-pill").GetAttribute("data-status").Should().Be("Ready", "the k6 provider status is unchanged by resource providers");
    }

    [Fact]
    public void Export_IncludesResourceSummary_WithoutRawSamples()
    {
        var run = Run(PerformanceRunState.Completed) with { Resources = Resources() };
        var html = PerformanceTestPresentation.Export(run);
        html.Should().Contain("<h2>Resource Stability</h2>").And.Contain("Payments API").And.Contain("ResourceGrowthExceededPolicy").And.Contain("Only container-level memory was available");
        html.Should().NotContain("695.0").And.NotContain("\"InstanceId\"");
    }

    [Fact]
    public void WithoutATargetEnvironment_SaysWhatIsMissing()
    {
        Register(profile: false);
        Page().Find("[data-testid=pt-no-target]").TextContent.Should().Contain("Select a Target Environment");
    }

    [Fact]
    public void Landing_ShowsTargetEnvironmentProviderAndPurpose_NoFakeScore()
    {
        Register();
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-hero-readiness]").TextContent.Should().Be("Ready"));
        cut.Find("[data-testid=pt-purpose]").TextContent.Should().Contain("controlled load");
        cut.Find("[data-testid=pt-hero-target]").TextContent.Should().Be("https://paymenthub-qa.example.test");
        cut.Find("[data-testid=pt-hero-provider]").TextContent.Should().Be("k6 0.52.0");
        cut.Find("[data-testid=pt-overview-latest]").TextContent.Should().Be("No run yet");
        cut.Markup.Should().NotContain("0 %").And.NotContain("0%");
        cut.Find("[data-testid=pt-run-open]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ProviderUnavailable_IsAToolLimitation_AndRunIsDisabled()
    {
        Register(availability: ProviderAvailability.Unavailable, readiness: new PerformanceTestReadiness
        {
            Ready = false, Blockers = ["k6 is not installed or configured on this BirkNext host."],
            Items = [new("provider", "Provider", PerformanceReadinessState.ProviderUnavailable, "k6 is not installed or configured on this BirkNext host.", true)],
        });
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-hero-provider]").TextContent.Should().Contain("Unavailable (tool limitation)"));
        cut.Find("[data-testid=pt-run-open]").HasAttribute("disabled").Should().BeTrue();
        Tab(cut, "readiness");
        cut.Find("[data-testid=pt-readiness-item][data-key=provider]").TextContent.Should().Contain("Provider unavailable").And.Contain("not installed");
        cut.FindAll(".sd-pill-attention").Should().BeEmpty("a missing provider is not shown with the failure tone");
    }

    [Fact]
    public void ProductionBlocked_ReadinessExplainsWhy()
    {
        Register(readiness: new PerformanceTestReadiness
        {
            Ready = false, Blockers = ["Production environments cannot be used for performance tests."],
            Items = [new("environment", "Environment and production guard", PerformanceReadinessState.UnsafeEnvironment, "Production environments cannot be used for performance tests.", true)],
        });
        var cut = Page();
        Tab(cut, "readiness");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-readiness-item][data-key=environment]").TextContent.Should().Contain("Blocked — unsafe environment").And.Contain("Production environments cannot be used"));
        Tab(cut, "run");
        cut.Find("[data-testid=pt-run-start]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Scenario_GraphQlIsQueryOnly_AndEditsMarkTheDefinitionUnsaved()
    {
        Register();
        var cut = Page();
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-target-type]").Change("GraphQlHttp");
        cut.Find("[data-testid=pt-graphql-note]").TextContent.Should().Contain("query operations only").And.Contain("Mutations and subscriptions are blocked");
        cut.Find("[data-testid=pt-hero-readiness]").TextContent.Should().Be("Unsaved changes");
        cut.Find("[data-testid=pt-run-open]").HasAttribute("disabled").Should().BeTrue("an unsaved definition cannot run");
        cut.Find("[data-testid=pt-save]").Click();
        _api.Calls.Should().Contain("update");
    }

    [Fact]
    public void Workload_ShowsTimelineAndSafetyLimits()
    {
        Register();
        var cut = Page();
        Tab(cut, "workload");
        cut.Find("[data-testid=pt-vus]").Change("50");
        cut.Find("[data-testid=pt-timeline]").TextContent.Should().Contain("Warm-up at 5 virtual users").And.Contain("Ramp 5 → 50 virtual users").And.Contain("Hold 50 virtual users");
        cut.Find("[data-testid=pt-safety]").TextContent.Should().Contain("Max virtual users50").And.Contain("50 virtual users");
    }

    [Fact]
    public void Run_StartsOnlyOnExplicitAction_ThenShowsProgressAndCancel()
    {
        Register();
        var running = Run(PerformanceRunState.Running);
        _api.StartResult = PerformanceApiResult<PerformanceTestRun>.Ok(running);
        _api.Runs = [];
        var cut = Page();
        _api.Calls.Should().NotContain("start", "a test never starts on page open");
        Tab(cut, "run");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-run-start]").HasAttribute("disabled").Should().BeFalse());
        _api.Runs = [running];
        cut.Find("[data-testid=pt-run-start]").Click();
        _api.Calls.Should().Contain("start");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-progress-state]").TextContent.Should().Contain("Running"));
        cut.Find("[data-testid=pt-cancel]").Click();
        _api.Calls.Should().Contain("cancel");
    }

    [Fact]
    public void StartBlocked_ShowsTheBackendBlockers()
    {
        Register();
        _api.StartResult = PerformanceApiResult<PerformanceTestRun>.Fail("The performance test cannot run.", ["40 virtual users exceed the safety limit of 20."]);
        var cut = Page();
        Tab(cut, "run");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-run-start]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-start-blockers]").TextContent.Should().Contain("exceed the safety limit"));
    }

    [Fact]
    public void Results_RequiredThresholdFailure_IsCompletedExecution_WithFailedQuality()
    {
        Register();
        var result = new PerformanceThresholdResult { ThresholdId = "p95", Metric = PerformanceMetric.LatencyP95Ms, Operator = ThresholdOperator.LessThan, Expected = 500, Measured = 610,
            Severity = ThresholdSeverity.Required, Outcome = CheckOutcome.Fail };
        _api.Runs = [Run(PerformanceRunState.Completed, PerformanceQualityVerdict.Fail, [result], ScoreSemantics.Compute([CheckOutcome.Fail]))];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-result-state]").TextContent.Should().Be("Completed"));
        cut.Find("[data-testid=pt-result-quality]").TextContent.Should().Contain("Fail");
        cut.Find("[data-testid=pt-threshold-result]").TextContent.Should().Contain("610 ms").And.Contain("< 500 ms").And.Contain("Fail").And.Contain("Required");
        cut.Find("[data-testid=pt-latency]").TextContent.Should().Contain("P95610 ms");
        cut.Find("[data-testid=pt-result-nobaseline]").TextContent.Should().Contain("Not configured");
    }

    [Fact]
    public void Results_NoThresholds_AreNotAssessed_NeverZero()
    {
        Register();
        _api.Runs = [Run(PerformanceRunState.Completed, PerformanceQualityVerdict.NotAssessed, [], ScoreSemantics.Compute([]))];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-no-thresholds]").TextContent.Should().Contain("measured, not assessed"));
        cut.Find("[data-testid=pt-result-quality]").TextContent.Should().Contain("Not assessed").And.NotContain("0");
    }

    [Fact]
    public void Results_ExecutionFailure_IsNotAPerformanceResult()
    {
        Register();
        _api.Runs = [Run(PerformanceRunState.ExecutionFailed) with { StateReason = "k6 exited with code 107." }];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-result-state]").TextContent.Should().Contain("not a performance result"));
        cut.Find("[data-testid=pt-result-quality]").TextContent.Should().Contain("Not assessed");
    }

    [Fact]
    public void History_PromoteToBaseline_IsExplicit_WithAReason()
    {
        Register();
        var completed = Run(PerformanceRunState.Completed, PerformanceQualityVerdict.Pass);
        _api.Runs = [completed, Run(PerformanceRunState.Cancelled)];
        var cut = Page();
        Tab(cut, "history");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pt-history-row]").Should().HaveCount(2));
        cut.FindAll("[data-testid=pt-promote-open]").Should().ContainSingle("only a completed run with metrics can become a baseline");
        cut.Find("[data-testid=pt-promote-open]").Click();
        cut.Find("[data-testid=pt-promote-reason]").Change("infrastructure upgraded");
        cut.Find("[data-testid=pt-promote-confirm]").Click();
        _api.Calls.Should().Contain("promote:infrastructure upgraded");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-baseline-row]").GetAttribute("data-status").Should().Be("Active"));
        cut.Find("[data-testid=pt-history-filter]").Change("Cancelled");
        cut.FindAll("[data-testid=pt-history-row]").Should().ContainSingle().Which.GetAttribute("data-state").Should().Be("Cancelled");
    }

    [Fact]
    public void Compare_ShowsDeltasAndCompatibility_AsAdHoc()
    {
        Register();
        var a = Run(PerformanceRunState.Completed); var b = Run(PerformanceRunState.Completed);
        _api.Runs = [a, b];
        var cut = Page();
        Tab(cut, "compare");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-compare-reference]"));
        cut.Find("[data-testid=pt-compare-reference]").Change(b.RunId.ToString());
        cut.Find("[data-testid=pt-compare-go]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-compare-kind]").TextContent.Should().Contain("Ad-hoc comparison"));
        cut.Find("[data-testid=pt-compare-note]").TextContent.Should().Be("Not comparable — workload changed.");
        cut.Find("[data-testid=pt-delta][data-metric=LatencyP95Ms]").TextContent.Should().Contain("+80 ms (+23.5 %)");
    }

    [Fact]
    public void NewProject_StartsFromAGenericDefinition_BasedOnTheTargetEnvironment()
    {
        Register(definitions: []);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-hero-readiness]").TextContent.Should().Be("Not saved yet"));
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-target]").TextContent.Should().Be("https://paymenthub-qa.example.test", "only the configured target's origin; no free destination field");
        cut.Find("[data-testid=pt-save-state]").TextContent.Should().Be("Not saved yet.");
        cut.Find("[data-testid=pt-save]").Click();
        _api.Calls.Should().Contain("create");
    }

    // ── Podman runtime / image / container network ───────────────────────────────────────────────────────────────

    [Fact]
    public void Hero_ShowsProviderRuntimeAndImage_SeparatelyAsToolStates()
    {
        Register(availability: ProviderAvailability.RuntimeUnavailable);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-hero-runtime]").TextContent.Should().Be("Podman: Unavailable (tool limitation)"));
        cut.Find("[data-testid=pt-hero-provider]").TextContent.Should().Be("k6: Runtime unavailable (tool limitation)");
        cut.Find("[data-testid=pt-hero-image]").TextContent.Should().Contain("docker.io/grafana/k6:1.0.0");
    }

    [Fact]
    public void Readiness_ChecksTheContainerNetwork_OnExplicitAction()
    {
        Register(readiness: new PerformanceTestReadiness
        {
            Ready = false, Blockers = ["Not checked yet."],
            Items = [new("network", "Container network", PerformanceReadinessState.NeedsConfiguration, "Not checked yet: check that the k6 container can reach the target (one request). Host reachability does not prove container reachability.", true)],
        });
        var cut = Page();
        Tab(cut, "readiness");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-network-result]").TextContent.Should().Contain("not the BirkNext host"));
        _api.Calls.Should().NotContain("network-check", "the check sends a request, so it runs only on explicit action");
        _api.Readiness = ReadyState();
        cut.Find("[data-testid=pt-network-check]").Click();
        _api.Calls.Should().Contain("network-check");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-network-result]").TextContent.Should().Contain("Reachable: The k6 container reached"));
        cut.Find("[data-testid=pt-readiness-summary]").TextContent.Should().Contain("Ready to run.");
    }

    [Fact]
    public void Results_ShowContainerProvenance()
    {
        Register();
        _api.Runs = [Run(PerformanceRunState.Completed) with { RuntimeId = PerformanceProviderIds.PodmanRuntime, RuntimeVersion = "5.4.2", ContainerImage = "docker.io/grafana/k6:1.0.0", ImageDigest = "sha256:72f3",
            Reachability = new PerformanceReachability { ExecutionOrigin = "http://host.containers.internal:5095", Reachable = true } }];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-provenance-runtime]").TextContent.Should().Contain("container.podman 5.4.2").And.Contain("docker.io/grafana/k6:1.0.0 (sha256:72f3)")
            .And.Contain("host.containers.internal:5095"));
        Tab(cut, "history");
        cut.Find("[data-testid=pt-history-row]").TextContent.Should().Contain("Podman 5.4.2");
    }

    [Fact]
    public void SystemSettingsEngines_ShowRuntimeImage_AndOfferAPullOnlyWhenPolicyAllows()
    {
        Register(availability: ProviderAvailability.ImageMissing);
        _api.ProviderList = [Provider(ProviderAvailability.ImageMissing) with { Detail = "The k6 image docker.io/grafana/k6:1.0.0 is not available locally." }];
        var cut = Render<BirkNext.Web.Components.PerformanceTestEngineStatus>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pte-runtime]").TextContent.Should().Contain("Podman 5.4.2").And.Contain("Available"));
        cut.Find("[data-testid=pte-image] dd").TextContent.Should().Be("grafana/k6:1.0.0");
        cut.Find("[data-testid=pte-image-status]").TextContent.Should().Contain("Missing");
        cut.Find("[data-testid=pte-technical-body]").TextContent.Should().Contain("Allowed on request");
        cut.Find("[data-testid=pte-pull]").Click();
        _api.Calls.Should().Contain("prepare");
        _api.ProviderList = [Provider(ProviderAvailability.ImageMissing) with { AllowImagePull = false }];
        var noPull = Render<BirkNext.Web.Components.PerformanceTestEngineStatus>();
        noPull.WaitForAssertion(() => noPull.Find("[data-testid=pte-guidance]").TextContent.Should().Contain("Automatic image pull is disabled"));
        noPull.FindAll("[data-testid=pte-pull]").Should().BeEmpty();
    }
}
