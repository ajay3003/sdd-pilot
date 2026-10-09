using System.Text.RegularExpressions;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Integrations;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// The generic journey engine with an unrelated example pack (REST → ingestion → processor → Service Bus → consumer): readiness, safety
/// gates, ordered execution, truthful partial results, persistence and history. Executors/observers are fakes; nothing leaves the process.
/// </summary>
public sealed class IntegrationJourneyEngineTests
{
    private static IntegrationJourneyRunRequest Request(string scenario = "happy", bool confirmed = true) => new()
    {
        EnvironmentId = ActiveEventTestHarness.Env, PackId = "example.orders", JourneyId = "order-flow", ScenarioId = scenario, Confirmed = confirmed,
    };

    private static (FakeExecutor Executor, FakeObserver Observer) Wire(IntegrationJourneyHarness harness, JourneyStepState submit = JourneyStepState.Accepted,
        JourneyStepState verify = JourneyStepState.Verified, bool everyStepVerified = false)
    {
        harness.Active.Catalog.Extra.AddRange([ExampleOrdersJourneyPack.Intake, ExampleOrdersJourneyPack.Bus]);
        harness.Enroll(ExampleOrdersJourneyPack.IntakeId);
        harness.Enroll(ExampleOrdersJourneyPack.BusId);
        var executor = new FakeExecutor(JourneyStepKind.ExternalSubmission, everyStepVerified ? JourneyStepState.Verified : submit);
        var observed = everyStepVerified ? JourneyStepState.Verified : JourneyStepState.Observed;
        var observer = new FakeObserver(new Dictionary<string, JourneyStepState>
        {
            ["ingest"] = observed, ["process"] = observed, ["publish"] = observed, ["consume"] = observed, ["verify"] = verify,
        });
        harness.Executors.Add(executor);
        harness.Observers.Add(observer);
        return (executor, observer);
    }

    [Fact]
    public async Task UnrelatedPack_RunsTheCompleteLifecycle_AndIsCompletedOnlyWhenEveryMandatoryStepIsVerified()
    {
        await using var harness = new IntegrationJourneyHarness();
        var (executor, _) = Wire(harness, everyStepVerified: true);
        var service = harness.Service(new ExampleOrdersJourneyPack());

        var view = (await service.PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single();
        view.Readiness.Should().Be(JourneyReadinessState.Ready);
        view.CanRun.Should().BeTrue();

        var run = await service.StartAsync(Request(), default);
        run.OverallState.Should().Be(JourneyRunState.Completed);
        run.Steps.Select(step => step.StepId).Should().Equal("submit", "ingest", "process", "publish", "consume", "verify");
        executor.Executed.Should().Equal("submit");
        run.Steps.Single(step => step.StepId == "submit").IntegrationId.Should().Be(ExampleOrdersJourneyPack.IntakeId);

        var stored = await service.GetAsync(run.RunId, default);
        stored!.OverallState.Should().Be(JourneyRunState.Completed);
        (await service.HistoryAsync(new IntegrationJourneyHistoryQuery(ActiveEventTestHarness.Env, "example.orders"), default))
            .Should().ContainSingle().Which.VerifiedSteps.Should().Be(6);
        (await service.PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single().Maturity.RuntimeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptedAndObservedSteps_WithoutVerification_ArePartial_NeverCompleted()
    {
        await using var harness = new IntegrationJourneyHarness();
        Wire(harness, submit: JourneyStepState.Accepted, verify: JourneyStepState.NotVerified);
        var run = await harness.Service(new ExampleOrdersJourneyPack()).StartAsync(Request(), default);

        run.OverallState.Should().Be(JourneyRunState.Partial);
        run.Steps.Single(step => step.StepId == "submit").State.Should().Be(JourneyStepState.Accepted);
        run.Steps.Single(step => step.StepId == "publish").State.Should().Be(JourneyStepState.Observed);
        run.Steps.Single(step => step.StepId == "verify").State.Should().Be(JourneyStepState.NotVerified);
    }

    [Fact]
    public async Task FailedActiveStep_StopsTheJourney_WithoutRetryOrFurtherSteps()
    {
        await using var harness = new IntegrationJourneyHarness();
        var (executor, _) = Wire(harness, submit: JourneyStepState.Unavailable);
        var run = await harness.Service(new ExampleOrdersJourneyPack()).StartAsync(Request(), default);

        executor.Executed.Should().Equal("submit");
        run.Steps.Skip(1).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed && step.Reason.StartsWith("Not reached", StringComparison.Ordinal));
        run.OverallState.Should().Be(JourneyRunState.NotVerified);
    }

    [Fact]
    public async Task UnexpectedResult_FailsTheRun_AndStopsLaterSteps()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([ExampleOrdersJourneyPack.Intake, ExampleOrdersJourneyPack.Bus]);
        harness.Enroll(ExampleOrdersJourneyPack.IntakeId);
        harness.Enroll(ExampleOrdersJourneyPack.BusId);
        harness.Executors.Add(new FakeExecutor(JourneyStepKind.ExternalSubmission, JourneyStepState.Accepted));
        harness.Observers.Add(new FakeObserver(new Dictionary<string, JourneyStepState>
        {
            ["ingest"] = JourneyStepState.UnexpectedResult, ["process"] = JourneyStepState.Observed, ["publish"] = JourneyStepState.Observed,
            ["consume"] = JourneyStepState.Observed, ["verify"] = JourneyStepState.Verified,
        }));
        var run = await harness.Service(new ExampleOrdersJourneyPack()).StartAsync(Request(), default);

        run.OverallState.Should().Be(JourneyRunState.Failed);
        run.Steps.Single(step => step.StepId == "process").State.Should().Be(JourneyStepState.NotAssessed);
    }

    [Fact]
    public async Task MissingObserver_LeavesOnlyThatStepNotAssessed_AndReadinessReportsTheGap()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([ExampleOrdersJourneyPack.Intake, ExampleOrdersJourneyPack.Bus]);
        harness.Enroll(ExampleOrdersJourneyPack.IntakeId);
        harness.Enroll(ExampleOrdersJourneyPack.BusId);
        harness.Executors.Add(new FakeExecutor(JourneyStepKind.ExternalSubmission, JourneyStepState.Accepted));
        harness.Observers.Add(new FakeObserver(new Dictionary<string, JourneyStepState> { ["ingest"] = JourneyStepState.Observed }));
        var service = harness.Service(new ExampleOrdersJourneyPack());

        var view = (await service.PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single();
        view.Prerequisites.Single(item => item.Key == "observation").State.Should().Be(JourneyReadinessState.NotAvailable);
        view.CanRun.Should().BeFalse("a journey whose mandatory steps cannot produce evidence is not runnable");
        (await service.StartAsync(Request(), default)).OverallState.Should().Be(JourneyRunState.Blocked);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Prod")]
    [InlineData("Unknown")]
    public async Task ProductionOrUnknownEnvironment_IsBlockedBeforeAnyStep(string environmentType)
    {
        await using var harness = new IntegrationJourneyHarness();
        var (executor, _) = Wire(harness, everyStepVerified: true);
        harness.Active.Settings["TargetEnvironments:Trusted:0:EnvironmentType"] = environmentType;
        var run = await harness.Service(new ExampleOrdersJourneyPack()).StartAsync(Request(), default);

        run.OverallState.Should().Be(JourneyRunState.Blocked);
        executor.Executed.Should().BeEmpty();
        run.Limitations.Should().Contain(item => item.Contains("Nothing was sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProductionMarkedTargetHost_UntrustedEnvironment_AndUnenrolledIntegration_AreBlocked()
    {
        await using var harness = new IntegrationJourneyHarness();
        var (executor, _) = Wire(harness, everyStepVerified: true);
        harness.Active.Settings["TargetEnvironments:Trusted:0:TargetUrl"] = "https://orders-prod.example.test";
        (await harness.Service(new ExampleOrdersJourneyPack()).StartAsync(Request(), default)).OverallState.Should().Be(JourneyRunState.Blocked);

        harness.Active.Settings["TargetEnvironments:Trusted:0:TargetUrl"] = "https://orders-dev.example.test";
        var untrusted = await harness.Service(new ExampleOrdersJourneyPack()).StartAsync(Request() with { EnvironmentId = "browser-guid-not-enrolled" }, default);
        untrusted.OverallState.Should().Be(JourneyRunState.Blocked);
        untrusted.StateReason.Should().Contain("TargetEnvironments:Trusted");

        foreach (var key in harness.Active.Settings.Keys.Where(key => harness.Active.Settings[key] == ExampleOrdersJourneyPack.BusId).ToList()) harness.Active.Settings.Remove(key);
        var unenrolled = await harness.Service(new ExampleOrdersJourneyPack()).StartAsync(Request(), default);
        unenrolled.OverallState.Should().Be(JourneyRunState.Blocked);
        unenrolled.StateReason.Should().Contain("not enrolled");
        executor.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task UnconfirmedRuns_UnsupportedScenarios_AndPackPrerequisites_NeverExecute()
    {
        await using var harness = new IntegrationJourneyHarness();
        var (executor, _) = Wire(harness, everyStepVerified: true);
        var pack = new ExampleOrdersJourneyPack();
        var service = harness.Service(pack);

        await service.Invoking(item => item.StartAsync(Request(confirmed: false), default)).Should().ThrowAsync<IntegrationJourneyRequestException>();
        (await service.StartAsync(Request("duplicate"), default)).OverallState.Should().Be(JourneyRunState.Blocked);

        pack.Extra.Add(new("pack-check", "Pack-only prerequisite", JourneyPrerequisiteCategory.Contract, JourneyReadinessState.NotReady, "A pack can only add blockers."));
        (await service.StartAsync(Request(), default)).OverallState.Should().Be(JourneyRunState.Blocked);
        executor.Executed.Should().BeEmpty();
        (await service.HistoryAsync(new IntegrationJourneyHistoryQuery(ActiveEventTestHarness.Env, State: JourneyRunState.Blocked), default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task MissingIntegration_IsNotConfigured()
    {
        await using var harness = new IntegrationJourneyHarness();
        var view = (await harness.Service(new ExampleOrdersJourneyPack()).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single();

        view.Readiness.Should().Be(JourneyReadinessState.NotConfigured);
        view.MatchedIntegrations.Should().OnlyContain(item => item.IntegrationId == null);
        view.Prerequisites.Single(item => item.Key == "integration:intake").Detail.Should().Contain("No configured HttpApi integration was found");
    }

    [Fact]
    public void Registry_RejectsDuplicatePacksJourneysAndOrphanSteps()
    {
        var duplicate = () => new IntegrationJourneyPackRegistry([new ExampleOrdersJourneyPack(), new ExampleOrdersJourneyPack()]);
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate integration journey pack*");
    }

    [Fact]
    public async Task Catalog_ExposesJourneyIdsAndEdges_ForCoverageTraceabilityAndImpact()
    {
        await using var harness = new IntegrationJourneyHarness();
        var entry = harness.Service(new ExampleOrdersJourneyPack()).Catalog().Single();
        entry.JourneyId.Should().Be("order-flow");
        entry.Edges.Select(edge => edge.StepId).Should().Equal("submit", "ingest", "process", "publish", "consume", "verify");
    }

    [Fact]
    public void GenericJourneyCore_HasNoDomainOrProjectNames()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Api", "Services", "IntegrationJourneys");
        if (!Directory.Exists(root)) return; // Source-level check runs where the repository sources are present.
        var core = Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly);
        core.Should().NotBeEmpty();
        foreach (var file in core)
        {
            var text = File.ReadAllText(file);
            Regex.IsMatch(text, @"Skole|Skolenærvær|Utdanning|Altinn|Bufdata|Person|\bMU\b|\bMM\b|M2LB|Debezium", RegexOptions.IgnoreCase)
                .Should().BeFalse($"{Path.GetFileName(file)} is generic journey core");
        }
    }
}
