using System.Text.Json;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Release evidence is build-bound. Flows run with or without a build and keep their results; only a run recorded against
/// the named build is evidence, and with no build named the release verdict is NotEvaluated — never Ready, never Failed.
/// </summary>
public sealed class CriticalE2EBuildEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Known = ["M02", "EN01", "M05"];

    private static CriticalE2EFlowDefinition Flow(string id, string module, bool required = true, bool enabled = true,
        CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical) => new()
        {
            Id = id, Module = module, Name = id, Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = "env-1",
            Enabled = enabled, RequiredForRelease = required, Kind = kind,
            Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible,
                Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "status" }, IsFinalAssertion = true }],
        };

    private static CriticalE2ERunResult Run(string flowId, CriticalE2EStatus status, string? build, int minutesAgo = 1) => new()
    {
        RunId = $"{flowId}-{build}-{minutesAgo}", FlowId = flowId, EnvironmentId = "env-1", Status = status,
        StartedAt = Now.AddMinutes(-minutesAgo), BuildId = build, RequiredForRelease = true,
    };

    private static CriticalE2EReleaseStatus Release(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history, string? build) =>
        CriticalE2ECoverage.Release(flows, history, Known, "env-1", build, null);

    private static readonly CriticalE2EFlowDefinition[] TwoRequired = [Flow("m02", "M02"), Flow("en01", "EN01")];

    // 1, 13. The key regression: a recent pass with no build is execution history, not release readiness.
    [Fact]
    public void NoBuildWithARecentPassIsNotEvaluated()
    {
        var history = new[] { Run("m02", CriticalE2EStatus.Passed, build: null) };
        var status = Release([Flow("m02", "M02")], history, build: null);

        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.NotEvaluated);
        status.Disposition.Should().NotBe(CriticalE2EReleaseDisposition.Ready).And.NotBe(CriticalE2EReleaseDisposition.Blocked);
        status.RequiredFlowsPassed.Should().Be(0, "no result is release evidence without a build");
        status.RequiredFlowsFailed.Should().Be(0);
        status.Summary.Should().Contain("No build is selected");

        // Execution history is untouched: the flow still reads Passed, it just does not match a release.
        var summary = CriticalE2ECoverage.Summarize(Flow("m02", "M02"), history, null);
        summary.LastStatus.Should().Be(CriticalE2EStatus.Passed);
        summary.LastResultMatchesRelease.Should().BeFalse();
    }

    // 2. Even when every required flow passed recently, and on some build.
    [Fact]
    public void NoBuildWithAllRequiredPassingIsStillNotReady()
    {
        var history = new[] { Run("m02", CriticalE2EStatus.Passed, "RC57"), Run("en01", CriticalE2EStatus.Passed, null) };
        Release(TwoRequired, history, build: null).Disposition.Should().Be(CriticalE2EReleaseDisposition.NotEvaluated);
        Release(TwoRequired, history, build: "  ").Disposition.Should().Be(CriticalE2EReleaseDisposition.NotEvaluated, "whitespace is not a build");
    }

    // 3.
    [Fact]
    public void BuildSetWithEveryRequiredFlowPassingOnItIsReady()
    {
        var status = Release(TwoRequired, [Run("m02", CriticalE2EStatus.Passed, "RC57"), Run("en01", CriticalE2EStatus.Passed, "rc57")], "RC57");
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready, "build ids match case-insensitively, as before");
        status.RequiredFlowsPassed.Should().Be(2);
    }

    // 4.
    [Fact]
    public void BuildSetWithOneRequiredRunMissingIsIncomplete()
    {
        var status = Release(TwoRequired, [Run("m02", CriticalE2EStatus.Passed, "RC57")], "RC57");
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete);
        status.RequiredFlowsPending.Should().Be(1);
    }

    // 5.
    [Fact]
    public void APassOnAnotherBuildDoesNotCount()
    {
        var status = Release(TwoRequired, [Run("m02", CriticalE2EStatus.Passed, "RC57"), Run("en01", CriticalE2EStatus.Passed, "RC56")], "RC57");
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete);
        status.RequiredFlowsPassed.Should().Be(1);
    }

    // 6, 7. The latest run on the named build decides; an older pass never hides a newer failure or block.
    [Theory]
    [InlineData(CriticalE2EStatus.Failed)]
    [InlineData(CriticalE2EStatus.Blocked)]
    public void TheLatestRunOnTheBuildControls(CriticalE2EStatus latest)
    {
        var history = new[] { Run("m02", CriticalE2EStatus.Passed, "RC57", minutesAgo: 10), Run("m02", latest, "RC57", minutesAgo: 1) };
        Release([Flow("m02", "M02")], history, "RC57").Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);
    }

    // A later run on another build does not erase this build's evidence.
    [Fact]
    public void ALaterRunOnAnotherBuildDoesNotEraseThisBuildsEvidence()
    {
        var history = new[] { Run("m02", CriticalE2EStatus.Passed, "RC57", minutesAgo: 10), Run("m02", CriticalE2EStatus.Failed, "RC58", minutesAgo: 1) };
        Release([Flow("m02", "M02")], history, "RC57").Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);
        Release([Flow("m02", "M02")], history, "RC58").Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);

        // The flow row agrees with the verdict: it reports the run on the named build.
        var rc57 = CriticalE2ECoverage.Summarize(Flow("m02", "M02"), history, "RC57");
        (rc57.LastStatus, rc57.LastBuildId, rc57.LastResultMatchesRelease).Should().Be((CriticalE2EStatus.Passed, "RC57", true));
        // A build with no run yet reports the latest run anywhere, which matches no release.
        var rc59 = CriticalE2ECoverage.Summarize(Flow("m02", "M02"), history, "RC59");
        (rc59.LastStatus, rc59.LastBuildId, rc59.LastResultMatchesRelease).Should().Be((CriticalE2EStatus.Failed, "RC58", false));
    }

    // 8, 9. Diagnostic passes and diagnostic modules never touch the release.
    [Fact]
    public void DiagnosticFlowsNeitherGateNorCover()
    {
        var flows = new[] { Flow("m02", "M02"), Flow("smoke", "SMOKE", kind: CriticalE2EFlowKind.Diagnostic) };
        var history = new[] { Run("smoke", CriticalE2EStatus.Passed, "RC57") };
        var status = CriticalE2ECoverage.Release(flows, history, [.. Known, "SMOKE"], "env-1", "RC57", null);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete, "the smoke pass does not stand in for M02");
        status.RequiredFlowsTotal.Should().Be(1);
        CriticalE2ECoverage.Modules(flows, history, [.. Known, "SMOKE"], "RC57").Select(m => m.Module).Should().NotContain("SMOKE");

        var onlySmoke = CriticalE2ECoverage.Release([flows[1]], history, [], "env-1", "RC57", null);
        onlySmoke.Disposition.Should().Be(CriticalE2EReleaseDisposition.NotConfigured);
    }

    // 10. Optional and disabled flows do not become obligations.
    [Fact]
    public void OnlyEnabledRequiredCriticalFlowsGate()
    {
        var flows = new[] { Flow("m02", "M02"), Flow("opt", "EN01", required: false), Flow("off", "M05", enabled: false) };
        var status = Release(flows, [Run("m02", CriticalE2EStatus.Passed, "RC57"), Run("opt", CriticalE2EStatus.Failed, "RC57")], "RC57");
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);
        status.RequiredFlowsTotal.Should().Be(1);
    }

    // 11. Configured coverage needs no build.
    [Fact]
    public void ConfiguredCoverageIsReportedWithoutABuild()
    {
        var status = Release(TwoRequired, [], build: null);
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.NotEvaluated);
        status.ModulesTotal.Should().Be(3);
        status.ModulesCovered.Should().Be(2);
        status.RequiredFlowsTotal.Should().Be(2);
    }

    // With nothing required, the gate says nothing, build or not.
    [Fact]
    public void NoRequiredFlowsIsNotConfiguredEvenWithoutABuild() =>
        Release([Flow("opt", "M02", required: false)], [], build: null).Disposition.Should().Be(CriticalE2EReleaseDisposition.NotConfigured);

    // 12. Runs of a flow that no longer exists never satisfy the current scope.
    [Fact]
    public void RunsOfARemovedFlowDoNotSatisfyTheActiveScope()
    {
        var history = new[] { Run("deleted", CriticalE2EStatus.Passed, "RC57"), Run("m02", CriticalE2EStatus.Passed, "RC57") };
        var status = Release(TwoRequired, history, "RC57");
        status.Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete, "EN01's required flow has no run on RC57");
        status.RequiredFlowsPassed.Should().Be(1);
    }

    // 13. The new state is additive and serializes by name; existing values keep their numbers.
    [Fact]
    public void NotEvaluatedIsAnAdditiveNamedValue()
    {
        JsonSerializer.Serialize(CriticalE2EReleaseDisposition.NotEvaluated).Should().Be("\"NotEvaluated\"");
        JsonSerializer.Deserialize<CriticalE2EReleaseDisposition>("\"Ready\"").Should().Be(CriticalE2EReleaseDisposition.Ready);
        ((int)CriticalE2EReleaseDisposition.Ready).Should().Be(0);
        ((int)CriticalE2EReleaseDisposition.NotConfigured).Should().Be(3);
        ((int)CriticalE2EReleaseDisposition.NotEvaluated).Should().Be(4);
    }
}
