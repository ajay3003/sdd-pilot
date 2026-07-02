using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BirkNext.Api.Tests.Services;

public class WorkspacePersistenceServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IWorkspacePersistenceService _service;
    private readonly ILogger<WorkspacePersistenceService> _logger;

    public WorkspacePersistenceServiceTests()
    {
        // Use in-memory database for testing
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(options);

        // Create a test logger factory
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<WorkspacePersistenceService>();

        _service = new WorkspacePersistenceService(_db, _logger);

        // Create tables
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db?.Dispose();
    }

    // Test 1: Save workspace as creates new workspace in database
    [Fact]
    public async Task SaveAsAsync_CreatesNewWorkspace()
    {
        // Arrange
        var name = "Test Workspace";

        // Act
        var result = await _service.SaveAsAsync(name);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(name, result.Name);
        Assert.Equal(1, await _db.SavedWorkspaces.CountAsync());
    }

    // Test 2: Save current workspace when no current workspace creates new
    [Fact]
    public async Task SaveCurrentAsync_WithoutCurrent_CreatesNew()
    {
        // Arrange & Act
        var result = await _service.SaveCurrentAsync("Auto Workspace");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Auto Workspace", result.Name);
    }

    // Test 3: Load workspace retrieves saved workspace
    [Fact]
    public async Task LoadAsync_RetrievesWorkspace()
    {
        // Arrange
        var created = await _service.SaveAsAsync("Load Test");

        // Act
        var loaded = await _service.LoadAsync(created.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("Load Test", loaded.Name);
    }

    // Test 4: List returns all non-deleted workspaces for user
    [Fact]
    public async Task ListAsync_ReturnsAllWorkspaces()
    {
        // Arrange
        await _service.SaveAsAsync("WS1");
        await _service.SaveAsAsync("WS2");
        await _service.SaveAsAsync("WS3");

        // Act
        var list = await _service.ListAsync("default-user");

        // Assert
        Assert.NotNull(list);
        Assert.Equal(3, list.Count);
    }

    // Test 5: Rename updates workspace name
    [Fact]
    public async Task RenameAsync_UpdatesWorkspaceName()
    {
        // Arrange
        var created = await _service.SaveAsAsync("Original Name");
        var id = created.Id;

        // Act
        var renamed = await _service.RenameAsync(id, "New Name");

        // Assert
        Assert.NotNull(renamed);
        Assert.Equal("New Name", renamed.Name);
        var verified = await _service.LoadAsync(id);
        Assert.Equal("New Name", verified.Name);
    }

    // Test 6: Duplicate creates copy with new name
    [Fact]
    public async Task DuplicateAsync_CreatesCopyWithNewName()
    {
        // Arrange
        var original = await _service.SaveAsAsync("Original");
        var originalId = original.Id;

        // Act
        var duplicate = await _service.DuplicateAsync(originalId, "Copy");

        // Assert
        Assert.NotNull(duplicate);
        Assert.NotEqual(originalId, duplicate.Id);
        Assert.Equal("Copy", duplicate.Name);
        Assert.Equal(2, await _db.SavedWorkspaces.CountAsync(w => !w.IsDeleted));
    }

    // Test 7: Delete soft-deletes workspace
    [Fact]
    public async Task DeleteAsync_SoftDeletesWorkspace()
    {
        // Arrange
        var workspace = await _service.SaveAsAsync("To Delete");
        var id = workspace.Id;

        // Act
        await _service.DeleteAsync(id);

        // Assert
        var deleted = await _db.SavedWorkspaces.FindAsync(id);
        Assert.NotNull(deleted);
        Assert.True(deleted.IsDeleted);
    }

    // Test 8: Auto-save creates or updates with AutoSaved flag
    [Fact]
    public async Task AutoSaveAsync_WithoutCurrent_CreatesWithAutoSavedFlag()
    {
        // Arrange & Act
        var result = await _service.AutoSaveAsync("AutoSave_1");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.AutoSaved);
    }

    // Test 9: Set current workspace tracks current workspace ID
    [Fact]
    public async Task SetCurrentWorkspaceAsync_TracksCurrent()
    {
        // Arrange
        var workspace = await _service.SaveAsAsync("Current Test");

        // Act
        await _service.SetCurrentWorkspaceAsync(workspace.Id);
        var currentId = await _service.GetCurrentWorkspaceIdAsync();

        // Assert
        Assert.Equal(workspace.Id, currentId);
    }

    // Test 10: Clear current workspace unsets current
    [Fact]
    public async Task ClearCurrentWorkspaceAsync_UnsetsCurrent()
    {
        // Arrange
        var workspace = await _service.SaveAsAsync("To Clear");
        await _service.SetCurrentWorkspaceAsync(workspace.Id);

        // Act
        await _service.ClearCurrentWorkspaceAsync();
        var currentId = await _service.GetCurrentWorkspaceIdAsync();

        // Assert
        Assert.Null(currentId);
    }

    // Test 11: Compute artifact set hash creates consistent hash
    [Fact]
    public async Task ComputeArtifactSetHashAsync_ComputesConsistentHash()
    {
        // Arrange
        var workspace = await _service.SaveAsAsync("Hash Test");

        // Act
        var hash1 = await _service.ComputeArtifactSetHashAsync(workspace.Id);
        var hash2 = await _service.ComputeArtifactSetHashAsync(workspace.Id);

        // Assert
        Assert.NotNull(hash1);
        Assert.Equal(hash1, hash2);
    }

    // Test 12: Export JSON includes all workspace data
    [Fact]
    public async Task ExportJsonAsync_IncludesAllWorkspaceData()
    {
        // Arrange
        var workspace = await _service.SaveAsAsync("Export Test");

        // Act
        var json = await _service.ExportJsonAsync(workspace.Id);

        // Assert
        Assert.NotNull(json);
        Assert.Contains("schemaVersion", json);
        Assert.Contains("1.0", json);
        Assert.Contains("Export Test", json);
        Assert.Contains("workspace", json);
        Assert.Contains("artifacts", json);
    }

    // Test 13: Import JSON creates workspace from valid export
    [Fact]
    public async Task ImportJsonAsync_ImportsValidExport()
    {
        // Arrange
        var created = await _service.SaveAsAsync("Import Test");
        var json = await _service.ExportJsonAsync(created.Id);

        // Clear current workspace
        await _service.ClearCurrentWorkspaceAsync();

        // Act
        var imported = await _service.ImportJsonAsync(json);

        // Assert
        Assert.NotNull(imported);
        Assert.Equal("Import Test", imported.Name);
        Assert.Contains("Import", imported.Name);
    }
}
