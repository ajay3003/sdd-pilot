using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using D = BirkNext.Web.Services.PerformanceTestReviewDashboard;
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
        /// <summary>The persisted Endpoint Discovery history (null = none); a factory so a test can hold it pending or make it fail.</summary>
        public Func<Task<BirkNext.LocalHttpsProxy.EndpointObservationHistoryView?>>? History { get; set; }
        public Task<BirkNext.LocalHttpsProxy.EndpointObservationHistoryView?> ObservationHistoryAsync(string environmentId, string targetUrl, CancellationToken ct = default) =>
            History?.Invoke() ?? Task.FromResult<BirkNext.LocalHttpsProxy.EndpointObservationHistoryView?>(null);
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
        {
            Calls.Add(create ? "create" : "update");
            if (SaveResult is { } result) return Task.FromResult(result);
            var saved = definition with { Id = "def-1", Version = create ? 1 : definition.Version + 1 };
            if (Overview is not null) Overview = Overview with { Definitions = [.. Overview.Definitions.Where(d => d.Id != saved.Id), saved] };
            return Task.FromResult(PerformanceApiResult<PerformanceTestDefinition>.Ok(saved));
        }
        public Task<PerformanceApiResult<PerformanceTestDataProfile>> SaveDataProfileAsync(string environmentId, PerformanceTestDataProfile profile, CancellationToken ct = default) =>
            Task.FromResult(PerformanceApiResult<PerformanceTestDataProfile>.Ok(profile));
        public Task<PerformanceTestReadiness?> ReadinessAsync(string environmentId, string definitionId, CancellationToken ct = default) => Task.FromResult(Readiness);
        /// <summary>The coordinated Check readiness; null = answer with the current Readiness and a new reachable check.</summary>
        public Func<Task<PerformanceApiResult<PerformanceReadinessCheck>>>? Check { get; set; }
        public Task<PerformanceApiResult<PerformanceReadinessCheck>> ReadinessCheckAsync(string environmentId, string definitionId,
            BirkNext.LocalHttpsProxy.AuthenticatedReviewIdentity? identity, CancellationToken ct = default)
        {
            Calls.Add("readiness-check");
            return Check?.Invoke() ?? Task.FromResult(PerformanceApiResult<PerformanceReadinessCheck>.Ok(new PerformanceReadinessCheck
            {
                Readiness = Readiness!, Connectivity = PerformanceConnectivityOutcomes.Checked, ConnectivityDetail = "Checked now from the k6 container: the base target answered HTTP 200.",
                Reachability = Reachability,
                Steps = [new("definition", "Checking definition", "Done", "Saved version evaluated."), new("environment", "Checking execution environment", "Done", "Available."),
                    new("connectivity", "Checking target connectivity", "Done", "Checked now from the k6 container: the base target answered HTTP 200."),
                    new("blockers", "Evaluating blockers", Readiness!.Ready ? "Done" : "Attention", Readiness.Ready ? "No blocker." : "Blocked.")],
            }));
        }
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
        public Task<PerformanceBaselineDecisionResult> ProposeBaselineAsync(string environmentId, PerformanceBaselineProposal request, CancellationToken ct = default)
        {
            Calls.Add($"propose:{request.Reason}:{request.AcceptWarnings}");
            var b = new PerformanceBaseline { BaselineId = "prop-1", RunId = request.RunId, Version = 1, DefinitionId = "def-1", Status = PerformanceBaselineStatus.Proposed,
                Approval = BaselineApprovalState.Proposed, ProposedBy = "tester-a", ComparisonFingerprint = "cf" };
            Baselines = [.. Baselines, b];
            return Task.FromResult(new PerformanceBaselineDecisionResult(b, null, null));
        }
        public Task<PerformanceBaselineDecisionResult> ApproveBaselineAsync(string environmentId, string baselineId, string? note, CancellationToken ct = default)
        {
            Calls.Add("approve:" + baselineId);
            var b = Baselines.Single(x => x.BaselineId == baselineId) with { Status = PerformanceBaselineStatus.Active, Approval = BaselineApprovalState.Approved, ApprovedBy = "lead-b" };
            Baselines = [.. Baselines.Where(x => x.BaselineId != baselineId), b];
            return Task.FromResult(new PerformanceBaselineDecisionResult(b, null, null));
        }
        public Task<PerformanceBaselineDecisionResult> RejectBaselineAsync(string environmentId, string baselineId, string reason, CancellationToken ct = default)
        {
            Calls.Add($"reject:{baselineId}:{reason}");
            var b = Baselines.Single(x => x.BaselineId == baselineId) with { Status = PerformanceBaselineStatus.Rejected, Approval = BaselineApprovalState.Rejected, RejectedBy = "lead-b", DecisionNote = reason };
            Baselines = [.. Baselines.Where(x => x.BaselineId != baselineId), b];
            return Task.FromResult(new PerformanceBaselineDecisionResult(b, null, null));
        }
        public string? DeleteError { get; set; }
        public Task<string?> DeleteRunAsync(string environmentId, Guid runId, CancellationToken ct = default)
        {
            Calls.Add("delete:" + runId);
            if (DeleteError is null) Runs = [.. Runs.Where(r => r.RunId != runId)];
            return Task.FromResult(DeleteError);
        }
        public List<(Guid Current, Guid? Reference, string? Baseline)> Compared { get; } = [];
        public Task<PerformanceRunComparison?> CompareAsync(string environmentId, Guid current, Guid? reference, string? baselineId, CancellationToken ct = default) =>
            Compare(current, reference, baselineId) ? null! : Task.FromResult<PerformanceRunComparison?>(new PerformanceRunComparison
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

    private static bool Compare(Guid current, Guid? reference, string? baseline)
    {
        _lastFake?.Compared.Add((current, reference, baseline));
        return false;
    }

    private static FakeApi? _lastFake;

    [Fact]
    public void Results_OpenFullComparison_OpensCompareWithTheRunAndItsBaselineSelected_AndBackReturns()
    {
        Register();
        _lastFake = _api;
        var run = Run(PerformanceRunState.Completed) with
        {
            BaselineIdAtRun = "base-1",
            Drift = new PerformanceDriftAssessment { BaselineId = "base-1", BaselineVersion = 2, BaselineRunId = Guid.NewGuid(), Compatible = true, State = PerformanceDriftState.Stable,
                Deltas = [PerformanceTestRules.Delta(PerformanceMetric.LatencyP95Ms, 600, 610)] },
        };
        _api.Runs = [run];
        _api.Baselines = [new PerformanceBaseline { BaselineId = "base-1", RunId = Guid.NewGuid(), Version = 2, DefinitionId = "def-1", Status = PerformanceBaselineStatus.Active, ComparisonFingerprint = "cf", EffectiveFrom = DateTimeOffset.UtcNow }];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-result-baseline-delta][data-metric=LatencyP95Ms]"));
        cut.Find("[data-testid=pt-results-open-compare]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-tab-compare]").GetAttribute("aria-pressed").Should().Be("true"));
        _api.Compared.Should().ContainSingle().Which.Should().Be((run.RunId, (Guid?)null, "base-1"), "the run and its baseline are preselected — no re-selection");
        cut.Find("[data-testid=pt-compare-current]").GetAttribute("value").Should().Be(run.RunId.ToString());
        cut.Find("[data-testid=pt-compare-reference]").GetAttribute("value").Should().Be("b:base-1");
        cut.Find("[data-testid=pt-compare-back]").Click();
        cut.Find("[data-testid=pt-tab-results]").GetAttribute("aria-pressed").Should().Be("true");
    }

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

    private void Register(bool profile = true, ProviderAvailability availability = ProviderAvailability.Available, PerformanceTestReadiness? readiness = null, List<PerformanceTestDefinition>? definitions = null,
        FrontendEnvironmentType environmentType = FrontendEnvironmentType.QA)
    {
        _api.Overview = new PerformanceTestOverview { Definitions = definitions ?? [Definition], Provider = Provider(availability), Limits = new PerformanceSafetyLimits(50, 50, 1800, 3600, 200_000, true, true) };
        _api.Readiness = readiness ?? ReadyState();
        Services.AddSingleton<IPerformanceTestApiService>(_api);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = profile ? new FrontendAnalysisProfile { Id = "pay", Name = "PaymentHub QA", EnvironmentType = environmentType, TargetUrl = "https://paymenthub-qa.example.test/app" } : null,
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
        cut.Find("[data-testid=pt-run-open]").ClassList.Should().Contain("btn-secondary", "Run test never competes with the real next action while blocked");
        cut.Find("[data-testid=pt-run-open]").Click();
        _api.Calls.Should().NotContain("start", "Run test evaluates readiness and stops at the blocker");
        cut.Find("[data-testid=pt-tab-readiness]").GetAttribute("aria-pressed").Should().Be("true");
        // The single blocker's explanation is stated once, in the blocker panel; the list item names it and points there.
        cut.Find("[data-testid=pt-readiness-item][data-key=provider]").TextContent.Should().Contain("Provider unavailable").And.Contain("See the blocker above");
        cut.Find("[data-testid=pt-blocker-detail]").TextContent.Should().Contain("not installed");
        // Blocked is danger everywhere (shared vocabulary); a missing provider is Blocked — never Failed — and its text says it is a tool limitation.
        cut.FindAll("[data-semantic=Blocked]").Should().NotBeEmpty();
        cut.FindAll("[data-semantic=Failed]").Should().BeEmpty("a missing provider is not a failure");
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
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-readiness-item][data-key=environment]").TextContent.Should().Contain("Blocked — unsafe environment"));
        cut.Find("[data-testid=pt-blocker-detail]").TextContent.Should().Contain("Production environments cannot be used");
        cut.Find("[data-testid=pt-blocker-category]").TextContent.Should().NotBe("Local execution prerequisite", "production safety is not a local setup issue");
        Tab(cut, "run");
        cut.FindAll("[data-testid=pt-run-start]").Should().BeEmpty("a blocked run offers no Run button that cannot run");
        cut.Find("[data-testid=pt-run-blocked]").TextContent.Should().Contain("Run test — blocked by environment and production guard").And.Contain("Production environments cannot be used").And.NotContain("Cannot run yet");
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
        cut.Find("[data-testid=pt-definition-state] .sd-pill").GetAttribute("data-status").Should().Be("Unsaved changes to v2");
        cut.Find("[data-testid=pt-run-open]").HasAttribute("disabled").Should().BeTrue("edits of a saved version are never versioned silently by Run");
        cut.Find("[data-testid=pt-save]").TextContent.Should().Be("Save changes as new version");
        cut.Find("[data-testid=pt-save]").ClassList.Should().Contain("btn-primary");
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
        cut.Find("[data-testid=pt-latency]").TextContent.Should().Contain("P95 latency: 610 ms");
        cut.Find("[data-testid=pt-result-nobaseline]").TextContent.Should().StartWith("No baseline selected").And.Contain("Promote a completed run in History");
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
    public void History_ProposalShowsEligibility_IsApprovedOrRejectedWithAReason_AndAProtectedRunExplainsWhy()
    {
        Register();
        var completed = Run(PerformanceRunState.Completed, PerformanceQualityVerdict.Fail);
        _api.Runs = [completed];
        var cut = Page();
        Tab(cut, "history");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pt-history-row]").Should().ContainSingle());
        cut.Find("[data-testid=pt-promote-open]").Click();
        cut.Find("[data-testid=pt-promote-eligibility]").TextContent.Should().Contain("Required thresholds met").And.Contain("A required threshold failed");
        cut.Find("[data-testid=pt-promote-accept]").Change(true);
        cut.Find("[data-testid=pt-propose-confirm]").Click();
        _api.Calls.Should().Contain(c => c.StartsWith("propose:") && c.EndsWith(":True"), "the accepted warnings are sent explicitly");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-baseline-approval]").TextContent.Should().Contain("Proposed by tester-a"));

        cut.Find("[data-testid=pt-baseline-reject]").Click();
        cut.Find("[data-testid=pt-baseline-reject-confirm]").Click();
        cut.Find("[data-testid=pt-baseline-decision-error]").TextContent.Should().Contain("reason");
        cut.Find("[data-testid=pt-baseline-approve]").Click();
        _api.Calls.Should().Contain("approve:prop-1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-baseline-approval]").TextContent.Should().Contain("Approved by lead-b"));

        _api.DeleteError = "Protected: the run is the source of Baseline v1 (v1, Active). A baseline always keeps its run.";
        cut.Find("[data-testid=pt-run-delete]").Click();
        cut.Find("[data-testid=pt-run-delete]").TextContent.Should().Be("Confirm delete", "the first click only asks");
        _api.Calls.Should().NotContain(c => c.StartsWith("delete:"));
        cut.Find("[data-testid=pt-run-delete]").Click();
        cut.Find("[data-testid=pt-run-delete-error]").TextContent.Should().Contain("Protected");
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

    /// <summary>A new definition starts with zero steps (no silent "GET /"): the tester adds a manual step explicitly, then returns to Overview.</summary>
    private void AddManualStep(IRenderedComponent<PerformanceTestReview> cut)
    {
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-tab-scenario]"));
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-steps-empty]").TextContent.Should().Contain("never starts with a placeholder request");
        cut.Find("[data-testid=pt-add-step]").Click();
        // A manual step starts without a path (never an executable "GET /"): the tester enters it.
        cut.Find("[data-testid=pt-step-path]").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid=pt-step-path]").Change("/api/health-check");
        Tab(cut, "overview");
    }

    [Fact]
    public void NewProject_StartsFromAGenericDefinition_BasedOnTheTargetEnvironment()
    {
        Register(definitions: []);
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Draft · incomplete", "a new definition has no placeholder step"));
        AddManualStep(cut);
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-hero-readiness]").TextContent.Should().Be("Draft"));
        cut.Find("[data-testid=pt-readiness-reason]").TextContent.Should().Be("Check readiness asks to save the draft as v1, then evaluates that version.");
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-target]").TextContent.Should().Be("https://paymenthub-qa.example.test", "only the configured target's origin; no free destination field");
        cut.Find("[data-testid=pt-save-state] .sd-pill").GetAttribute("data-status").Should().Be("Draft · will be saved automatically before readiness check");
        cut.Find("[data-testid=pt-save]").ClassList.Should().Contain("btn-secondary", "a deliberate save stays available, but it is not the primary step");
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
        cut.Find("[data-testid=pt-hero-image]").TextContent.Should().Contain("grafana/k6:1.0.0");
        cut.Find("[data-testid=pt-technical-body]").TextContent.Should().Contain("docker.io/grafana/k6:1.0.0", "the exact image reference stays available in Technical details");
    }

    [Fact]
    public void Readiness_ChecksTheContainerNetwork_OnExplicitAction()
    {
        Register(readiness: new PerformanceTestReadiness
        {
            Ready = false, Blockers = ["Not checked yet."],
            Items = [NetworkBlocker()],
        });
        var cut = Page();
        Tab(cut, "readiness");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-blocker-headline]").TextContent.Should().Be("Container reachability has not been checked"));
        cut.Find("[data-testid=pt-blocker-detail]").TextContent.Should().Contain("Host reachability does not prove container reachability");
        var summary = cut.Find("[data-testid=pt-readiness-summary]").TextContent;
        summary.Should().StartWith("Blocked · 1 required prerequisite:").And.Contain("Container network: The load generator must");
        summary.Split("Blocked").Length.Should().Be(2, "the blocked state is stated once, followed by what blocks");
        _api.Calls.Should().NotContain("network-check", "the check sends a request, so it runs only on explicit action");
        var action = cut.Find("[data-testid=pt-blocker-action]");
        action.TextContent.Should().Be("Check container network");
        action.ClassList.Should().Contain("btn-primary", "the only blocker's action is a strong primary button, never a faint link");
        cut.Find("[data-testid=pt-blocker-action-disclosure]").TextContent.Should().Be("This sends one request from the k6 container to the PaymentHub QA target. It is not a load test.");
        _api.Readiness = ReadyState();
        action.Click();
        _api.Calls.Should().Contain("network-check").And.NotContain("start").And.NotContain("update", "re-checking never re-saves an unchanged definition");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-network-result]").TextContent.Should().StartWith("Target reachable — ", "the base target answered; no scenario endpoint is claimed verified"));
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

    // ── Control center: status, cards, next step, timeline, grouped readiness ────────────────────────────────────────

    private static string Status(IRenderedComponent<PerformanceTestReview> cut, string testId) =>
        cut.Find($"[data-testid={testId}] .sd-pill").GetAttribute("data-status")!;

    [Fact]
    public void ControlCenter_Ready_ShowsTargetEnvironmentReadinessEngine_AndRunAsTheNextStep()
    {
        Register(readiness: ReadyState() with
        {
            Items = [new("environment", "Environment and production guard", PerformanceReadinessState.Ready, "QA — a non-production environment.", false), .. ReadyState().Items,
                new("baseline", "Baseline", PerformanceReadinessState.Optional, "Optional — no baseline selected.", false)],
        });
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Ready"));

        cut.Find("[data-testid=pt-status] .visually-hidden").TextContent.Should().Be("Test readiness status: ");
        cut.Find("[data-testid=pt-card-target]").ClassList.Should().Contain("ptc-accent-info");
        Status(cut, "pt-env-safety").Should().Be("Allowed");
        cut.Find("[data-testid=pt-card-environment]").TextContent.Should().Contain("Non-production");
        cut.Find("[data-testid=pt-readiness-count]").TextContent.Should().Be("4 of 4 required readiness checks satisfied for this definition");
        cut.FindAll("[data-testid=pt-target-chips] li").Select(li => li.TextContent.Trim()).Should().Equal("✓ REST", "✓ HTTP", "✓ GraphQL queries");
        cut.FindAll("[data-testid=pt-run-reason]").Should().BeEmpty();
        cut.Find("[data-testid=pt-next-step]").GetAttribute("data-tone").Should().Be("Ready");
        cut.Find("[data-testid=pt-next-step]").TextContent.Should().Contain("Ready to run. Run the baseline test");
        cut.Find("[data-testid=pt-next-action]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Draft_IsADraft_InAmber_CheckReadinessIsPrimary_AndSaveIsSecondary()
    {
        Register(definitions: []);
        var cut = Page();
        AddManualStep(cut);
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Draft"));

        cut.Find("[data-testid=pt-status] .sd-pill").ClassList.Should().Contain("sd-pill-muted").And.NotContain("sd-pill-attention", "a draft is a neutral editor state (shared vocabulary), never a failure");
        Status(cut, "pt-definition-state").Should().Be("Draft · will be saved automatically before readiness check");
        cut.Find("[data-testid=pt-primary-action]").TextContent.Should().Be("Check readiness");
        cut.Find("[data-testid=pt-primary-action]").ClassList.Should().Contain("btn-primary");
        cut.Find("[data-testid=pt-run-open]").ClassList.Should().Contain("btn-secondary");
        cut.Find("[data-testid=pt-run-open]").GetAttribute("aria-describedby").Should().Be("pt-run-reason");
        cut.Find("#pt-run-reason").TextContent.Should().Be("Run test saves the draft as v1, checks readiness and then asks for confirmation.");
        cut.Find("[data-testid=pt-next-step]").TextContent.Should().Contain("Check readiness. You are asked to save the prefilled draft as v1 first; nothing runs.");
        cut.FindAll("[data-testid=pt-save]").Should().ContainSingle().Which.ClassList.Should().Contain("btn-secondary");
    }

    [Fact]
    public void CheckReadiness_OnANewDraft_AsksToSaveV1_ThenEvaluates_AndNothingRuns()
    {
        Register(definitions: []);
        var cut = Page();
        AddManualStep(cut);
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-primary-action]"));
        cut.Find("[data-testid=pt-primary-action]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-save-first]").TextContent.Should().Contain("Save this draft as v1 first?"));
        _api.Calls.Should().BeEmpty("nothing is saved without the explicit save");
        cut.Find("[data-testid=pt-save-and-check]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-message]").TextContent.Should().Be("Definition saved as v1 before readiness evaluation."));
        _api.Calls.Should().Equal(["create", "readiness-check"], "one explicit first save, then the coordinated readiness check of v1 — and no load");
        cut.FindAll("[data-testid=pt-flow-step]").Select(s => s.GetAttribute("data-state")).Should().Equal(["done", "done", "done", "done"]);
        cut.Find("[data-testid=pt-tab-readiness]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=pt-readiness-summary]").TextContent.Should().Contain("Ready to run.");
        Status(cut, "pt-definition-state").Should().Be("Saved · v1");
        cut.FindAll("[data-testid=pt-run-start]").Should().BeEmpty("readiness never starts a run");
    }

    [Fact]
    public void RunTest_OnANewDraft_SavesAndChecksReadiness_ThenAsksForConfirmation_DisclosingRealLoad()
    {
        Register(definitions: []);
        var cut = Page();
        AddManualStep(cut);
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-run-open]"));
        cut.Find("[data-testid=pt-run-open]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-save-and-check]"));
        _api.Calls.Should().BeEmpty("Run test never saves a new draft silently");
        cut.Find("[data-testid=pt-save-and-check]").Click();
        cut.Find("[data-testid=pt-run-open]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-tab-run]").GetAttribute("aria-pressed").Should().Be("true"));
        _api.Calls.Where(c => c != "readiness-check").Should().Equal(["create"], "Run test re-evaluates readiness (the backend reuses recent connectivity evidence) but saves only once");
        _api.Calls.Should().Contain("readiness-check");
        var traffic = cut.Find("[data-testid=pt-real-traffic]").TextContent;
        traffic.Should().Contain("This sends real load to the QA target.").And.Contain("https://paymenthub-qa.example.test").And.Contain("Nothing starts until you select Run performance test");
        cut.Find("[data-testid=pt-run-start]").TextContent.Should().Be("Run performance test");
        _api.Calls.Should().NotContain("start", "a real run needs the explicit Run performance test action");
        cut.Find("[data-testid=pt-run-start]").Click();
        _api.Calls.Should().Contain("start");
    }

    [Fact]
    public void SavedUnchangedDefinition_RunTestNeverCreatesANewVersion()
    {
        Register();
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Ready"));
        cut.Find("[data-testid=pt-run-open]").ClassList.Should().Contain("btn-primary");
        cut.Find("[data-testid=pt-run-open]").Click();
        _api.Calls.Should().NotContain("create").And.NotContain("update").And.NotContain("start");
        cut.Find("[data-testid=pt-tab-run]").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void EditedSavedDefinition_NeedsAnExplicitNewVersion_BeforeReadiness()
    {
        Register();
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Ready"));
        Tab(cut, "workload");
        cut.Find("[data-testid=pt-vus]").Change("7");
        cut.Find("[data-testid=pt-primary-action]").TextContent.Should().Be("Save changes as new version");
        Tab(cut, "readiness");
        cut.Find("[data-testid=pt-readiness-unsaved]").TextContent.Should().Contain("You changed v2");
        _api.Calls.Should().BeEmpty("nothing is versioned silently by opening Readiness");
        cut.Find("[data-testid=pt-readiness-primary]").Click();
        _api.Calls.Should().Equal(["update"]);
        cut.WaitForAssertion(() => Status(cut, "pt-definition-state").Should().Be("Saved · v3"));
    }

    private static PerformanceReadinessItem NetworkBlocker() => new("network", "Container network", PerformanceReadinessState.NeedsConfiguration,
        "The load generator must be able to reach the target independently of the BirkNext host. Host reachability does not prove container reachability.", true)
    {
        Category = PerformanceReadinessCategories.Reachability, Summary = "container network", Headline = "Container reachability has not been checked",
        Action = PerformanceReadinessActions.CheckNetwork, Status = "Not checked",
    };

    private static PerformanceReadinessItem CaMissing() => new("ca-trust", "Corporate CA trust", PerformanceReadinessState.NeedsConfiguration,
        "The k6 container cannot validate the certificate chain for paymenthub-qa.example.test: the target TLS certificate is not trusted by the default k6 container CA store. Configure the corporate CA bundle.", true)
    {
        Category = PerformanceReadinessCategories.ExecutionSetup, Summary = "container TLS trust", Headline = "Container TLS trust is not configured", Status = "Missing",
        Owner = "Local execution configuration", ConfigurationKey = "PerformanceTests:Container:CaBundlePath", Action = PerformanceReadinessActions.OpenLocalExecutionSetup,
        Technical = "TlsTrustFailure · code 1310 · TLS verification will not be disabled.",
    };

    [Fact]
    public void AnIncompleteServerChain_BlocksRuntimeExecutionOnly_TheReviewStaysUsableForDesign()
    {
        var blocked = CaMissing() with
        {
            Label = "Container TLS trust", Status = "Blocked", Headline = "Execution blocked — target certificate chain is incomplete",
            Detail = "Execution blocked — target certificate chain is incomplete. Recommended fix: configure the server, reverse proxy, ingress, gateway or load balancer to serve the complete certificate chain.",
        };
        Register(readiness: new PerformanceTestReadiness { Ready = false, Blockers = ["x"], Items = [blocked] });
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-runtime-blocked]").TextContent.Should()
            .Contain("Runtime execution unavailable for").And.Contain("target certificate chain is incomplete").And.Contain("continue designing and reviewing"));
        cut.Find("[data-testid=pt-runtime-blocked-details]").GetAttribute("href").Should().Contain("focus=container-tls-trust");
        cut.FindAll("[data-testid=pt-tabs] button, [data-testid=pt-tabs] a").Should().NotBeEmpty("designing the test is never blocked by the target's TLS");
        Tab(cut, "scenario");
        cut.FindAll("[data-testid=pt-run-start]").Should().BeEmpty("only runtime execution is blocked");
    }

    [Fact]
    public void A_missing_execution_credential_blocks_and_its_fix_opens_Scenario_authentication_focused()
    {
        var credential = new PerformanceReadinessItem("credential", "Execution credential", PerformanceReadinessState.NeedsAuthentication,
            "No API credential has been captured for this Target Environment.", true)
            { Category = PerformanceReadinessCategories.ExecutionSetup, Headline = "Authentication unresolved — execution blocked", Action = PerformanceReadinessActions.EditScenario };
        Register(readiness: new PerformanceTestReadiness { Ready = false, Blockers = [credential.Detail], Items = [credential] });
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-next-action]"));
        cut.Find("[data-testid=pt-readiness-reason]").TextContent.Should().StartWith("Authentication unresolved — execution blocked");

        cut.Find("[data-testid=pt-next-action]").Click();

        cut.Find("[data-testid=pt-tab-scenario]").GetAttribute("aria-pressed").Should().Be("true");
        cut.WaitForAssertion(() => JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Select(i => i.Arguments[0]?.ToString()).Should().Contain("pt-scenario-auth"),
            TimeSpan.FromSeconds(3));
        cut.Find("#pt-scenario-auth").GetAttribute("tabindex").Should().Be("-1", "the section can take focus");
    }

    [Fact]
    public void OneBlocker_IsNamedAtTheTop_AndOverviewOffersItsAction()
    {
        Register(readiness: new PerformanceTestReadiness { Ready = false, Blockers = ["x"], Items = [NetworkBlocker(), new("baseline", "Baseline", PerformanceReadinessState.Optional, "none", false) { Category = "Optional" }] });
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Blocked — container network"));
        cut.Find("[data-testid=pt-readiness-reason]").TextContent.Should().StartWith("Container reachability has not been checked.");
        var next = cut.Find("[data-testid=pt-next-action]");
        next.TextContent.Should().Be("Check container network");
        next.ClassList.Should().Contain("btn-primary");
        cut.Find("[data-testid=pt-primary-action]").GetAttribute("data-action").Should().Be(PerformanceReadinessActions.CheckNetwork, "the header offers the same operation");
        cut.Find("[data-testid=pt-next-action-disclosure]").TextContent.Should().Contain("It is not a load test");
        Tab(cut, "readiness");
        cut.Find("[data-testid=pt-blocker-count]").TextContent.Should().Be("Resolve 1 required item before running.");
        cut.Find("[data-testid=pt-blocker-count]").TagName.Should().Be("BUTTON", "the blocker count is focusable and moves to the blocker");
        cut.Find("[data-testid=pt-blocker-count]").Click();
        JSInterop.VerifyInvoke("birknextFocusElement").Arguments.Should().Equal(["pt-blocker-network"]);
        cut.Find("#pt-blocker-network").GetAttribute("tabindex").Should().Be("-1");
        cut.Find("[data-testid=pt-readiness-item][data-key=baseline]").GetAttribute("data-blocking").Should().Be("false", "optional items never block");
    }

    [Fact]
    public void CorporateCaMissing_IsAnExecutionSetupBlocker_WithOwnerKey_AndNoInsecureOption()
    {
        Register(readiness: new PerformanceTestReadiness
        {
            Ready = false, Blockers = ["x"],
            Items = [CaMissing(), new("network", "Container network", PerformanceReadinessState.Optional, "TLS trust", false) { Category = "Reachability", Action = PerformanceReadinessActions.CheckNetwork, Status = "TlsTrustFailure" }],
        });
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Blocked — container TLS trust"));
        cut.Find("[data-testid=pt-readiness-reason]").TextContent.Should().Contain("1 required prerequisite must be resolved").And.Contain("Local execution prerequisite")
            .And.NotContain("certificate chain", "the full TLS explanation is stated once, in the blocker panel");
        Tab(cut, "readiness");
        cut.Find("[data-testid=pt-blocker-category]").TextContent.Should().Be("Local execution prerequisite");
        cut.Find("[data-testid=pt-blocker-safety]").TextContent.Should().Contain("TLS verification remains enabled and must not be bypassed");
        cut.FindAll("[data-testid=pt-tls-policy]").Should().BeEmpty("the safety note is not repeated below the panel");
        cut.Markup.Should().NotContainAny("--insecure", "insecureSkipTLSVerify", "Skip certificate");
        cut.Find("[data-testid=pt-blocker-headline]").TextContent.Should().Be("Container TLS trust is not configured");
        cut.Find("[data-testid=pt-blocker-owner]").TextContent.Should().Be("Owner: Local execution configuration · Configuration: PerformanceTests:Container:CaBundlePath");
        var href = cut.Find("[data-testid=pt-blocker-action]").GetAttribute("href")!;
        href.Should().StartWith("admin/system-settings?section=performance-test-engines&focus=container-tls-trust&returnTo=", "the blocker deep-links to the Container TLS trust card");
        Uri.UnescapeDataString(href.Split("returnTo=")[1]).Should().MatchRegex(@"^performance-test-review\?definition=[^&]+&tab=readiness$", "Back returns to the same definition's readiness");
        cut.Find("[data-testid=pt-readiness-group][data-group='Execution setup']").TextContent.Should().Contain("Corporate CA trust").And.Contain("Missing");
        cut.Find("[data-testid=pt-item-technical]").TextContent.Should().Contain("code 1310");
        cut.Find("[data-testid=pt-item-action-network]").TextContent.Should().Be("Re-check container network", "after fixing the CA, readiness refreshes independently");
        // The TLS safety rule is stated once, in the blocker panel (the general policy line is not repeated beneath it).
        cut.Find("[data-testid=pt-blocker-safety]").TextContent.Should().Contain("TLS verification remains enabled");
        cut.Markup.Should().NotContainAny("insecure", "Insecure", "skip TLS", "Skip TLS");
    }

    [Fact]
    public void SeveralBlockers_AreCounted_EachWithItsOwnSecondaryAction()
    {
        Register(readiness: new PerformanceTestReadiness { Ready = false, Blockers = ["a", "b"], Items = [CaMissing(), NetworkBlocker()] });
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Blocked — 2 required prerequisites"));
        cut.Find("[data-testid=pt-next-action]").TextContent.Should().Be("Review blockers");
        Tab(cut, "readiness");
        cut.Find("[data-testid=pt-blocker-callout]").GetAttribute("data-count").Should().Be("2");
        cut.FindAll("[data-testid=pt-blocker-action]").Should().BeEmpty();
        cut.Find("[data-testid=pt-item-action-network]").ClassList.Should().Contain("btn-secondary");
        cut.Find("[data-testid=pt-item-action-ca-trust]").ClassList.Should().Contain("btn-secondary");
        cut.FindAll("[data-testid=pt-readiness-group]").Select(g => g.GetAttribute("data-group")).Should().Equal("Definition", "Execution setup", "Reachability");
    }

    [Fact]
    public void Overview_ShowsSharedGuidedSetupProgress_WithImportAndExport()
    {
        Register(readiness: new PerformanceTestReadiness { Ready = false, Blockers = ["x"], Items = [CaMissing()] });
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-setup-progress]").TextContent.Should().StartWith("Lookup baseline · ").And.EndWith("setup steps completed"));
        cut.Find("[data-testid=pt-setup-meaning]").TextContent.Should().Contain("not the execution-readiness count");
        cut.Find("[data-testid=pt-setup-next]").TextContent.Should().Contain("Corporate CA trust");
        cut.Find("[data-testid=pt-setup-import]").Click();
        cut.Find("[data-testid=sp-panel]").GetAttribute("data-mode").Should().Be("Import");
        cut.Find("[data-testid=pt-setup-continue]").Click();
        cut.Find("[data-testid=pt-tab-readiness]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=js-setup]").GetAttribute("data-journey").Should().Be("performance-def-1");
        cut.FindAll("[data-testid=js-step]").Select(s => s.GetAttribute("data-kind")).Should().Contain(["CaTrust", "ReadyToRun"]);
    }

    [Fact]
    public void Production_IsBlocked_InRed_AndRunIsDisabled()
    {
        // The Target Environment owns the environment type: a Production environment, not only a definition that says so.
        Register(definitions: [Definition with { EnvironmentType = "Production" }], environmentType: FrontendEnvironmentType.Production);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-production-block]").TextContent.Should().Contain("cannot run against Production"));

        Status(cut, "pt-status").Should().Be("Blocked");
        cut.Find("[data-testid=pt-status] .sd-pill").ClassList.Should().Contain("sd-pill-attention");
        Status(cut, "pt-env-safety").Should().Be("Blocked");
        cut.Find("[data-testid=pt-run-open]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#pt-run-reason").TextContent.Should().Contain("Production");
    }

    [Fact]
    public void ProviderUnavailable_IsBlockedByTooling_NotAFailure_WithALinkToTheEngines()
    {
        Register(availability: ProviderAvailability.Unavailable, readiness: new PerformanceTestReadiness
        {
            Ready = false, Blockers = ["k6 is not installed."],
            Items = [new("provider", "Provider", PerformanceReadinessState.ProviderUnavailable, "k6 is not installed.", true)],
        });
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Blocked — provider"));

        cut.Find("[data-testid=pt-readiness-reason]").TextContent.Should().StartWith("A tool limitation, not a performance result");
        cut.Find("[data-testid=pt-open-engines]").GetAttribute("href").Should().Be("admin/system-settings?section=performance-test-engines");
        cut.Find("[data-testid=pt-status] .sd-pill").GetAttribute("data-semantic").Should().Be("Blocked", "Blocked has one meaning (danger); the reason says tool limitation");
        cut.FindAll("[data-semantic=Failed]").Should().BeEmpty();
    }

    [Fact]
    public void DestructiveOrInvalidScenario_IsBlocked()
    {
        Register(readiness: new PerformanceTestReadiness
        {
            Ready = false, Blockers = ["DELETE is not allowed."],
            Items = [new("scenario", "Scenario", PerformanceReadinessState.InvalidScenario, "Destructive methods are not allowed (DELETE).", true)],
        });
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Blocked — scenario"));
        cut.Find("[data-testid=pt-status] .sd-pill").ClassList.Should().Contain("sd-pill-attention");
        cut.Find("[data-testid=pt-readiness-reason]").TextContent.Should().Contain("Destructive methods are not allowed");
    }

    [Fact]
    public void Overview_KeepsOptionalAndHistoricalStatesNeutral()
    {
        Register();
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-overview-resource-card]"));

        cut.Find("[data-testid=pt-overview-resource-card] .sd-pill").GetAttribute("data-status").Should().Be("Disabled · optional");
        cut.Find("[data-testid=pt-overview-resource-card] .sd-pill").ClassList.Should().Contain("ptc-tone-neutral").And.NotContain("sd-pill-partial");
        cut.Find("[data-testid=pt-overview-latest-card] .sd-pill").GetAttribute("data-status").Should().Be("No run yet");
        cut.Find("[data-testid=pt-overview-latest-card] .sd-pill").ClassList.Should().Contain("ptc-tone-neutral");
        cut.Find("[data-testid=pt-baseline-card]").GetAttribute("data-state").Should().Be("none");
        cut.Find("[data-testid=pt-baseline-card] .sd-pill").ClassList.Should().Contain("ptc-tone-baseline").And.NotContain("sd-pill-partial", "no baseline is informational");
        cut.Find("[data-testid=pt-open-history]").Click();
        cut.Find("[data-testid=pt-history-empty]").TextContent.Should().Contain("No performance tests have been executed for this definition");
    }

    [Fact]
    public void Overview_ShowsTheBaselineAndACompletedPassingRun()
    {
        Register();
        var run = Run(PerformanceRunState.Completed, PerformanceQualityVerdict.Pass);
        _api.Runs = [run];
        _api.Baselines = [new PerformanceBaseline { RunId = run.RunId, Version = 2, DefinitionId = "def-1", Status = PerformanceBaselineStatus.Active, ComparisonFingerprint = "cf", EffectiveFrom = DateTimeOffset.UtcNow }];
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-baseline-card]").GetAttribute("data-state").Should().Be("active"));

        cut.Find("[data-testid=pt-baseline-card] .sd-pill").GetAttribute("data-status").Should().Be("Baseline v2");
        cut.Find("[data-testid=pt-baseline-card]").TextContent.Should().Contain("Baseline P95 latency: 610 ms");
        cut.Find("[data-testid=pt-overview-latest-card] .sd-pill").GetAttribute("data-status").Should().Be("Completed · Pass");
        cut.Find("[data-testid=pt-overview-latest-card] .sd-pill").ClassList.Should().Contain("sd-pill-complete");
        cut.Find("[data-testid=pt-overview-latest-card]").TextContent.Should().Contain("P95 latency: 610 ms");
    }

    [Fact]
    public void WorkloadTimeline_ShowsOnlyConfiguredPhases_WithATextEquivalent()
    {
        Register(definitions: [Definition with { Workload = new PerformanceWorkload { VirtualUsers = 5, WarmupSeconds = 15, RampUpSeconds = 0, SteadyStateSeconds = 180, RampDownSeconds = 0 } }]);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-timeline-visual]"));

        cut.FindAll("[data-testid=pt-timeline-visual] .ptc-phase-label").Select(e => e.TextContent).Should().Equal("Warm-up", "Steady state");
        cut.Find("[data-testid=pt-timeline-visual] .visually-hidden").TextContent.Should().Be("Workload timeline: Warm-up 15 s, Steady state 3 min 0 s.");
        cut.Find("[data-testid=pt-timeline-visual] ol").GetAttribute("aria-hidden").Should().Be("true");
    }

    [Fact]
    public void WorkloadPolicy_FlagsAWorkloadBeyondTheLimits()
    {
        Register(definitions: [Definition with { Workload = new PerformanceWorkload { VirtualUsers = 80, SteadyStateSeconds = 60 } }]);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-page]"));
        Tab(cut, "workload");
        Status(cut, "pt-workload-policy").Should().Be("Exceeds policy");
        cut.Find("[data-testid=pt-workload-over]").TextContent.Should().Contain("80 VUs > 50");
    }

    [Fact]
    public void ReadinessTab_GroupsRequiredSafetyAndOptional_WithACallToAction()
    {
        Register(readiness: ReadyState() with
        {
            Items = [new("environment", "Environment and production guard", PerformanceReadinessState.Ready, "QA.", false), new("limits", "Safety limits", PerformanceReadinessState.Ready, "Within limits.", false),
                .. ReadyState().Items, new("baseline", "Baseline", PerformanceReadinessState.Optional, "Optional — no baseline selected.", false)],
        });
        var cut = Page();
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Ready"));
        Tab(cut, "readiness");

        cut.FindAll("[data-testid=pt-readiness-group]").Select(g => g.GetAttribute("data-group")).Should().Equal("Required", "Safety", "Optional");
        cut.Find("[data-testid=pt-readiness-group][data-group=Required]").TextContent.Should().Contain("Definition version").And.Contain("Provider");
        cut.Find("[data-testid=pt-readiness-group][data-group=Safety]").TextContent.Should().Contain("Safety limits");
        cut.Find("[data-testid=pt-readiness-group][data-group=Optional]").TextContent.Should().Contain("Baseline");
        cut.Find("[data-testid=pt-readiness-cta]").TextContent.Should().Contain("Run performance test");
    }

    [Fact]
    public void Tabs_CarryAccessibleStatusBadges()
    {
        Register();
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-tab-thresholds] .ptc-tab-badge"));

        cut.Find("[data-testid=pt-tab-thresholds]").TextContent.Should().Contain(", 1 configured");
        cut.Find("[data-testid=pt-tab-resources]").TextContent.Should().Contain(", Optional");
        cut.Find("[data-testid=pt-tab-overview]").GetAttribute("aria-pressed").Should().Be("true");
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-tab-scenario]").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void BackendUnavailable_ShowsOneErrorCard_WithoutFabricatedValues()
    {
        Register();
        _api.Overview = null;
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-unavailable]").TextContent.Should().Contain("could not be loaded"));
        cut.FindAll("[data-testid=pt-card-target], [data-testid=pt-status]").Should().BeEmpty();
    }

    [Fact]
    public void Presentation_RunStatesStayDistinct()
    {
        D.LatestRun(Run(PerformanceRunState.Completed, PerformanceQualityVerdict.Fail)).Tone.Should().Be(PtTone.Blocked, "an actual required-threshold failure");
        D.LatestRun(Run(PerformanceRunState.Completed, PerformanceQualityVerdict.NotAssessed)).Label.Should().Be("Completed · Not assessed");
        D.LatestRun(Run(PerformanceRunState.Cancelled)).Tone.Should().Be(PtTone.Neutral);
        D.LatestRun(Run(PerformanceRunState.ExecutionFailed)).Tone.Should().Be(PtTone.Attention, "a tool/run error is not a performance failure");
        D.LatestRun(Run(PerformanceRunState.Running)).Tone.Should().Be(PtTone.Running);
        D.PageStatus(false, false, ReadyState(), Run(PerformanceRunState.Running), "QA").Label.Should().Be("Running");
        D.Thresholds([]).Label.Should().Be("None — measured only");
        D.Resources(null).Tone.Should().Be(PtTone.Neutral);
        D.Phases(new PerformanceWorkload { WarmupSeconds = 0, RampUpSeconds = 30, SteadyStateSeconds = 60, RampDownSeconds = 10 }).Select(p => p.Key).Should().Equal("rampup", "steady", "rampdown");
    }

    // ── Workflow simplification: one Save, one technical-details block, grouped sections, honest empty states ─────────

    [Fact]
    public void TechnicalDetails_RenderOnce_CollapsedByDefault_OnEveryTab()
    {
        Register();
        var cut = Page();
        foreach (var tab in new[] { "overview", "scenario", "workload", "thresholds", "resources", "readiness", "run", "results", "compare", "history" })
        {
            Tab(cut, tab);
            cut.FindAll("[data-testid=pt-technical]").Should().ContainSingle($"the {tab} tab shares the one technical-details block");
            cut.Find("[data-testid=pt-technical-toggle]").GetAttribute("aria-expanded").Should().Be("false");
            cut.Find("[data-testid=pt-technical-toggle]").TextContent.Should().Contain("Show technical details");
        }
        cut.Find("[data-testid=pt-technical-toggle]").Click();
        cut.Find("[data-testid=pt-technical-toggle]").TextContent.Should().Contain("Hide technical details");
    }

    [Fact]
    public void Save_IsOnePageLevelAction_DirtyAcrossDefineTabs_AndNoTabSaveBars()
    {
        Register();
        var cut = Page();
        cut.FindAll("[data-testid=pt-save]").Should().ContainSingle();
        cut.FindAll("[data-testid=pt-header-save]").Should().BeEmpty();
        cut.FindAll("[data-testid=pt-readiness-save]").Should().BeEmpty();
        cut.Find("[data-testid=pt-save]").HasAttribute("disabled").Should().BeTrue("nothing to save on a clean saved definition");
        cut.Find("[data-testid=pt-save-hint]").TextContent.Should().Be("Saved as v2.");

        Tab(cut, "workload");
        cut.Find("[data-testid=pt-rampdown]").Change("15");
        var dirtyState = Status(cut, "pt-definition-state");
        dirtyState.Should().NotBe("Saved");
        cut.Find("[data-testid=pt-save]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=pt-save-hint]").TextContent.Should().Contain("Unsaved changes");

        foreach (var tab in new[] { "thresholds", "resources", "readiness", "run", "history" })
        {
            Tab(cut, tab);
            cut.FindAll("[data-testid=pt-save]").Should().ContainSingle($"the {tab} tab keeps the one Save");
            Status(cut, "pt-definition-state").Should().Be(dirtyState, "dirty state survives switching tabs");
        }

        cut.Find("[data-testid=pt-save]").Click();
        cut.WaitForAssertion(() => _api.Calls.Should().Contain("update"));
    }

    [Fact]
    public void Save_Failure_IsVisible_KeepsTheDraft_AndOffersRetry()
    {
        Register();
        _api.SaveResult = PerformanceApiResult<PerformanceTestDefinition>.Fail("Steady state exceeds the policy limit.");
        var cut = Page();
        Tab(cut, "thresholds");
        cut.Find("[data-testid=pt-add-threshold]").Click();
        cut.Find("[data-testid=pt-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-save-error]").TextContent.Should().Contain("Steady state exceeds the policy limit."));
        cut.Find("[data-testid=pt-save-error]").GetAttribute("role").Should().Be("alert");
        Status(cut, "pt-save-state").Should().Be("Save failed");
        cut.Find("[data-testid=pt-save]").TextContent.Should().Be("Retry save");
        cut.FindAll("[data-testid=pt-threshold-row]").Should().HaveCount(2, "a failed save never reverts the draft");
    }

    [Fact]
    public void Tabs_AreGrouped_DefineExecuteAnalyze_WithOverviewAlone()
    {
        Register();
        var cut = Page();
        cut.Find("[data-testid=pt-tabs]").TagName.Should().Be("NAV");
        var groups = cut.FindAll("[data-testid=pt-tab-group]");
        groups.Select(g => g.GetAttribute("data-group")).Should().Equal("Define", "Execute", "Analyze");
        groups[0].QuerySelectorAll(".ptc-tab").Select(b => b.GetAttribute("data-testid")).Should().Equal("pt-tab-scenario", "pt-tab-workload", "pt-tab-thresholds", "pt-tab-resources");
        groups[1].QuerySelectorAll(".ptc-tab").Select(b => b.GetAttribute("data-testid")).Should().Equal("pt-tab-readiness", "pt-tab-run");
        groups[2].QuerySelectorAll(".ptc-tab").Select(b => b.GetAttribute("data-testid")).Should().Equal("pt-tab-results", "pt-tab-compare", "pt-tab-history");
        foreach (var g in groups) cut.Find("#" + g.GetAttribute("aria-labelledby")).TextContent.Should().Be(g.GetAttribute("data-group"));
        cut.FindAll(".ptc-tab").Should().HaveCount(10);
    }

    [Fact]
    public void Workload_Timeline_ListsEveryPhase_IncludingRampDown_OnWorkloadAndOverview()
    {
        Register(definitions: [Definition with { Workload = new PerformanceWorkload { WarmupSeconds = 10, RampUpSeconds = 30, SteadyStateSeconds = 120, RampDownSeconds = 20 } }]);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-timeline-visual]"));
        Labels(cut).Should().Equal("Warm-up", "Ramp-up", "Steady state", "Ramp-down");
        Tab(cut, "workload");
        Labels(cut).Should().Equal("Warm-up", "Ramp-up", "Steady state", "Ramp-down");
        cut.Find("[data-testid=pt-timeline-visual] .ptc-phase-rampdown .ptc-phase-time").TextContent.Should().Be("2 min 40 s–3 min 0 s · 20 s");
        cut.Find("[data-testid=pt-safety-meaning]").TextContent.Should().Contain("not");

        static List<string> Labels(IRenderedComponent<PerformanceTestReview> c) => c.FindAll("[data-testid=pt-timeline-visual] .ptc-phase-label").Select(l => l.TextContent).ToList();
    }

    [Fact]
    public void EmptyStates_ExplainWhatEachTabWillShow()
    {
        Register(definitions: []);
        var cut = Page();
        AddManualStep(cut);
        cut.WaitForAssertion(() => Status(cut, "pt-status").Should().Be("Draft"));

        Tab(cut, "resources");
        cut.Find("[data-testid=prc-disabled]").TextContent.Should().Contain("optional and off");
        Tab(cut, "readiness");
        cut.Find("[data-testid=pt-readiness-unsaved]");
        cut.FindAll("[data-testid=pt-precheck]").Should().NotBeEmpty();
        Tab(cut, "run");
        cut.Find("[data-testid=pt-run-blocked]").TextContent.Should().Contain("Run test — ").And.NotContain("Cannot run yet");
        cut.FindAll("[data-testid=pt-run-start]").Should().BeEmpty();
        cut.Find("[data-testid=pt-run-go-readiness]").Click();
        cut.Find("[data-testid=pt-tab-readiness]").GetAttribute("aria-pressed").Should().Be("true");
        Tab(cut, "results");
        cut.Find("[data-testid=pt-no-results]").TextContent.Should().Contain("No completed run selected").And.Contain("choose one from History");
        cut.FindAll("[data-testid=pt-latency]").Should().BeEmpty("no empty charts or counters without a run");
        Tab(cut, "compare");
        cut.Find("[data-testid=pt-compare-empty]").TextContent.Should().Contain("No comparable runs yet");
        Tab(cut, "history");
        cut.Find("[data-testid=pt-history-empty]");
    }

    [Fact]
    public void EngineAvailable_IsNotTestReadiness()
    {
        Register(definitions: []);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-engine-meaning]").TextContent.Should().Contain("Target execution readiness is evaluated separately"));
        cut.Find("#pt-card-ready-h").TextContent.Should().Contain("Test readiness");
        cut.Find("[data-testid=pt-status] .visually-hidden").TextContent.Should().Be("Test readiness status: ");
        Status(cut, "pt-status").Should().StartWith("Draft", "an available engine does not make an unsaved draft ready");
    }

    // ── Smart Scenario: steps reference central Test Data; inline authoring is gone ────────────────────────────────────────────────

    private static PerformanceTestDefinition Mapped() => Definition with
    {
        Scenario = new PerformanceScenario
        {
            Name = "Person lookup baseline", TestDataProfileId = "legacy-1",
            Steps =
            [
                new HttpPerformanceStep { Name = "getperson", RelativePath = "/api/person/{personId}", Source = new ScenarioStepSource { OperationKey = "openapi:persons#GET /api/person/{personId}",
                    Origin = ScenarioOperationOrigin.OpenApi, Evidence = ScenarioEvidence.DeclaredAndObserved, ContractName = "Persons API", ObservedCount = 44, ObservedLastSeen = new DateTimeOffset(2026, 10, 23, 8, 54, 0, TimeSpan.Zero) },
                    DataMappings = [new ScenarioDataMapping("personId", ["person-id-baseline"], BirkNext.TestData.TestDataDataType.SimpleValues, "path")] },
                new HttpPerformanceStep { Name = "health", RelativePath = "/health" },
            ],
        },
    };

    /// <summary>Opens every step's focused editor (one is open by default) — for tests that work across several steps.</summary>
    private static void OpenAll<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent
    {
        for (var i = 0; i < cut.FindAll("[data-testid=pt-step-toggle]").Count; i++)
            if (cut.FindAll("[data-testid=pt-step-toggle]")[i].GetAttribute("aria-expanded") == "false") cut.FindAll("[data-testid=pt-step-toggle]")[i].Click();
    }

    /// <summary>Switches every open step editor to one part.</summary>
    private static void PartAll<T>(IRenderedComponent<T> cut, string part) where T : Microsoft.AspNetCore.Components.IComponent
    {
        for (var i = 0; i < cut.FindAll("[data-testid=pt-step]").Count; i++)
            cut.FindAll("[data-testid=pt-step]")[i].QuerySelector($"[data-testid=pt-step-part][data-part={part}]")?.Click();
    }

    [Fact]
    public void ScenarioTab_ShowsTargetServiceAndStepProvenance_WithoutInlineTestDataAuthoring()
    {
        Register(definitions: [Mapped()]);
        _api.Overview = _api.Overview! with { DataProfiles = [new PerformanceTestDataProfile { Id = "legacy-1", Name = "Old local rows" }] };
        var cut = Page();
        Tab(cut, "scenario");
        cut.Find("#pt-scenario-context-h").TextContent.Should().StartWith("Target service");
        cut.Find("[data-testid=psb]");
        var sources = cut.FindAll("[data-testid=pt-step-source]").Select(e => System.Text.RegularExpressions.Regex.Replace(e.TextContent, @"\s+", " ").Trim()).ToList();
        sources[0].Should().Be("Added from detected operation · Declared + Observed · OpenAPI · Persons API · observed 44× (last 2026-10-23 08:54 UTC)");
        sources[1].Should().Be("Manual step");
        cut.FindAll("[data-testid=pt-step-origin]").Should().ContainSingle("only the evidence-backed step carries the detected-operation provenance");
        cut.FindAll("[data-testid=pt-step-endpoint]")[0].TextContent.Should().StartWith("GET /", "the full method and path are shown above the editable fields");
        cut.FindAll("[data-testid=pt-data-save]").Should().BeEmpty("Performance Review no longer authors reusable Test Data");
        cut.Markup.Should().NotContain("Rows (one per line");
        cut.Find("[data-testid=pt-legacy-profile]").TextContent.Should().Contain("Old local rows").And.Contain("not shared");
        OpenAll(cut);
        PartAll(cut, "parameters");
        cut.Find("[data-testid=pt-step-mapping][data-variable=personId]").GetAttribute("data-state").Should().Be("mapped");
        cut.Find("[data-testid=pt-testdata-asset]").TextContent.Should().StartWith("person-id-baseline · Simple values / parameters").And.Contain("used by getperson");
        PartAll(cut, "request");
        cut.FindAll("[data-testid=pt-step-edit]")[0].Click();
        cut.Find("[data-testid=pt-step-delay]").ParentElement!.TextContent.Should().Contain("Delay before next step").And.Contain("not a request timeout");
    }

    [Fact]
    public void ScenarioSteps_ValidateExpectedResponses_ReorderAndMarkEditedDeclaredSteps()
    {
        Register(definitions: [Mapped()]);
        var cut = Page();
        Tab(cut, "scenario");
        OpenAll(cut);
        PartAll(cut, "request");
        cut.FindAll("[data-testid=pt-step-edit]")[0].Click();
        cut.FindAll("[data-testid=pt-step-edit]")[1].Click();
        cut.FindAll("[data-testid=pt-step-expected]")[0].Change("200, abc");
        cut.Find("[data-testid=pt-step-expected-error]").TextContent.Should().Contain("previous value is kept");
        cut.FindAll("[data-testid=pt-step-expected]")[0].Change("200, 204");
        cut.FindAll("[data-testid=pt-step-expected-error]").Should().BeEmpty();
        cut.FindAll("[data-testid=pt-step-expected]")[0].GetAttribute("value").Should().Be("200, 204");
        cut.FindAll("[data-testid=pt-step-path]")[0].Change("/api/person/{personId}/details");
        cut.FindAll("[data-testid=pt-step-source]")[0].TextContent.Trim().Should().StartWith("Added from detected operation · Modified from declared operation");
        cut.FindAll("[data-testid=pt-step-down]")[0].Click();
        cut.FindAll("[data-testid=pt-step-name]").Select(e => e.GetAttribute("value")).Should().Equal("health", "getperson");
        cut.FindAll("[data-testid=pt-step-enabled]")[1].Click();
        cut.FindAll("[data-testid=pt-step]")[1].GetAttribute("data-disabled").Should().Be("true");
        Status(cut, "pt-definition-state").Should().Be("Unsaved changes to v2");
    }

    [Fact]
    public void Scenario_authentication_summarises_requirements_never_lets_None_pass_and_applies_the_default_only_after_a_preview()
    {
        const string Observed = "Authenticated requests observed by Endpoint Discovery";
        Register(definitions: [Definition with { Scenario = new PerformanceScenario { Steps =
        [
            new HttpPerformanceStep { Name = "children", RelativePath = "/api/autorisasjon/v1/brukere/1/barn", Authentication = StepAuthenticationMode.None, Source = new() { ObservedAuthentication = Observed } },
            new HttpPerformanceStep { Name = "profile", RelativePath = "/api/profile", Authentication = StepAuthenticationMode.Override,
                AuthenticationReference = PerformanceScenarioRuntime.TargetEnvironmentAuthentication, Source = new() { ObservedAuthentication = Observed } },
            new HttpPerformanceStep { Name = "health", RelativePath = "/api/health" },
        ] } }]);
        var cut = Page();
        Tab(cut, "scenario");

        cut.Find("[data-testid=pt-auth-summary]").TextContent.Should().Be("Authentication unresolved — 1 step blocked", "a step set to None never satisfies an observed requirement");
        cut.Find("[data-testid=pt-auth-counts]").TextContent.Should().Contain("Scenario default: None").And.Contain("2 steps require authentication · 0 resolved · 1 unresolved · 1 not executable · 1 step override",
            "resolved means configured and executable: attended sign-in is counted apart");
        cut.FindAll("[data-testid=pt-auth-step]").Select(s => s.TextContent).Should().Contain(t => t.Contains("Step 1") && t.Contains("set to None"))
            .And.Contain(t => t.Contains("Step 2") && t.Contains("attended sign-in — not executable"));

        cut.FindAll("[data-testid=pt-unresolved-group]").Select(g => g.GetAttribute("data-cause")).Should().Contain("authentication");
        cut.Find("[data-testid=pt-unresolved-group][data-cause=authentication]").TextContent.Should().Contain("1 step affected");
        cut.Find("[data-testid=pt-auth-use-te]").Click();
        cut.Find("[data-testid=pt-auth-counts]").TextContent.Should().Contain("Scenario default: Target Environment API credential");
        cut.Find("[data-testid=pt-auth-summary]").TextContent.Should().StartWith("Authentication unresolved", "the explicit None on step 1 still wins over the default");

        cut.Find("[data-testid=pt-auth-apply]").Click();
        cut.FindAll("[data-testid=pt-auth-preview-step]").Select(s => s.TextContent).Should().Equal("Step 1 · children");
        cut.Find("[data-testid=pt-auth-preview-kept]").TextContent.Should().Contain("1 step override");
        cut.Find("[data-testid=pt-auth-apply-confirm]").Click();

        cut.FindAll("[data-testid=pt-auth-preview]").Should().BeEmpty();
        cut.Find("[data-testid=pt-auth-counts]").TextContent.Should().Contain("0 unresolved");
        cut.FindAll("[data-testid=pt-auth-step]").Should().ContainSingle(s => s.TextContent.Contains("Step 2") && s.TextContent.Contains("not executable"),
            "the override is kept as it is — and still reported honestly");
        Status(cut, "pt-definition-state").Should().StartWith("Unsaved changes", "nothing is saved behind the person's back");
    }

    [Fact]
    public void ScenarioStep_UnmappedPlaceholder_IsARuntimeValueNotConfigured_AndTestDataLinksWithContext()
    {
        Register(definitions: [Definition with { Scenario = new PerformanceScenario { Steps = [new HttpPerformanceStep { Name = "lookup", RelativePath = "/api/person/{personId}" }] } }]);
        var cut = Page();
        Tab(cut, "scenario");
        var mapping = cut.Find("[data-testid=pt-step-mapping][data-variable=personId]");
        mapping.GetAttribute("data-state").Should().Be("missing");
        cut.Find("[data-testid=pt-step-mapping-state]").TextContent.Should().Contain("Runtime value missing — choose a value source").And.NotContain("Test Data missing", "a dynamic parameter is not a Test Data requirement");
        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Needs configuration");
        cut.Find("[data-testid=pt-step-map]").Click();
        cut.FindAll("[data-testid=pt-value-source-option]").Select(o => o.GetAttribute("data-source")).Should().Equal(["Fixed", "PreviousStep", "ScenarioVariable", "TestData"],
            "a scenario value first; Test Data is optional and offered after it");
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "TestData").Change(true);
        cut.Find("[data-testid=pt-value-testdata-why]").TextContent.Should().Contain("Test Data is optional");
        cut.Find("[data-testid=tdm-open-test-data]").GetAttribute("href").Should()
            .Be("/test-data?consumer=performance-test-review&consumerId=def-1&environment=PaymentHub%20QA&operation=GET%20%2Fapi%2Fperson%2F%7BpersonId%7D&variable=personId&in=path&dataType=SimpleValues");
    }

    [Fact]
    public void ScenarioStep_MapTestData_OffersOnlyCompatibleApprovedAssets_StoresTheAlias_AndReturnsFocus()
    {
        var testData = new Mock<ITestDataApiService>();
        testData.Setup(t => t.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new BirkNext.TestData.TestDataCatalogView(
        [
            new BirkNext.TestData.TestDataAssetSummary { Id = "a", Alias = "person-id-qa", Kind = BirkNext.TestData.TestDataKind.Scalar, Approval = BirkNext.TestData.TestDataApproval.Approved, Capabilities = ["personId"], Source = "Configured" },
            new BirkNext.TestData.TestDataAssetSummary { Id = "b", Alias = "draft-person-id", Kind = BirkNext.TestData.TestDataKind.Scalar, Approval = BirkNext.TestData.TestDataApproval.Unapproved, Source = "Configured" },
            new BirkNext.TestData.TestDataAssetSummary { Id = "c", Alias = "person-payload", Kind = BirkNext.TestData.TestDataKind.Structured, MediaType = "application/json", Approval = BirkNext.TestData.TestDataApproval.Approved, Source = "Configured" },
        ], [], []), (string?)null));
        Services.AddSingleton(testData.Object);
        Register(definitions: [Definition with { Scenario = new PerformanceScenario { Steps = [new HttpPerformanceStep { Name = "lookup", RelativePath = "/api/person/{personId}" }] } }]);
        var cut = Page();
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-data-selection-note]").TextContent.Should().Be("Selection strategy applies after Test Data is mapped.");
        var row = cut.Find("[data-testid=pt-step-mapping][data-variable=personId]");
        row.TextContent.Should().Contain("Path parameter");
        cut.Find("[data-testid=pt-step-mapping-state]").TextContent.Should().Contain("Runtime value missing — choose a value source");
        cut.Find("[data-testid=pt-value-suggestion]").TextContent.Should().Contain("Suggested value source: Test Data").And.Contain("person-id-qa");

        cut.Find("[data-testid=pt-step-choose-testdata]").Click();
        cut.Find("[data-testid=pt-step-map]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=tdm]").GetAttribute("role").Should().Be("dialog");
        cut.FindAll("[data-testid=tdm-option]").Select(o => o.GetAttribute("value")).Should().Equal(["person-id-qa"], "only approved assets of the compatible data type");
        cut.Find("[data-testid=tdm-use]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tdm-option]").Change("person-id-qa");
        cut.Find("[data-testid=tdm-use]").Click();

        cut.Find("[data-testid=pt-step-mapping][data-variable=personId]").GetAttribute("data-state").Should().Be("mapped");
        cut.Find("[data-testid=pt-step-mapping-state]").TextContent.Should().Be("Ready");
        cut.Find("[data-testid=pt-value-source]").TextContent.Should().Be("Test Data");
        cut.Find("[data-testid=pt-value-text]").TextContent.Should().Be("person-id-qa");
        cut.Find("[data-testid=pt-step-message]").TextContent.Should().Be("Step 1: personId → Test Data · person-id-qa.");
        cut.FindAll("[data-testid=tdm]").Should().BeEmpty();
        cut.FindAll("[data-testid=pt-data-selection-note]").Should().BeEmpty();
        JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Select(i => i.Arguments[0]?.ToString())
            .Should().Contain(id => id!.StartsWith("pt-req-") && id.EndsWith("-personId"), "focus returns to the requirement row");
    }

    [Fact]
    public void ScenarioStep_WithoutCompatibleTestData_SaysSo_AndOffersOpenTestData()
    {
        Register(definitions: [Definition with { Scenario = new PerformanceScenario { Steps = [new HttpPerformanceStep { Name = "lookup", RelativePath = "/api/person/{personId}" }] } }]);
        var cut = Page();
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-step-choose-testdata]").Click();
        cut.Find("[data-testid=tdm-none]").TextContent.Should().Be("No compatible approved Test Data found.");
        cut.Find("[data-testid=tdm-open-test-data]").GetAttribute("href").Should().StartWith("/test-data?consumer=performance-test-review");
        cut.Find("[data-testid=tdm-cancel]").Click();
        cut.FindAll("[data-testid=tdm]").Should().BeEmpty();
    }

    // ── NEXT51: authentication requirements are never claimed absent without the endpoint evidence ──────────────────────────────────

    static PerformanceTestDefinition AutorisasjonGets => Definition with { Scenario = new PerformanceScenario { Steps =
    [
        new HttpPerformanceStep { Name = "brukere", Method = "GET", RelativePath = "/api/autorisasjon/v1/brukere/{brukerId}" },
        new HttpPerformanceStep { Name = "barn", Method = "GET", RelativePath = "/api/autorisasjon/v1/brukere/{brukerId}/barn" },
    ] } };

    [Fact]
    public void While_the_endpoint_history_loads_the_summary_checks_instead_of_claiming_no_authentication()
    {
        var pending = new TaskCompletionSource<BirkNext.LocalHttpsProxy.EndpointObservationHistoryView?>();
        _api.History = () => pending.Task;
        Register(definitions: [AutorisasjonGets]);
        var cut = Render<PerformanceTestReview>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-page]"));
        cut.Markup.Should().NotContain("No step requires authentication", "a claim without evidence while the endpoint history loads");
        pending.SetResult(null);
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-tab-scenario]"));
        Tab(cut, "scenario");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-auth-summary]").TextContent.Should().Be("No step requires authentication"));
    }

    [Fact]
    public void When_the_endpoint_history_cannot_be_read_requirements_are_not_fully_known_and_retry_is_offered()
    {
        _api.History = () => Task.FromException<BirkNext.LocalHttpsProxy.EndpointObservationHistoryView?>(new HttpRequestException("down"));
        Register(definitions: [AutorisasjonGets]);
        var cut = Page();
        Tab(cut, "scenario");

        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-auth-summary]").TextContent.Should().Be("Authentication requirements not fully known"));
        cut.Find("[data-testid=pt-auth-summary]").GetAttribute("data-evidence").Should().Be("partial");
        cut.Find("[data-testid=pt-auth-evidence-unavailable]").TextContent.Should().Contain("Readiness and the run still check it on the server");
        _api.History = () => Task.FromResult<BirkNext.LocalHttpsProxy.EndpointObservationHistoryView?>(null);
        cut.Find("[data-testid=pt-auth-evidence-retry]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-auth-summary]").TextContent.Should().Be("No step requires authentication"));
    }

    // ── NEXT55 follow-up: one scenario status on every tab, explicit versions, unsaved changes ──────────────────────────────

    [Fact]
    public void The_scenario_status_model_blocks_on_missing_bearer_and_brukerId_and_never_reads_unknown_evidence_as_none_required()
    {
        IReadOnlySet<string> observed = new HashSet<string> { "GET /api/autorisasjon/v1/brukere/{id}", "GET /api/autorisasjon/v1/brukere/{id}/barn" };
        var status = PerformanceScenarioStatusModel.Of(AutorisasjonGets, null, observed);
        status.Steps.Should().Be(2);
        status.AuthRequired.Should().Be(2);
        status.AuthMissing.Should().Be(2, "an observed Bearer with nothing configured is missing on every step");
        status.ValuesResolved.Should().BeLessThan(status.ValuesTotal, "brukerId has no value source");
        status.Executable.Should().Be(0);
        status.AllExecutable.Should().BeFalse();
        status.Issues.Should().Contain(i => i.Contains("brukere")).And.Contain(i => i.Contains("Step 2"));

        var unknown = PerformanceScenarioStatusModel.Of(AutorisasjonGets, null, null);
        unknown.AuthEvidenceKnown.Should().BeFalse("unloaded endpoint evidence is not \"no authentication required\"");
    }

    [Fact]
    public void Every_tab_shows_the_same_scenario_status_and_unsaved_changes_are_named_with_the_version_they_would_create()
    {
        Register(definitions: [AutorisasjonGets]);
        var cut = Page();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-status-strip]"));
        string Chips() => string.Join(" | ", cut.FindAll("[data-testid=pt-status-chip]").Select(c => $"{c.GetAttribute("data-key")}={c.GetAttribute("data-ok")}:{c.TextContent.Trim()}"));
        var overview = Chips();
        overview.Should().Contain("executable=false");
        foreach (var tab in new[] { "scenario", "readiness", "run" })
        {
            Tab(cut, tab);
            cut.FindAll("[data-testid=pt-status-strip]").Should().ContainSingle($"the {tab} tab shows the same status");
            Chips().Should().Be(overview);
        }

        cut.FindAll("[data-testid=pt-unsaved-banner]").Should().BeEmpty("nothing changed yet");
        Tab(cut, "scenario");
        cut.Find("[data-testid=pt-step-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-testid=pt-step-part]").Single(p => p.GetAttribute("data-part") == "request").Click();
        cut.Find("[data-testid=pt-step-edit]").Click();
        cut.Find("[data-testid=pt-step-expected]").Change("200, 204");
        var banner = cut.Find("[data-testid=pt-unsaved-banner]").TextContent;
        banner.Should().Contain($"Unsaved changes to v{AutorisasjonGets.Version}").And.Contain($"v{AutorisasjonGets.Version + 1}");
        cut.FindAll("[data-testid=pt-flow-step]").First().GetAttribute("data-state").Should().Be("current");
        _api.Calls.Should().NotContain("update", "an edit is never saved or versioned without a click");
    }

    // ── NEXT58B: the four redesigned tabs ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Workload_shows_units_inline_errors_per_field_and_an_estimate_and_hides_nothing_invalid()
    {
        Register();
        var cut = Page();
        Tab(cut, "workload");
        cut.Find("[data-testid=pt-workload]").GetAttribute("data-valid").Should().Be("true");
        cut.FindAll("[data-testid=pt-workload-field] .ptc-unit").Select(u => u.TextContent).Should().Contain(["(users)", "(s)"]);
        cut.Find("[data-testid=pt-estimate-requests]").TextContent.Should().Contain("of 200,000 allowed");
        cut.Find("[data-testid=pt-workload-iterations]").TextContent.Should().StartWith("Not a separate setting");
        cut.Find("[data-testid=pt-workload-think]").TextContent.Should().Contain("None");

        cut.Find("[data-testid=pt-steady]").Change("5");
        cut.Find("[data-testid=pt-steady]").GetAttribute("aria-invalid").Should().Be("true");
        cut.Find("[data-testid=pt-field-error][data-field=steady]").TextContent.Should().Be("The steady state must last at least 10 seconds.");
        cut.Find("[data-testid=pt-steady]").GetAttribute("aria-describedby").Should().Be("pt-field-steady-error");
        cut.Find("[data-testid=pt-vus]").Change("80");
        cut.Find("[data-testid=pt-field-error][data-field=vus]").GetAttribute("data-safety").Should().Be("true");
        cut.Find("[data-testid=pt-workload]").GetAttribute("data-valid").Should().Be("false");
        cut.FindAll("[data-testid=pt-workload-issue]").Should().HaveCount(2);
        cut.Find("[data-testid=pt-tab-workload]").TextContent.Should().Contain("2 problem(s)");
        cut.Find("[data-testid=pt-unsaved-banner]");
        _api.Calls.Should().NotContain("start", "editing a workload never runs anything");
    }

    [Fact]
    public void Thresholds_distinguish_configured_valid_evaluated_passed_and_failed()
    {
        Register();
        var t = Definition.Thresholds[0];
        _api.Runs = [Run(PerformanceRunState.Completed, PerformanceQualityVerdict.Fail,
            [new PerformanceThresholdResult { ThresholdId = t.Id, Metric = t.Metric, Operator = t.Operator, Expected = 500, Measured = 610, Severity = ThresholdSeverity.Required, Outcome = CheckOutcome.Fail }])];
        var cut = Page();
        Tab(cut, "thresholds");
        string Stage(string key) => cut.Find($"[data-testid=pt-threshold-stage][data-key={key}] strong").TextContent;
        cut.WaitForAssertion(() => Stage("evaluated").Should().Be("1"));
        (Stage("configured"), Stage("valid"), Stage("passed"), Stage("failed")).Should().Be(("1", "1", "0", "1"));
        cut.Find("[data-testid=pt-threshold-row]").GetAttribute("data-run-state").Should().Be("Failed");
        cut.Find("[data-testid=pt-threshold-run]").TextContent.Should().Contain("Failed · measured 610");
        cut.Find("[data-testid=pt-threshold-table] thead").TextContent.Should().ContainAll("Metric", "Condition", "Target value", "Unit", "Policy", "Validation", "Readiness impact", "Latest evaluation");

        cut.Find("[data-testid=pt-threshold-value]").Change("-1");
        cut.Find("[data-testid=pt-threshold-row]").GetAttribute("data-valid").Should().Be("false");
        cut.Find("[data-testid=pt-threshold-validation]").TextContent.Should().Be("The value must be 0 or more.");
        cut.Find("[data-testid=pt-threshold-impact]").TextContent.Should().Be("Blocks readiness until fixed");
        Stage("valid").Should().Be("0");
        Stage("evaluated").Should().Be("0", "unsaved edits were never evaluated");
        cut.Find("[data-testid=pt-threshold-evaluated-in]").TextContent.Should().Contain("Unsaved changes");
        cut.Find("[data-testid=pt-tab-thresholds]").TextContent.Should().Contain("1 invalid");
    }

    [Fact]
    public void Resources_show_each_execution_resource_by_stage_and_never_promote_unenforced_limits()
    {
        var readiness = ReadyState() with
        {
            Items = [.. ReadyState().Items,
                new("runtime", "Execution runtime", PerformanceReadinessState.Ready, "Podman 5.4.2 is available.", false),
                new("image", "Provider image", PerformanceReadinessState.Ready, "docker.io/grafana/k6:1.0.0", false),
                new("container-limits", "Container resource limits", PerformanceReadinessState.Optional,
                    "Requested: memory 1024 MiB · 2 CPU · 512 processes · read-only root file system with /tmp 64 MiB. Not enforced: memory, process limits — no cgroup controller.", false)
                    { Status = "Partly enforced", Technical = "cgroup controllers: cpu" }],
        };
        Register(readiness: readiness);
        _api.Runs = [Run(PerformanceRunState.Completed) with { ExecutionEvidence = new PerformanceExecutionEvidence(), RuntimeId = "podman", RuntimeVersion = "5.4.2",
            ContainerImage = "docker.io/grafana/k6:1.0.0", Limitations = ["The container ran without memory/pids limits: the Podman host delegates no such cgroup controller."] }];
        var cut = Page();
        Tab(cut, "resources");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-exec-resources]"));
        string State(string key, string stage) => cut.FindAll($"[data-testid=pt-exec-resource][data-key={key}] [data-testid=pt-exec-stage]").Single(s => s.GetAttribute("data-stage") == stage).GetAttribute("data-state")!;
        State("limits", "Runtime available").Should().Be("Warning");
        State("limits", "Execution verified").Should().Be("Warning");
        State("image", "Runtime available").Should().Be("Yes");
        State("runtime", "Execution verified").Should().Be("Yes");
        cut.FindAll("[data-testid=pt-runtime-warning]").Select(w => w.TextContent).Should().Contain(w => w.StartsWith("Container resource limits"))
            .And.Contain("The provider cannot cancel a run in progress.", "this provider fixture declares no cancellation capability");
        cut.Find("[data-testid=prc]");
        cut.FindAll("[data-testid=pt-exec-stage]").Should().OnlyContain(td => !string.IsNullOrEmpty(td.GetAttribute("data-label")), "each stage cell is labelled for the stacked 390 px layout");
    }

    [Fact]
    public void Results_label_partial_and_cancelled_runs_and_keep_execution_failure_apart_from_application_failure()
    {
        Register();
        var cancelled = Run(PerformanceRunState.Completed) with { State = PerformanceRunState.Cancelled, MetricsPartial = true, StateReason = "Cancelled by the tester" };
        _api.Runs = [cancelled];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-result-interpretation]"));
        cut.Find("[data-testid=pt-result-completeness]").TextContent.Should().Be("Partial");
        cut.Find("[data-testid=pt-result-failure-kind]").TextContent.Should().Contain("Cancelled").And.Contain("not a completed run");
        cut.Find("[data-testid=pt-result-status]").TextContent.Should().NotContain("completed", "a cancelled run is never shown as a completed one");
        cut.Find("[data-testid=pt-result-dropped]").TextContent.Should().Be("Not reported by this run");
        cut.Find("[data-testid=pt-result-scenario-version]").TextContent.Should().StartWith("Definition v2");
    }

    [Fact]
    public void Results_of_an_execution_failure_show_no_metrics_and_never_an_application_failure()
    {
        Register();
        _api.Runs = [Run(PerformanceRunState.ExecutionFailed) with { StateReason = "k6 exited before the workload started" }];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-result-interpretation]"));
        cut.Find("[data-testid=pt-result-interpretation]").GetAttribute("data-failure").Should().Be("Execution failure");
        cut.Find("[data-testid=pt-result-completeness]").TextContent.Should().Be("No metrics");
        cut.FindAll("[data-testid=pt-latency]").Should().BeEmpty("no metrics are shown for a run that measured nothing");
        cut.FindAll("[data-testid=pt-results] svg").Should().BeEmpty("no result graphs without executed evidence");
    }

    [Fact]
    public void The_baseline_snapshot_identifies_both_runs()
    {
        Register();
        var baselineRun = Guid.NewGuid();
        var run = Run(PerformanceRunState.Completed) with
        {
            Drift = new PerformanceDriftAssessment { BaselineId = "b1", BaselineVersion = 1, BaselineRunId = baselineRun, Compatible = true, State = PerformanceDriftState.Stable,
                Deltas = [PerformanceTestRules.Delta(PerformanceMetric.LatencyP95Ms, 600, 610)] },
        };
        _api.Runs = [run];
        var cut = Page();
        Tab(cut, "results");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-result-baseline-runs]").TextContent.Should()
            .Contain($"Baseline run {baselineRun.ToString("N")[..8]}").And.Contain($"this run {run.RunId.ToString("N")[..8]}"));
    }

    [Fact]
    public void An_unsaved_edit_is_reflected_the_same_way_on_workload_thresholds_resources_and_readiness()
    {
        Register();
        var cut = Page();
        Tab(cut, "workload");
        cut.Find("[data-testid=pt-steady]").Change("130");
        foreach (var tab in new[] { "workload", "thresholds", "resources", "readiness", "run" })
        {
            Tab(cut, tab);
            cut.Find("[data-testid=pt-unsaved-banner]").TextContent.Should().Contain("Unsaved changes to v2", tab);
            cut.Find("[data-testid=pt-status-strip]");
        }
        Tab(cut, "thresholds");
        cut.Find("[data-testid=pt-threshold-evaluated-in]").TextContent.Should().Contain("Unsaved changes");
        Tab(cut, "resources");
        cut.Find("[data-testid=pt-resources-unsaved]");
        cut.FindAll("[data-testid=pt-exec-stage]").Where(s => s.GetAttribute("data-stage") == "Validated").Select(s => s.GetAttribute("data-state"))
            .Should().OnlyContain(s => s == "NotChecked" || s == "NotApplicable", "validation is never claimed for unsaved changes");
        _api.Calls.Should().NotContain("start");
    }

    // ── Coordinated Check readiness: connectivity included when eligible, explicit action only ─────────────────────────────────

    [Fact]
    public void Check_readiness_includes_target_connectivity_and_never_probes_on_load()
    {
        Register(readiness: new PerformanceTestReadiness { Ready = false, Blockers = ["Not checked yet."], Items = [NetworkBlocker()] });
        var cut = Page();
        Tab(cut, "readiness");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-check-readiness]"));
        _api.Calls.Should().NotContain("readiness-check").And.NotContain("network-check", "loading, rendering and tab switches never send a probe");
        _api.Readiness = ReadyState();
        cut.Find("[data-testid=pt-check-readiness]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-check-steps]").GetAttribute("data-connectivity").Should().Be("Checked"));
        _api.Calls.Count(c => c == "readiness-check").Should().Be(1);
        _api.Calls.Should().NotContain("start").And.NotContain("update").And.NotContain("network-check", "the coordinated check made the one probe; no second manual check");
        cut.FindAll("[data-testid=pt-check-step]").Select(s => s.GetAttribute("data-key")).Should().Equal("definition", "environment", "connectivity", "blockers");
        cut.Find("[data-testid=pt-network-result]").GetAttribute("data-state").Should().Be("Reachable");
        cut.Find("[data-testid=pt-readiness-summary]").TextContent.Should().Contain("Ready to run.");
    }

    [Fact]
    public void A_second_click_while_checking_is_ignored_and_the_check_can_be_cancelled()
    {
        Register(readiness: new PerformanceTestReadiness { Ready = false, Blockers = ["Not checked yet."], Items = [NetworkBlocker()] });
        var pending = new TaskCompletionSource<PerformanceApiResult<PerformanceReadinessCheck>>();
        _api.Check = () => pending.Task;
        var cut = Page();
        Tab(cut, "readiness");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-check-readiness]"));
        cut.Find("[data-testid=pt-check-readiness]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-check-progress]"));
        cut.Find("[data-testid=pt-check-readiness]").HasAttribute("disabled").Should().BeTrue("one check at a time");
        cut.Find("[data-testid=pt-check-progress]").TextContent.Should().Contain("target connectivity (when eligible)");
        cut.Find("[data-testid=pt-check-cancel]").Click();
        pending.SetResult(PerformanceApiResult<PerformanceReadinessCheck>.Fail("cancelled"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pt-check-progress]").Should().BeEmpty());
        _api.Calls.Count(c => c == "readiness-check").Should().Be(1);
        cut.FindAll("[data-testid=pt-network-result]").Should().BeEmpty("a cancelled check is never shown as reachable");
    }

    [Fact]
    public void A_skipped_connectivity_check_says_why_and_is_never_shown_as_reachable()
    {
        var blocked = new PerformanceTestReadiness { Ready = false, Blockers = ["Not checked yet."], Items = [NetworkBlocker()] };
        Register(readiness: blocked);
        _api.Check = () => Task.FromResult(PerformanceApiResult<PerformanceReadinessCheck>.Ok(new PerformanceReadinessCheck
        {
            Readiness = blocked, Connectivity = PerformanceConnectivityOutcomes.SkippedRuntimeUnavailable,
            ConnectivityDetail = "Not checked — the container runtime, k6 image or provider is not available.",
            Steps = [new("connectivity", "Checking target connectivity", "Skipped", "Not checked — the container runtime, k6 image or provider is not available.")],
        }));
        var cut = Page();
        Tab(cut, "readiness");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-check-readiness]"));
        cut.Find("[data-testid=pt-check-readiness]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pt-check-step][data-key=connectivity]").GetAttribute("data-state").Should().Be("Skipped"));
        cut.Find("[data-testid=pt-check-step][data-key=connectivity]").TextContent.Should().Contain("Skipped").And.Contain("not available");
        cut.FindAll("[data-testid=pt-network-result]").Should().BeEmpty();
        cut.Find("[data-testid=pt-readiness-summary]").TextContent.Should().StartWith("Blocked");
    }
}
