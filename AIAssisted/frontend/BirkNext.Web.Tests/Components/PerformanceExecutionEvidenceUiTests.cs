using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Execution trust in the UI: Run shows the executable summary and blocks observed authentication + None with an explicit, recorded alternative;
/// progress says what was observed (container ≠ k6 ≠ requests; unknown ≠ 0; stuck is shown); Results never says "no metrics" while running,
/// names the stop cause, and says when load generation could not be confirmed. Persisted evidence only; generic fixtures.
/// </summary>
public sealed class PerformanceExecutionEvidenceUiTests : BunitContext
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 43, 7, TimeSpan.Zero);

    private static PerformanceTestDefinition Definition(ScenarioStepSource? source = null) => new()
    {
        Id = "def-1", EnvironmentId = "dev", Name = "Barn lookup", EnvironmentName = "Development", EnvironmentType = "Development", TargetOrigin = "https://app.example.test",
        Version = 3,
        Scenario = new PerformanceScenario { Name = "Main", Steps = [new HttpPerformanceStep { Name = "barn", Method = "GET", RelativePath = "/api/autorisasjon/v1/brukere/{brukerId}/barn", ExpectedStatusCodes = [200], Source = source }] },
        Workload = new PerformanceWorkload { Purpose = WorkloadPurpose.Baseline, Mode = WorkloadMode.VirtualUsers, VirtualUsers = 5, WarmupSeconds = 15, RampUpSeconds = 30, SteadyStateSeconds = 120, RampDownSeconds = 30 },
    };

    private static ScenarioStepSource Discovered(string? auth = "Authenticated requests observed by Endpoint Discovery") => new()
    {
        OperationKey = "observed:GET /api/autorisasjon/v1/brukere/{id}/barn", Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly,
        ObservedAuthentication = auth, EvidenceIdentity = "GET /api/autorisasjon/v1/brukere/{id}/barn", ObservedLastSeen = T0,
    };

    private static EndpointObservationHistoryView History(DateTimeOffset lastSeen, bool auth = true) => new("dev", "app.example.test", true,
        new EndpointDiscoveryRunSummary("run-2", "dev", "app.example.test", lastSeen, lastSeen, EndpointDiscoveryRunSummary.Completed, 1, 0, 1, 0), null, [],
        [new ObservedOperationHistory { Identity = "GET /api/autorisasjon/v1/brukere/{id}/barn", Host = "app.example.test", Method = "GET", Path = "/api/autorisasjon/v1/brukere/{id}/barn",
            AuthObserved = auth, LastObservedAt = lastSeen, FirstObservedAt = T0, LastRunId = "run-2", State = ObservedOperationState.ObservedAgain }]);

    private static PerformanceTestRun Run(PerformanceRunState state, PerformanceExecutionEvidence? evidence, PerformanceMetrics? metrics = null) => new()
    {
        RunId = Guid.Parse("22222222-2222-3333-4444-555555555555"), DefinitionId = "def-1", DefinitionSnapshot = Definition(), State = state, TargetOrigin = "https://app.example.test",
        ProviderId = PerformanceProviderIds.K6, StartedAt = T0, FinishedAt = state is PerformanceRunState.Running ? null : T0.AddSeconds(95), ExecutionEvidence = evidence, Metrics = metrics,
    };

    // ── Execution summary + authentication decision ────────────────────────────────────────────────────────────────

    [Fact]
    public void Observed_authentication_with_None_blocks_and_offers_configure_or_an_explicit_recorded_decision()
    {
        UnauthenticatedExecutionOverride? recorded = null;
        var cut = Render<PerformanceExecutionSummary>(p => p.Add(x => x.Definition, Definition(Discovered())).Add(x => x.History, History(T0))
            .Add(x => x.Readiness, new PerformanceTestReadiness { EstimatedMaxRequests = 1350 })
            .Add(x => x.OnAcknowledgeUnauthenticated, (UnauthenticatedExecutionOverride o) => recorded = o));

        cut.Find("[data-testid=pt-summary-auth]").TextContent.Should().StartWith("Missing — Blocked");
        var blocked = cut.Find("[data-testid=pt-auth-blocked]");
        blocked.TextContent.Should().Contain("Run blocked — authentication required").And.Contain("GET /api/autorisasjon/v1/brukere/{brukerId}/barn")
            .And.Contain("no valid authentication configuration is available for this operation");
        cut.Find("[data-testid=pt-summary-estimate]").TextContent.Should().Contain("~1,350").And.Contain("estimate").And.Contain("not measured traffic");

        cut.Find("[data-testid=pt-auth-run-without]").Click();
        cut.Find("[data-testid=pt-auth-confirm]").TextContent.Should().Contain(UnauthenticatedExecutionOverride.Statement);
        recorded.Should().BeNull("nothing is recorded before the explicit confirmation");
        cut.Find("[data-testid=pt-auth-confirm-yes]").Click();
        recorded!.Operations.Should().Equal("GET /api/autorisasjon/v1/brukere/{id}/barn");
        recorded.EvidenceFingerprint.Should().NotBeEmpty();
    }

    [Fact]
    public void An_acknowledged_decision_is_shown_prominently_and_does_not_block()
    {
        var d = Definition(Discovered());
        d = d with { UnauthenticatedOverride = PerformanceScenarioRuntime.AcknowledgeUnauthenticated(d, null, T0, null) };
        var cut = Render<PerformanceExecutionSummary>(p => p.Add(x => x.Definition, d).Add(x => x.History, History(T0)));
        cut.FindAll("[data-testid=pt-auth-blocked]").Should().BeEmpty();
        cut.Find("[data-testid=pt-auth-intentional]").TextContent.Should().Contain("Intentionally unauthenticated").And.Contain("2026-11-02 12:43 UTC").And.Contain("401/403");
    }

    [Fact]
    public void Newer_endpoint_evidence_is_reported_and_manual_steps_claim_no_provenance()
    {
        var cut = Render<PerformanceExecutionSummary>(p => p.Add(x => x.Definition, Definition(Discovered(auth: null))).Add(x => x.History, History(T0.AddMinutes(28), auth: false)));
        cut.Find("[data-testid=pt-endpoint-newer]").TextContent.Should().Contain("Newer endpoint evidence available").And.Contain("never overwritten silently");
        cut.Find("[data-testid=pt-endpoint-step]").GetAttribute("data-state").Should().Be(nameof(PerformanceEndpointEvidenceState.NewerAvailable));

        var manual = Render<PerformanceExecutionSummary>(p => p.Add(x => x.Definition, Definition()).Add(x => x.History, History(T0, auth: false)));
        manual.Find("[data-testid=pt-summary-evidence]").TextContent.Should().Be("Manual steps — no endpoint provenance");
        manual.FindAll("[data-testid=pt-endpoint-evidence]").Should().BeEmpty();
    }

    [Fact]
    public void Missing_evidence_is_never_presented_as_current()
    {
        var cut = Render<PerformanceExecutionSummary>(p => p.Add(x => x.Definition, Definition(Discovered(auth: null))).Add(x => x.History, (EndpointObservationHistoryView?)null));
        cut.Find("[data-testid=pt-summary-evidence]").TextContent.Should().StartWith("Unknown");
    }

    // ── Live progress ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_running_container_without_requests_is_not_load_and_unknown_is_never_zero()
    {
        var evidence = new PerformanceExecutionEvidence { Stage = PerformanceRunStage.StartingK6, Container = PerformanceObservedState.Running, K6Process = PerformanceObservedState.Running,
            ContainerObservedAt = T0.AddSeconds(4), Telemetry = PerformanceTelemetryState.NotYetReceived, ExpectedWorkloadEndAt = T0.AddMinutes(3).AddSeconds(15) };
        var cut = Render<PerformanceExecutionProgress>(p => p.Add(x => x.Run, Run(PerformanceRunState.Running, evidence)).Add(x => x.At, T0.AddSeconds(95)));
        cut.Find("[data-testid=pt-progress-headline]").TextContent.Should().Be("Started, but no request activity observed yet.");
        cut.Find("[data-testid=pt-progress-requests]").TextContent.Should().Be("Not started yet");
        cut.Find("[data-testid=pt-progress-state]").TextContent.Should().Contain("Starting k6").And.Contain("elapsed 1:35 / 3:15");
        cut.Find("[data-testid=pt-progress-eta]").TextContent.Should().StartWith("~1:40 remaining");

        var uninstrumented = Render<PerformanceExecutionProgress>(p => p.Add(x => x.Run, Run(PerformanceRunState.Running, evidence with { Telemetry = PerformanceTelemetryState.NotInstrumented })));
        uninstrumented.Find("[data-testid=pt-progress-requests]").TextContent.Should().Be("Unknown — live request metrics unavailable for this provider");
        uninstrumented.Find("[data-testid=pt-progress-limitation]").TextContent.Should().StartWith("Live request metrics unavailable for this provider");
    }

    [Fact]
    public void Requests_flowing_are_shown_as_recorded_and_a_stuck_run_is_visible()
    {
        var evidence = new PerformanceExecutionEvidence { Stage = PerformanceRunStage.Running, Container = PerformanceObservedState.Running, K6Process = PerformanceObservedState.Running,
            Telemetry = PerformanceTelemetryState.Receiving, ScriptInitializedAt = T0.AddSeconds(5), RequestsObserved = 210, IterationsObserved = 42, FirstRequestAt = T0.AddSeconds(6),
            LastRequestAt = T0.AddSeconds(90), LastHeartbeatAt = T0.AddSeconds(90), AppearsStuck = true, StuckDetail = "No k6 heartbeat for 70 s." };
        var cut = Render<PerformanceExecutionProgress>(p => p.Add(x => x.Run, Run(PerformanceRunState.Running, evidence)).Add(x => x.At, T0.AddSeconds(160)));
        cut.Find("[data-testid=pt-progress-requests]").TextContent.Should().Be("210");
        cut.Find("[data-testid=pt-progress-headline]").TextContent.Should().StartWith("Requests flowing");
        cut.Find("[data-testid=pt-progress-stuck]").TextContent.Should().Contain("Run may be stuck").And.Contain("No k6 heartbeat for 70 s.");
    }

    // ── Results ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Results_never_say_no_metrics_recorded_while_the_run_is_active()
    {
        var cut = Render<PerformanceRunResults>(p => p.Add(x => x.Run, Run(PerformanceRunState.Running, new PerformanceExecutionEvidence { Stage = PerformanceRunStage.Running })));
        cut.Find("[data-testid=pt-result-no-metrics]").TextContent.Should().StartWith("Metrics are not available yet");
        cut.Markup.Should().NotContain("No metrics were recorded");
    }

    [Fact]
    public void A_completed_run_with_zero_requests_says_so_and_differs_from_telemetry_unavailable()
    {
        var zero = Run(PerformanceRunState.ExecutionFailed, new PerformanceExecutionEvidence { Telemetry = PerformanceTelemetryState.Collected, RequestsObserved = 0, ScriptInitializedAt = T0,
            ResultArtifact = PerformanceResultArtifactState.Parsed });
        Render<PerformanceRunResults>(p => p.Add(x => x.Run, zero)).Find("[data-testid=pt-result-no-metrics]").TextContent.Should().Be("No metrics were recorded for this run. k6 recorded no requests.");
        PerformanceExecutionAssessment.RequestsLabel(zero).Should().Be("0");
        PerformanceExecutionAssessment.RequestsLabel(Run(PerformanceRunState.ExecutionFailed, new PerformanceExecutionEvidence { Telemetry = PerformanceTelemetryState.NotInstrumented }))
            .Should().StartWith("Unknown");
        var missing = Run(PerformanceRunState.ExecutionFailed, new PerformanceExecutionEvidence { ResultArtifact = PerformanceResultArtifactState.Missing });
        Render<PerformanceRunResults>(p => p.Add(x => x.Run, missing)).Find("[data-testid=pt-result-no-metrics]").TextContent.Should().Contain("k6 wrote no result artifact");
    }

    [Fact]
    public void A_user_cancellation_before_any_request_names_the_cause_and_says_load_was_not_confirmed()
    {
        var evidence = new PerformanceExecutionEvidence
        {
            Stage = PerformanceRunStage.Finished, Container = PerformanceObservedState.Exited, ContainerStartedAt = T0.AddSeconds(3), K6Process = PerformanceObservedState.Exited,
            Telemetry = PerformanceTelemetryState.Unavailable, StopReason = PerformanceStopReason.CancelledByUser, StopRequestedAt = T0.AddSeconds(95), ContainerRemoved = true,
            LastHeartbeatAt = null, Timeline = [new(T0, "Queued"), new(T0.AddSeconds(95), "Cancelled by user", "Cancel requested by the user.")],
        };
        var cut = Render<PerformanceRunResults>(p => p.Add(x => x.Run, Run(PerformanceRunState.Cancelled, evidence)));
        cut.Find("[data-testid=pt-result-status]").TextContent.Should().Contain("Cancelled by user").And.Contain("No metrics captured — no pass/fail conclusion");
        cut.Find("[data-testid=pt-execution-stop]").TextContent.Should().Be("Cancelled by user — 1 min 35 s elapsed");
        cut.Find("[data-testid=pt-execution-insufficient]").TextContent.Should().Contain("Execution evidence insufficient").And.Contain("could not confirm that load requests were generated before the run was cancelled");
        Status(cut, "container").Should().Be("Confirmed");
        Status(cut, "requests").Should().Be("Unknown", "no telemetry confirmed zero: unknown, never 0");
        Status(cut, "metrics").Should().Be("None available");
        Status(cut, "cleanup").Should().Be("Completed");
        cut.Find("[data-testid=pt-result-no-metrics]").TextContent.Should().Be("No metrics were recorded for this run. The run was cancelled before metrics were produced.");
    }

    [Fact]
    public void A_cancellation_after_requests_confirms_traffic_and_a_watchdog_stop_is_never_the_users()
    {
        var evidence = new PerformanceExecutionEvidence { Stage = PerformanceRunStage.Finished, Telemetry = PerformanceTelemetryState.Collected, ScriptInitializedAt = T0, RequestsObserved = 12,
            FirstRequestAt = T0.AddSeconds(2), StopReason = PerformanceStopReason.Watchdog, StopDetail = "No k6 heartbeat for 310 s." };
        var cut = Render<PerformanceRunResults>(p => p.Add(x => x.Run, Run(PerformanceRunState.Cancelled, evidence)));
        cut.Find("[data-testid=pt-execution-stop]").TextContent.Should().StartWith("Terminated by watchdog").And.NotContain("user");
        cut.FindAll("[data-testid=pt-execution-insufficient]").Should().BeEmpty();
        cut.Find("[data-testid=pt-execution-summary]").TextContent.Should().Be("HTTP traffic observed — 12 request(s) recorded by k6.");
    }

    [Fact]
    public void A_legacy_run_without_execution_evidence_says_so_instead_of_inventing_it()
    {
        var cut = Render<PerformanceRunResults>(p => p.Add(x => x.Run, Run(PerformanceRunState.Cancelled, null)));
        cut.Find("[data-testid=pt-execution-summary]").TextContent.Should().StartWith("Legacy evidence unavailable");
        cut.Find("[data-testid=pt-execution-milestone]").GetAttribute("data-status").Should().Be("Legacy evidence unavailable");
    }

    private static string Status(IRenderedComponent<PerformanceRunResults> cut, string key) =>
        cut.FindAll("[data-testid=pt-execution-milestone]").Single(m => m.GetAttribute("data-key") == key).GetAttribute("data-status")!;
}
