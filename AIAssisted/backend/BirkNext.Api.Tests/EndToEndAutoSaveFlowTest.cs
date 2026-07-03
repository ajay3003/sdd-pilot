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
/// End-to-end integration test that simulates the complete browser flow:
/// 1. Load 5 artifacts into repository (simulating SampleProjects.LoadArtifacts)
/// 2. Call AutoSave with those artifacts
/// 3. Verify artifacts persisted to database
/// 4. Verify Workspace Manager can load and display them
/// </summary>
public class EndToEndAutoSaveFlowTest
{
    private readonly ITestOutputHelper _output;

    public EndToEndAutoSaveFlowTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CompleteFlowLoadArtifactsAutoSaveVerifyInWorkspaceManager()
    {
        var dbName = $"e2e_test_{Guid.NewGuid()}";

        // Setup DI with all required services
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddLogging(builder => builder.AddSimpleConsole());
        services.AddScoped<IWorkspacePersistenceService, WorkspacePersistenceService>();

        var provider = services.BuildServiceProvider();

        // Ensure database is created
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Database.EnsureCreated();
        }

        Guid savedWorkspaceId = Guid.Empty;

        _output.WriteLine("=== STEP 1: Simulate SampleProjects.LoadArtifacts ===");
        _output.WriteLine("Creating 5 artifacts to simulate LoadArtifacts behavior");

        // Create the 5 artifacts that LoadArtifacts would load
        var artifactsToLoad = new List<WorkspaceArtifactDto>
        {
            new()
            {
                ArtifactType = ArtifactType.Constitution,
                FileName = "constitution.md",
                Content = "constitution content from sample project"
            },
            new()
            {
                ArtifactType = ArtifactType.Specification,
                FileName = "spec.md",
                Content = "spec content from sample project"
            },
            new()
            {
                ArtifactType = ArtifactType.DataModel,
                FileName = "data-model.md",
                Content = "datamodel content from sample project"
            },
            new()
            {
                ArtifactType = ArtifactType.Plan,
                FileName = "plan.md",
                Content = "plan content from sample project"
            },
            new()
            {
                ArtifactType = ArtifactType.Tasks,
                FileName = "tasks.md",
                Content = "tasks content from sample project"
            }
        };

        _output.WriteLine($"✓ Created {artifactsToLoad.Count} artifacts");
        foreach (var artifact in artifactsToLoad)
        {
            _output.WriteLine($"  - {artifact.ArtifactType}: {artifact.Content.Length} bytes");
        }

        _output.WriteLine("");
        _output.WriteLine("=== STEP 2: Simulate WorkspaceAutoSaveService calling AutoSave ===");
        _output.WriteLine("Calling WorkspacePersistenceService.AutoSaveAsync with artifacts");

        using (var scope = provider.CreateScope())
        {
            var persistenceService = scope.ServiceProvider.GetRequiredService<IWorkspacePersistenceService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<EndToEndAutoSaveFlowTest>>();

            // This is what WorkspaceAutoSaveService.PerformAutoSaveAsync would call
            var generatedName = $"Auto_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            logger.LogInformation("Calling AutoSaveAsync with {Count} artifacts", artifactsToLoad.Count);

            var savedWorkspace = await persistenceService.AutoSaveAsync(generatedName, artifactsToLoad);

            savedWorkspaceId = savedWorkspace.Id;
            var artifactCountInResponse = savedWorkspace.Artifacts.Count;

            _output.WriteLine($"✓ AutoSaveAsync returned workspace {savedWorkspaceId}");
            _output.WriteLine($"✓ Response contains {artifactCountInResponse} artifacts (in-memory navigation property)");

            Assert.Equal(5, artifactCountInResponse);
        }

        _output.WriteLine("");
        _output.WriteLine("=== STEP 3: Verify Workspace Manager Can Load the Workspace ===");
        _output.WriteLine($"Querying database for workspace {savedWorkspaceId}");

        using (var scope = provider.CreateScope())
        {
            var persistenceService = scope.ServiceProvider.GetRequiredService<IWorkspacePersistenceService>();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<EndToEndAutoSaveFlowTest>>();

            // Simulate what Workspace Manager does: load workspace from database
            var loadedWorkspace = await context.SavedWorkspaces
                .Include(w => w.Artifacts)
                .FirstOrDefaultAsync(w => w.Id == savedWorkspaceId && !w.IsDeleted);

            Assert.NotNull(loadedWorkspace);
            _output.WriteLine($"✓ Workspace loaded from database: Id={loadedWorkspace.Id}, Name={loadedWorkspace.Name}");

            var loadedArtifactCount = loadedWorkspace.Artifacts.Count;
            _output.WriteLine($"✓ Workspace contains {loadedArtifactCount} artifacts from database");

            Assert.Equal(5, loadedArtifactCount);

            // Verify each artifact is complete
            _output.WriteLine("✓ Verifying each artifact:");
            foreach (var artifact in loadedWorkspace.Artifacts)
            {
                Assert.NotNull(artifact.Content);
                Assert.NotEmpty(artifact.Content);
                _output.WriteLine($"    - {artifact.ArtifactType}: {artifact.Content.Length} bytes, fileName={artifact.FileName}");
            }
        }

        _output.WriteLine("");
        _output.WriteLine("=== STEP 4: Verify Workspace Manager List Shows the Workspace ===");
        _output.WriteLine("Calling ListAsync to simulate Workspace Manager loading workspace list");

        using (var scope = provider.CreateScope())
        {
            var persistenceService = scope.ServiceProvider.GetRequiredService<IWorkspacePersistenceService>();

            // Simulate what Workspace Manager does: list all workspaces
            var workspaces = await persistenceService.ListAsync("default-user");

            Assert.NotEmpty(workspaces);
            _output.WriteLine($"✓ Found {workspaces.Count} workspace(s) for user");

            var ourWorkspace = workspaces.FirstOrDefault(w => w.Id == savedWorkspaceId);
            Assert.NotNull(ourWorkspace);
            _output.WriteLine($"✓ Auto-saved workspace found in list");
            _output.WriteLine($"  - Name: {ourWorkspace.Name}");
            _output.WriteLine($"  - AutoSaved: {ourWorkspace.AutoSaved}");
            _output.WriteLine($"  - CreatedAt: {ourWorkspace.CreatedAt}");
            _output.WriteLine($"  - Artifacts: {ourWorkspace.Artifacts.Count}");

            Assert.True(ourWorkspace.AutoSaved);
            Assert.Equal(5, ourWorkspace.Artifacts.Count);
        }

        _output.WriteLine("");
        _output.WriteLine("=== STEP 5: Direct Database Query Verification ===");
        _output.WriteLine("Querying SavedWorkspaceArtifacts table directly");

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var allArtifacts = await context.SavedWorkspaceArtifacts
                .Where(a => a.WorkspaceId == savedWorkspaceId)
                .ToListAsync();

            _output.WriteLine($"✓ Found {allArtifacts.Count} artifacts in database table");

            var types = allArtifacts.Select(a => a.ArtifactType).Distinct().ToList();
            _output.WriteLine($"✓ Artifact types: {string.Join(", ", types)}");

            Assert.Equal(5, allArtifacts.Count);
            Assert.Contains(ArtifactType.Constitution, types);
            Assert.Contains(ArtifactType.Specification, types);
            Assert.Contains(ArtifactType.DataModel, types);
            Assert.Contains(ArtifactType.Plan, types);
            Assert.Contains(ArtifactType.Tasks, types);

            // Verify content integrity
            foreach (var artifact in allArtifacts)
            {
                Assert.NotNull(artifact.Content);
                Assert.NotEmpty(artifact.Content);
                _output.WriteLine($"    - {artifact.ArtifactType}: {artifact.Content.Length} bytes");
            }
        }

        _output.WriteLine("");
        _output.WriteLine("=== TEST COMPLETE ===");
        _output.WriteLine("✓ All 5 artifacts successfully saved via AutoSave");
        _output.WriteLine("✓ Artifacts persisted to database");
        _output.WriteLine("✓ Workspace Manager can load and display workspace with artifacts");
        _output.WriteLine("✓ Direct database queries confirm all artifacts are present and complete");
    }
}
