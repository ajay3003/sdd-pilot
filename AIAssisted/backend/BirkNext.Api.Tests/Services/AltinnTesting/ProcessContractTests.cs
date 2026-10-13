using System.Text.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// The process contract is judged for the one operation BirkNext performs — a bounded process/next from the form's data task — not for the
/// whole BPMN topology. process.bpmn is optional; the app's deployed OpenAPI contract (app-lib's generated task inventory and the process/next
/// operation) can confirm the operation on its own. Before the transition the live instance must be at the planned task, of the planned type
/// and completable; after it, only an ended process counts, and anything else stops the run with no second process/next. All HTTP goes to the
/// in-process fakes.
/// </summary>
public sealed class ProcessContractTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private static readonly OpenApiExtractor Extractor = new(NullLogger<OpenApiExtractor>.Instance);

    private static AltinnDeployedProcessContract Deployed(string dataType = AltinnTestKit.RealFormDataType, string taskRows = AltinnTestKit.RealTaskRow, bool processNext = true,
        bool bodyRequired = false, string? stateFields = null) =>
        AltinnDeployedOpenApiReader.Read(stateFields is null
            ? AltinnTestKit.DeployedOpenApi(dataType, taskRows, processNext, bodyRequired)
            : AltinnTestKit.DeployedOpenApi(dataType, taskRows, processNext, bodyRequired, stateFields), AltinnTestKit.AppId, Extractor);

    private static AltinnProcessAssessment Assess(IReadOnlyList<AltinnProcessTask>? bpmn, AltinnDeployedProcessContract? deployed, int max = 1,
        string? metadataTask = "Task_1", string formType = AltinnTestKit.RealFormDataType) =>
        AltinnProcessContractEvaluator.Assess(bpmn, deployed, formType, metadataTask, max);

    // ── Deployed OpenAPI facts ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDeployedSkolenaervaerContract_YieldsItsTaskInventory_DataTypeBinding_AndProcessNextOperation()
    {
        var deployed = Deployed();

        deployed.TaskInventoryFound.Should().BeTrue();
        deployed.Tasks.Should().Equal([new AltinnDeclaredProcessTask("Task_1", "Utfylling", "data", [])], (a, b) => a.TaskId == b.TaskId && a.Name == b.Name && a.TaskType == b.TaskType && a.Actions.Count == b.Actions.Count);
        deployed.DataTypeTasks.Should().Contain(new AltinnDataTypeTask(AltinnTestKit.RealFormDataType, "Task_1")).And.Contain(new AltinnDataTypeTask("ref-data-as-pdf", null));
        deployed.ProcessNextDeclared.Should().BeTrue();
        deployed.ProcessNextMethod.Should().Be("PUT");
        deployed.ProcessNextBodyRequired.Should().BeFalse("the ProcessNext body is optional");
        deployed.ProcessNextBodyFields.Should().BeEquivalentTo(["action", "actionOnBehalfOf"]);
        deployed.ProcessNextResponseSchema.Should().Be("AppProcessState");
        deployed.ProcessStateFields.Should().Contain(["currentTask", "ended", "endEvent", "actions"]);
        deployed.AppLibVersion.Should().Be("8.12.7.264");
        deployed.Fingerprint.Should().HaveLength(64);
    }

    [Fact]
    public void ADocumentForAnotherApp_IsNotEvidenceForTheConfiguredApp()
    {
        var act = () => AltinnDeployedOpenApiReader.Read(AltinnTestKit.DeployedOpenApi(app: "other/app"), AltinnTestKit.AppId, Extractor);
        act.Should().Throw<AltinnContractException>().WithMessage("*does not describe buf/skolenaervaer*");
    }

    // ── Assessment ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AValidSingleTaskProcessBpmn_ConfirmsTheOperation_AndKnowsTheTopology()
    {
        var process = Assess([new("Task_1", "data")], null);
        process.Status.Should().Be(AltinnProcessContractStatus.ConfirmedForCurrentOperation);
        process.Transitions.Should().Be(1);
        process.TopologyKnown.Should().BeTrue();
        process.StaticDefinition.Should().Be("Provided · 1 sequential task(s)");
        process.Sources.Should().Equal(AltinnProcessContractEvaluator.StaticLabel);
    }

    [Fact]
    public void TheDeployedContractAlone_ConfirmsOneBoundedTransition_WithoutClaimingTheTopology()
    {
        var process = Assess(null, Deployed());

        process.Status.Should().Be(AltinnProcessContractStatus.ConfirmedForCurrentOperation, string.Join(" | ", process.Reasons));
        process.Sources.Should().Equal(AltinnProcessContractEvaluator.DeployedLabel);
        process.DeclaredTaskCount.Should().Be(1);
        process.PlannedTasks.Should().ContainSingle().Which.Should().Match<AltinnDeclaredProcessTask>(task => task.TaskId == "Task_1" && task.Name == "Utfylling" && task.TaskType == "data");
        process.Operation.Should().Be("Normal next transition · no action body");
        process.Transitions.Should().Be(1);
        process.MaxTransitions.Should().Be(1);
        process.StaticDefinition.Should().Be("Not provided · Optional", "a missing process.bpmn is not a failure");
        process.TopologyKnown.Should().BeFalse();
        process.Summary.Should().Contain("full BPMN topology (gateways, events) is not known");
        process.ExpectedPostCondition.Should().Contain("ended and endEvent set, no current task").And.Contain("stops the run");
    }

    [Fact]
    public void NoProcessEvidenceAtAll_IsNotConfirmed()
    {
        var process = Assess(null, null);
        process.Status.Should().Be(AltinnProcessContractStatus.NotConfirmed);
        process.Transitions.Should().Be(0);
        process.Reasons.Should().ContainSingle().Which.Should().Contain("No process evidence");
    }

    [Theory]
    [InlineData("no-process-next", "does not declare PUT …/process/next")]
    [InlineData("actions", "declares action(s) reject")]
    [InlineData("body-required", "requires a process/next body")]
    [InlineData("no-end-event", "does not declare endEvent")]
    [InlineData("two-tasks", "not their order")]
    [InlineData("confirmation-task", "confirmation task")]
    [InlineData("unknown-form-task", "not established")]
    public void ADeployedContractMissingAFactTheOperationNeeds_IsPartial_WithThatExactReason(string variant, string reason)
    {
        var deployed = variant switch
        {
            "no-process-next" => Deployed(processNext: false),
            "actions" => Deployed(taskRows: "| Task_1 | Utfylling | data | reject |"),
            "body-required" => Deployed(bodyRequired: true),
            "no-end-event" => Deployed(stateFields: "\"currentTask\": {}, \"ended\": {}"),
            "two-tasks" => Deployed(taskRows: "| Task_1 | Utfylling | data |  |\n| Task_2 | Bekreft | confirmation |  |"),
            "confirmation-task" => Deployed(taskRows: "| Task_1 | Bekreft | confirmation |  |"),
            _ => Deployed(dataType: "another-type"),
        };
        var process = Assess(null, deployed, metadataTask: variant == "unknown-form-task" ? null : "Task_1");

        process.Status.Should().Be(AltinnProcessContractStatus.Partial);
        process.Confirmed.Should().BeFalse();
        process.Reasons.Should().Contain(item => item.Contains(reason));
    }

    [Fact]
    public void ADeployedDocumentWithoutTheGeneratedTaskTable_IsNoProcessEvidence()
    {
        var deployed = AltinnDeployedOpenApiReader.Read(AltinnTestKit.DeployedOpenApi(AltinnTestKit.RealFormDataType, taskTable: false), AltinnTestKit.AppId, Extractor);
        deployed.TaskInventoryFound.Should().BeFalse();
        Assess(null, deployed).Status.Should().Be(AltinnProcessContractStatus.NotConfirmed);
    }

    [Fact]
    public void BpmnAndTheDeployedContract_WhenTheyAgree_ConfirmTogether_AndWhenTheyDisagree_AreAConflict()
    {
        var agree = Assess([new("Task_1", "data")], Deployed());
        agree.Status.Should().Be(AltinnProcessContractStatus.ConfirmedForCurrentOperation);
        agree.Sources.Should().Equal(AltinnProcessContractEvaluator.StaticLabel, AltinnProcessContractEvaluator.DeployedLabel);
        agree.StaticDefinition.Should().Contain("agrees with the deployed contract");
        agree.PlannedTasks.Single().Name.Should().Be("Utfylling", "the deployed contract names the task");

        var conflict = Assess([new("Task_1", "data"), new("Task_2", "confirmation")], Deployed());
        conflict.Status.Should().Be(AltinnProcessContractStatus.Conflict);
        conflict.Transitions.Should().Be(0);
        conflict.StaticDefinition.Should().Contain("conflicts");
        conflict.Reasons.Should().Contain(item => item.Contains("not what is deployed"));
    }

    [Fact]
    public void MetadataAndTheDeployedContract_DisagreeingOnTheFormsTask_IsAConflict()
    {
        Assess(null, Deployed(), metadataTask: "Task_9").Status.Should().Be(AltinnProcessContractStatus.Conflict);
    }

    [Fact]
    public void APlanNeedingMoreTransitionsThanThePolicyAllows_IsNotExecuted_NeverTruncated()
    {
        var twoTasks = new List<AltinnProcessTask> { new("Task_1", "data"), new("Task_2", "data") };
        var bounded = Assess(twoTasks, null, max: 1);
        bounded.Status.Should().Be(AltinnProcessContractStatus.Partial);
        bounded.Reasons.Should().Contain("The plan needs 2 process transitions; the execution policy allows 1.");

        Assess(twoTasks, null, max: 2).Status.Should().Be(AltinnProcessContractStatus.ConfirmedForCurrentOperation, "a verified multi-task plan within the bound");
        new AltinnTestingPolicy(new AltinnTestingOptions()).MaxProcessTransitions.Should().Be(1, "first runs are bounded to one transition by default");
        new AltinnTestingPolicy(new AltinnTestingOptions { MaxProcessTransitions = 0 }).MaxProcessTransitions.Should().Be(1);
        new AltinnTestingPolicy(new AltinnTestingOptions { MaxProcessTransitions = 99 }).MaxProcessTransitions.Should().Be(1);
    }

    // ── Pre- and post-transition checks ───────────────────────────────────────────────────────────────────────────────────

    private static AltinnInstanceState Live(string? task = "Task_1", string? type = "data", bool? canComplete = true, DateTimeOffset? ended = null) =>
        new(AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid, AltinnTestKit.DataGuid, task, ended, null, null) { CurrentTaskType = type, CanCompleteTask = canComplete };

    private static readonly AltinnDeclaredProcessTask Task1 = new("Task_1", "Utfylling", "data", []);

    [Fact]
    public void TheLiveInstance_MustBeAtThePlannedTask_OfThePlannedType_AndCompletable()
    {
        AltinnProcessContractEvaluator.PreTransitionProblem(Live(), Task1).Should().BeNull();
        AltinnProcessContractEvaluator.PreTransitionProblem(Live(canComplete: null), Task1).Should().BeNull("validated.canCompleteTask is not always reported");
        AltinnProcessContractEvaluator.PreTransitionProblem(Live(task: "Task_2"), Task1).Should().Contain("at Task_2, not Task_1");
        AltinnProcessContractEvaluator.PreTransitionProblem(Live(type: "confirmation"), Task1).Should().Contain("reported as confirmation, not data");
        AltinnProcessContractEvaluator.PreTransitionProblem(Live(type: null), Task1).Should().Contain("unknown type");
        AltinnProcessContractEvaluator.PreTransitionProblem(Live(canComplete: false), Task1).Should().Contain("cannot be completed");
        AltinnProcessContractEvaluator.PreTransitionProblem(Live(task: null, ended: DateTimeOffset.UnixEpoch), Task1).Should().Contain("already ended");
    }

    [Theory]
    [InlineData(null, null, "2026-10-12T10:00:00Z", "EndEvent_1", AltinnTransitionOutcome.ProcessEnded)]
    [InlineData(null, "Task_2", null, null, AltinnTransitionOutcome.UnexpectedNextTask)]
    [InlineData(null, "Task_1", null, null, AltinnTransitionOutcome.UnexpectedNextTask)]
    [InlineData(null, null, "2026-10-12T10:00:00Z", null, AltinnTransitionOutcome.UnexpectedProcessState)]
    [InlineData(null, null, null, "EndEvent_1", AltinnTransitionOutcome.UnexpectedProcessState)]
    [InlineData(null, null, null, null, AltinnTransitionOutcome.UnexpectedProcessState)]
    [InlineData("Task_2", "Task_2", null, null, AltinnTransitionOutcome.MovedToExpectedTask)]
    [InlineData("Task_2", null, "2026-10-12T10:00:00Z", "EndEvent_1", AltinnTransitionOutcome.UnexpectedProcessState)]
    public void TheReturnedProcessState_IsClassifiedAgainstThePlan(string? expectedNext, string? task, string? ended, string? endEvent, AltinnTransitionOutcome outcome)
    {
        var state = new AltinnProcessStateResult(task, task is null ? null : "data", ended is null ? null : DateTimeOffset.Parse(ended), endEvent);
        AltinnProcessContractEvaluator.Classify(state, expectedNext).Should().Be(outcome);
    }

    // ── Mocked E2E-01 with the deployed contract and no process.bpmn ──────────────────────────────────────────────────────

    [Fact]
    public async Task E2E01_WithOnlyTheDeployedContract_AdvancesOnce_VerifiesTheEndedState_AndRecordsEachFactSeparately()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit);

        var run = await Service(harness, kit).StartAsync(Run(), "local-session:loopback", default);

        run.Steps.Take(6).Should().OnlyContain(step => step.State == JourneyStepState.Verified, string.Join(" | ", run.Steps.Select(step => $"{step.StepId}: {step.State} {step.Reason}")));
        Facts(run, "altinn-validate").Should().Contain("Validated");
        Facts(run, "altinn-submit").Should().Equal("Live task before transition", "Process advanced", "Process ended", "Submitted");
        Fact(run, "altinn-submit", "Live task before transition").Should().Be("Task_1 · data");
        Fact(run, "altinn-submit", "Process advanced").Should().Be("1 of at most 1 transition(s)");
        Fact(run, "altinn-submit", "Process ended").Should().StartWith("end event EndEvent_1");
        Facts(run, "altinn-receipt").Should().Contain("Receipt confirmed");
        run.Steps.Single(step => step.StepId == "altinn-submit").Reason.Should().Contain("Not received by MU");
        run.OverallState.Should().Be(JourneyRunState.Partial, "process ended ≠ MU received; downstream steps stay not assessed");
        NextCalls(kit).Should().Be(1);
        Paths(kit).Should().Equal("POST instances", "PUT data", "GET validate", "GET instance", "PUT process/next", "GET instance");
        Paths(kit).Should().NotContain(item => item.Contains("customOpenapi"), "the contract was read before the run, not during it");
    }

    [Theory]
    [InlineData("another-task", "Unexpected next task")]
    [InlineData("same-task", "Unexpected next task")]
    [InlineData("ambiguous", "Unexpected process state")]
    public async Task E2E01_AnUnexpectedReturnedState_StopsTheRun_WithNoSecondProcessNext(string variant, string fact)
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit);
        kit.Http.NextJson = variant switch
        {
            "another-task" => """{"currentTask":{"elementId":"Task_2","altinnTaskType":"confirmation"},"ended":null,"endEvent":null}""",
            "same-task" => """{"currentTask":{"elementId":"Task_1","altinnTaskType":"data"},"ended":null,"endEvent":null}""",
            _ => """{"currentTask":null,"ended":null,"endEvent":null}""",
        };

        var run = await Service(harness, kit).StartAsync(Run(), "local-session:loopback", default);

        var submit = run.Steps.Single(step => step.StepId == "altinn-submit");
        submit.State.Should().Be(JourneyStepState.UnexpectedResult);
        submit.Reason.Should().StartWith("Stopped:");
        Facts(run, "altinn-submit").Should().Contain(["Process advanced", fact]).And.NotContain(["Process ended", "Submitted"]);
        run.Steps.Single(step => step.StepId == "altinn-receipt").State.Should().Be(JourneyStepState.NotAssessed);
        NextCalls(kit).Should().Be(1, "an unexpected state never triggers another transition");
        run.OverallState.Should().NotBe(JourneyRunState.Completed);
    }

    [Theory]
    [InlineData("task", "at Task_2, not Task_1")]
    [InlineData("type", "reported as confirmation, not data")]
    [InlineData("cannot-complete", "cannot be completed")]
    public async Task E2E01_ALiveInstanceNotAtThePlannedCompletableDataTask_IsNeverAdvanced(string variant, string reason)
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit);
        kit.Http.LiveInstanceJson = FakeAltinn.InstanceWith(variant switch
        {
            "task" => FakeAltinn.ActiveProcess(task: "Task_2"),
            "type" => FakeAltinn.ActiveProcess(type: "confirmation"),
            _ => FakeAltinn.ActiveProcess(canComplete: false),
        });

        var run = await Service(harness, kit).StartAsync(Run(), "local-session:loopback", default);

        var submit = run.Steps.Single(step => step.StepId == "altinn-submit");
        submit.State.Should().Be(JourneyStepState.UnexpectedResult);
        submit.Reason.Should().Contain(reason).And.Contain("process/next was not called");
        NextCalls(kit).Should().Be(0);
    }

    [Fact]
    public async Task E2E01_ValidationErrors_StopBeforeProcessNext()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit);
        kit.Http.ValidationJson = """[{"severity":1,"code":"required","description":"x","source":"Required"}]""";

        var run = await Service(harness, kit).StartAsync(Run(), "local-session:loopback", default);

        run.Steps.Single(step => step.StepId == "altinn-validate").State.Should().Be(JourneyStepState.UnexpectedResult);
        run.Steps.Single(step => step.StepId == "altinn-submit").State.Should().Be(JourneyStepState.NotAssessed);
        NextCalls(kit).Should().Be(0);
    }

    [Fact]
    public async Task TheExecutor_WithoutAConfirmedProcessOperation_SendsNothing_EvenIfReadinessIsBypassed()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit, deployed: AltinnTestKit.DeployedOpenApi(processNext: false));
        var journey = Pack(harness, kit).Journeys.Single(item => item.JourneyId == "rapportmottak");
        var trusted = BirkNext.Api.Services.ActiveEventTesting.TrustedExecutionEnvironmentRegistry.From(harness.Active.Configuration()).Resolve(Env, out _);
        var prepared = SkoleNaervaerReportInput.Prepare(journey.Scenarios[0], Report());

        var result = await Executor(harness, kit).ExecuteAsync(new JourneyStepContext(Guid.NewGuid(), Env, trusted, journey, journey.Scenarios[0], journey.Steps[0], null, [], prepared.Values, "local-session:loopback"), default);

        result.State.Should().Be(JourneyStepState.Unavailable);
        result.Reason.Should().Contain("process/next");
        kit.Http.Calls.Should().BeEmpty("nothing is authenticated or sent without a confirmed, bounded process operation");
    }

    [Fact]
    public async Task ReadinessAndDryRun_ShowTheDeployedContract_TheTransitionBound_AndBpmnAsOptional()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit);

        var view = (await Service(harness, kit).PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");
        var row = view.Prerequisites.Single(item => item.Key == "process-contract");
        row.State.Should().Be(JourneyReadinessState.Ready);
        row.Mandatory.Should().BeFalse("the contract row already gates execution; this row explains it");
        row.Detail.Should().Contain("process/next × 1").And.Contain("Task_1 (data)").And.Contain("not known and not needed");
        view.RunnableScenarios.Should().Contain("valid-report");

        var validation = await Service(harness, kit).ValidateAsync(Run() with { Confirmed = false }, default);
        validation.Valid.Should().BeTrue(string.Join(" | ", validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Key}: {c.Detail}")));
        var check = validation.Checks.Single(c => c.Key == "process-plan");
        check.Label.Should().Be("Process contract");
        check.Detail.Should().StartWith("Confirmed for current operation").And.Contain("maximum 1 process transition(s)");
        validation.Preview.Should().Contain(
        [
            new("Process contract", "Confirmed for current operation"), new("Process source", "TT02 deployed OpenAPI contract"), new("Declared process tasks", "1"),
            new("Task", "Task_1 · Utfylling · data"), new("Process action", "Normal next transition · no action body"), new("Execution bound", "Maximum 1 process transition(s)"),
            new("Expected verification", "Returned AppProcessState must indicate process completion; otherwise stop."),
            new("BPMN topology", "Not known (not needed for this operation)"), new("process.bpmn", "Not provided · Optional"),
        ]);
        kit.Http.Calls.Should().BeEmpty("readiness and the dry run make no external call");
    }

    [Fact]
    public async Task UploadedBpmnThatDisagreesWithTheDeployedContract_IsAConflict_AndBlocks()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit);
        harness.Sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles(process: AltinnTestKit.Process(secondTaskType: "confirmation"))));
        var view = await kit.Contracts(harness.MessageFlows, harness.Sources).UseSourceAnalysisAsync(Env, null, default);

        view.Process.Status.Should().Be(AltinnProcessContractStatus.Conflict);
        view.Complete.Should().BeFalse();
        var journey = (await Service(harness, kit).PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");
        journey.RunnableScenarios.Should().NotContain("valid-report");
        journey.Prerequisites.Single(item => item.Key == "process-contract").Detail.Should().StartWith("Conflict");
    }

    [Fact]
    public async Task TheRunHistory_KeepsOnlyNormalizedFacts_NeverTheOpenApiDocumentOrTokens()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyFromDeployedContract(kit);
        var run = await Service(harness, kit).StartAsync(Run(), "local-session:loopback", default);

        var stored = JsonSerializer.Serialize(await harness.Store.GetAsync(run.RunId, default));
        stored.Should().NotContainAny("Altinn 3 App API", "AppProcessState\":", "customOpenapi", AltinnTestKit.AltinnToken, AltinnTestKit.IdPortenToken, AltinnTestKit.ReporterIdentifier);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ready for an unattended run with the contract from public TT02 metadata (incl. the deployed OpenAPI) and no process.bpmn.</summary>
    private static async Task<IntegrationJourneyHarness> ReadyFromDeployedContract(AltinnTestKit kit, string? deployed = null)
    {
        var harness = Harness(kit);
        kit.ConfigureUnattended();
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        if (deployed is not null) kit.Http.OpenApiJson = deployed;
        await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        kit.Http.Calls.Clear();
        return harness;
    }

    private static Dictionary<string, string> Report() => new()
    {
        [SkoleNaervaerReportInput.ReporterKey] = AltinnTestKit.ReporterAlias, [SkoleNaervaerReportInput.IdentitySourceKey] = "registered",
        [SkoleNaervaerAltinnForm.ChildIdentifier] = SyntheticPersonIdentifier.Generate(), [SkoleNaervaerAltinnForm.PeriodFrom] = "2026-10-01",
        [SkoleNaervaerAltinnForm.PeriodTo] = "2026-10-31", [SkoleNaervaerAltinnForm.PlannedHours] = "100", [SkoleNaervaerAltinnForm.ActualHours] = "92",
    };

    private static IntegrationJourneyRunRequest Run() => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = true, Inputs = Report(),
    };

    private static IReadOnlyList<string> Facts(IntegrationJourneyRun run, string step) => run.Steps.Single(item => item.StepId == step).Facts.Select(fact => fact.Label).ToArray();
    private static string Fact(IntegrationJourneyRun run, string step, string label) => run.Steps.Single(item => item.StepId == step).Facts.Single(fact => fact.Label == label).Value;
    private static int NextCalls(AltinnTestKit kit) => kit.Http.Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/process/next", StringComparison.Ordinal));

    /// <summary>The Altinn app calls in order, after authentication (party), as short labels.</summary>
    private static IReadOnlyList<string> Paths(AltinnTestKit kit) => kit.Http.Calls
        .Where(call => call.Uri.Host == "buf.apps.tt02.altinn.no" && !call.Uri.AbsolutePath.Contains("/api/authorization/", StringComparison.Ordinal))
        .Select(call => call.Uri.AbsolutePath switch
        {
            var path when path.EndsWith("/process/next", StringComparison.Ordinal) => "PUT process/next",
            var path when path.EndsWith("/validate", StringComparison.Ordinal) => "GET validate",
            var path when path.Contains("/data/", StringComparison.Ordinal) => "PUT data",
            var path when path.EndsWith("/instances", StringComparison.Ordinal) => "POST instances",
            var path when path.EndsWith(AltinnTestKit.InstanceGuid.ToString("D"), StringComparison.Ordinal) => "GET instance",
            var path => $"{call.Method} {path}",
        }).ToArray();

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static SkoleNaervaerAltinnExecutor Executor(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(new ServiceCollection()
        .AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows)
        .AddTransient(_ => kit.Policy)
        .AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System);

    private static IntegrationJourneyHarness Harness(AltinnTestKit kit)
    {
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
            Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        harness.Enroll("dev:http:altinn-skolenaervaer");
        harness.Executors.Add(Executor(harness, kit));
        return harness;
    }

    private static IntegrationJourneyService Service(IntegrationJourneyHarness harness, AltinnTestKit kit) => harness.Service(Pack(harness, kit));
}
