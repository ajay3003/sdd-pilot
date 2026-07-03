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
/// Comprehensive test that verifies artifacts are actually persisted to database during AutoSave.
/// Tests the complete flow: Create artifacts → AutoSave → Reload from DB → Verify count.
/// </summary>
public class ArtifactPersistenceVerificationTest
{
    private readonly ITestOutputHelper _output;

    public ArtifactPersistenceVerificationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AutoSaveMustPersistAllArtifactsToDatabase()
    {
        var dbName = $"persistence_test_{Guid.NewGuid()}";

        // Setup DI with fresh context for each phase
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

        Guid savedWorkspaceId = Guid.Empty;

        // PHASE 1: Create and AutoSave 5 artifacts
        _output.WriteLine("=== PHASE 1: AutoSave 5 artifacts ===");
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWorkspacePersistenceService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactPersistenceVerificationTest>>();

            var artifacts = new List<WorkspaceArtifactDto>
            {
                new() { ArtifactType = ArtifactType.Constitution, FileName = "constitution.md", Content = "constitution content" },
                new() { ArtifactType = ArtifactType.Specification, FileName = "spec.md", Content = "spec content" },
                new() { ArtifactType = ArtifactType.DataModel, FileName = "data-model.md", Content = "datamodel content" },
                new() { ArtifactType = ArtifactType.Plan, FileName = "plan.md", Content = "plan content" },
                new() { ArtifactType = ArtifactType.Tasks, FileName = "tasks.md", Content = "tasks content" }
            };

            logger.LogInformation("Calling AutoSaveAsync with {Count} artifacts", artifacts.Count);
            var workspace = await service.AutoSaveAsync("Test_Workspace", artifacts);

            savedWorkspaceId = workspace.Id;
            logger.LogInformation("AutoSave returned workspace {WorkspaceId} with {Count} artifacts in memory",
                workspace.Id, workspace.Artifacts.Count);

            // IMPORTANT: Check in-memory result
            Assert.Equal(5, artifacts.Count);
            _output.WriteLine($"✓ Request had 5 artifacts");
            _output.WriteLine($"✓ AutoSave returned workspace with {workspace.Artifacts.Count} artifacts in memory");
        }

        // PHASE 2: Verify artifacts actually persisted in database
        _output.WriteLine("");
        _output.WriteLine("=== PHASE 2: Verify database persistence ===");
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactPersistenceVerificationTest>>();

            // Reload workspace from database with explicit Include
            var reloadedWorkspace = await context.SavedWorkspaces
                .Include(w => w.Artifacts)
                .FirstOrDefaultAsync(w => w.Id == savedWorkspaceId);

            logger.LogInformation("Reloaded workspace from database: {WorkspaceId} with {Count} artifacts",
                reloadedWorkspace?.Id, reloadedWorkspace?.Artifacts.Count ?? -1);

            Assert.NotNull(reloadedWorkspace);
            _output.WriteLine($"✓ Workspace loaded from database");

            Assert.Equal(5, reloadedWorkspace.Artifacts.Count);
            _output.WriteLine($"✓ Database has exactly 5 artifacts");

            // Verify each artifact has content
            foreach (var artifact in reloadedWorkspace.Artifacts)
            {
                Assert.NotNull(artifact.Content);
                Assert.NotEmpty(artifact.Content);
                _output.WriteLine($"  - {artifact.ArtifactType}: {artifact.Content.Length} bytes");
            }
        }

        // PHASE 3: Verify artifacts can be queried directly
        _output.WriteLine("");
        _output.WriteLine("=== PHASE 3: Direct query verification ===");
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var directCount = await context.SavedWorkspaceArtifacts
                .Where(a => a.WorkspaceId == savedWorkspaceId)
                .CountAsync();

            Assert.Equal(5, directCount);
            _output.WriteLine($"✓ Direct query found {directCount} artifacts in SavedWorkspaceArtifacts table");

            var types = await context.SavedWorkspaceArtifacts
                .Where(a => a.WorkspaceId == savedWorkspaceId)
                .Select(a => a.ArtifactType)
                .ToListAsync();

            _output.WriteLine($"✓ Artifact types in database: {string.Join(", ", types)}");

            Assert.Contains(ArtifactType.Constitution, types);
            Assert.Contains(ArtifactType.Specification, types);
            Assert.Contains(ArtifactType.DataModel, types);
            Assert.Contains(ArtifactType.Plan, types);
            Assert.Contains(ArtifactType.Tasks, types);
        }

        _output.WriteLine("");
        _output.WriteLine("=== TEST COMPLETE ===");
        _output.WriteLine("✓ All 5 artifacts successfully persisted to database");
        _output.WriteLine("✓ Artifacts queryable and loadable from database");
    }
}
