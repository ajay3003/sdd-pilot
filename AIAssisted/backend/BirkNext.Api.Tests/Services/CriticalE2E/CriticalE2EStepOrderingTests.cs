using System.Text.Json;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

public sealed class CriticalE2EStepOrderingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "e2e-order-" + Guid.NewGuid().ToString("N"));
    private CriticalE2EStore Store() => new(_directory, NullLogger<CriticalE2EStore>.Instance);
    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class Executor : ICriticalE2EStepExecutor
    {
        public List<string> Executed { get; } = [];
        public bool CanExecute(CriticalE2EStepDefinition step) => true;
        public Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken ct)
        {
            Executed.Add(step.StepId);
            return Task.FromResult(CriticalE2EStepOutcome.From(step, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CriticalE2EStatus.Passed));
        }
    }

    private static CriticalE2EFlowDefinition Flow(CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical) => new()
    {
        Id = "flow", Name = "Search", Module = "Barn", ProfileId = "dev", EnvironmentId = "dev", Kind = kind,
        Steps = [
            new() { StepId = "A", BrowserAction = CompanionActionKind.Click, Selector = new() { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Start søk" } },
            new() { StepId = "B", BrowserAction = CompanionActionKind.Fill, Value = "lars", Selector = new() { Value = "search" } },
            new() { StepId = "C", BrowserAction = CompanionActionKind.AssertText, Expected = "Prøv å laste tilganger på nytt", Selector = new() { Value = "message" }, IsFinalAssertion = true }
        ]
    };

    [Theory]
    [InlineData(CriticalE2EFlowKind.Critical)]
    [InlineData(CriticalE2EFlowKind.Diagnostic)]
    public async Task SavedArrayOrderControlsExecutionAndOldActiveAndArchivedEvidenceStayImmutable(CriticalE2EFlowKind kind)
    {
        var store = Store();
        var original = store.Save(Flow(kind));
        var executor = new Executor();
        var runner = new CriticalE2ERunner([executor], TimeProvider.System, NullLogger<CriticalE2ERunner>.Instance);
        var run = await runner.RunAsync(new() { Flow = original }, CancellationToken.None);
        store.Record(run);
        store.Record(run with { RunId = "archived", ArchivedAt = DateTimeOffset.UtcNow });
        var historyBefore = File.ReadAllBytes(Path.Combine(_directory, "history.json"));

        store.Save(original with { Steps = [original.Steps[2], original.Steps[0], original.Steps[1]] });
        var reloaded = Store();
        var current = reloaded.Flow("flow")!;
        current.Steps.Select(s => s.StepId).Should().Equal("C", "A", "B");
        current.Steps.Should().BeEquivalentTo(original.Steps);
        current.Kind.Should().Be(kind);
        current.ConfigurationProblem().Should().BeNull("the final business assertion can be in the middle or first");
        executor.Executed.Clear();
        var result = await runner.RunAsync(new() { Flow = current }, CancellationToken.None);
        executor.Executed.Should().Equal("C", "A", "B");
        result.Status.Should().Be(CriticalE2EStatus.Passed);
        result.StepResults.Select(s => s.StepId).Should().Equal("C", "A", "B");
        foreach (var old in reloaded.FlowHistory("flow")) old.StepResults.Select(s => s.StepId).Should().Equal("A", "B", "C");
        File.ReadAllBytes(Path.Combine(_directory, "history.json")).Should().Equal(historyBefore);
    }

    [Fact]
    public void LegacyJsonOrderSurvivesLoadAndSaveAndDuplicateOrMissingIdsNormalizeWithoutSorting()
    {
        Directory.CreateDirectory(_directory);
        var flow = Flow();
        flow = flow with { Steps = [flow.Steps[2] with { StepId = "same" }, flow.Steps[0] with { StepId = "same" }, flow.Steps[1] with { StepId = "" }] };
        File.WriteAllText(Path.Combine(_directory, "flows.json"), JsonSerializer.Serialize(new[] { flow }));
        var store = Store();
        store.Flow("flow")!.Steps.Should().Equal(flow.Steps);
        store.Save(store.Flow("flow")!);
        var saved = Store().Flow("flow")!;
        saved.Steps.Select(s => s.StepId).Should().OnlyHaveUniqueItems().And.NotContain("");
        saved.Steps.Select(s => s.BrowserAction).Should().Equal(CompanionActionKind.AssertText, CompanionActionKind.Click, CompanionActionKind.Fill);
        saved.Steps.Select(s => s.Selector).Should().Equal(flow.Steps.Select(s => s.Selector));
    }
}
