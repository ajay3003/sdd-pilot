using BirkNext.Api.Services.ProjectImport;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Tests.ProjectImport;

public sealed class ProjectCompatibilityDiagnosticServiceTests
{
    [Fact]
    public void Run_UsesGeneratedArchivesAndProductionDiscovery_WithoutImportingOrCreatingPersistentState()
    {
        var service = Service();

        var result = service.Run();

        result.RunId.Should().NotBeEmpty();
        result.RealProjectFixtureUsed.Should().BeFalse();
        result.Scenarios.Should().Contain(s => s.Name == "Optional real-project acceptance" && s.Status == ProjectCompatibilityStatus.NotRun);
        result.OverallStatus.Should().Be(ProjectCompatibilityStatus.Partial, "unsupported technology is honestly identified as partial. Scenarios: {0}",
            string.Join(" | ", result.Scenarios.Select(s => $"{s.Name}: {s.Status} ({s.ObservedBehavior} {s.Notes})")));
        result.Scenarios.Should().HaveCountGreaterThanOrEqualTo(8);
        result.Scenarios.Should().Contain(s => s.Name == "Renamed Markdown documents" && s.Status == ProjectCompatibilityStatus.Pass);
        result.Scenarios.Should().Contain(s => s.Name == "Moved and nested documents" && s.Status == ProjectCompatibilityStatus.Pass);
        result.Scenarios.Should().Contain(s => s.Name == "Ambiguous specification" && s.Status == ProjectCompatibilityStatus.Pass);
        result.Scenarios.Should().Contain(s => s.Name == "Documents only" && s.Status == ProjectCompatibilityStatus.Pass);
        result.Scenarios.Should().Contain(s => s.Name == "Source only" && s.Status == ProjectCompatibilityStatus.Pass);
        result.Scenarios.Should().Contain(s => s.Name == "Unsupported technology" && s.Status == ProjectCompatibilityStatus.Partial);
        result.Scenarios.Should().NotContain(s => s.Status == ProjectCompatibilityStatus.Fail);
        result.CompletedAt.Should().BeOnOrAfter(result.StartedAt);
    }

    [Fact]
    public void Run_IsDeterministicInScenarioIdentityAndHasNoProjectSpecificNames()
    {
        var service = Service();

        var first = service.Run();
        var second = service.Run();

        first.Scenarios.Select(s => (s.Name, s.Status)).Should().Equal(second.Scenarios.Select(s => (s.Name, s.Status)));
        first.Scenarios.Select(s => s.Name).Should().NotContain(n => n.Contains("M2LB", StringComparison.OrdinalIgnoreCase));
        first.Scenarios.Select(s => s.Name).Should().NotContain(n => n.Contains("PersonAdapter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConcurrentRuns_UseIndependentRunIdsAndScenarioCollections()
    {
        var service = Service();

        var runs = await Task.WhenAll(Task.Run(() => service.Run()), Task.Run(() => service.Run()));

        runs.Select(r => r.RunId).Distinct().Should().HaveCount(2);
        runs[0].Scenarios.Select(s => (s.Name, s.Status)).Should().Equal(runs[1].Scenarios.Select(s => (s.Name, s.Status)));
    }

    private static ProjectCompatibilityDiagnosticService Service() => new(new ConfigurationBuilder().Build());
}
