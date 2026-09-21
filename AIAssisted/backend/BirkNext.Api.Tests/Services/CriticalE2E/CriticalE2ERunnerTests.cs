using BirkNext.Api.Services.CriticalE2E;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// What a flow result means. The runner's whole job is to keep four things apart that a naive implementation merges:
/// the steps ran, the steps passed, the business assertion held, and the prerequisites were there. Only the third
/// produces a Passed flow.
/// </summary>
public sealed class CriticalE2ERunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private sealed class ScriptedExecutor(Func<CriticalE2EStepDefinition, CriticalE2EStatus> outcomes) : ICriticalE2EStepExecutor
    {
        public List<string> Executed { get; } = [];
        public bool CanExecute(CriticalE2EStepDefinition step) => true;
        public Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken)
        {
            Executed.Add(step.StepId);
            var status = outcomes(step);
            if (status == CriticalE2EStatus.Cancelled) throw new OperationCanceledException();
            return Task.FromResult(CriticalE2EStepOutcome.From(step, Now, Now, status, error: status == CriticalE2EStatus.Passed ? null : "reason"));
        }
    }

    private static CriticalE2EStepDefinition Step(string id, bool final = false) =>
        new() { StepId = id, Description = id, BrowserAction = CompanionActionKind.Click, IsFinalAssertion = final };

    private static CriticalE2EFlowDefinition Flow(params CriticalE2EStepDefinition[] steps) => new()
    {
        Id = "flow-1", Module = "Tjeneste", Name = "Open a service and verify status",
        Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = "env-1",
        RequiredForRelease = true, Steps = [.. steps],
    };

    private static async Task<(CriticalE2ERunResult Result, ScriptedExecutor Executor)> RunAsync(
        CriticalE2EFlowDefinition flow, Func<CriticalE2EStepDefinition, CriticalE2EStatus> outcomes)
    {
        var executor = new ScriptedExecutor(outcomes);
        var runner = new CriticalE2ERunner([executor], TimeProvider.System, NullLogger<CriticalE2ERunner>.Instance);
        var result = await runner.RunAsync(new CriticalE2ERunRequest { Flow = flow, TargetOrigin = "https://m2lbdev.bufetat.no", BuildId = "12345" }, CancellationToken.None);
        return (result, executor);
    }

    [Fact]
    public async Task EveryStepPassingAndTheFinalAssertionHoldingIsAPass()
    {
        var (result, _) = await RunAsync(Flow(Step("a"), Step("b"), Step("assert", final: true)), _ => CriticalE2EStatus.Passed);
        result.Status.Should().Be(CriticalE2EStatus.Passed);
        result.PassedSteps.Should().Be(3);
        result.FailureReason.Should().BeNull();
        result.CorrelationId.Should().StartWith(CriticalE2ECorrelation.Prefix);
        result.BuildId.Should().Be("12345");
    }

    [Fact]
    public async Task EveryClickSucceedingWithNoFinalAssertionIsNotAPass()
    {
        // The flow ran perfectly and proved nothing. Reporting that as green is the failure mode this whole capability
        // exists to prevent.
        var (result, _) = await RunAsync(Flow(Step("a"), Step("b")), _ => CriticalE2EStatus.Passed);
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.FailureReason.Should().Contain("final business assertion");
    }

    [Fact]
    public async Task AFailedAssertionFailsTheFlowAndNamesTheStep()
    {
        var (result, _) = await RunAsync(Flow(Step("open"), Step("assert", final: true)),
            step => step.StepId == "assert" ? CriticalE2EStatus.Failed : CriticalE2EStatus.Passed);
        result.Status.Should().Be(CriticalE2EStatus.Failed);
        result.FailureReason.Should().Contain("assert");
    }

    [Fact]
    public async Task AMissingPrerequisiteBlocksTheFlowRatherThanFailingIt()
    {
        var (result, _) = await RunAsync(Flow(Step("open"), Step("assert", final: true)), _ => CriticalE2EStatus.Blocked);
        result.Status.Should().Be(CriticalE2EStatus.Blocked, "a flow that never ran is not a flow that found a defect");
    }

    [Fact]
    public async Task StepsAfterAFailureAreRecordedAsNotRunRatherThanExecuted()
    {
        var (result, executor) = await RunAsync(Flow(Step("a"), Step("b"), Step("assert", final: true)),
            step => step.StepId == "a" ? CriticalE2EStatus.Failed : CriticalE2EStatus.Passed);

        executor.Executed.Should().Equal("a");
        result.StepResults.Should().HaveCount(3);
        result.StepResults.Skip(1).Should().OnlyContain(s => s.Status == CriticalE2EStatus.NotRun,
            "later steps assume the earlier ones happened; running them anyway produces failures that describe nothing");
    }

    [Fact]
    public async Task CancellationIsItsOwnOutcome()
    {
        var (result, _) = await RunAsync(Flow(Step("a"), Step("assert", final: true)), _ => CriticalE2EStatus.Cancelled);
        result.Status.Should().Be(CriticalE2EStatus.Cancelled);
    }

    [Fact]
    public async Task AFlowWithNoStepsIsBlockedNotPassed()
    {
        var (result, _) = await RunAsync(Flow(), _ => CriticalE2EStatus.Passed);
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.FailureReason.Should().Contain("no steps");
    }

    [Fact]
    public async Task AStepNoExecutorHandlesIsBlockedWithAConfigurationReason()
    {
        var orphan = new CriticalE2EStepDefinition { StepId = "orphan", IsFinalAssertion = true };
        var runner = new CriticalE2ERunner([], TimeProvider.System, NullLogger<CriticalE2ERunner>.Instance);
        var result = await runner.RunAsync(new CriticalE2ERunRequest { Flow = Flow(orphan) }, CancellationToken.None);
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.FailureReason.Should().Contain("No executor");
    }

    [Theory]
    [InlineData("${RunId}")]
    [InlineData("${CorrelationId}")]
    [InlineData("${Timestamp}")]
    [InlineData("${RandomGuid}")]
    public void EverySupportedVariableResolvesToSomethingConcrete(string template)
    {
        var context = new CriticalE2ERunContext { Flow = Flow(), RunId = "run-1", CorrelationId = "M2LB-E2E-x", StartedAt = Now };
        CriticalE2EVariables.Resolve(template, context, Now).Should().NotBeNullOrWhiteSpace().And.NotBe(template);
    }

    [Fact]
    public void AnUnknownVariableStaysLiteralRatherThanBecomingAnEmptySearch()
    {
        var context = new CriticalE2ERunContext { Flow = Flow(), RunId = "run-1", CorrelationId = "c", StartedAt = Now };
        // Substituting "" here would make a flow search for nothing and pass.
        CriticalE2EVariables.Resolve("case ${Nonsense}", context, Now).Should().Be("case ${Nonsense}");
    }

    [Fact]
    public void AnEarlierStepsOutputIsAddressableByStepId()
    {
        var context = new CriticalE2ERunContext { Flow = Flow(), RunId = "run-1", CorrelationId = "c", StartedAt = Now };
        context.Outputs["read-id"] = "SAK-42";
        CriticalE2EVariables.Resolve("${step:read-id}", context, Now).Should().Be("SAK-42");
    }
}
