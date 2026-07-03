using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests;

/// <summary>
/// Automated test that traces artifact count through the complete LoadArtifacts → AutoSave flow.
/// Reports repository identity (hash) and artifact count at each step.
/// </summary>
public class ArtifactCountTraceTest
{
    private readonly ITestOutputHelper _output;

    public ArtifactCountTraceTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TraceArtifactCountThroughAutoSaveFlow()
    {
        var dbName = $"trace_test_{Guid.NewGuid()}";

        // Setup DI
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddLogging();
        services.AddScoped<IWorkspacePersistenceService, WorkspacePersistenceService>();

        var provider = services.BuildServiceProvider();

        // Ensure DB is created
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Database.EnsureCreated();
        }

        // Now get the actual logger and service
        using (var scope = provider.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactCountTraceTest>>();
            var service = scope.ServiceProvider.GetRequiredService<IWorkspacePersistenceService>();

            logger.LogInformation("=== ARTIFACT COUNT TRACE TEST ===");
            logger.LogInformation("");

            // PHASE 1: Simulate LoadArtifacts - create artifacts
            logger.LogInformation("PHASE 1: SampleProjects.LoadArtifacts");

            var artifacts = new List<WorkspaceArtifactDto>
            {
                new() { ArtifactType = ArtifactType.Constitution, FileName = "constitution.md", Content = "constitution content" },
                new() { ArtifactType = ArtifactType.Specification, FileName = "spec.md", Content = "spec content" },
                new() { ArtifactType = ArtifactType.DataModel, FileName = "data-model.md", Content = "datamodel content" },
                new() { ArtifactType = ArtifactType.Plan, FileName = "plan.md", Content = "plan content" },
                new() { ArtifactType = ArtifactType.Tasks, FileName = "tasks.md", Content = "tasks content" }
            };

            logger.LogInformation("  RepositoryType=WorkspacePersistenceService");
            logger.LogInformation("  ArtifactCount={Count}", artifacts.Count);
            logger.LogInformation("  Artifacts={Artifacts}", string.Join(",", artifacts.Select(a => a.ArtifactType)));

            // PHASE 2: Call AutoSaveAsync (simulating what happens when auto-save triggers)
            logger.LogInformation("");
            logger.LogInformation("PHASE 2: WorkspaceAutoSaveService.PerformAutoSaveAsync");
            logger.LogInformation("  (Calls AutoSaveAsync with artifacts)");

            var workspace = await service.AutoSaveAsync("Test_Workspace_Auto", artifacts);

            logger.LogInformation("  WorkspaceId={WorkspaceId}", workspace.Id);
            logger.LogInformation("  SavedArtifactCount={Count}", workspace.Artifacts.Count);

            // PHASE 3: Verify what was persisted
            logger.LogInformation("");
            logger.LogInformation("PHASE 3: Database Verification");
            logger.LogInformation("  RequestArtifacts={Count}", artifacts.Count);
            logger.LogInformation("  SavedArtifacts={Count}", workspace.Artifacts.Count);

            // PHASE 4: Summary
            logger.LogInformation("");
            logger.LogInformation("=== ANALYSIS ===");

            if (artifacts.Count == workspace.Artifacts.Count && workspace.Artifacts.Count == 5)
            {
                logger.LogInformation("✓ SUCCESS: All 5 artifacts saved correctly");
            }
            else
            {
                logger.LogInformation("✗ FAILURE: Artifact count mismatch");
                logger.LogInformation("  Request artifacts: {Request}", artifacts.Count);
                logger.LogInformation("  Saved artifacts: {Saved}", workspace.Artifacts.Count);
            }

            // Assertions
            Assert.Equal(5, artifacts.Count);
            Assert.Equal(5, workspace.Artifacts.Count);

            foreach (var artifact in workspace.Artifacts)
            {
                Assert.NotNull(artifact.Content);
                Assert.NotEmpty(artifact.Content);
            }

            _output.WriteLine("");
            _output.WriteLine("=== TEST COMPLETE ===");
            _output.WriteLine($"Workspace ID: {workspace.Id}");
            _output.WriteLine($"Total Artifacts Saved: {workspace.Artifacts.Count}");
        }
    }
}
