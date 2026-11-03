using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>A performance run records the source snapshot its scenario came from, so it stays reproducible after newer snapshots are imported.</summary>
public sealed class PerformanceRunProvenanceTests
{
    private static PerformanceTestDefinition Definition(Guid? stepSnapshot, Guid? contractSnapshot) => new()
    {
        Scenario = new PerformanceScenario
        {
            ContractSnapshotId = contractSnapshot,
            Steps = [new HttpPerformanceStep { RelativePath = "/a" }, new HttpPerformanceStep { RelativePath = "/b", Source = stepSnapshot is null ? null : new ScenarioStepSource { SnapshotId = stepSnapshot } }],
        },
    };

    [Fact]
    public void The_steps_source_snapshot_is_the_provenance_then_the_contract_snapshot_and_none_for_hand_written_scenarios()
    {
        var step = Guid.NewGuid();
        var contract = Guid.NewGuid();

        PerformanceTestExecutionService.ProvenanceSnapshot(Definition(step, contract)).Should().Be(step);
        PerformanceTestExecutionService.ProvenanceSnapshot(Definition(null, contract)).Should().Be(contract);
        PerformanceTestExecutionService.ProvenanceSnapshot(Definition(null, null)).Should().BeNull("a hand-written scenario has no source snapshot");
    }
}
