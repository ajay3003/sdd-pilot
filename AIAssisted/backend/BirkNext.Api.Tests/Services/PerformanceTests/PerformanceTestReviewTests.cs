using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Path = System.IO.Path;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Performance Test Review: generic contracts, backend safety (production, host, methods, GraphQL mutations, limits, secrets), readiness,
/// the k6 provider (detection, deterministic injection-safe script, structured summary parsing) through a fake process runner, execution
/// semantics (execution ≠ quality, no threshold ≠ pass, cancel/timeout/crash ≠ fail), immutable history, explicit versioned baselines and drift.
/// Fixtures are generic (PaymentHub-style API on a QA host); no k6 installation is required except for the gated live test.
/// </summary>
public sealed class PerformanceTestReviewTests
{
    private static readonly PerformanceTestOptions Options = new();

    internal static PerformanceTestDefinition Definition(Func<PerformanceTestDefinition, PerformanceTestDefinition>? change = null)
    {
        var d = new PerformanceTestDefinition
        {
            Id = "def-1", EnvironmentId = "pay-qa", Name = "Payment lookup baseline", TargetType = PerformanceTargetType.RestHttp,
            TargetOrigin = "https://paymenthub-qa.example.test", EnvironmentType = "QA", EnvironmentName = "PaymentHub QA",
            Scenario = new PerformanceScenario { Name = "Lookup", Steps = [new HttpPerformanceStep { Name = "lookup", Method = "GET", RelativePath = "/api/payments/{paymentId}", ExpectedStatusCodes = [200], ThinkTimeMs = 500 }],
                TestDataProfileId = "data-1" },
            Workload = new PerformanceWorkload { Purpose = WorkloadPurpose.Baseline, Mode = WorkloadMode.VirtualUsers, VirtualUsers = 5, WarmupSeconds = 10, RampUpSeconds = 10, SteadyStateSeconds = 30, RampDownSeconds = 5 },
            Thresholds =
            [
                new PerformanceThreshold { Id = "p95", Metric = PerformanceMetric.LatencyP95Ms, Operator = ThresholdOperator.LessThan, Value = 500, Severity = ThresholdSeverity.Required },
                new PerformanceThreshold { Id = "err", Metric = PerformanceMetric.ErrorRatePercent, Operator = ThresholdOperator.LessThan, Value = 1, Severity = ThresholdSeverity.Required },
                new PerformanceThreshold { Id = "rps", Metric = PerformanceMetric.ThroughputRps, Operator = ThresholdOperator.GreaterThanOrEqual, Value = 20, Severity = ThresholdSeverity.Advisory },
            ],
        };
        return change is null ? d : change(d);
    }

    internal static PerformanceTestDataProfile Data() => new()
    {
        Id = "data-1", EnvironmentId = "pay-qa", Name = "Synthetic payments", Columns = ["paymentId"], Rows = [["PAY-0001"], ["PAY-0002"]], ApprovedSynthetic = true,
    };

    private static List<PerformanceTestSafety.Issue> Issues(PerformanceTestDefinition d, PerformanceTestDataProfile? data = null, PerformanceTestOptions? o = null) =>
        PerformanceTestSafety.Check(d, data ?? Data(), o ?? Options);

    // ── Safety ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SafeRestGet_OnQa_HasNoIssues() => Issues(Definition()).Should().BeEmpty();

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("")]
    [InlineData("Custom")]
    public void ProductionAndUnclassifiedEnvironments_AreBlocked(string type)
    {
        var issues = Issues(Definition(d => d with { EnvironmentType = type }));
        issues.Should().Contain(i => i.State == PerformanceReadinessState.UnsafeEnvironment && i.Key == "environment");
        if (type.Equals("Production", StringComparison.OrdinalIgnoreCase)) issues.Should().Contain(i => i.Message == "Production environments cannot be used for performance tests.");
    }

    [Theory]
    [InlineData("https://paymenthub-prod.example.test")]
    [InlineData("https://api.prd.example.test")]
    [InlineData("https://production.example.test")]
    public void QaClassification_WithAProductionHost_IsBlocked(string host) =>
        Issues(Definition(d => d with { TargetOrigin = host })).Should().Contain(i => i.Key == "target" && i.Message.Contains("production marker"));

    [Fact]
    public void ConfiguredBlockedAndAllowListedHosts_AreEnforced()
    {
        Issues(Definition(), o: Options with { BlockedHosts = ["paymenthub-qa.example.test"] }).Should().Contain(i => i.Message.Contains("blocked for performance tests"));
        Issues(Definition(), o: Options with { AllowedHosts = ["*.other.test"] }).Should().Contain(i => i.Message.Contains("allow-list"));
        Issues(Definition(), o: Options with { AllowedHosts = ["*.example.test"] }).Should().BeEmpty();
        Issues(Definition(d => d with { TargetOrigin = "http://localhost:5094", EnvironmentType = "Local" }), o: Options with { AllowedHosts = ["*.example.test"] }).Should().BeEmpty("loopback is always allowed for local targets");
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public void WriteMethods_AreBlocked(string method) =>
        Issues(Definition(d => d with { Scenario = d.Scenario with { Steps = [d.Scenario.Steps[0] with { Method = method }] } }))
            .Should().Contain(i => i.Key == "method" && i.Message.Contains("write method"));

    [Fact]
    public void RestPost_NeedsAnExplicitSafeReadClassification()
    {
        var post = Definition(d => d with { Scenario = d.Scenario with { Steps = [d.Scenario.Steps[0] with { Method = "POST", BodyTemplate = "{\"q\":\"{paymentId}\"}" }] } });
        Issues(post).Should().Contain(i => i.Key == "method" && i.Message.Contains("safe read"));
        Issues(post with { Scenario = post.Scenario with { Steps = [post.Scenario.Steps[0] with { PostIsSafeRead = true }] } }).Should().BeEmpty();
    }

    private static PerformanceTestDefinition GraphQl(string query) => Definition(d => d with
    {
        TargetType = PerformanceTargetType.GraphQlHttp,
        Scenario = d.Scenario with { TestDataProfileId = null, Steps = [new HttpPerformanceStep { Name = "payments", Method = "POST", RelativePath = "/graphql", GraphQlQuery = query, ExpectedStatusCodes = [200] }] },
    });

    [Fact]
    public void GraphQlQuery_IsAllowed_MutationAndSubscription_AreBlocked()
    {
        Issues(GraphQl("query Payments { payments(first: 5) { id amount } }"), data: null).Should().BeEmpty();
        Issues(GraphQl("{ payments { id } }")).Should().BeEmpty();
        Issues(GraphQl("mutation Pay { pay(id: 1) { id } }")).Should().Contain(i => i.Key == "graphql" && i.Message.Contains("mutations are blocked"));
        Issues(GraphQl("subscription S { payments { id } }")).Should().Contain(i => i.Key == "graphql" && i.Message.Contains("subscriptions"));
        Issues(GraphQl("query A { a } mutation B { b }")).Should().Contain(i => i.Message.Contains("mutation"), "any mutation in the document blocks it");
        Issues(GraphQl("query {")).Should().Contain(i => i.Message.Contains("could not be parsed"));
    }

    [Theory]
    [InlineData("https://evil.example.test/x")]
    [InlineData("//evil.example.test/x")]
    [InlineData("/api/../admin")]
    [InlineData("api/payments")]
    [InlineData("/api/payments?x=1")]
    public void Paths_MustStayUnderTheConfiguredTarget(string path) =>
        Issues(Definition(d => d with { Scenario = d.Scenario with { TestDataProfileId = null, Steps = [d.Scenario.Steps[0] with { RelativePath = path }] } }))
            .Should().Contain(i => i.Key == "path");

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    public void CredentialHeaders_CannotBeStored(string header) =>
        Issues(Definition(d => d with { Scenario = d.Scenario with { Steps = [d.Scenario.Steps[0] with { Headers = [new(header, "Bearer abc")] }] } }))
            .Should().Contain(i => i.Key == "header" && i.Message.Contains("credential"));

    [Fact]
    public void Limits_VirtualUsersRateDurationAndRequests_AreEnforced()
    {
        Issues(Definition(d => d with { Workload = d.Workload with { VirtualUsers = 51 } })).Should().Contain(i => i.Key == "limit-vus");
        Issues(Definition(d => d with { Workload = d.Workload with { Mode = WorkloadMode.ArrivalRate, RequestsPerSecond = 51 } })).Should().Contain(i => i.Key == "limit-rate");
        Issues(Definition(d => d with { Workload = d.Workload with { SteadyStateSeconds = 31 * 60 } })).Should().Contain(i => i.Key == "limit-duration");
        Issues(Definition(d => d with { Workload = d.Workload with { Purpose = WorkloadPurpose.Soak, SteadyStateSeconds = 50 * 60 } })).Should().NotContain(i => i.Key == "limit-duration", "soak has its own longer maximum");
        Issues(Definition(d => d with { Workload = d.Workload with { Purpose = WorkloadPurpose.Soak, SteadyStateSeconds = 61 * 60 } })).Should().Contain(i => i.Key == "limit-duration");
        Issues(Definition(d => d with { SafetyPolicy = new PerformanceTestSafetyPolicy { MaxVirtualUsers = 3 } })).Should().Contain(i => i.Key == "limit-vus", "a definition can only be stricter");
        Issues(Definition(d => d with { Workload = d.Workload with { VirtualUsers = 50, SteadyStateSeconds = 1700 }, Scenario = d.Scenario with { Steps = [d.Scenario.Steps[0] with { ThinkTimeMs = 0 }] } }))
            .Should().Contain(i => i.Key == "limit-requests");
        Issues(Definition(d => d with { Workload = d.Workload with { WarmupSeconds = -1 } })).Should().Contain(i => i.Message.Contains("negative"));
        Issues(Definition(d => d with { Workload = d.Workload with { Purpose = WorkloadPurpose.Stress } }), o: Options with { AllowStressTest = false }).Should().Contain(i => i.Key == "limit-purpose");
    }

    [Fact]
    public void TestData_MustBeApprovedSynthetic_AndNeverLookLikeIdentityNumbers()
    {
        Issues(Definition(), Data() with { ApprovedSynthetic = false }).Should().Contain(i => i.State == PerformanceReadinessState.NeedsTestData && i.Message.Contains("not confirmed"));
        Issues(Definition(), Data() with { Rows = [["01019912345"]] }).Should().Contain(i => i.Message.Contains("national identity number"));
        Issues(Definition(), Data() with { Rows = [["\";alert(1);//"]] }).Should().Contain(i => i.Message.Contains("may contain"));
        Issues(Definition(d => d with { Scenario = d.Scenario with { TestDataProfileId = null } })).Should().Contain(i => i.Message.Contains("no approved test data profile"));
        Issues(Definition(), Data() with { Columns = ["otherId"] }).Should().Contain(i => i.Key == "placeholder");
    }

    [Fact]
    public void Authentication_IsANamedPrerequisite_NotAStoredCredential() =>
        Issues(Definition(d => d with { AuthenticationReference = "target-environment" })).Should().ContainSingle(i => i.State == PerformanceReadinessState.NeedsAuthentication)
            .Which.Message.Should().Contain("never simulated");

    // ── Provider (fake process runner) ──────────────────────────────────────────────────────────────────────────────

    internal sealed class FakeRunner : IProcessRunner
    {
        public string Version { get; set; } = "k6 v0.52.0 (go1.22.5, windows/amd64)";
        public bool Installed { get; set; } = true;
        public Func<ProcessSpec, ProcessOutcome>? OnRun { get; set; }
        public string? SummaryJson { get; set; } = Summary();
        public List<ProcessSpec> Calls { get; } = [];
        public string? LastScript { get; private set; }
        public string? LastWorkingDirectory { get; private set; }

        public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
        {
            Calls.Add(spec);
            if (!Installed) return new ProcessOutcome(null, "", "", false, false, "The executable could not be started (Win32Exception).");
            if (spec.Arguments[0] == "version") return new ProcessOutcome(0, Version, "", false, false, null);
            LastWorkingDirectory = spec.WorkingDirectory;
            LastScript = await File.ReadAllTextAsync(spec.Arguments[^1], ct);
            if (OnRun is not null)
            {
                if (OnRun(spec) is { } custom) return custom;
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(20), ct); }
            catch (OperationCanceledException) { return new ProcessOutcome(null, "", "", false, true, null); }
            if (SummaryJson is not null) await File.WriteAllTextAsync(Path.Combine(spec.WorkingDirectory, "birknext-summary.json"), SummaryJson, ct);
            return new ProcessOutcome(0, "", "", false, false, null);
        }
    }

    internal static string Summary(double p95 = 420, double errorRate = 0.002, double rps = 25.5, long count = 1500) => JsonSerializer.Serialize(new
    {
        birknext = 1,
        state = new { testRunDurationMs = 55000.0 },
        metrics = new Dictionary<string, object>
        {
            ["http_reqs"] = new { type = "counter", values = new { count, rate = rps } },
            ["http_req_duration"] = new { type = "trend", values = new Dictionary<string, double> { ["avg"] = 210, ["min"] = 40, ["med"] = 180, ["max"] = 900, ["p(90)"] = 350, ["p(95)"] = p95, ["p(99)"] = 700 } },
            ["http_req_failed"] = new { type = "rate", values = new { rate = errorRate, passes = (long)Math.Round(count * errorRate), fails = count - (long)Math.Round(count * errorRate) } },
            ["http_req_duration{name:lookup}"] = new { type = "trend", values = new Dictionary<string, double> { ["p(95)"] = p95, ["p(99)"] = 700 } },
            ["http_reqs{name:lookup}"] = new { type = "counter", values = new { count, rate = rps } },
            ["http_req_failed{name:lookup}"] = new { type = "rate", values = new { rate = errorRate } },
        },
    });

    private static K6PerformanceTestProvider Provider(FakeRunner runner, PerformanceTestOptions? o = null) => new(o ?? Options, runner, NullLogger<K6PerformanceTestProvider>.Instance);

    [Fact]
    public async Task ProviderDetection_ReportsAvailability_NeverAFailure()
    {
        (await Provider(new FakeRunner()).StatusAsync()).Should().Match<PerformanceProviderStatus>(s => s.Availability == ProviderAvailability.Available && s.Version == "0.52.0");
        (await Provider(new FakeRunner { Installed = false }).StatusAsync()).Availability.Should().Be(ProviderAvailability.Unavailable);
        (await Provider(new FakeRunner { Installed = false }).StatusAsync()).Detail.Should().Contain("not installed or configured");
        (await Provider(new FakeRunner { Version = "k6 v0.40.0" }).StatusAsync()).Availability.Should().Be(ProviderAvailability.VersionUnsupported);
        (await Provider(new FakeRunner(), Options with { K6ExecutablePath = @"C:\missing\k6.exe" }).StatusAsync()).Availability.Should().Be(ProviderAvailability.Misconfigured);
        Provider(new FakeRunner()).ProviderId.Should().Be("performance.k6");
    }

    [Fact]
    public void Script_IsDeterministic_AndUserValuesCannotEscapeStringBoundaries()
    {
        var hostile = Definition(d => d with
        {
            Scenario = d.Scenario with
            {
                Steps = [d.Scenario.Steps[0] with
                {
                    Name = "lookup", RelativePath = "/api/x\";require('child_process');//",
                    Headers = [new("X-Trace", "a\"; } import os from 'k6/x'; //\u2028</script>")],
                    QueryParameters = [new("q", "'+process.exit()+'")],
                }],
            },
        });
        var a = K6ScriptGenerator.Generate(hostile, Data(), @"C:\tmp\summary.json");
        var b = K6ScriptGenerator.Generate(hostile, Data(), @"C:\tmp\summary.json");
        a.Should().Be(b, "the same definition always yields the same script");
        a.Should().NotContain("\";require('child_process')", "a quote in a value is escaped inside its JSON literal");
        a.Should().Contain("\\u0022;require(\\u0027child_process\\u0027);//");
        a.Should().NotContain("</script>").And.NotContain("\u2028");
        a.Split('\n').Where(l => l.Contains("import os from")).Should().OnlyContain(l => l.StartsWith("const STEPS = ", StringComparison.Ordinal), "hostile text stays inside its JSON string literal");
        a.Split('\n').Count(l => l.StartsWith("import ", StringComparison.Ordinal)).Should().Be(3, "only BirkNext's own imports");
        a.Should().Contain("executor: 'ramping-vus'").And.Contain("{ duration: '30s', target: 5 }");
        K6ScriptGenerator.Scenario(new PerformanceWorkload { Mode = WorkloadMode.ArrivalRate, RequestsPerSecond = 2.5, StartRequestsPerSecond = 0.5, VirtualUsers = 10 })
            .Should().Contain("executor: 'ramping-arrival-rate'").And.Contain("timeUnit: '1m'").And.Contain("target: 150");
    }

    [Fact]
    public void SummaryParser_NormalizesMetrics_AndRejectsMalformedOutput()
    {
        var (m, error) = K6SummaryParser.Parse(Summary(), ["lookup"]);
        error.Should().BeNull();
        m!.RequestCount.Should().Be(1500);
        m.FailedRequests.Should().Be(3);
        m.SuccessfulRequests.Should().Be(1497);
        m.ErrorRatePercent.Should().Be(0.2);
        m.RequestsPerSecond.Should().Be(25.5);
        m.Latency.Should().BeEquivalentTo(new PerformanceLatency { MinMs = 40, MeanMs = 210, P50Ms = 180, P90Ms = 350, P95Ms = 420, P99Ms = 700, MaxMs = 900 });
        m.DurationSeconds.Should().Be(55);
        m.Steps.Should().ContainSingle(s => s.StepName == "lookup" && s.P95Ms == 420);
        K6SummaryParser.Parse("not json", []).Metrics.Should().BeNull();
        K6SummaryParser.Parse("{\"metrics\":{}}", []).Error.Should().Contain("no HTTP request metrics", "missing output is never zero metrics");
    }

    // ── Execution (in-memory store + fake runner) ───────────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public FakeRunner Runner { get; } = new();
        public ServiceProvider Services { get; }
        public PerformanceTestExecutionService Execution => Services.GetRequiredService<PerformanceTestExecutionService>();

        public Harness(PerformanceTestOptions? options = null)
        {
            var name = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton(options ?? Options);
            services.AddSingleton<IProcessRunner>(Runner);
            services.AddSingleton<IPerformanceTestProvider, K6PerformanceTestProvider>();
            services.AddSingleton<PerformanceTestProviderRegistry>();
            services.AddSingleton<PerformanceTestReadinessService>();
            services.AddSingleton<PerformanceTestExecutionService>();
            services.AddScoped<PerformanceTestStore>();
            services.AddScoped<IqrSourceStore>();
            Services = services.BuildServiceProvider();
        }

        public PerformanceTestStore Store() => Services.CreateScope().ServiceProvider.GetRequiredService<PerformanceTestStore>();

        public async Task<PerformanceTestDefinition> SeedAsync(PerformanceTestDefinition? d = null)
        {
            var store = Store();
            await store.SaveDataProfileAsync("pay-qa", Data(), DateTimeOffset.UtcNow);
            return await store.SaveDefinitionAsync("pay-qa", d ?? Definition(), DateTimeOffset.UtcNow);
        }

        public async Task<PerformanceTestRun> RunToEndAsync(string definitionId)
        {
            var started = await Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = definitionId });
            started.Run.Should().NotBeNull(string.Join(" ", started.Blockers) + started.Conflict);
            await Execution.LastExecution!;
            return (await Store().RunAsync(started.Run!.RunId))!;
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    [Fact]
    public async Task ProviderMissing_BlocksReadiness_AsAToolLimitation()
    {
        await using var h = new Harness();
        h.Runner.Installed = false;
        var d = await h.SeedAsync();
        var readiness = await h.Services.GetRequiredService<PerformanceTestReadinessService>().EvaluateAsync(d, Data(), false);
        readiness.Ready.Should().BeFalse();
        readiness.Items.Single(i => i.Key == "provider").State.Should().Be(PerformanceReadinessState.ProviderUnavailable);
        readiness.Blockers.Should().ContainSingle(b => b.Contains("k6 is not installed or configured on this BirkNext host"));
        readiness.Items.Should().NotContain(i => i.Detail.Contains("Fail", StringComparison.Ordinal));
        var started = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        started.Run.Should().BeNull("nothing runs and nothing is scored");
        (await h.Store().RunsAsync("pay-qa")).Should().BeEmpty();
    }

    [Fact]
    public async Task SafeRestGet_IsReady_WithOptionalThresholdsBaselineAndObservability()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition(x => x with { Thresholds = [] }));
        var readiness = await h.Services.GetRequiredService<PerformanceTestReadinessService>().EvaluateAsync(d, Data(), false);
        readiness.Ready.Should().BeTrue(string.Join(" | ", readiness.Blockers));
        readiness.Items.Single(i => i.Key == "thresholds").Should().Match<PerformanceReadinessItem>(i => i.State == PerformanceReadinessState.Optional && i.Detail.Contains("measured, not assessed"));
        readiness.Items.Single(i => i.Key == "baseline").State.Should().Be(PerformanceReadinessState.Optional);
        readiness.Items.Single(i => i.Key == "observability").State.Should().Be(PerformanceReadinessState.Optional);
        readiness.Timeline.Should().HaveCount(4);
    }

    [Fact]
    public async Task CompletedRun_AllThresholdsPass_AndTempArtifactsAreRemoved()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync();
        var run = await h.RunToEndAsync(d.Id);
        run.State.Should().Be(PerformanceRunState.Completed);
        run.Verdict.Should().Be(PerformanceQualityVerdict.Pass);
        run.Quality!.QualityPercent.Should().Be(100);
        run.Quality.Coverage.AssessmentCoveragePercent.Should().Be(100);
        run.ThresholdResults.Should().OnlyContain(t => t.Outcome == CheckOutcome.Pass);
        run.Metrics!.Latency.P95Ms.Should().Be(420);
        run.ProviderVersion.Should().Be("0.52.0");
        run.DefinitionVersion.Should().Be(1);
        run.Host!.LogicalProcessors.Should().BeGreaterThan(0);
        run.StartedAt.Should().NotBeNull(); run.FinishedAt.Should().NotBeNull();
        Directory.Exists(h.Runner.LastWorkingDirectory).Should().BeFalse("the generated script and summary are deleted");
        h.Runner.LastScript.Should().Contain("PAY-0001", "approved synthetic test data is embedded").And.NotContain("Bearer");
        h.Runner.Calls.Single(c => c.Arguments[0] == "run").Arguments.Should().BeEquivalentTo(["run", "--no-color", "--quiet", "--no-usage-report", Path.Combine(h.Runner.LastWorkingDirectory!, "birknext-test.js")],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task RequiredThresholdFailure_IsACompletedRunWithFailedQuality_NotAnExecutionFailure()
    {
        await using var h = new Harness();
        h.Runner.SummaryJson = Summary(p95: 610);
        var run = await h.RunToEndAsync((await h.SeedAsync()).Id);
        run.State.Should().Be(PerformanceRunState.Completed);
        run.Verdict.Should().Be(PerformanceQualityVerdict.Fail);
        run.ThresholdResults.Single(t => t.ThresholdId == "p95").Outcome.Should().Be(CheckOutcome.Fail);
    }

    [Fact]
    public async Task AdvisoryThresholdFailure_IsAWarning()
    {
        await using var h = new Harness();
        h.Runner.SummaryJson = Summary(rps: 12);
        var run = await h.RunToEndAsync((await h.SeedAsync()).Id);
        run.Verdict.Should().Be(PerformanceQualityVerdict.Warning);
        run.ThresholdResults.Single(t => t.ThresholdId == "rps").Outcome.Should().Be(CheckOutcome.Warning);
        run.Quality!.Failed.Should().Be(0);
    }

    [Fact]
    public async Task NoThresholds_MeasuredNotPassed_NoScore()
    {
        await using var h = new Harness();
        var run = await h.RunToEndAsync((await h.SeedAsync(Definition(d => d with { Thresholds = [] }))).Id);
        run.State.Should().Be(PerformanceRunState.Completed);
        run.Metrics.Should().NotBeNull();
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        run.Quality!.QualityPercent.Should().BeNull("no assessed threshold is no score, never 0");
        run.Limitations.Should().Contain(l => l.Contains("measured, not assessed"));
    }

    [Fact]
    public async Task ProviderCrash_IsExecutionFailed_WithNothingAssessed()
    {
        await using var h = new Harness();
        h.Runner.OnRun = _ => new ProcessOutcome(107, "", "script exception at line 3 Authorization: Bearer abc.def", false, false, null);
        var run = await h.RunToEndAsync((await h.SeedAsync()).Id);
        run.State.Should().Be(PerformanceRunState.ExecutionFailed);
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        run.ThresholdResults.Should().OnlyContain(t => t.Outcome == CheckOutcome.NotAssessed);
        run.Quality!.QualityPercent.Should().BeNull();
        run.ProviderDiagnostics.Should().NotContain("abc.def", "provider output is redacted");
    }

    [Fact]
    public async Task MalformedOrMissingProviderOutput_IsExecutionFailed_NeverZeroMetrics()
    {
        await using var h = new Harness();
        h.Runner.SummaryJson = "{ broken";
        var run = await h.RunToEndAsync((await h.SeedAsync()).Id);
        run.State.Should().Be(PerformanceRunState.ExecutionFailed);
        run.Metrics.Should().BeNull();
        run.StateReason.Should().Contain("not valid JSON");
        h.Runner.SummaryJson = null;
        var missing = await h.RunToEndAsync((await h.Store().DefinitionAsync("pay-qa", "def-1"))!.Id);
        missing.StateReason.Should().Contain("without writing its structured summary");
    }

    [Fact]
    public async Task Timeout_IsTimedOut_NotAssessed()
    {
        await using var h = new Harness();
        h.Runner.OnRun = _ => new ProcessOutcome(null, "", "", true, false, null);
        var run = await h.RunToEndAsync((await h.SeedAsync()).Id);
        run.State.Should().Be(PerformanceRunState.TimedOut);
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        Directory.Exists(h.Runner.LastWorkingDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_TerminatesTheRun_AsCancelled_NotAQualityResult()
    {
        // A provider that runs until cancelled; cancel as soon as it started.
        var slow = new SlowRunner(new FakeRunner());
        await using var h2 = new HarnessWith(slow);
        var d2 = await h2.SeedAsync();
        var started = await h2.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d2.Id });
        await slow.Started.Task;
        var cancelling = await h2.Execution.CancelAsync(started.Run!.RunId);
        cancelling!.State.Should().Be(PerformanceRunState.Cancelling);
        await h2.Execution.LastExecution!;
        var run = (await h2.Store().RunAsync(started.Run.RunId))!;
        run.State.Should().Be(PerformanceRunState.Cancelled);
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        run.Metrics.Should().BeNull();
        slow.WasCancelled.Should().BeTrue("the provider process was terminated");
    }

    private sealed class SlowRunner(FakeRunner versions) : IProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }
        public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
        {
            if (spec.Arguments[0] == "version") return await versions.RunAsync(spec, timeout, maxOutputBytes, ct);
            Started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { WasCancelled = true; return new ProcessOutcome(null, "", "", false, true, null); }
            return new ProcessOutcome(0, "", "", false, false, null);
        }
    }

    private sealed class HarnessWith : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public PerformanceTestExecutionService Execution => _services.GetRequiredService<PerformanceTestExecutionService>();
        public HarnessWith(IProcessRunner runner)
        {
            var name = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton(Options);
            services.AddSingleton(runner);
            services.AddSingleton<IPerformanceTestProvider, K6PerformanceTestProvider>();
            services.AddSingleton<PerformanceTestProviderRegistry>();
            services.AddSingleton<PerformanceTestReadinessService>();
            services.AddSingleton<PerformanceTestExecutionService>();
            services.AddScoped<PerformanceTestStore>();
            services.AddScoped<IqrSourceStore>();
            _services = services.BuildServiceProvider();
        }
        public PerformanceTestStore Store() => _services.CreateScope().ServiceProvider.GetRequiredService<PerformanceTestStore>();
        public async Task<PerformanceTestDefinition> SeedAsync()
        {
            var store = Store();
            await store.SaveDataProfileAsync("pay-qa", Data(), DateTimeOffset.UtcNow);
            return await store.SaveDefinitionAsync("pay-qa", Definition(), DateTimeOffset.UtcNow);
        }
        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    [Fact]
    public async Task OneActiveRunPerEnvironment()
    {
        var slow = new SlowRunner(new FakeRunner());
        await using var h = new HarnessWith(slow);
        var d = await h.SeedAsync();
        var first = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        await slow.Started.Task;
        var second = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        second.Conflict.Should().Contain("already running");
        await h.Execution.CancelAsync(first.Run!.RunId);
        await h.Execution.LastExecution!;
    }

    // ── History, versions, baselines, drift ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_RunsPersist_AreImmutable_AndKeepTheirDefinitionVersion()
    {
        await using var h = new Harness();
        var v1 = await h.SeedAsync();
        var a = await h.RunToEndAsync(v1.Id);
        var v2 = await h.Store().SaveDefinitionAsync("pay-qa", v1 with { Thresholds = [v1.Thresholds[0] with { Value = 300 }] }, DateTimeOffset.UtcNow);
        v2.Version.Should().Be(2);
        v2.ComparisonFingerprint.Should().Be(v1.ComparisonFingerprint, "a threshold change keeps runs comparable");
        var b = await h.RunToEndAsync(v2.Id);
        var runs = await h.Store().RunsAsync("pay-qa", v1.Id);
        runs.Should().HaveCount(2);
        runs.Single(r => r.RunId == a.RunId).Should().Match<PerformanceTestRun>(r => r.DefinitionVersion == 1 && r.Verdict == PerformanceQualityVerdict.Pass && r.DefinitionSnapshot.Thresholds[0].Value == 500);
        runs.Single(r => r.RunId == b.RunId).Should().Match<PerformanceTestRun>(r => r.DefinitionVersion == 2 && r.Verdict == PerformanceQualityVerdict.Fail, "420 ms misses the new 300 ms threshold; run A is not re-evaluated");
        var save = () => h.Store().SaveRunAsync(a with { Verdict = PerformanceQualityVerdict.Fail });
        await save.Should().ThrowAsync<InvalidOperationException>("a finished run is immutable");
        var v3 = await h.Store().SaveDefinitionAsync("pay-qa", v2 with { Workload = v2.Workload with { VirtualUsers = 10 } }, DateTimeOffset.UtcNow);
        v3.ComparisonFingerprint.Should().NotBe(v2.ComparisonFingerprint, "a workload change makes runs not directly comparable");
        (await h.Store().DeleteDefinitionAsync("pay-qa", v1.Id)).Should().Be("Archived", "history is never cascade-deleted");
        (await h.Store().RunsAsync("pay-qa", v1.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task BaselineLifecycle_ExplicitVersionedSupersededAndBoundAtRunTime()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition(x => x with { DriftPolicies = [new PerformanceDriftPolicy { Metric = PerformanceMetric.LatencyP95Ms, AllowedRelativeChangePercent = 20 }] }));
        var run1 = await h.RunToEndAsync(d.Id);
        (await h.Store().BaselinesAsync("pay-qa")).Should().BeEmpty("the latest run never becomes a baseline automatically");
        var v1 = (await h.Store().PromoteAsync("pay-qa", new PerformanceBaselinePromotion { RunId = run1.RunId, Reason = "first accepted reference" }, DateTimeOffset.UtcNow)).Baseline!;
        v1.Version.Should().Be(1);

        h.Runner.SummaryJson = Summary(p95: 480);
        var run2 = await h.RunToEndAsync(d.Id);
        run2.BaselineIdAtRun.Should().Be(v1.BaselineId);
        run2.Drift!.State.Should().Be(PerformanceDriftState.DegradedWithinTolerance, "+14.3 % is within the accepted +20 %");

        h.Runner.SummaryJson = Summary(p95: 300);
        var run3 = await h.RunToEndAsync(d.Id);
        var promoted = await h.Store().PromoteAsync("pay-qa", new PerformanceBaselinePromotion { RunId = run3.RunId, Reason = "infrastructure upgraded" }, DateTimeOffset.UtcNow);
        promoted.Baseline!.Version.Should().Be(2);
        promoted.Superseded!.BaselineId.Should().Be(v1.BaselineId);
        var baselines = await h.Store().BaselinesAsync("pay-qa", d.Id);
        baselines.Should().HaveCount(2);
        baselines.Count(b => b.Status == PerformanceBaselineStatus.Active).Should().Be(1);
        baselines.Single(b => b.Version == 1).Should().Match<PerformanceBaseline>(b => b.Status == PerformanceBaselineStatus.Superseded && b.SupersededByBaselineId == promoted.Baseline.BaselineId);

        (await h.Store().RunAsync(run2.RunId))!.Drift!.BaselineVersion.Should().Be(1, "run 2 keeps its original baseline after v2 is promoted");
        h.Runner.SummaryJson = Summary(p95: 400);
        var run4 = await h.RunToEndAsync(d.Id);
        run4.Drift!.BaselineVersion.Should().Be(2);
        run4.Drift.State.Should().Be(PerformanceDriftState.Regression, "+33 % against v2's 300 ms exceeds +20 %");
        run4.Verdict.Should().Be(PerformanceQualityVerdict.Pass, "the absolute threshold (< 500 ms) still passes: drift and thresholds are separate");
        run4.Drift.Findings.Should().ContainSingle(f => f.Metric == PerformanceMetric.LatencyP95Ms && f.Baseline == 300 && f.Current == 400);

        (await h.Store().ArchiveBaselineAsync("pay-qa", v1.BaselineId, DateTimeOffset.UtcNow))!.Status.Should().Be(PerformanceBaselineStatus.Historical);
        (await h.Store().BaselinesAsync("pay-qa", d.Id)).Should().HaveCount(2, "a baseline is archived, never deleted");
    }

    [Fact]
    public async Task OnlyCompletedRunsWithMetrics_CanBecomeBaselines()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync();
        h.Runner.OnRun = _ => new ProcessOutcome(107, "", "", false, false, null);
        var failed = await h.RunToEndAsync(d.Id);
        (await h.Store().PromoteAsync("pay-qa", new PerformanceBaselinePromotion { RunId = failed.RunId }, DateTimeOffset.UtcNow)).Error.Should().Contain("ExecutionFailed run cannot become a baseline");
        h.Runner.OnRun = null;
        h.Runner.SummaryJson = Summary(p95: 900);
        var thresholdFailed = await h.RunToEndAsync(d.Id);
        (await h.Store().PromoteAsync("pay-qa", new PerformanceBaselinePromotion { RunId = thresholdFailed.RunId }, DateTimeOffset.UtcNow)).Error.Should().Contain("Confirm explicitly");
        (await h.Store().PromoteAsync("pay-qa", new PerformanceBaselinePromotion { RunId = thresholdFailed.RunId, AcceptThresholdFailures = true }, DateTimeOffset.UtcNow)).Baseline.Should().NotBeNull();
    }

    private static PerformanceTestRun Completed(double p95, double rps = 50, double errors = 0.2, PerformanceWorkload? workload = null, string environment = "pay-qa") => new()
    {
        EnvironmentId = environment, TargetOrigin = "https://paymenthub-qa.example.test", ComparisonFingerprint = workload is null ? "same" : "other", State = PerformanceRunState.Completed,
        DefinitionSnapshot = Definition(d => workload is null ? d : d with { Workload = workload }),
        Metrics = new PerformanceMetrics { RequestCount = 1000, RequestsPerSecond = rps, ErrorRatePercent = errors, Latency = new PerformanceLatency { P50Ms = p95 / 2, P90Ms = p95 * .9, P95Ms = p95, P99Ms = p95 * 1.5, MeanMs = p95 / 2 } },
    };

    private static readonly PerformanceBaseline Base = new() { BaselineId = "b1", Version = 1 };

    [Fact]
    public void ThresholdFail_CanCoexistWithImprovementAgainstBaseline()
    {
        var policies = new[] { new PerformanceDriftPolicy { Metric = PerformanceMetric.LatencyP95Ms, AllowedRelativeChangePercent = 20 } };
        var current = Completed(600);
        var thresholds = PerformanceTestRules.EvaluateThresholds([new PerformanceThreshold { Metric = PerformanceMetric.LatencyP95Ms, Value = 500 }], current.Metrics, true);
        PerformanceTestRules.Quality(thresholds).Verdict.Should().Be(PerformanceQualityVerdict.Fail);
        PerformanceTestRules.Drift(current, Completed(700), Base, policies).State.Should().Be(PerformanceDriftState.Improved);
    }

    [Fact]
    public void Drift_DeltasPolicyZeroBaselineAndCompatibility()
    {
        var baseline = Completed(300, rps: 50, errors: 0.5);
        var drift = PerformanceTestRules.Drift(Completed(420, rps: 40, errors: 1.0), baseline, Base,
        [
            new PerformanceDriftPolicy { Metric = PerformanceMetric.ThroughputRps, AllowedRelativeChangePercent = 15 },
            new PerformanceDriftPolicy { Metric = PerformanceMetric.ErrorRatePercent, AllowedAbsoluteChange = 0.5 },
        ]);
        var p95 = drift.Deltas.Single(d => d.Metric == PerformanceMetric.LatencyP95Ms);
        p95.AbsoluteDelta.Should().Be(120); p95.RelativePercent.Should().Be(40); p95.Direction.Should().Be(MetricChangeDirection.Worse);
        p95.PolicyState.Should().BeNull("no policy for P95: descriptive only");
        drift.Deltas.Single(d => d.Metric == PerformanceMetric.ThroughputRps).PolicyState.Should().Be(PerformanceDriftState.Regression, "−20 % throughput exceeds −15 %");
        drift.Deltas.Single(d => d.Metric == PerformanceMetric.ErrorRatePercent).PolicyState.Should().Be(PerformanceDriftState.DegradedWithinTolerance, "+0.5 pp is exactly the accepted change");
        drift.State.Should().Be(PerformanceDriftState.Regression);

        PerformanceTestRules.Drift(Completed(420), baseline, Base, []).State.Should().Be(PerformanceDriftState.NotAssessed, "no drift policy: comparison only, never Regression");
        PerformanceTestRules.Delta(PerformanceMetric.ErrorRatePercent, 0, 0.4).RelativePercent.Should().BeNull("relative change from a zero baseline is unavailable");
        PerformanceTestRules.Delta(PerformanceMetric.ErrorRatePercent, 0, 0.4).AbsoluteDelta.Should().Be(0.4);
        PerformanceTestRules.Delta(PerformanceMetric.LatencyP95Ms, null, 400).Direction.Should().Be(MetricChangeDirection.NotAvailable);
        PerformanceTestRules.Delta(PerformanceMetric.LatencyP95Ms, 400, 402).Direction.Should().Be(MetricChangeDirection.Unchanged);
        PerformanceTestRules.Delta(PerformanceMetric.LatencyP95Ms, 400, 300).Direction.Should().Be(MetricChangeDirection.Improved);

        var otherWorkload = PerformanceTestRules.Drift(Completed(420, workload: new PerformanceWorkload { VirtualUsers = 100 }), baseline, Base, [new PerformanceDriftPolicy { Metric = PerformanceMetric.LatencyP95Ms, AllowedRelativeChangePercent = 1 }]);
        otherWorkload.State.Should().Be(PerformanceDriftState.NotComparable);
        otherWorkload.CompatibilityNotes.Should().Contain("Not comparable — workload changed.");
        otherWorkload.Findings.Should().BeEmpty("no authoritative drift for incompatible runs");
        PerformanceTestRules.Drift(Completed(420, environment: "pay-test"), baseline, Base, []).CompatibilityNotes.Should().Contain("Not comparable — different environment.");
    }

    // ── API: the backend refuses production whatever the client sends ───────────────────────────────────────────────

    [Fact]
    public async Task Api_RejectsProductionDefinitions_AndBlocksRunsWithExactReasons()
    {
        await using var h = new Harness();
        var sp = h.Services.CreateScope().ServiceProvider;
        var controller = new BirkNext.Api.Controllers.PerformanceTestsController(sp.GetRequiredService<PerformanceTestStore>(), sp.GetRequiredService<PerformanceTestReadinessService>(),
            h.Execution, sp.GetRequiredService<PerformanceTestProviderRegistry>(), Options);
        var production = await controller.Create("pay-qa", Definition(d => d with { EnvironmentType = "Production" }), CancellationToken.None);
        (production.Result as Microsoft.AspNetCore.Mvc.BadRequestObjectResult)!.Value.Should().Be("Production environments cannot be used for performance tests.");
        var prodHost = await controller.Create("pay-qa", Definition(d => d with { TargetOrigin = "https://paymenthub.prod.example.test" }), CancellationToken.None);
        prodHost.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        var withPath = await controller.Create("pay-qa", Definition(d => d with { TargetOrigin = "https://paymenthub-qa.example.test/some/page?x=1" }), CancellationToken.None);
        ((withPath.Result as Microsoft.AspNetCore.Mvc.OkObjectResult)!.Value as PerformanceTestDefinition)!.TargetOrigin.Should().Be("https://paymenthub-qa.example.test", "only the origin is kept");

        h.Runner.Installed = false;
        await h.Store().SaveDataProfileAsync("pay-qa", Data(), DateTimeOffset.UtcNow);
        var saved = ((await controller.Create("pay-qa", Definition(), CancellationToken.None)).Result as Microsoft.AspNetCore.Mvc.OkObjectResult)!.Value as PerformanceTestDefinition;
        var run = await controller.Run("pay-qa", new PerformanceRunRequest { DefinitionId = saved!.Id }, CancellationToken.None);
        var blocked = (run as Microsoft.AspNetCore.Mvc.UnprocessableEntityObjectResult)!.Value!;
        JsonSerializer.Serialize(blocked).Should().Contain("k6 is not installed or configured on this BirkNext host");
    }

    // ── Applicability ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Applicability_NeedsAnHttpTarget_NeverFails()
    {
        BirkNext.Technology.ApplicabilityEvaluator.Evaluate("performance-test-review", new BirkNext.Technology.ProjectApplicabilityInput { HasApiTarget = true }).Status.Should().Be(ApplicabilityStatus.Applicable);
        BirkNext.Technology.ApplicabilityEvaluator.Evaluate("performance-test-review", new BirkNext.Technology.ProjectApplicabilityInput()).Status.Should().Be(ApplicabilityStatus.NeedsConfiguration);
        BirkNext.Technology.ApplicabilityEvaluator.Evaluate("performance-test-review", new BirkNext.Technology.ProjectApplicabilityInput { HasSourceSnapshot = true }).Status.Should().Be(ApplicabilityStatus.NotApplicable,
            "a data pipeline without HTTP API or frontend has nothing to load-test");
    }

    // ── Live k6 (gated) ─────────────────────────────────────────────────────────────────────────────────────────────

    [K6LiveFact]
    public async Task LiveK6_TinyLocalRun()
    {
        using var listener = new System.Net.HttpListener();
        var port = Random.Shared.Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () => { while (listener.IsListening) { try { var c = await listener.GetContextAsync(); c.Response.StatusCode = 200; c.Response.Close(); } catch { break; } } });
        var provider = new K6PerformanceTestProvider(Options, new SystemProcessRunner(), NullLogger<K6PerformanceTestProvider>.Instance);
        var d = Definition(x => x with
        {
            TargetOrigin = $"http://127.0.0.1:{port}", EnvironmentType = "Local",
            Scenario = x.Scenario with { TestDataProfileId = null, Steps = [new HttpPerformanceStep { Name = "ping", RelativePath = "/ping", ExpectedStatusCodes = [200], ThinkTimeMs = 100 }] },
            Workload = new PerformanceWorkload { VirtualUsers = 2, WarmupSeconds = 0, RampUpSeconds = 1, SteadyStateSeconds = 10, RampDownSeconds = 0 },
        });
        var dir = Path.Combine(Path.GetTempPath(), "birknext-performance-live", Guid.NewGuid().ToString("N"));
        var result = await provider.ExecuteAsync(new PerformanceProviderInput(Guid.NewGuid(), d, null, dir, TimeSpan.FromMinutes(2)), null, CancellationToken.None);
        result.State.Should().Be(PerformanceRunState.Completed, result.Reason + result.Diagnostics);
        result.Metrics!.RequestCount.Should().BeGreaterThan(0);
        result.Metrics.Latency.P95Ms.Should().NotBeNull();
        Directory.Exists(dir).Should().BeFalse();
        listener.Stop();
    }
}

/// <summary>Runs only when a k6 executable is on PATH; otherwise reported as skipped with the reason (never a silent pass).</summary>
public sealed class K6LiveFactAttribute : FactAttribute
{
    public K6LiveFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var found = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "k6.exe")) || File.Exists(Path.Combine(dir, "k6")));
        if (!found) Skip = "Live k6 test: k6 is not on PATH on this machine. Install k6 to run a tiny local load test (2 virtual users, ~10 s, loopback only).";
    }
}
