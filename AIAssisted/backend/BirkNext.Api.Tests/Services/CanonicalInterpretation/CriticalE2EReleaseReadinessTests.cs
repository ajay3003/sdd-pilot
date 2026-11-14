using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.CriticalE2E;
using BirkNext.Governance;
using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Release Readiness reads the authoritative, versioned Critical E2E gate evaluation (CriticalE2EService.ReleaseGate — the code behind the Critical E2E
/// page's overview) for the readiness's environment and build. It never evaluates the gate itself, never passes on another build or environment (Stale),
/// keeps a failed required UI smoke flow as a release blocker without Business E2E credit, keeps policy-1 builds at policy 1, and never recomputes a
/// frozen report. Runs are recorded straight into the store: nothing here executes a flow.
/// </summary>
public sealed class CriticalE2EReleaseReadinessTests : IDisposable
{
    private const string Env = "env-1";
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-e2e-rr-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CriticalE2EStore _store;
    private readonly CriticalE2EService _critical;

    public CriticalE2EReleaseReadinessTests()
    {
        _store = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
        var companion = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), TimeProvider.System, NullLogger<BrowserCompanionService>.Instance);
        _critical = new CriticalE2EService(_store, companion, new NoGateway(), new CriticalE2ERunner([], TimeProvider.System, NullLogger<CriticalE2ERunner>.Instance),
            TimeProvider.System, NullLogger<CriticalE2EService>.Instance);
        foreach (var module in new[] { "M02", "EN01" })
            _store.RegisterModule(Env, new CriticalE2ERegisteredModule { Name = module, Source = "Test", RegisteredAt = Now });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity i, string m, string u, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity i, string e, string q, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity i, string e, CancellationToken ct = default) => Task.FromResult(new AuthenticatedGraphQlSchemaOutcome());
    }

    private sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private CanonicalInterpretationService Readiness() => new(
        new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"rr-e2e-{Guid.NewGuid():N}").Options),
        new NoSources(), TimeProvider.System, criticalE2E: _critical);

    private CriticalE2EFlowDefinition Flow(string id, CriticalE2ETestType type, string module = "M02", bool required = true) => _store.Save(new CriticalE2EFlowDefinition
    {
        Id = id, Module = module, Name = id, Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = Env, EnvironmentId = Env,
        Enabled = true, RequiredForRelease = required, Kind = CriticalE2EFlowKind.Critical, TestType = type,
        Boundary = new CriticalE2EAutomationBoundary { Start = "Approved application page", End = "Status visible" },
        Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "status" }, IsFinalAssertion = true }],
    });

    /// <param name="policy">Gate policy recorded on the run; null = recorded before versioned policies (legacy build).</param>
    private CriticalE2ERunResult Record(CriticalE2EFlowDefinition flow, CriticalE2EStatus status, string build = "RC60", string? policy = CriticalE2EGatePolicy.Current, int minutesAgo = 1)
    {
        var run = new CriticalE2ERunResult
        {
            RunId = $"{flow.Id}-{build}-{minutesAgo}", FlowId = flow.Id, FlowName = flow.Name, Module = flow.Module, EnvironmentId = Env, Status = status,
            StartedAt = Now.AddMinutes(-minutesAgo), BuildId = build, RequiredForRelease = flow.RequiredForRelease, FlowVersion = flow.Version,
            ReleaseEvidence = true, GatePolicyVersion = policy,
        };
        _store.Record(run);
        return run;
    }

    private async Task<(ReleaseReadinessAssessment Assessment, ReadinessDimension Dimension, CriticalE2EReadinessEvaluation Evaluation)> Assess(string? build)
    {
        var assessment = await Readiness().AssessReleaseAsync(Env, null, null, default, new ReleaseEvaluationContext(build, "M2LB DEV", "Development"));
        var dimension = assessment.Dimensions.Single(d => d.Key == ReadinessDimensionKey.CriticalE2E);
        return (assessment, dimension, dimension.CriticalE2E!);
    }

    /// <summary>Release Readiness carries exactly the verdict the Critical E2E page's overview shows for the same environment and build.</summary>
    private void ShouldMatchOverview(CriticalE2EReadinessEvaluation evaluation, string? build)
    {
        var overview = _critical.Overview(new CriticalE2EOverviewRequest { EnvironmentId = Env, EnvironmentName = "M2LB DEV", EnvironmentType = "Development", BuildId = build,
            BuildSource = build is null ? null : "Manual" }).Release;
        (evaluation.Disposition, evaluation.PolicyVersion, evaluation.GateSummary, evaluation.RequiredFlows, evaluation.BusinessE2EFlowsCounted, evaluation.BuildId, evaluation.EnvironmentId)
            .Should().Be((overview.Disposition, overview.PolicyVersion, overview.Summary, overview.RequiredFlowsTotal, overview.BusinessE2EFlowsCounted, overview.BuildId, overview.EnvironmentId));
        evaluation.Flows.Select(f => f.FlowId).Should().Equal(overview.RequiredFlowIds);
        evaluation.Flows.Where(f => f.MandatoryReleaseBlocker).Select(f => f.FlowId).Should().BeEquivalentTo(overview.BlockingFlows.Select(b => b.FlowId));
    }

    [Fact]
    public async Task A_compatible_policy_2_evaluation_that_passed_is_Passed_for_that_build_and_environment()
    {
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        var run = Record(biz, CriticalE2EStatus.Passed);

        var (_, dimension, e) = await Assess("RC60");

        (e.State, e.PolicyVersion, e.Disposition, e.BuildId, e.EnvironmentId, e.ReadinessBuildId, e.ReadinessEnvironmentId)
            .Should().Be((CriticalE2EReadinessState.Passed, CriticalE2EGatePolicy.Current, CriticalE2EReleaseDisposition.Ready, "RC60", Env, "RC60", Env));
        (dimension.State, dimension.Applicability).Should().Be((GateDecision.Ready, DimensionApplicability.Required));
        dimension.Href.Should().Be("critical-e2e-regression?environment=env-1&build=RC60");
        var flow = e.Flows.Should().ContainSingle().Subject;
        (flow.Eligibility, flow.BusinessE2EContribution, flow.MandatoryReleaseBlocker, flow.ExecutionResult, flow.RunId, flow.TestType, flow.Criticality, flow.ReleaseReady)
            .Should().Be((CriticalE2EGateEligibilityKind.Counts, true, false, CriticalE2EStatus.Passed, run.RunId, CriticalE2ETestType.BusinessE2E, CriticalE2EFlowKind.Critical, true));
        e.EvaluatedAt.Should().NotBe(default);
        ShouldMatchOverview(e, "RC60");
    }

    [Fact]
    public void An_evaluation_of_another_build_or_environment_is_Stale_and_never_Passed()
    {
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        Record(biz, CriticalE2EStatus.Passed, build: "RC59");
        var gate = _critical.ReleaseGate(CriticalE2EReleaseReadiness.Request(Env, new("RC59")));
        gate.Release.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready, "RC59 itself passed");

        var otherBuild = CriticalE2EReleaseReadiness.Evaluate(gate, Env, "RC60", Now);
        otherBuild.State.Should().Be(CriticalE2EReadinessState.Stale);
        otherBuild.StaleReason.Should().Contain("build RC59").And.Contain("RC60");
        CriticalE2EReleaseReadiness.Dimension(otherBuild).State.Should().Be(GateDecision.NotAssessed, "a pass on another build is never this build's pass");
        CriticalE2EReleaseReadiness.Dimension(otherBuild).Applicability.Should().Be(DimensionApplicability.Required);

        var otherEnvironment = CriticalE2EReleaseReadiness.Evaluate(gate, "env-2", "RC59", Now);
        otherEnvironment.State.Should().Be(CriticalE2EReadinessState.Stale);
        otherEnvironment.StaleReason.Should().Contain("environment env-1");
        CriticalE2EReleaseReadiness.Dimension(otherEnvironment).State.Should().NotBe(GateDecision.Ready);
    }

    [Fact]
    public async Task A_run_on_another_build_is_not_evidence_and_a_flow_without_execution_is_Incomplete()
    {
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        Record(biz, CriticalE2EStatus.Passed, build: "RC59");

        var (assessment, dimension, e) = await Assess("RC60");

        (e.State, e.Disposition).Should().Be((CriticalE2EReadinessState.Incomplete, CriticalE2EReleaseDisposition.Incomplete));
        dimension.State.Should().Be(GateDecision.NotAssessed, "a defined flow without a run on this build is not a pass");
        dimension.Applicability.Should().Be(DimensionApplicability.Required);
        assessment.Decision.Should().NotBe(GateDecision.Ready);
        var flow = e.Flows.Single();
        (flow.ExecutionResult, flow.RunId, flow.Eligibility).Should().Be((CriticalE2EStatus.NotRun, (string?)null, CriticalE2EGateEligibilityKind.NotExecuted));
        ShouldMatchOverview(e, "RC60");
    }

    [Fact]
    public async Task A_failed_required_UI_smoke_flow_blocks_with_no_Business_E2E_contribution()
    {
        var smoke = Flow("smoke", CriticalE2ETestType.UiSmoke);
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E, module: "EN01");
        Record(smoke, CriticalE2EStatus.Failed);
        Record(biz, CriticalE2EStatus.Passed);

        var (assessment, dimension, e) = await Assess("RC60");

        (e.State, e.Disposition, e.BlockingFlows).Should().Be((CriticalE2EReadinessState.Blocked, CriticalE2EReleaseDisposition.Blocked, 1));
        dimension.State.Should().Be(GateDecision.Blocked);
        assessment.Decision.Should().Be(GateDecision.Blocked, "a required dimension that is Blocked blocks the release");
        var flow = e.Flows.Single(f => f.FlowId == "smoke");
        (flow.BusinessE2EContribution, flow.MandatoryReleaseBlocker, flow.BlockerReason, flow.Eligibility)
            .Should().Be((false, true, "Failed required flow on this build", CriticalE2EGateEligibilityKind.Never));
        e.Flows.Single(f => f.FlowId == "biz").MandatoryReleaseBlocker.Should().BeFalse();
        dimension.Evidence.Should().Contain(line => line.StartsWith("smoke — UiSmoke") && line.Contains("blocks release: Failed required flow on this build"));
        ShouldMatchOverview(e, "RC60");
    }

    [Fact]
    public async Task A_smoke_flow_that_is_not_required_neither_counts_nor_blocks()
    {
        var smoke = Flow("smoke", CriticalE2ETestType.UiSmoke, required: false);
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        Record(smoke, CriticalE2EStatus.Failed);
        Record(biz, CriticalE2EStatus.Passed);

        var (_, _, e) = await Assess("RC60");

        e.State.Should().Be(CriticalE2EReadinessState.Passed);
        e.Flows.Select(f => f.FlowId).Should().Equal("biz");
        (e.RequiredFlows, e.BusinessE2EFlowsCounted, e.BlockingFlows).Should().Be((1, 1, 0));
        ShouldMatchOverview(e, "RC60");
    }

    [Theory]
    [InlineData(CriticalE2ETestType.UiFunctional, CriticalE2EGateEligibilityKind.FunctionalOnly)]
    [InlineData(CriticalE2ETestType.IntegrationApi, CriticalE2EGateEligibilityKind.IntegrationOnly)]
    public async Task UI_functional_and_Integration_API_flows_that_passed_are_not_Business_E2E_so_never_Passed(CriticalE2ETestType type, CriticalE2EGateEligibilityKind eligibility)
    {
        var flow = Flow("flow", type);
        Record(flow, CriticalE2EStatus.Passed);

        var (_, dimension, e) = await Assess("RC60");

        (e.State, e.Disposition).Should().Be((CriticalE2EReadinessState.Incomplete, CriticalE2EReleaseDisposition.NotConfigured));
        dimension.State.Should().Be(GateDecision.NotAssessed);
        var only = e.Flows.Single();
        (only.Eligibility, only.BusinessE2EContribution, only.MandatoryReleaseBlocker, only.ExecutionResult).Should().Be((eligibility, false, false, CriticalE2EStatus.Passed));
        ShouldMatchOverview(e, "RC60");
    }

    [Fact]
    public async Task An_unclassified_legacy_required_flow_needs_review_and_is_never_Passed()
    {
        var legacy = Flow("legacy", CriticalE2ETestType.Unknown);
        Record(legacy, CriticalE2EStatus.Passed);

        var (_, dimension, e) = await Assess("RC60");

        (e.State, e.Disposition, e.PolicyVersion).Should().Be((CriticalE2EReadinessState.Incomplete, CriticalE2EReleaseDisposition.ReviewRequired, CriticalE2EGatePolicy.Current));
        e.Flows.Single().Eligibility.Should().Be(CriticalE2EGateEligibilityKind.LegacyReview);
        dimension.State.Should().Be(GateDecision.NotAssessed);
        dimension.Summary.Should().StartWith("Review required on build RC60");
        dimension.NextAction.Should().Contain("Classify the test type");
        ShouldMatchOverview(e, "RC60");
    }

    [Fact]
    public async Task A_build_evaluated_with_policy_1_keeps_policy_1_and_its_verdict()
    {
        var legacy = Flow("legacy", CriticalE2ETestType.Unknown);
        Record(legacy, CriticalE2EStatus.Passed, build: "RC50", policy: null);

        var (_, dimension, e) = await Assess("RC50");

        (e.PolicyVersion, e.Disposition, e.State).Should().Be((CriticalE2EGatePolicy.Legacy, CriticalE2EReleaseDisposition.Ready, CriticalE2EReadinessState.Passed),
            "the legacy build's verdict is unchanged: evidence recorded before test types is evaluated by policy 1");
        dimension.Summary.Should().Contain(CriticalE2EGatePolicy.Legacy);
        e.Flows.Single().Eligibility.Should().Be(CriticalE2EGateEligibilityKind.Counts);
        ShouldMatchOverview(e, "RC50");
    }

    [Fact]
    public async Task Without_a_build_or_without_required_flows_Critical_E2E_is_Not_assessed()
    {
        var empty = await Assess("RC60");
        (empty.Evaluation.State, empty.Dimension.State, empty.Dimension.Applicability)
            .Should().Be((CriticalE2EReadinessState.NotAssessed, GateDecision.NotAssessed, DimensionApplicability.Optional), "no required flow is configured");

        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        Record(biz, CriticalE2EStatus.Passed);
        var noBuild = await Assess(null);
        (noBuild.Evaluation.State, noBuild.Evaluation.Disposition, noBuild.Dimension.State, noBuild.Dimension.Applicability)
            .Should().Be((CriticalE2EReadinessState.NotAssessed, CriticalE2EReleaseDisposition.NotEvaluated, GateDecision.NotAssessed, DimensionApplicability.Required));
        noBuild.Dimension.Href.Should().Be("critical-e2e-regression?environment=env-1");
        noBuild.Dimension.NextAction.Should().Contain("Enter the build being released");
        ShouldMatchOverview(noBuild.Evaluation, null);
    }

    [Fact]
    public async Task A_frozen_report_keeps_its_Critical_E2E_dimension_and_is_never_recomputed()
    {
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        var service = Readiness();
        var frozen = await service.FreezeReleaseAsync(Env, new FreezeReleaseReadinessRequest("Sprint 47", null, "RC60", "M2LB DEV", "Development"), default);
        var frozenE2E = frozen.Assessment.Dimensions.Single(d => d.Key == ReadinessDimensionKey.CriticalE2E);
        frozenE2E.CriticalE2E!.State.Should().Be(CriticalE2EReadinessState.Incomplete);
        frozen.Assessment.BuildId.Should().Be("RC60");

        // Later evidence: the flow passes on RC60. The live assessment changes; the frozen report does not.
        Record(biz, CriticalE2EStatus.Passed);
        (await service.AssessReleaseAsync(Env, null, null, default, new("RC60", "M2LB DEV", "Development")))
            .Dimensions.Single(d => d.Key == ReadinessDimensionKey.CriticalE2E).CriticalE2E!.State.Should().Be(CriticalE2EReadinessState.Passed);
        var again = (await service.ReleaseReportAsync(Env, frozen.Id, default))!;
        var stored = again.Assessment.Dimensions.Single(d => d.Key == ReadinessDimensionKey.CriticalE2E);
        stored.CriticalE2E!.State.Should().Be(CriticalE2EReadinessState.Incomplete);
        stored.CriticalE2E.PolicyVersion.Should().Be(CriticalE2EGatePolicy.Current);
        JsonSerializer.Serialize(again.Assessment).Should().Be(JsonSerializer.Serialize(frozen.Assessment), "the frozen assessment is exactly as frozen");
    }

    [Fact]
    public async Task Freezing_the_same_evidence_twice_is_refused_even_though_the_gate_was_read_at_another_time()
    {
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        Record(biz, CriticalE2EStatus.Passed);
        var service = Readiness();
        await service.FreezeReleaseAsync(Env, new FreezeReleaseReadinessRequest("RC60", null, "RC60"), default);
        var twice = () => service.FreezeReleaseAsync(Env, new FreezeReleaseReadinessRequest("RC60", null, "RC60"), default);
        await twice.Should().ThrowAsync<CanonicalRequestException>().WithMessage("*already freezes this scope*");
        (await service.FreezeReleaseAsync(Env, new FreezeReleaseReadinessRequest("RC60", null, "RC61"), default)).Assessment.BuildId
            .Should().Be("RC61", "another build is other evidence, so another report");
    }

    [Fact]
    public async Task Without_the_Critical_E2E_capability_nothing_is_invented()
    {
        var service = new CanonicalInterpretationService(new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>()
            .UseInMemoryDatabase($"rr-e2e-none-{Guid.NewGuid():N}").Options), new NoSources(), TimeProvider.System);
        (await service.AssessReleaseAsync(Env, null, null, default, new("RC60"))).Dimensions.Should().ContainSingle("only the scope dimension, as before");
        var unavailable = CriticalE2EReleaseReadiness.Unavailable();
        (unavailable.State, unavailable.Applicability, unavailable.CriticalE2E).Should().Be((GateDecision.NotAssessed, DimensionApplicability.Optional, (CriticalE2EReadinessEvaluation?)null));
    }
}
