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

    [Fact]
    public void Real_project_mutation_that_only_removes_folder_conventions_is_partial_not_a_failure()
    {
        // A design note in a documentation tree's plans/ folder (as in infrastructure repositories): the plans/ folder is classifier evidence, so
        // moving and renaming the file changes its discovery by construction. That is path-dependent discovery (Partial), never a Fail.
        var design = """
            # Adapter storage shape

            **Status:** Design (approved)
            **Scope:** `terraform/layers/03-data`

            ## Context

            The adapter module has no database wiring today.

            ## Problem

            Adapters need durable storage for events.

            ## Design

            ### Item 1 — New databases

            Three new databases, one per adapter.

            ### Item 2 — Grants

            Runtime and deploy users get grants.
            """;
        var archive = Path.Combine(Path.GetTempPath(), $"compat-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var (path, content) in new[]
                {
                    ("docs/plans/2026-05-11-adapter-storage-design.md", design),
                    ("docs/plans/2026-05-13-gateway-bootstrap-design.md", design.Replace("Adapter storage shape", "Gateway bootstrap")),
                    ("src/Adapter/Adapter.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
                    ("src/Adapter/Program.cs", "class Program { static void Main() {} }"),
                })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                    writer.Write(content);
                }
            }
            var service = new ProjectCompatibilityDiagnosticService(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ProjectCompatibility:AcceptanceArchivePath"] = archive }).Build());

            var mutation = service.Run().Scenarios.Single(s => s.Name == "Optional real-project Markdown mutation");

            mutation.Status.Should().Be(ProjectCompatibilityStatus.Partial, "{0} {1}", mutation.ObservedBehavior, mutation.Notes);
            mutation.ObservedBehavior.Should().Contain("path-dependent");
        }
        finally { File.Delete(archive); }
    }

    private static ProjectCompatibilityDiagnosticService Service() => new(new ConfigurationBuilder().Build());
}
