using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ContainerRuntime;
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

    // ── Provider over a fake container runtime ──────────────────────────────────────────────────────────────────────

    /// <summary>A container runtime that runs nothing: it answers k6 "version", the network probe and test runs by writing the result files into the
    /// run's mounted output directory, records every spec and every removal.</summary>
    internal sealed class FakeRuntime : IContainerExecutionRuntime
    {
        public ProviderAvailability RuntimeAvailability { get; set; } = ProviderAvailability.Available;
        public bool ImagePresent { get; set; } = true;
        public string K6Version { get; set; } = "k6 v1.0.0 (go1.24.1, linux/amd64)";
        public int ProbeStatus { get; set; } = 200;
        public int ProbeErrorCode { get; set; }
        public Func<ContainerRunSpec, ContainerRunOutcome?>? OnRun { get; set; }
        public string? SummaryJson { get; set; } = Summary();
        public List<ContainerRunSpec> Runs { get; } = [];
        public List<string> Removed { get; } = [];
        public List<string> Managed { get; } = [];
        public string? LastScript { get; private set; }
        public string? LastWorkingDirectory { get; private set; }

        public string RuntimeId => PerformanceProviderIds.PodmanRuntime;
        public string DisplayName => "Podman";
        public string HostGatewayAlias => "host.containers.internal";
        /// <summary>cgroup controllers (default: all delegated).</summary>
        public HashSet<string> Controllers { get; set; } = ["cpu", "memory", "pids"];
        /// <summary>Containers that "exist" for inspect/stats (resource tests).</summary>
        public Dictionary<string, ContainerInstanceInfo> Containers { get; } = new(StringComparer.Ordinal);
        public Func<string, ContainerStatsSnapshot>? OnStats { get; set; }
        public List<string> StatsCalls { get; } = [];

        public Task<IReadOnlySet<string>> ControllersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlySet<string>>(Controllers);
        public Task<ContainerInstanceInfo?> InspectAsync(string name, CancellationToken ct = default) => Task.FromResult(Containers.GetValueOrDefault(name));
        public Task<ContainerStatsSnapshot> StatsAsync(string name, CancellationToken ct = default)
        {
            lock (StatsCalls) StatsCalls.Add(name);
            return Task.FromResult(OnStats?.Invoke(name) ?? new ContainerStatsSnapshot(null, null, null, null, "The container is not running or not found."));
        }

        public Task<PerformanceRuntimeStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(new PerformanceRuntimeStatus
        {
            RuntimeId = RuntimeId, DisplayName = DisplayName, Availability = RuntimeAvailability, Version = "5.4.2",
            Detail = RuntimeAvailability == ProviderAvailability.Available ? "Podman 5.4.2 is available." : "Podman is not installed on this BirkNext host (the podman CLI could not be started).",
        });

        public Task<ContainerImageStatus> ImageAsync(string image, CancellationToken ct = default) =>
            Task.FromResult(new ContainerImageStatus(image, ImagePresent, ImagePresent ? "sha256:" + new string('a', 64) : null, ImagePresent ? "present" : "missing"));

        public Task<ContainerImageStatus> PullAsync(string image, CancellationToken ct = default) { ImagePresent = true; return ImageAsync(image, ct); }

        public async Task<ContainerRunOutcome> RunAsync(ContainerRunSpec spec, CancellationToken ct)
        {
            Runs.Add(spec);
            try
            {
                if (spec.Command[0] == "version") return new ContainerRunOutcome(0, K6Version, "", false, false, null, true);
                var input = spec.Mounts.Single(m => m.ContainerPath == "/birknext/in").HostPath;
                var output = spec.Mounts.Single(m => m.ContainerPath == "/birknext/out").HostPath;
                LastWorkingDirectory = Path.GetDirectoryName(input);
                var script = Path.Combine(input, Path.GetFileName(spec.Command[^1]));
                LastScript = await File.ReadAllTextAsync(script, ct);
                if (script.EndsWith("probe.js", StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(Path.Combine(output, "probe.json"), JsonSerializer.Serialize(new { metrics = new Dictionary<string, object>
                    {
                        ["birknext_probe_status"] = new { values = new { max = ProbeStatus } }, ["birknext_probe_error"] = new { values = new { max = ProbeErrorCode } },
                    } }), ct);
                    return new ContainerRunOutcome(0, "", "", false, false, null, true);
                }
                if (OnRun?.Invoke(spec) is { } custom) return custom;
                try { await Task.Delay(TimeSpan.FromMilliseconds(20), ct); }
                catch (OperationCanceledException) { return new ContainerRunOutcome(null, "", "", false, true, null, true); }
                if (SummaryJson is not null) await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), SummaryJson, ct);
                return new ContainerRunOutcome(0, "", "", false, false, null, true);
            }
            finally { Removed.Add(spec.Name); }
        }

        public Task<bool> RemoveAsync(string name, CancellationToken ct = default) { Removed.Add(name); Managed.Remove(name); return Task.FromResult(true); }
        public Task<IReadOnlyList<string>> ListManagedAsync(string component, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([.. Managed]);
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

    private static K6PerformanceTestProvider Provider(FakeRuntime runtime, PerformanceTestOptions? o = null) => new(o ?? Options, runtime, NullLogger<K6PerformanceTestProvider>.Instance);

    [Fact]
    public async Task ProviderDetection_SeparatesRuntimeImageAndVersion_NeverAFailure()
    {
        var available = await Provider(new FakeRuntime()).StatusAsync();
        available.Should().Match<PerformanceProviderStatus>(s => s.Availability == ProviderAvailability.Available && s.Version == "1.0.0" && s.ImagePresent && s.Runtime!.Version == "5.4.2");
        available.Image.Should().Be(PerformanceContainerOptions.DefaultImage);
        var noPodman = await Provider(new FakeRuntime { RuntimeAvailability = ProviderAvailability.Unavailable }).StatusAsync();
        noPodman.Availability.Should().Be(ProviderAvailability.RuntimeUnavailable);
        noPodman.Detail.Should().Contain("Podman is not installed");
        var noImage = await Provider(new FakeRuntime { ImagePresent = false }).StatusAsync();
        noImage.Availability.Should().Be(ProviderAvailability.ImageMissing);
        noImage.Detail.Should().Contain("podman pull docker.io/grafana/k6:1.0.0", "no pull without policy: the person is told how");
        (await Provider(new FakeRuntime { K6Version = "k6 v0.40.0" }).StatusAsync()).Availability.Should().Be(ProviderAvailability.VersionUnsupported);
        (await Provider(new FakeRuntime(), Options with { Container = Options.Container with { Image = "docker.io/grafana/k6:latest" } }).StatusAsync())
            .Availability.Should().Be(ProviderAvailability.Misconfigured, "a floating image is never authoritative");
        (await Provider(new FakeRuntime { ImagePresent = false }).PrepareAsync()).Should().Contain("disabled", "pulls need AllowImagePull");
        var pullAllowed = new FakeRuntime { ImagePresent = false };
        (await Provider(pullAllowed, Options with { Container = Options.Container with { AllowImagePull = true } }).PrepareAsync()).Should().BeNull();
        pullAllowed.ImagePresent.Should().BeTrue();
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
        var a = K6ScriptGenerator.Generate(hostile, Data(), "/birknext/out/summary.json");
        var b = K6ScriptGenerator.Generate(hostile, Data(), "/birknext/out/summary.json");
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

    // ── Podman runtime: argument generation, hardening, injection, cleanup ──────────────────────────────────────────

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Func<ProcessSpec, ProcessOutcome>? Respond { get; set; }
        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(Respond?.Invoke(spec) ?? new ProcessOutcome(spec.Arguments[0] == "container" ? 1 : 0, "5.4.2", "", false, false, null));
        }
    }

    private static PodmanContainerExecutionRuntime Podman(RecordingProcessRunner runner) => new(runner, Options, NullLogger<PodmanContainerExecutionRuntime>.Instance);

    [Fact]
    public async Task ProviderRun_ProducesAHardenedEphemeralPodmanCommand_WithoutUserValues()
    {
        var runtime = new FakeRuntime();
        var provider = Provider(runtime, Options with { Container = Options.Container with { HttpsProxy = "http://proxy.example.test:3128" } });
        var hostile = Definition(d => d with { Scenario = d.Scenario with { Steps = [d.Scenario.Steps[0] with { Headers = [new("X-Trace", "x --privileged -v /:/host")], RelativePath = "/api/$(rm -rf)" }] } });
        var dir = Path.Combine(Path.GetTempPath(), "birknext-performance-test", Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid();
        var result = await provider.ExecuteAsync(new PerformanceProviderInput(runId, hostile, Data(), dir, TimeSpan.FromMinutes(2)), null, CancellationToken.None);
        result.State.Should().Be(PerformanceRunState.Completed);
        var spec = runtime.Runs.Single(s => s.Command[0] == "run");
        spec.Name.Should().Be($"birknext-k6-run-{runId:N}");
        spec.Labels.Should().Contain(new KeyValuePair<string, string>("birknext.managed", "true")).And.Contain(new KeyValuePair<string, string>("birknext.component", "performance-test"))
            .And.Contain(new KeyValuePair<string, string>("birknext.run-id", runId.ToString("N")));
        spec.Mounts.Should().HaveCount(2).And.OnlyContain(m => m.HostPath.StartsWith(dir, StringComparison.Ordinal), "only the run's own directories are mounted");
        spec.Mounts.Single(m => m.ContainerPath == "/birknext/in").ReadOnly.Should().BeTrue();
        spec.Environment.Keys.Should().BeEquivalentTo(["K6_NO_USAGE_REPORT", "HTTPS_PROXY"], "the host environment is never passed through");

        var args = PodmanContainerExecutionRuntime.RunArguments(spec);
        args.Should().Contain(["--rm", "--pull=never", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges"]);
        args.Should().NotContain(a => a.Contains("privileged") && a != "--security-opt=no-new-privileges").And.NotContain("--network=host").And.NotContain(a => a.Contains("podman.sock") || a.Contains("docker.sock"));
        args.Should().NotContain(a => a.Contains("rm -rf") || a.Contains("X-Trace") || a.Contains("/:/host"), "user values live only in the generated script file, never in arguments");
        args.Last().Should().Be("/birknext/in/test.js");
        runtime.LastScript.Should().Contain("rm -rf").And.Contain("--privileged", "…where they are inert JSON string content");
        Directory.Exists(dir).Should().BeFalse("temp script and result are removed");
        runtime.Removed.Should().Contain(spec.Name);
        result.ContainerImage.Should().Be(PerformanceContainerOptions.DefaultImage);
        result.ImageDigest.Should().StartWith("sha256:");
        result.RuntimeId.Should().Be("container.podman");
    }

    [Fact]
    public void RunArguments_RejectAnythingThatCouldBecomeAFlag()
    {
        var ok = new ContainerRunSpec { Name = "birknext-k6-run-1", Image = PerformanceContainerOptions.DefaultImage, Command = ["version"] };
        PodmanContainerExecutionRuntime.RunArguments(ok).Should().StartWith(["run", "--name", "birknext-k6-run-1"]);
        PodmanContainerExecutionRuntime.RunArguments(ok, new HashSet<string>()).Should().NotContain(x => x.StartsWith("--memory") || x.StartsWith("--cpus") || x.StartsWith("--pids-limit"),
            "a limit whose cgroup controller is not delegated is left out (and reported), not a failed run");
        PodmanContainerExecutionRuntime.RunArguments(ok, new HashSet<string> { "memory", "cpu", "pids" }).Should().Contain(["--pids-limit=512", "--memory=1024m", "--cpus=2"]);
        foreach (var bad in new[]
        {
            ok with { Name = "--privileged" }, ok with { Image = "--privileged" }, ok with { Image = "docker.io/grafana/k6:latest" }, ok with { Network = "host --privileged" },
            ok with { Labels = new Dictionary<string, string> { ["birknext.x"] = "a b" } }, ok with { Environment = new Dictionary<string, string> { ["PATH"] = "x\n--privileged" } },
            ok with { Mounts = [new ContainerMount(@"C:\x,y=z", "/birknext/in", true)] }, ok with { Mounts = [new ContainerMount(@"C:\x", "/../etc", true)] },
        })
            ((Action)(() => PodmanContainerExecutionRuntime.RunArguments(bad))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PodmanRuntime_DetectsCliAndMachine_AndAlwaysRemovesTheContainer()
    {
        var missing = new RecordingProcessRunner { Respond = _ => new ProcessOutcome(null, "", "", false, false, "The executable could not be started (Win32Exception).") };
        (await Podman(missing).StatusAsync()).Availability.Should().Be(ProviderAvailability.Unavailable);
        var noMachine = new RecordingProcessRunner { Respond = s => s.Arguments[0] == "info" ? new ProcessOutcome(125, "", "cannot connect", false, false, null) : new ProcessOutcome(0, "5.4.2", "", false, false, null) };
        (await Podman(noMachine).StatusAsync()).Should().Match<PerformanceRuntimeStatus>(s => s.Availability == ProviderAvailability.RuntimeUnavailable && s.Detail.Contains("podman machine start"));
        var ok = new RecordingProcessRunner();
        (await Podman(ok).StatusAsync()).Should().Match<PerformanceRuntimeStatus>(s => s.Availability == ProviderAvailability.Available && s.Version == "5.4.2");

        var cancelled = new RecordingProcessRunner { Respond = s => s.Arguments[0] == "run" ? new ProcessOutcome(null, "", "", false, true, null) : new ProcessOutcome(s.Arguments[0] == "container" ? 1 : 0, "", "", false, false, null) };
        var outcome = await Podman(cancelled).RunAsync(new ContainerRunSpec { Name = "birknext-k6-run-abc", Image = PerformanceContainerOptions.DefaultImage, Command = ["run", "/birknext/in/test.js"] }, CancellationToken.None);
        outcome.Cancelled.Should().BeTrue();
        cancelled.Calls.Should().Contain(c => c.Arguments.SequenceEqual(new[] { "rm", "--force", "--ignore", "--time", "5", "birknext-k6-run-abc" }), "killing the client never stops the container: it is force-removed");
        cancelled.Calls.Should().Contain(c => c.Arguments.SequenceEqual(new[] { "container", "exists", "birknext-k6-run-abc" }));
        var listing = new RecordingProcessRunner();
        await Podman(listing).ListManagedAsync("performance-test");
        listing.Calls.Single().Arguments.Should().Contain(["label=birknext.managed=true", "label=birknext.component=performance-test"], "only BirkNext-managed containers are ever listed");
    }

    [Fact]
    public void Networking_LocalhostIsNeverTheContainerItself()
    {
        var provider = Provider(new FakeRuntime(), Options with { Container = Options.Container with { TargetNetworks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["orders-api"] = "birknext-studio-local_default" } } });
        provider.ResolveTarget("http://localhost:5095").Should().Match<ContainerTarget>(t => t.ExecutionOrigin == "http://host.containers.internal:5095" && t.HostGateway && t.Network == null);
        provider.ResolveTarget("http://127.0.0.1:5000").ExecutionOrigin.Should().Be("http://host.containers.internal:5000");
        provider.ResolveTarget("http://orders-api:8080").Should().Match<ContainerTarget>(t => t.ExecutionOrigin == "http://orders-api:8080" && t.Network == "birknext-studio-local_default" && !t.HostGateway);
        provider.ResolveTarget("https://paymenthub-qa.example.test").Should().Match<ContainerTarget>(t => t.ExecutionOrigin == "https://paymenthub-qa.example.test" && t.Network == null && !t.HostGateway);
    }

    [Theory]
    [InlineData(200, 0, "Reachable", true)]
    [InlineData(404, 0, "Reachable", true)]
    [InlineData(0, 1101, "DnsFailure", false)]
    [InlineData(0, 1212, "ConnectionFailed", false)]
    [InlineData(0, 1310, "TlsFailure", false)]
    [InlineData(0, 1050, "Timeout", false)]
    public async Task ContainerNetworkCheck_ClassifiesFromTheContainer(int status, int code, string state, bool reachable)
    {
        var runtime = new FakeRuntime { ProbeStatus = status, ProbeErrorCode = code };
        var r = await Provider(runtime).CheckReachabilityAsync(Definition());
        r.State.Should().Be(state);
        r.Reachable.Should().Be(reachable);
        runtime.Runs.Single(s => s.Command[^1].EndsWith("probe.js", StringComparison.Ordinal)).Name.Should().StartWith("birknext-k6-probe-");
        if (state == "TlsFailure") r.Detail.Should().Contain("CaBundlePath").And.Contain("never disabled");
    }

    [Fact]
    public async Task OrphanCleanup_RemovesOnlyInactiveBirkNextK6Containers()
    {
        var active = Guid.NewGuid();
        var runtime = new FakeRuntime();
        runtime.Managed.AddRange([K6PerformanceTestProvider.ContainerName(active), K6PerformanceTestProvider.ContainerName(Guid.NewGuid()), "someone-elses-container"]);
        var removed = await Provider(runtime).CleanupOrphansAsync(new HashSet<Guid> { active });
        removed.Should().ContainSingle().Which.Should().StartWith("birknext-k6-run-");
        runtime.Managed.Should().Contain(K6PerformanceTestProvider.ContainerName(active)).And.Contain("someone-elses-container");
    }

    /// <summary>Two BirkNext instances may share one Podman: a young k6 container may be the other instance's live run and must not be killed.</summary>
    [Fact]
    public async Task OrphanCleanup_KeepsYoungContainersOfOtherInstances_AndRemovesOnlyStaleOnes()
    {
        var runtime = new FakeRuntime();
        var young = K6PerformanceTestProvider.ContainerName(Guid.NewGuid());
        var stale = K6PerformanceTestProvider.ContainerName(Guid.NewGuid());
        runtime.Managed.AddRange([young, stale]);
        runtime.Containers[young] = new ContainerInstanceInfo("a1b2c3d4e5f6", DateTimeOffset.UtcNow.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss.fffffff00 +0000", System.Globalization.CultureInfo.InvariantCulture) + " UTC", 0, true, null, null, null);
        runtime.Containers[stale] = new ContainerInstanceInfo("b1b2c3d4e5f6", DateTimeOffset.UtcNow.AddHours(-6).ToString("yyyy-MM-dd HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture) + " UTC", 0, true, null, null, null);
        var removed = await Provider(runtime).CleanupOrphansAsync(new HashSet<Guid>());
        removed.Should().Equal(stale);
        runtime.Managed.Should().Contain(young);
        K6PerformanceTestProvider.ParseStartedAt("2026-10-02 10:00:00.123456789 +0000 UTC").Should().Be(new DateTimeOffset(2026, 10, 2, 10, 0, 0, 123, TimeSpan.Zero).AddTicks(4567));
        K6PerformanceTestProvider.ParseStartedAt("2026-10-02 18:21:37.73539807 +0200 CEST").Should().Be(new DateTimeOffset(2026, 10, 2, 16, 21, 37, TimeSpan.Zero).AddTicks(7353980), "real Podman output uses local time with a zone abbreviation");
        K6PerformanceTestProvider.ParseStartedAt("2026-10-02T10:00:00Z").Should().Be(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero));
        K6PerformanceTestProvider.ParseStartedAt("0001-01-01 00:00:00 +0000 UTC").Should().BeNull();
    }

    // ── Execution (in-memory store + fake runtime) ──────────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public FakeRuntime Runner { get; }
        public ServiceProvider Services { get; }
        public PerformanceTestExecutionService Execution => Services.GetRequiredService<PerformanceTestExecutionService>();

        public Harness(PerformanceTestOptions? options = null, FakeRuntime? runtime = null, IContainerExecutionRuntime? custom = null)
        {
            Runner = runtime ?? new FakeRuntime();
            var name = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton(options ?? Options);
            services.AddSingleton(custom ?? Runner);
            services.AddSingleton<IPerformanceTestProvider, K6PerformanceTestProvider>();
            services.AddSingleton<PerformanceTestProviderRegistry>();
            services.AddSingleton<PerformanceTestReadinessService>();
            services.AddSingleton<PerformanceTestExecutionService>();
            services.AddScoped<PerformanceTestStore>();
            services.AddScoped<IqrSourceStore>();
            Services = services.BuildServiceProvider();
        }

        public PerformanceTestStore Store() => Services.CreateScope().ServiceProvider.GetRequiredService<PerformanceTestStore>();
        public PerformanceTestProviderRegistry Registry => Services.GetRequiredService<PerformanceTestProviderRegistry>();

        public async Task<PerformanceTestDefinition> SeedAsync(PerformanceTestDefinition? d = null)
        {
            var store = Store();
            await store.SaveDataProfileAsync("pay-qa", Data(), DateTimeOffset.UtcNow);
            return await store.SaveDefinitionAsync("pay-qa", d ?? Definition(), DateTimeOffset.UtcNow);
        }

        public async Task<PerformanceTestRun> RunToEndAsync(string definitionId)
        {
            var definition = (await Store().DefinitionAsync("pay-qa", definitionId))!;
            await Registry.CheckReachabilityAsync(definition);
            var started = await Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = definitionId });
            started.Run.Should().NotBeNull(string.Join(" ", started.Blockers) + started.Conflict);
            await Execution.LastExecution!;
            return (await Store().RunAsync(started.Run!.RunId))!;
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    [Fact]
    public async Task PodmanMissing_BlocksReadiness_AsAToolLimitation()
    {
        await using var h = new Harness();
        h.Runner.RuntimeAvailability = ProviderAvailability.Unavailable;
        var d = await h.SeedAsync();
        var readiness = await h.Services.GetRequiredService<PerformanceTestReadinessService>().EvaluateAsync(d, Data(), false);
        readiness.Ready.Should().BeFalse();
        readiness.Items.Single(i => i.Key == "runtime").State.Should().Be(PerformanceReadinessState.RuntimeUnavailable);
        readiness.Blockers.Should().ContainSingle(b => b.Contains("Podman is not installed"));
        readiness.Items.Single(i => i.Key == "network").State.Should().Be(PerformanceReadinessState.Optional, "network is checked once the runtime exists");
        var started = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        started.Run.Should().BeNull("nothing runs and nothing is scored");
        (await h.Store().RunsAsync("pay-qa")).Should().BeEmpty();
        h.Runner.Runs.Should().BeEmpty("no container is launched");
    }

    [Fact]
    public async Task ImageMissing_IsItsOwnReadinessState()
    {
        await using var h = new Harness();
        h.Runner.ImagePresent = false;
        var d = await h.SeedAsync();
        var readiness = await h.Services.GetRequiredService<PerformanceTestReadinessService>().EvaluateAsync(d, Data(), false);
        readiness.Items.Single(i => i.Key == "image").State.Should().Be(PerformanceReadinessState.ImageMissing);
        readiness.Items.Should().NotContain(i => i.Key == "provider", "the image is the blocker, reported once");
    }

    [Fact]
    public async Task ContainerNetwork_MustBeCheckedFromTheContainer_AndAnUnreachableTargetBlocks()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync();
        var readiness = h.Services.GetRequiredService<PerformanceTestReadinessService>();
        (await readiness.EvaluateAsync(d, Data(), false)).Items.Single(i => i.Key == "network").Should().Match<PerformanceReadinessItem>(i => i.State == PerformanceReadinessState.NeedsConfiguration && i.Blocking
            && i.Detail.Contains("Host reachability does not prove container reachability"));
        h.Runner.ProbeStatus = 0; h.Runner.ProbeErrorCode = 1101;
        await h.Registry.CheckReachabilityAsync(d);
        var blocked = await readiness.EvaluateAsync(d, Data(), false);
        blocked.Items.Single(i => i.Key == "network").State.Should().Be(PerformanceReadinessState.NetworkUnavailable);
        blocked.Ready.Should().BeFalse();
        h.Runner.ProbeStatus = 200; h.Runner.ProbeErrorCode = 0;
        await h.Registry.CheckReachabilityAsync(d);
        (await readiness.EvaluateAsync(d, Data(), false)).Ready.Should().BeTrue();

        // Ready at start, unreachable at launch: the run is Blocked (precondition), never ExecutionFailed or a quality result.
        h.Runner.ProbeStatus = 0; h.Runner.ProbeErrorCode = 1212;
        var started = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        await h.Execution.LastExecution!;
        var run = (await h.Store().RunAsync(started.Run!.RunId))!;
        run.State.Should().Be(PerformanceRunState.Blocked);
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        run.Reachability!.State.Should().Be("ConnectionFailed");
        h.Runner.Runs.Should().NotContain(s => s.Command.Last() == "/birknext/in/test.js", "no load container is started");
    }

    [Fact]
    public async Task SafeRestGet_IsReady_WithOptionalThresholdsBaselineAndObservability()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition(x => x with { Thresholds = [] }));
        await h.Registry.CheckReachabilityAsync(d);
        var readiness = await h.Services.GetRequiredService<PerformanceTestReadinessService>().EvaluateAsync(d, Data(), false);
        readiness.Ready.Should().BeTrue(string.Join(" | ", readiness.Blockers));
        readiness.Items.Select(i => i.Key).Should().ContainInOrder("target", "environment", "runtime", "image", "provider", "network", "tls");
        readiness.Items.Single(i => i.Key == "thresholds").Should().Match<PerformanceReadinessItem>(i => i.State == PerformanceReadinessState.Optional && i.Detail.Contains("measured, not assessed"));
        readiness.Items.Single(i => i.Key == "baseline").State.Should().Be(PerformanceReadinessState.Optional);
        readiness.Items.Single(i => i.Key == "observability").State.Should().Be(PerformanceReadinessState.Optional);
        readiness.Items.Single(i => i.Key == "tls").Detail.Should().Contain("verification is always on");
        readiness.Timeline.Should().HaveCount(4);
    }

    [Fact]
    public async Task CompletedRun_AllThresholdsPass_WithContainerProvenance_AndCleanup()
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
        run.ProviderVersion.Should().Be("1.0.0");
        run.RuntimeId.Should().Be("container.podman");
        run.RuntimeVersion.Should().Be("5.4.2");
        run.ContainerImage.Should().Be(PerformanceContainerOptions.DefaultImage);
        run.ImageDigest.Should().StartWith("sha256:");
        run.Reachability!.Reachable.Should().BeTrue();
        run.DefinitionVersion.Should().Be(1);
        run.Host!.LogicalProcessors.Should().BeGreaterThan(0);
        Directory.Exists(h.Runner.LastWorkingDirectory).Should().BeFalse("the generated script and summary are deleted");
        h.Runner.LastScript.Should().Contain("PAY-0001", "approved synthetic test data is embedded").And.NotContain("Bearer");
        h.Runner.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(run.RunId), "the container is removed after completion");
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
    public async Task ContainerCrash_IsExecutionFailed_WithNothingAssessed_AndRemoved()
    {
        await using var h = new Harness();
        h.Runner.OnRun = _ => new ContainerRunOutcome(107, "", "script exception at line 3 Authorization: Bearer abc.def", false, false, null, true);
        var run = await h.RunToEndAsync((await h.SeedAsync()).Id);
        run.State.Should().Be(PerformanceRunState.ExecutionFailed);
        run.StateReason.Should().Contain("exited with code 107");
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        run.ThresholdResults.Should().OnlyContain(t => t.Outcome == CheckOutcome.NotAssessed);
        run.Quality!.QualityPercent.Should().BeNull();
        run.ProviderDiagnostics.Should().NotContain("abc.def", "provider output is redacted");
        h.Runner.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(run.RunId));
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
    public async Task Timeout_IsTimedOut_NotAssessed_AndRemoved()
    {
        await using var h = new Harness();
        h.Runner.OnRun = _ => new ContainerRunOutcome(null, "", "", true, false, null, true);
        var run = await h.RunToEndAsync((await h.SeedAsync()).Id);
        run.State.Should().Be(PerformanceRunState.TimedOut);
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        Directory.Exists(h.Runner.LastWorkingDirectory).Should().BeFalse();
        h.Runner.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(run.RunId));
    }

    /// <summary>A runtime whose test container runs until cancelled.</summary>
    private sealed class SlowRuntime(FakeRuntime inner) : IContainerExecutionRuntime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }
        public List<string> Removed => inner.Removed;
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
            Started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new ContainerRunOutcome(0, "", "", false, false, null, true); }
            catch (OperationCanceledException) { WasCancelled = true; return new ContainerRunOutcome(null, "", "", false, true, null, true); }
            finally { inner.Removed.Add(spec.Name); }
        }
    }

    [Fact]
    public async Task Cancel_StopsAndRemovesTheContainer_AsCancelled_NotAQualityResult()
    {
        var slow = new SlowRuntime(new FakeRuntime());
        await using var h = new Harness(custom: slow);
        var d = await h.SeedAsync();
        await h.Registry.CheckReachabilityAsync(d);
        var started = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        await slow.Started.Task;
        var cancelling = await h.Execution.CancelAsync(started.Run!.RunId);
        cancelling!.State.Should().Be(PerformanceRunState.Cancelling);
        await h.Execution.LastExecution!;
        var run = (await h.Store().RunAsync(started.Run.RunId))!;
        run.State.Should().Be(PerformanceRunState.Cancelled);
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed);
        run.Metrics.Should().BeNull();
        slow.WasCancelled.Should().BeTrue();
        slow.Removed.Should().Contain(K6PerformanceTestProvider.ContainerName(run.RunId), "the container is removed after cancellation");
    }

    [Fact]
    public async Task OneActiveRunPerEnvironment()
    {
        var slow = new SlowRuntime(new FakeRuntime());
        await using var h = new Harness(custom: slow);
        var d = await h.SeedAsync();
        await h.Registry.CheckReachabilityAsync(d);
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
        h.Runner.OnRun = _ => new ContainerRunOutcome(107, "", "", false, false, null, true);
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

        h.Runner.RuntimeAvailability = ProviderAvailability.Unavailable;
        await h.Store().SaveDataProfileAsync("pay-qa", Data(), DateTimeOffset.UtcNow);
        var saved = ((await controller.Create("pay-qa", Definition(), CancellationToken.None)).Result as Microsoft.AspNetCore.Mvc.OkObjectResult)!.Value as PerformanceTestDefinition;
        var run = await controller.Run("pay-qa", new PerformanceRunRequest { DefinitionId = saved!.Id }, CancellationToken.None);
        var blocked = (run as Microsoft.AspNetCore.Mvc.UnprocessableEntityObjectResult)!.Value!;
        JsonSerializer.Serialize(blocked).Should().Contain("Podman is not installed");
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

    // ── Live Podman + k6 (gated) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A real ephemeral k6 container (2 VUs, ~10 s) against a throw-away busybox httpd container on a dedicated Podman network, reached by its
    /// container DNS name — no external load. Skipped with the reason when Podman or the pinned images are not available.
    /// </summary>
    [PodmanK6LiveFact]
    public async Task LivePodmanK6_TinyContainerTarget()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var network = $"birknext-pt-live-{id}";
        var target = $"birknext-pt-target-{id}";
        var runner = new SystemProcessRunner();
        async Task<ProcessOutcome> Podman(params string[] args) => await runner.RunAsync(new ProcessSpec("podman", args, Path.GetTempPath()), TimeSpan.FromMinutes(2), 64 * 1024, CancellationToken.None);
        try
        {
            (await Podman("network", "create", network)).ExitCode.Should().Be(0);
            (await Podman("run", "--detach", "--rm", "--name", target, "--network", network, "--label", "birknext.managed=true", "--label", "birknext.component=performance-test-live-target",
                "docker.io/library/busybox:latest", "httpd", "-f", "-p", "8080", "-h", "/etc")).ExitCode.Should().Be(0);
            var options = Options with { Container = Options.Container with { TargetNetworks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [target] = network } } };
            var runtime = new PodmanContainerExecutionRuntime(runner, options, NullLogger<PodmanContainerExecutionRuntime>.Instance);
            var provider = new K6PerformanceTestProvider(options, runtime, NullLogger<K6PerformanceTestProvider>.Instance);
            var d = Definition(x => x with
            {
                TargetOrigin = $"http://{target}:8080", EnvironmentType = "Test",
                Scenario = x.Scenario with { TestDataProfileId = null, Steps = [new HttpPerformanceStep { Name = "hostname", RelativePath = "/hostname", ExpectedStatusCodes = [200], ThinkTimeMs = 100 }] },
                Workload = new PerformanceWorkload { VirtualUsers = 2, WarmupSeconds = 0, RampUpSeconds = 1, SteadyStateSeconds = 10, RampDownSeconds = 0 },
            });
            var reach = await provider.CheckReachabilityAsync(d);
            reach.Reachable.Should().BeTrue(reach.Detail);
            var runId = Guid.NewGuid();
            var dir = Path.Combine(Path.GetTempPath(), "birknext-performance-live", runId.ToString("N"));
            var result = await provider.ExecuteAsync(new PerformanceProviderInput(runId, d, null, dir, TimeSpan.FromMinutes(3)), null, CancellationToken.None);
            result.State.Should().Be(PerformanceRunState.Completed, result.Reason + result.Diagnostics);
            result.Metrics!.RequestCount.Should().BeGreaterThan(0);
            result.Metrics.ErrorRatePercent.Should().Be(0);
            result.Metrics.Latency.P95Ms.Should().NotBeNull();
            result.ImageDigest.Should().StartWith("sha256:");
            Directory.Exists(dir).Should().BeFalse();
            (await Podman("container", "exists", K6PerformanceTestProvider.ContainerName(runId))).ExitCode.Should().NotBe(0, "the k6 container is removed after the run");
        }
        finally
        {
            await Podman("rm", "--force", "--ignore", target);
            await Podman("network", "rm", "--force", network);
        }
    }
}

/// <summary>Runs only when Podman is reachable and the pinned k6 and busybox images are present locally; otherwise skipped with the reason.</summary>
public sealed class PodmanK6LiveFactAttribute : FactAttribute
{
    public PodmanK6LiveFactAttribute()
    {
        static bool Ok(params string[] args)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("podman") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) info.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(info)!;
                return p.WaitForExit(20_000) && p.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }
        if (!Ok("info", "--format", "{{.Version.Version}}"))
            Skip = "Live Podman/k6 test: Podman is not installed or its machine is not running.";
        else if (!Ok("image", "exists", PerformanceContainerOptions.DefaultImage) || !Ok("image", "exists", "docker.io/library/busybox:latest"))
            Skip = $"Live Podman/k6 test: pull the pinned images first (podman pull {PerformanceContainerOptions.DefaultImage}; podman pull docker.io/library/busybox:latest).";
    }
}
