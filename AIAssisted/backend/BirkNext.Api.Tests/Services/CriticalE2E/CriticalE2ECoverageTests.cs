using BirkNext.Api.Services.CriticalE2E;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Coverage and release aggregation — the rules that decide whether a delivery requirement is satisfied. These are the
/// claims someone will repeat in a release meeting, so each one is tested as a rule rather than inferred from a UI.
/// </summary>
public sealed class CriticalE2ECoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Modules = ["Person", "Tjeneste", "Hendelse"];

    private static CriticalE2EFlowDefinition Flow(string id, string module, bool required = true, bool enabled = true,
        CriticalE2EExecutionMode mode = CriticalE2EExecutionMode.CompanionBrowser, bool wellFormed = true) => new()
        {
            Id = id, Module = module, Name = id, Mode = mode, ProfileId = "dev", EnvironmentId = "env-1",
            Enabled = enabled, RequiredForRelease = required,
            Steps = wellFormed
                ? [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible, IsFinalAssertion = true }]
                : [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Click }],
        };

    private static CriticalE2ERunResult Run(string flowId, CriticalE2EStatus status, string? buildId = "12345", int minutesAgo = 1) => new()
    {
        RunId = $"{flowId}-run", FlowId = flowId, EnvironmentId = "env-1", Status = status,
        StartedAt = Now.AddMinutes(-minutesAgo), BuildId = buildId, RequiredForRelease = true,
    };

    private static CriticalE2EReleaseStatus Release(IReadOnlyList<CriticalE2EFlowDefinition> flows,
        IReadOnlyList<CriticalE2ERunResult> history, string? buildId = "12345") =>
        CriticalE2ECoverage.Release(flows, history, Modules, "env-1", buildId, null);

    [Fact]
    public void OneRequiredFlowCoversItsModule()
    {
        var modules = CriticalE2ECoverage.Modules([Flow("f1", "Person")], [], Modules, "12345");
        modules.Single(m => m.Module == "Person").Covered.Should().BeTrue();
        modules.Single(m => m.Module == "Tjeneste").Covered.Should().BeFalse("a module with no required flow is not covered");
    }

    [Fact]
    public void SeveralFlowsOnOneModuleDoNotInflateTheModuleCount()
    {
        var modules = CriticalE2ECoverage.Modules([Flow("f1", "Person"), Flow("f2", "Person"), Flow("f3", "Person")], [], Modules, "12345");
        modules.Count(m => m.Covered).Should().Be(1, "counting flows would let one well-tested module hide two untested ones");
        modules.Should().HaveCount(3);
    }

    [Fact]
    public void ConfiguredIsNotPassed()
    {
        var summary = CriticalE2ECoverage.Summarize(Flow("f1", "Person"), [], "12345");
        summary.Configured.Should().BeTrue();
        summary.LastStatus.Should().Be(CriticalE2EStatus.NotRun);
        Release([Flow("f1", "Person")], []).Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete);
    }

    [Fact]
    public void BothExecutionModesCountAsRealCoverage()
    {
        var flows = new[] { Flow("f1", "Person", mode: CriticalE2EExecutionMode.AutomatedIntegration), Flow("f2", "Tjeneste") };
        CriticalE2ECoverage.Modules(flows, [], Modules, "12345").Count(m => m.Covered).Should().Be(2);
    }

    [Fact]
    public void AFlowWithoutAFinalAssertionIsReportedAsMisconfiguredBeforeItEverRuns()
    {
        var summary = CriticalE2ECoverage.Summarize(Flow("f1", "Person", wellFormed: false), [], "12345");
        summary.Configured.Should().BeFalse();
        summary.ConfigurationProblem.Should().Contain("final business assertion");
    }

    [Fact]
    public void AModeAndItsStepsMustAgree()
    {
        var mixed = Flow("f1", "Person", mode: CriticalE2EExecutionMode.AutomatedIntegration);
        CriticalE2ECoverage.ConfigurationProblem(mixed).Should().Contain("cannot contain browser steps");
    }

    [Fact]
    public void AllRequiredFlowsPassingAgainstThisBuildIsReady()
    {
        var flows = new[] { Flow("f1", "Person"), Flow("f2", "Tjeneste") };
        var status = Release(flows, [Run("f1", CriticalE2EStatus.Passed), Run("f2", CriticalE2EStatus.Passed)]);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);
        status.RequiredFlowsPassed.Should().Be(2);
    }

    [Fact]
    public void ARequiredFlowThatHasNotRunIsIncompleteNotBlocked()
    {
        var flows = new[] { Flow("f1", "Person"), Flow("f2", "Tjeneste") };
        var status = Release(flows, [Run("f1", CriticalE2EStatus.Passed)]);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete, "nothing is known to be wrong yet");
        status.RequiredFlowsPending.Should().Be(1);
    }

    [Fact]
    public void ARequiredFlowThatFailedBlocksTheRelease()
    {
        var status = Release([Flow("f1", "Person")], [Run("f1", CriticalE2EStatus.Failed)]);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);
        status.RequiredFlowsFailed.Should().Be(1);
    }

    [Fact]
    public void ARequiredFlowThatCouldNotRunAlsoBlocksTheRelease()
    {
        var status = Release([Flow("f1", "Person")], [Run("f1", CriticalE2EStatus.Blocked)]);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);
        status.Summary.Should().Contain("could not run");
    }

    [Fact]
    public void APassFromAnEarlierBuildDoesNotSatisfyThisBuild()
    {
        var status = Release([Flow("f1", "Person")], [Run("f1", CriticalE2EStatus.Passed, buildId: "12344")], buildId: "12345");
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete);
        status.RequiredFlowsPending.Should().Be(1, "a green run from a build nobody is shipping says nothing about the build they are");
    }

    [Fact]
    public void WithNoBuildSelectedTheMostRecentResultIsTheOneReported()
    {
        // Looking at the capability rather than gating a release: any recent result is informative.
        var status = Release([Flow("f1", "Person")], [Run("f1", CriticalE2EStatus.Passed, buildId: "anything")], buildId: null);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);
    }

    [Fact]
    public void AnOptionalFlowNeverBlocksTheRelease()
    {
        var flows = new[] { Flow("f1", "Person"), Flow("f2", "Tjeneste", required: false) };
        var status = Release(flows, [Run("f1", CriticalE2EStatus.Passed), Run("f2", CriticalE2EStatus.Failed)]);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);
        status.RequiredFlowsTotal.Should().Be(1);
    }

    [Fact]
    public void ADisabledFlowIsNotARequiredFlow()
    {
        var status = Release([Flow("f1", "Person", enabled: false)], []);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.NotConfigured);
    }

    [Fact]
    public void ARequiredFlowThatCannotRunAsConfiguredBlocksTheReleaseAndSaysWhy()
    {
        var status = Release([Flow("f1", "Person", wellFormed: false)], []);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);
        status.Summary.Should().Contain("cannot run as configured");
    }

    [Fact]
    public void NoRequiredFlowsMeansTheGateSaysNothingRatherThanReady()
    {
        Release([Flow("f1", "Person", required: false)], []).Disposition
            .Should().Be(CriticalE2EReleaseDisposition.NotConfigured, "an empty gate is not a passed gate");
    }

    [Fact]
    public void LatestResultWins()
    {
        var history = new[] { Run("f1", CriticalE2EStatus.Failed, minutesAgo: 5), Run("f1", CriticalE2EStatus.Passed, minutesAgo: 1) };
        CriticalE2ECoverage.Summarize(Flow("f1", "Person"), history, "12345").LastStatus.Should().Be(CriticalE2EStatus.Passed);
    }

    [Fact]
    public void AModuleWithNoFlowAtAllStillAppearsSoItsGapIsVisible()
    {
        CriticalE2ECoverage.Modules([], [], Modules, "12345")
            .Should().HaveCount(3).And.OnlyContain(m => !m.Covered);
    }
}
