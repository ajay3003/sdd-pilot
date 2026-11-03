using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using static BirkNext.Api.Tests.Services.PerformanceTests.PerformanceTestReviewTests;
using Path = System.IO.Path;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Execution readiness and execution evidence: observed authentication + None is Blocked (never "explicitly unauthenticated") unless an
/// explicit, fingerprinted override covers it; the backend re-checks it at start against the persisted Endpoint Discovery history; container
/// running ≠ k6 started ≠ scenario initialised ≠ requests sent; zero ≠ unknown; the watchdog never fabricates or cancels on missing telemetry;
/// cancellation is graceful first, keeps partial results and diagnostics and records who stopped it. Fake runtime; nothing leaves the machine.
/// </summary>
public sealed class PerformanceExecutionEvidenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 43, 7, TimeSpan.Zero);
    private const string ObservedAuth = "Authenticated requests observed by Endpoint Discovery";

    private static PerformanceTestDefinition Observed(StepAuthenticationMode mode = StepAuthenticationMode.Inherit, string? reference = null) => Definition(d => d with
    {
        AuthenticationReference = mode == StepAuthenticationMode.Override ? null : reference,
        Scenario = d.Scenario with
        {
            Steps =
            [
                d.Scenario.Steps[0] with
                {
                    Authentication = mode, AuthenticationReference = mode == StepAuthenticationMode.Override ? reference : null,
                    Source = new ScenarioStepSource { OperationKey = "observed:GET /api/payments/{id}", Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly,
                        ObservedAuthentication = ObservedAuth, EvidenceIdentity = "GET /api/payments/{id}", ObservedLastSeen = T0 },
                },
            ],
        },
    });

    // ── Authentication readiness (one evaluator) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Observed_authentication_with_the_default_None_blocks_and_None_is_not_a_confirmation()
    {
        var d = Observed();
        var r = PerformanceScenarioRuntime.AuthenticationRequirements(d).Single();
        r.State.Should().Be(StepAuthenticationState.Missing);
        PerformanceScenarioRuntime.Readiness(d, 0).Status.Should().Be(StepStatus.NeedsConfiguration, "the step needs configuration");
        PerformanceScenarioRuntime.ScenarioReadiness(d).Reasons.Should().Contain(x => x.Contains("authentication required") && x.Contains("None is not a confirmation"));
        PerformanceTestSafety.Check(d, PerformanceTestReviewTests.Data(), new PerformanceTestOptions()).Should().Contain(i => i.Key == "authentication" && i.State == PerformanceReadinessState.NeedsAuthentication
            && i.Message.StartsWith("Run blocked — authentication required"));
    }

    [Fact]
    public void An_explicit_step_None_is_still_blocked_without_the_override()
    {
        var d = Observed(StepAuthenticationMode.None);
        PerformanceScenarioRuntime.AuthenticationRequirements(d).Single().State.Should().Be(StepAuthenticationState.Missing);
    }

    [Fact]
    public void A_configured_reference_satisfies_the_requirement_and_step_override_wins_over_the_scenario_default()
    {
        PerformanceScenarioRuntime.AuthenticationRequirements(Observed(reference: "te-auth")).Single().Should().Match<StepAuthenticationRequirement>(r => r.State == StepAuthenticationState.Configured && r.Effective == "te-auth");
        PerformanceScenarioRuntime.AuthenticationRequirements(Observed(StepAuthenticationMode.Override, "step-auth")).Single().Effective.Should().Be("step-auth");
        PerformanceScenarioRuntime.AuthenticationRequirements(Observed(StepAuthenticationMode.None, "te-auth")).Single().State.Should().Be(StepAuthenticationState.Missing,
            "a deliberate step-level None is never silently replaced by the inherited default");
    }

    [Fact]
    public void The_unauthenticated_override_is_explicit_scoped_and_invalidated_by_a_changed_scenario()
    {
        var d = Observed();
        var ack = PerformanceScenarioRuntime.AcknowledgeUnauthenticated(d, null, T0, actor: null);
        ack.Operations.Should().Equal("GET /api/payments/{id}");
        ack.AcknowledgedBy.Should().BeNull("an unknown actor is never guessed");
        var acknowledged = d with { UnauthenticatedOverride = ack };
        PerformanceScenarioRuntime.AuthenticationRequirements(acknowledged).Single().State.Should().Be(StepAuthenticationState.IntentionallyUnauthenticated);
        PerformanceTestSafety.Check(acknowledged, PerformanceTestReviewTests.Data(), new PerformanceTestOptions()).Should().NotContain(i => i.Key == "authentication");

        var changed = acknowledged with { Scenario = acknowledged.Scenario with { Steps = [acknowledged.Scenario.Steps[0] with { RelativePath = "/api/payments/{id}/history",
            Source = acknowledged.Scenario.Steps[0].Source! with { EvidenceIdentity = "GET /api/payments/{id}/history" } }] } };
        PerformanceScenarioRuntime.AuthenticationRequirements(changed).Single().State.Should().Be(StepAuthenticationState.OverrideOutdated);
        PerformanceTestSafety.Check(changed, PerformanceTestReviewTests.Data(), new PerformanceTestOptions()).Should().Contain(i => i.Key == "authentication" && i.Message.Contains("no longer matches"));
    }

    [Fact]
    public void Persisted_endpoint_evidence_adds_a_requirement_the_step_did_not_record_and_changed_evidence_invalidates_the_override()
    {
        var plain = Definition();   // a step without any recorded authentication (e.g. created before this evidence existed)
        var observed = new HashSet<string>(StringComparer.Ordinal) { "GET /api/payments/{id}" };
        PerformanceScenarioRuntime.AuthenticationRequirements(plain, observed).Single().State.Should().Be(StepAuthenticationState.Missing);
        PerformanceScenarioRuntime.AuthenticationRequirements(plain).Single().State.Should().Be(StepAuthenticationState.NotRequired);

        var ack = plain with { UnauthenticatedOverride = PerformanceScenarioRuntime.AcknowledgeUnauthenticated(plain, observed, T0, null) };
        PerformanceScenarioRuntime.AuthenticationRequirements(ack, observed).Single().State.Should().Be(StepAuthenticationState.IntentionallyUnauthenticated);
        var withDeclared = ack with { Scenario = ack.Scenario with { Steps = [ack.Scenario.Steps[0] with { Source = new ScenarioStepSource { ObservedAuthentication = "Declared authentication (Bearer)" } }] } };
        PerformanceScenarioRuntime.AuthenticationRequirements(withDeclared, observed).Single().State.Should().Be(StepAuthenticationState.OverrideOutdated,
            "a materially changed requirement needs a new decision");
    }

    // ── Backend readiness + start (persisted Endpoint Discovery history) ────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public FakeRuntime Runner { get; }
        public ServiceProvider Services { get; }
        public PerformanceTestExecutionService Execution => Services.GetRequiredService<PerformanceTestExecutionService>();
        public PerformanceTestReadinessService Readiness => Services.GetRequiredService<PerformanceTestReadinessService>();

        public Harness(IContainerExecutionRuntime? custom = null, FakeRuntime? runtime = null, PerformanceTestOptions? options = null, ILocalHttpsProxyService? proxy = null)
        {
            Runner = runtime ?? new FakeRuntime();
            var name = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton(options ?? new PerformanceTestOptions());
            services.AddSingleton(custom ?? Runner);
            services.AddSingleton<IPerformanceTestProvider, K6PerformanceTestProvider>();
            services.AddSingleton<PerformanceTestProviderRegistry>();
            services.AddSingleton<PerformanceTestReadinessService>();
            services.AddSingleton<PerformanceTestExecutionService>();
            services.AddScoped<PerformanceTestStore>();
            services.AddScoped<IqrSourceStore>();
            services.AddSingleton(TimeProvider.System);
            services.AddScoped<EndpointObservationHistoryService>();
            if (proxy is not null) services.AddSingleton(proxy);
            Services = services.BuildServiceProvider();
        }

        public PerformanceTestStore Store() => Services.CreateScope().ServiceProvider.GetRequiredService<PerformanceTestStore>();
        public PerformanceTestProviderRegistry Registry => Services.GetRequiredService<PerformanceTestProviderRegistry>();

        public async Task<PerformanceTestDefinition> SeedAsync(PerformanceTestDefinition d)
        {
            var store = Store();
            await store.SaveDataProfileAsync("pay-qa", PerformanceTestReviewTests.Data(), DateTimeOffset.UtcNow);
            var saved = await store.SaveDefinitionAsync("pay-qa", d, DateTimeOffset.UtcNow);
            await Registry.CheckReachabilityAsync(saved);
            return saved;
        }

        public async Task RecordDiscoveryAsync(bool auth, DateTimeOffset seen) =>
            await Services.CreateScope().ServiceProvider.GetRequiredService<EndpointObservationHistoryService>().RecordRunAsync("pay-qa", "paymenthub-qa.example.test", $"run-{Guid.NewGuid():N}", seen, seen,
                [new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.Rest, Host = "paymenthub-qa.example.test", Port = 443, Method = "GET", Path = "/api/payments/12345678", Count = 3,
                    LastStatus = 200, AuthObserved = auth, FirstObservedAt = seen, LastObservedAt = seen, Provenance = RequestProvenance.ApplicationTraffic }], CancellationToken.None);

        public async Task<PerformanceTestRun> StartAndWaitAsync(string definitionId)
        {
            var started = await Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = definitionId });
            started.Run.Should().NotBeNull(string.Join(" ", started.Blockers) + started.Conflict);
            await Execution.LastExecution!;
            return (await Store().RunAsync(started.Run!.RunId))!;
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    [Fact]
    public async Task The_API_cannot_start_a_run_blocked_by_observed_authentication_and_unrelated_checks_stay_green()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition());
        await h.RecordDiscoveryAsync(auth: true, DateTimeOffset.UtcNow);

        var readiness = await h.Readiness.EvaluateAsync(d, PerformanceTestReviewTests.Data(), false);
        readiness.Ready.Should().BeFalse();
        readiness.Items.Single(i => i.Key == "authentication").Should().Match<PerformanceReadinessItem>(i => i.State == PerformanceReadinessState.NeedsAuthentication && i.Blocking
            && i.Detail.Contains("Run blocked — authentication required") && i.Detail.Contains("GET /api/payments/{paymentId}"));
        readiness.Items.Single(i => i.Key == "network").State.Should().Be(PerformanceReadinessState.Ready, "an authentication blocker never turns another dimension red");
        readiness.Items.Single(i => i.Key == "tls").State.Should().Be(PerformanceReadinessState.Ready);

        var started = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        started.Run.Should().BeNull("the backend re-evaluates readiness at start; a direct API call cannot bypass it");
        started.Blockers.Should().Contain(b => b.Contains("authentication required"));
        h.Runner.Runs.Should().NotContain(s => s.Command.Last() == "/birknext/in/test.js");
    }

    [Fact]
    public async Task An_acknowledged_override_lets_the_run_start_and_is_shown_in_readiness()
    {
        await using var h = new Harness();
        var observed = new HashSet<string>(StringComparer.Ordinal) { "GET /api/payments/{id}" };
        var plain = Definition();
        var d = await h.SeedAsync(plain with { UnauthenticatedOverride = PerformanceScenarioRuntime.AcknowledgeUnauthenticated(plain, observed, T0, null) });
        await h.RecordDiscoveryAsync(auth: true, DateTimeOffset.UtcNow);
        var readiness = await h.Readiness.EvaluateAsync(d, PerformanceTestReviewTests.Data(), false);
        readiness.Ready.Should().BeTrue(string.Join(" | ", readiness.Blockers));
        readiness.Items.Single(i => i.Key == "authentication").Detail.Should().StartWith("Intentionally unauthenticated — acknowledged 2026-11-02 12:43 UTC").And.Contain("401/403");
    }

    [Fact]
    public async Task Newer_endpoint_evidence_is_reported_as_a_warning_not_a_block()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition(x => x with { Scenario = x.Scenario with { Steps = [x.Scenario.Steps[0] with
        {
            Source = new ScenarioStepSource { Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly, EvidenceIdentity = "GET /api/payments/{id}", ObservedLastSeen = T0 },
        }] } }));
        await h.RecordDiscoveryAsync(auth: false, T0.AddMinutes(28));
        var readiness = await h.Readiness.EvaluateAsync(d, PerformanceTestReviewTests.Data(), false);
        readiness.Items.Single(i => i.Key == "endpoint-evidence").Should().Match<PerformanceReadinessItem>(i => !i.Blocking && i.State == PerformanceReadinessState.Optional
            && i.Status == "Newer observation available" && i.Detail.Contains("Newer observation available · last observed 13:11 UTC")
            && i.Detail.Contains("Reviewed against discovery 12:43 UTC · Latest discovery 13:11 UTC") && i.Detail.Contains("never overwritten silently"));
        readiness.Ready.Should().BeTrue(string.Join(" | ", readiness.Blockers));
    }

    private static PerformanceTestDefinition Observed(DateTimeOffset reviewed, Func<PerformanceTestDefinition, PerformanceTestDefinition>? change = null) => Definition(x =>
    {
        var d = x with { Scenario = x.Scenario with { Steps = [x.Scenario.Steps[0] with
        {
            Source = new ScenarioStepSource { Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly, EvidenceIdentity = "GET /api/payments/{id}", ObservedCount = 2, ObservedLastSeen = reviewed },
        }] } };
        return change is null ? d : change(d);
    });

    [Fact]
    public async Task Endpoint_evidence_reads_observed_in_readiness_in_the_step_cards_own_words()
    {
        await using var h = new Harness();
        var seen = DateTimeOffset.UtcNow.AddMinutes(-5);
        var d = await h.SeedAsync(Observed(seen));
        await h.RecordDiscoveryAsync(auth: false, seen);

        var item = (await h.Readiness.EvaluateAsync(d, PerformanceTestReviewTests.Data(), false)).Items.Single(i => i.Key == "endpoint-evidence");
        item.State.Should().Be(PerformanceReadinessState.Ready);
        item.Status.Should().Be("Observed");
        item.Detail.Should().StartWith($"Observed 3× · last observed {seen:HH:mm} UTC · Pinned.").And.Contain("No changes");
    }

    [Fact]
    public async Task A_step_created_from_a_capture_still_running_is_observed_in_readiness_not_missing_from_history()
    {
        // The Scenario tab reads the history view with the running capture merged in; readiness used to read only the persisted runs and so
        // reported "1 not in Endpoint Discovery history" for the same step the step card showed as "Observed 2×".
        var seen = DateTimeOffset.UtcNow.AddMinutes(-2);
        var proxy = new Mock<ILocalHttpsProxyService>();
        proxy.Setup(p => p.GetRuntimeAsync()).ReturnsAsync(new LocalHttpsProxyStatus
        {
            ProfileId = "pay-qa", RuntimeId = "runtime-1", State = LocalHttpsProxyState.Listening,
            ObservedNetworkEndpoints = [new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.Rest, Host = "paymenthub-qa.example.test", Port = 443, Method = "GET",
                Path = "/api/payments/12345678", Count = 2, LastStatus = 200, FirstObservedAt = seen, LastObservedAt = seen, Provenance = RequestProvenance.ApplicationTraffic }],
        });
        await using var h = new Harness(proxy: proxy.Object);
        var d = await h.SeedAsync(Observed(seen));

        var item = (await h.Readiness.EvaluateAsync(d, PerformanceTestReviewTests.Data(), false)).Items.Single(i => i.Key == "endpoint-evidence");
        item.Status.Should().Be("Observed");
        item.Detail.Should().StartWith("Observed 2×").And.NotContain("Not in Endpoint Discovery history");

        await using var stopped = new Harness();
        var persistedOnly = (await stopped.Readiness.EvaluateAsync(await stopped.SeedAsync(Observed(seen)), PerformanceTestReviewTests.Data(), false)).Items.Single(i => i.Key == "endpoint-evidence");
        persistedOnly.Status.Should().Be("Not in history", "with no capture running and nothing recorded, the step genuinely is not in the history");
    }

    [Fact]
    public async Task Configured_Target_Environment_authentication_blocks_execution_with_the_step_cards_reason_and_other_checks_stay_green()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition(x => x with { AuthenticationReference = PerformanceScenarioRuntime.TargetEnvironmentAuthentication }));

        var readiness = await h.Readiness.EvaluateAsync(d, PerformanceTestReviewTests.Data(), false);
        readiness.Items.Where(i => i.Blocking).Select(i => i.Key).Should().Equal(["authentication"], "only the execution credential is missing");
        readiness.Items.Single(i => i.Key == "authentication").Detail.Should().Be(PerformanceScenarioRuntime.NoExecutionCredential)
            .And.Contain("configured for attended/browser use").And.Contain("non-interactive credential is available to k6");
        var dims = PerformanceScenarioRuntime.Dimensions(d, 0);
        (dims.Execution.State, dims.Credential!.PerformanceCompatible, dims.Credential.Interactive)
            .Should().Be((StepDimensionState.Blocked, false, true), "the step card and Readiness apply the same rule");
    }

    [Fact]
    public void Target_reachability_names_the_base_target_and_never_claims_a_scenario_endpoint_was_verified()
    {
        var reach = new PerformanceReachability { TargetOrigin = "https://paymenthub-qa.example.test", State = PerformanceProbeStates.Reachable, Reachable = true, HttpStatus = 200, CheckedAt = DateTimeOffset.UtcNow };
        var item = PerformanceTestReadinessService.NetworkItem(true, reach, fresh: true, sameConfiguration: true);
        item.Label.Should().Be("Target reachability");
        item.Detail.Should().StartWith("Base target returned HTTP 200 to the k6 container.").And.Contain("Scenario endpoints are not verified");
    }

    // ── k6 telemetry stream ────────────────────────────────────────────────────────────────────────────────────────

    private static string Point(string metric, DateTimeOffset at, double value = 1) =>
        JsonSerializer.Serialize(new { type = "Point", metric, data = new { time = at.ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "12+00:00", value, tags = new { url = "https://x/api?token=secret" } } });

    [Fact]
    public void The_telemetry_reader_counts_k6_points_incrementally_and_unopened_is_not_zero()
    {
        var dir = Directory.CreateTempSubdirectory("k6tel");
        try
        {
            var path = Path.Combine(dir.FullName, "metrics.ndjson");
            var reader = new K6JsonTelemetryReader(path);
            reader.Read().Should().Be(PerformanceLiveTelemetry.NotOpened, "a stream that does not exist yet is 'not received', never 0 requests");

            File.WriteAllText(path, "{\"type\":\"Metric\",\"metric\":\"http_reqs\",\"data\":{}}\n" + Point("vus", T0) + "\n");
            reader.Read().Should().Match<PerformanceLiveTelemetry>(t => t.StreamOpened && t.Requests == 0 && t.LastHeartbeatAt != null, "an opened stream with no request is a real 0");

            File.AppendAllText(path, Point("http_reqs", T0.AddSeconds(1)) + "\n" + Point("http_reqs", T0.AddSeconds(2)) + "\n" + Point("http_req_failed", T0.AddSeconds(2), 1) + "\n"
                + Point("iterations", T0.AddSeconds(3)) + "\n" + Point("http_reqs", T0.AddSeconds(4))[..20]);   // partial last line
            var t = reader.Read();
            (t.Requests, t.FailedRequests, t.Iterations).Should().Be((2, 1, 1));
            t.FirstRequestAt.Should().BeCloseTo(T0.AddSeconds(1), TimeSpan.FromMilliseconds(1), "nanosecond timestamps are parsed");
            File.AppendAllText(path, Point("http_reqs", T0.AddSeconds(4))[20..] + "\n");
            reader.Read().Requests.Should().Be(3, "a line split across reads is counted once");
        }
        finally { dir.Delete(true); }
    }

    // ── Live monitor + watchdog (pure rules) ───────────────────────────────────────────────────────────────────────

    private static readonly PerformanceTestOptions Watch = new() { StartupTimeoutSeconds = 60, HeartbeatStaleSeconds = 30, WatchdogTerminateSeconds = 120 };
    private static PerformanceExecutionEvidence Fresh => new() { Stage = PerformanceRunStage.StartingContainer, ExpectedWorkloadEndAt = T0.AddMinutes(3), Telemetry = PerformanceTelemetryState.NotYetReceived };
    private static PerformanceLiveTelemetry Opened(int requests = 0, DateTimeOffset? beat = null) =>
        PerformanceLiveTelemetry.NotOpened with { StreamOpened = true, StreamOpenedAt = T0.AddSeconds(5), Requests = requests, FirstRequestAt = requests > 0 ? T0.AddSeconds(6) : null, LastHeartbeatAt = beat ?? T0.AddSeconds(6) };

    [Fact]
    public void A_running_container_is_not_load_and_a_started_k6_is_not_traffic()
    {
        var (running, _) = PerformanceLiveMonitor.Apply(Fresh, T0.AddSeconds(2), T0, new PerformanceContainerObservation(true, true, "abc123"), true, PerformanceLiveTelemetry.NotOpened, Watch);
        running.Container.Should().Be(PerformanceObservedState.Running);
        running.RequestsObserved.Should().BeNull("no request telemetry yet is unknown, not 0");
        running.Telemetry.Should().Be(PerformanceTelemetryState.NotYetReceived);
        running.Stage.Should().Be(PerformanceRunStage.StartingK6);

        var (initialised, _) = PerformanceLiveMonitor.Apply(running, T0.AddSeconds(7), T0, new(true, true, "abc123"), true, Opened(), Watch);
        initialised.ScriptInitializedAt.Should().NotBeNull();
        initialised.Stage.Should().Be(PerformanceRunStage.LoadingScenario);
        initialised.RequestsObserved.Should().Be(0);
        initialised.FirstRequestAt.Should().BeNull();

        var (flowing, _) = PerformanceLiveMonitor.Apply(initialised, T0.AddSeconds(9), T0, new(true, true, "abc123"), true, Opened(42), Watch);
        flowing.Stage.Should().Be(PerformanceRunStage.Running);
        flowing.Timeline.Select(e => e.Event).Should().Contain("First request observed");

        var (regress, _) = PerformanceLiveMonitor.Apply(flowing, T0.AddSeconds(11), T0, new(true, true, "abc123"), true, PerformanceLiveTelemetry.NotOpened, Watch);
        regress.Stage.Should().Be(PerformanceRunStage.Running, "an out-of-order observation never moves the stage back");
    }

    [Fact]
    public void Heartbeat_silence_first_appears_stuck_then_the_watchdog_terminates_with_its_own_reason()
    {
        var (stale, stop1) = PerformanceLiveMonitor.Apply(Fresh, T0.AddSeconds(50), T0, new(true, true, "c"), true, Opened(10, T0.AddSeconds(10)), Watch);
        stale.AppearsStuck.Should().BeTrue();
        stop1.Should().BeNull("appearing stuck is shown, not acted on");
        var (_, stop2) = PerformanceLiveMonitor.Apply(stale, T0.AddSeconds(140), T0, new(true, true, "c"), true, Opened(10, T0.AddSeconds(10)), Watch);
        stop2!.Reason.Should().Be(PerformanceStopReason.Watchdog);
    }

    [Fact]
    public void Without_live_telemetry_a_long_run_is_never_called_stuck_and_finalizing_is_not_stuck()
    {
        var (noTelemetry, stop) = PerformanceLiveMonitor.Apply(Fresh, T0.AddMinutes(10), T0, new(true, true, "c"), true, null, Watch);
        stop.Should().BeNull();
        noTelemetry.AppearsStuck.Should().BeFalse();
        noTelemetry.Stage.Should().Be(PerformanceRunStage.Finalizing, "after the configured workload end a running container is finalizing");

        var (finalizing, stop2) = PerformanceLiveMonitor.Apply(Fresh with { ScriptInitializedAt = T0.AddSeconds(5) }, T0.AddMinutes(4), T0, new(true, true, "c"), true, Opened(10, T0.AddSeconds(170)), Watch);
        stop2.Should().BeNull("heartbeat silence after the workload end is expected");
        finalizing.AppearsStuck.Should().BeFalse();
    }

    [Fact]
    public void A_scenario_that_never_initialises_hits_the_startup_timeout()
    {
        var (_, stop) = PerformanceLiveMonitor.Apply(Fresh, T0.AddSeconds(61), T0, new(true, true, "c"), true, PerformanceLiveTelemetry.NotOpened, Watch);
        stop!.Reason.Should().Be(PerformanceStopReason.StartupTimeout);
    }

    // ── End to end through the k6 provider and the fake runtime ─────────────────────────────────────────────────────

    private static ContainerRunOutcome WriteOutputs(ContainerRunSpec spec, int requests, bool summary, int exit = 0, string stderr = "")
    {
        var output = spec.Mounts.Single(m => m.ContainerPath == "/birknext/out").HostPath;
        var lines = Enumerable.Range(0, requests).Select(i => Point("http_reqs", T0.AddSeconds(i + 1))).Prepend(Point("vus", T0)).Append(Point("iterations", T0.AddSeconds(requests + 1)));
        File.WriteAllText(Path.Combine(output, K6PerformanceTestProvider.TelemetryFile), string.Join("\n", lines) + "\n");
        if (summary) File.WriteAllText(Path.Combine(output, "summary.json"), PerformanceTestReviewTests.Summary(count: requests));
        return new ContainerRunOutcome(exit, "", stderr, false, false, null, true);
    }

    [Fact]
    public async Task A_completed_run_records_the_k6_command_telemetry_artifact_and_cleanup_as_evidence()
    {
        await using var h = new Harness();
        h.Runner.OnRun = spec => WriteOutputs(spec, 7, summary: true);
        var run = await h.StartAndWaitAsync((await h.SeedAsync(Definition())).Id);

        h.Runner.Runs.Single(s => s.Command.Last() == "/birknext/in/test.js").Command.Should().ContainInOrder("--out", "json=/birknext/out/metrics.ndjson");
        run.State.Should().Be(PerformanceRunState.Completed);
        var e = run.ExecutionEvidence!;
        e.Should().Match<PerformanceExecutionEvidence>(x => x.Stage == PerformanceRunStage.Finished && x.Telemetry == PerformanceTelemetryState.Collected && x.RequestsObserved == 7
            && x.IterationsObserved == 1 && x.ExitCode == 0 && x.ResultArtifact == PerformanceResultArtifactState.Parsed && x.ContainerRemoved == true && x.StopReason == PerformanceStopReason.None);
        e.Timeline.Select(t => t.Event).Should().ContainInOrder("Queued", "Preparing", "Starting container", "Collecting results", "Finished · Completed");
        PerformanceExecutionAssessment.Level(run).Should().Be(PerformanceExecutionLevel.FinalResults);
        PerformanceExecutionAssessment.LoadConfirmed(run).Should().BeTrue();
        Directory.Exists(h.Runner.LastWorkingDirectory).Should().BeFalse("the run directory is removed only after evidence was collected");
    }

    [Fact]
    public async Task Zero_requests_recorded_is_zero_and_a_missing_artifact_is_named()
    {
        await using var h = new Harness();
        h.Runner.OnRun = spec => WriteOutputs(spec, 0, summary: false);
        var run = await h.StartAndWaitAsync((await h.SeedAsync(Definition())).Id);
        run.State.Should().Be(PerformanceRunState.ExecutionFailed);
        run.ExecutionEvidence!.ResultArtifact.Should().Be(PerformanceResultArtifactState.Missing);
        PerformanceExecutionAssessment.RequestsLabel(run).Should().Be("0", "k6 opened its stream and recorded no request: a real zero");
        PerformanceExecutionAssessment.LoadConfirmed(run).Should().BeFalse();
    }

    [Fact]
    public async Task An_unexpected_exit_is_classified_and_diagnostics_are_redacted()
    {
        await using var h = new Harness();
        h.Runner.OnRun = spec => WriteOutputs(spec, 2, summary: false, exit: 107, stderr: "GoError: request failed Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl");
        var run = await h.StartAndWaitAsync((await h.SeedAsync(Definition())).Id);
        run.ExecutionEvidence!.StopReason.Should().Be(PerformanceStopReason.UnexpectedProcessExit);
        run.ExecutionEvidence.ExitCode.Should().Be(107);
        run.StateReason.Should().StartWith("Unexpected process exit");
        run.ExecutionEvidence.RequestsObserved.Should().Be(2, "requests recorded before the crash stay attributed to this run");
        JsonSerializer.Serialize(run).Should().NotContain("eyJhbGciOiJIUzI1NiJ9", "tokens never reach diagnostics");
    }

    /// <summary>A runtime that stops gracefully: k6 then writes a partial summary and exits normally (as SIGTERM makes k6 do).</summary>
    private sealed class GracefulRuntime(FakeRuntime inner, int requestsBeforeStop) : IContainerExecutionRuntime
    {
        private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];
        public string RuntimeId => inner.RuntimeId;
        public string DisplayName => inner.DisplayName;
        public string HostGatewayAlias => inner.HostGatewayAlias;
        public Task<PerformanceRuntimeStatus> StatusAsync(CancellationToken ct = default) => inner.StatusAsync(ct);
        public Task<ContainerImageStatus> ImageAsync(string image, CancellationToken ct = default) => inner.ImageAsync(image, ct);
        public Task<ContainerImageStatus> PullAsync(string image, CancellationToken ct = default) => inner.PullAsync(image, ct);
        public Task<bool> RemoveAsync(string name, CancellationToken ct = default) { Events.Add("remove"); return Task.FromResult(true); }
        public Task<IReadOnlyList<string>> ListManagedAsync(string component, CancellationToken ct = default) => inner.ListManagedAsync(component, ct);
        public Task<IReadOnlySet<string>> ControllersAsync(CancellationToken ct = default) => inner.ControllersAsync(ct);
        public Task<ContainerInstanceInfo?> InspectAsync(string name, CancellationToken ct = default) => inner.InspectAsync(name, ct);
        public Task<ContainerStatsSnapshot> StatsAsync(string name, CancellationToken ct = default) => inner.StatsAsync(name, ct);
        public Task<bool> StopAsync(string name, int graceSeconds, CancellationToken ct = default) { Events.Add("stop"); _stop.TrySetResult(); return Task.FromResult(true); }
        public async Task<ContainerRunOutcome> RunAsync(ContainerRunSpec spec, CancellationToken ct)
        {
            if (spec.Command[0] == "version" || spec.Command[^1].EndsWith("probe.js", StringComparison.Ordinal)) return await inner.RunAsync(spec, ct);
            var output = spec.Mounts.Single(m => m.ContainerPath == "/birknext/out").HostPath;
            var telemetry = Path.Combine(output, K6PerformanceTestProvider.TelemetryFile);
            if (requestsBeforeStop >= 0) File.WriteAllText(telemetry, string.Join("\n", Enumerable.Range(0, requestsBeforeStop).Select(i => Point("http_reqs", T0.AddSeconds(i))).Prepend(Point("vus", T0))) + "\n");
            Started.TrySetResult();
            try
            {
                await _stop.Task.WaitAsync(ct);
                if (requestsBeforeStop > 0) File.WriteAllText(Path.Combine(output, "summary.json"), PerformanceTestReviewTests.Summary(count: requestsBeforeStop));
                Events.Add("exited");
                return new ContainerRunOutcome(0, "", "", false, false, null, true);
            }
            finally { Events.Add("remove"); }   // as the Podman runtime: the container is removed after the client exited
        }
    }

    [Fact]
    public async Task User_cancellation_is_graceful_keeps_partial_metrics_and_evidence_and_says_who_stopped_it()
    {
        var runtime = new GracefulRuntime(new FakeRuntime(), requestsBeforeStop: 12);
        await using var h = new Harness(custom: runtime);
        var d = await h.SeedAsync(Definition());
        var started = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        await runtime.Started.Task;
        (await h.Execution.CancelAsync(started.Run!.RunId))!.State.Should().Be(PerformanceRunState.Cancelling);
        await h.Execution.LastExecution!;
        var run = (await h.Store().RunAsync(started.Run.RunId))!;

        runtime.Events.Should().ContainInOrder("stop", "exited", "remove");
        run.State.Should().Be(PerformanceRunState.Cancelled);
        run.StateReason.Should().StartWith("Cancelled by user after ").And.Contain("asked to stop gracefully").And.Contain("partial end-of-test summary");
        run.MetricsPartial.Should().BeTrue();
        run.Verdict.Should().Be(PerformanceQualityVerdict.NotAssessed, "a cancelled run has no pass/fail conclusion");
        run.ExecutionEvidence!.Should().Match<PerformanceExecutionEvidence>(e => e.StopReason == PerformanceStopReason.CancelledByUser && e.RequestsObserved == 12 && e.ContainerRemoved == true);
        PerformanceExecutionAssessment.Level(run).Should().Be(PerformanceExecutionLevel.PartialResults);
        PerformanceExecutionAssessment.Milestones(run).Single(m => m.Key == "metrics").Status.Should().Be("Partial");
    }

    [Fact]
    public async Task Cancelling_before_the_first_request_reports_insufficient_execution_evidence()
    {
        var runtime = new GracefulRuntime(new FakeRuntime(), requestsBeforeStop: -1);
        await using var h = new Harness(custom: runtime);
        var d = await h.SeedAsync(Definition());
        var started = await h.Execution.StartAsync("pay-qa", new PerformanceRunRequest { DefinitionId = d.Id });
        await runtime.Started.Task;
        await h.Execution.CancelAsync(started.Run!.RunId);
        await h.Execution.LastExecution!;
        var run = (await h.Store().RunAsync(started.Run.RunId))!;
        run.Metrics.Should().BeNull();
        PerformanceExecutionAssessment.LoadConfirmed(run).Should().BeFalse();
        PerformanceExecutionAssessment.RequestsLabel(run).Should().StartWith("Unknown", "no stream was ever opened: unknown, never 0");
        PerformanceExecutionAssessment.Summary(run).Should().NotContain("HTTP traffic observed");
    }

    [Fact]
    public async Task A_run_orphaned_by_a_restart_is_interrupted_when_polled_never_left_running()
    {
        await using var h = new Harness();
        var d = await h.SeedAsync(Definition());
        var orphan = new PerformanceTestRun
        {
            EnvironmentId = "pay-qa", DefinitionId = d.Id, DefinitionSnapshot = d, ProviderId = d.ProviderId, TargetOrigin = d.TargetOrigin, CreatedAt = T0, StartedAt = T0,
            State = PerformanceRunState.Running, ExecutionEvidence = new PerformanceExecutionEvidence { Stage = PerformanceRunStage.Running, RequestsObserved = 5, Telemetry = PerformanceTelemetryState.Receiving },
        };
        await h.Store().SaveRunAsync(orphan);
        var polled = (await h.Execution.CurrentRunAsync(h.Store(), orphan.RunId))!;
        polled.State.Should().Be(PerformanceRunState.ExecutionFailed);
        polled.StateReason.Should().StartWith("Interrupted");
        polled.ExecutionEvidence!.Should().Match<PerformanceExecutionEvidence>(e => e.StopReason == PerformanceStopReason.ApplicationShutdown && e.RequestsObserved == 5
            && e.Telemetry == PerformanceTelemetryState.Unavailable, "what was recorded before is kept; nothing after it is invented");
        await FluentActions.Awaiting(() => h.Store().SaveRunAsync(polled with { State = PerformanceRunState.Completed })).Should().ThrowAsync<InvalidOperationException>("a late provider callback can never overwrite a terminal outcome");
    }
}
