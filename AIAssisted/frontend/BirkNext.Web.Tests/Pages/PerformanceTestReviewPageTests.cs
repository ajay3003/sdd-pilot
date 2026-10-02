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
}
