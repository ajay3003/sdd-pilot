using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// PHASE 2D: Lifecycle integration test for ReviewContextProvider.
///
/// Verifies that ReviewContext is rebuilt exactly once per logical workspace update:
/// - One RestoreWorkspaceAsync call = one rebuild
/// - One LoadArtifacts batch (BeginUpdate → Set × N → NotifyMutation → EndUpdate) = one rebuild
/// - Concurrent updates within a batch = one rebuild
///
/// This test validates the contract between:
/// - IWorkspaceUpdateCoordinator (batching, deferred events)
/// - ReviewContextProvider (subscribes to ArtifactsChanged, rebuilds on each event)
/// - WorkspaceSessionRestoreService (triggers rebuild after artifacts loaded)
/// </summary>
public class ReviewContextLifecycleIntegrationTest
{
    private readonly ITestOutputHelper _output;

    public ReviewContextLifecycleIntegrationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Setup Helpers

    private (IWorkspaceArtifactRepository repository, IWorkspaceUpdateCoordinator coordinator, ReviewContextProvider provider, Mock<EventHandler> rebuilds) SetupProvider()
    {
        var repository = new WorkspaceArtifactRepository();
        var coordinator = new WorkspaceUpdateCoordinator();

        // Create mocks for analysis services
        var constitutionService = new Mock<IConstitutionAnalysisService>();
        constitutionService
            .Setup(x => x.Parse(It.IsAny<string>()))
            .Returns((string _) => new ConstitutionDocument());

        var planService = new Mock<IPlanAnalysisService>();
        planService
            .Setup(x => x.Parse(It.IsAny<string>()))
            .Returns((string _) => new PlanDocument());

        var dataModelService = new Mock<IDataModelAnalysisService>();
        dataModelService
            .Setup(x => x.Parse(It.IsAny<string>()))
            .Returns((string _) => new DataModelDocument());

        var provider = new ReviewContextProvider(
            repository,
            coordinator,
            constitutionService.Object,
            planService.Object,
            dataModelService.Object,
            new MockLogger<ReviewContextProvider>());

        var rebuilds = new Mock<EventHandler>();
        provider.ReviewContextChanged += rebuilds.Object;

        return (repository, coordinator, provider, rebuilds);
    }

    #endregion

    #region TEST 1: Single Batch Update = Single Rebuild

    [Fact]
    public void SingleWorkspaceUpdateShouldTriggerExactlyOneRebuild()
    {
        _output.WriteLine("=== TEST 1: Single batch update triggers exactly one rebuild ===");

        var (repository, coordinator, provider, rebuildsMock) = SetupProvider();

        // Act: Simulate LoadArtifacts batch
        coordinator.BeginUpdate();
        repository.Set(WorkspaceArtifactType.Constitution, "constitution content");
        repository.Set(WorkspaceArtifactType.Specification, "spec content");
        repository.Set(WorkspaceArtifactType.Plan, "plan content");
        coordinator.NotifyMutation();
        coordinator.EndUpdate();

        // Assert: Exactly one rebuild
        _output.WriteLine("✓ Batch completed");
        rebuildsMock.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Once);
        _output.WriteLine("✓ ReviewContextChanged fired exactly once");

        var context = provider.GetCurrent();
        Assert.NotNull(context);
        _output.WriteLine("✓ ReviewContext is available");
    }

    #endregion

    #region TEST 2: Nested Batches = Single Rebuild

    [Fact]
    public void NestedBatchesShouldTriggerExactlyOneRebuild()
    {
        _output.WriteLine("=== TEST 2: Nested batches (depth > 1) trigger exactly one rebuild ===");

        var (repository, coordinator, provider, rebuildsMock) = SetupProvider();

        // Act: Simulate nested batches
        coordinator.BeginUpdate();
        _output.WriteLine("Depth: 1");

        coordinator.BeginUpdate();
        _output.WriteLine("Depth: 2");
        repository.Set(WorkspaceArtifactType.Constitution, "constitution");
        coordinator.NotifyMutation();
        coordinator.EndUpdate();
        _output.WriteLine("Depth: 1 (EndUpdate from depth 2)");

        coordinator.BeginUpdate();
        _output.WriteLine("Depth: 2 (again)");
        repository.Set(WorkspaceArtifactType.Specification, "spec");
        coordinator.NotifyMutation();
        coordinator.EndUpdate();
        _output.WriteLine("Depth: 1 (EndUpdate from depth 2)");

        coordinator.EndUpdate();
        _output.WriteLine("Depth: 0 (EndUpdate from depth 1) - should fire event");

        // Assert: Exactly one rebuild despite two nested BeginUpdate calls
        rebuildsMock.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Once);
        _output.WriteLine("✓ ReviewContextChanged fired exactly once despite nesting");

        var context = provider.GetCurrent();
        Assert.NotNull(context);
        // Mocked services return empty documents, so no requirements
        Assert.Empty(context.GetRequirementsWithoutTests());
        _output.WriteLine("✓ ReviewContext is built (mocks return empty models)");
    }

    #endregion

    #region TEST 3: Multiple Sequential Updates = Multiple Rebuilds

    [Fact]
    public void MultipleSequentialUpdatesShouldTriggerMultipleRebuilds()
    {
        _output.WriteLine("=== TEST 3: Multiple sequential updates trigger multiple rebuilds ===");

        var (repository, coordinator, provider, rebuildsMock) = SetupProvider();

        // Act: First update
        coordinator.BeginUpdate();
        repository.Set(WorkspaceArtifactType.Constitution, "constitution");
        coordinator.NotifyMutation();
        coordinator.EndUpdate();

        _output.WriteLine("✓ First update completed");

        // Act: Second update
        coordinator.BeginUpdate();
        repository.Set(WorkspaceArtifactType.Specification, "spec");
        coordinator.NotifyMutation();
        coordinator.EndUpdate();

        _output.WriteLine("✓ Second update completed");

        // Assert: Two rebuilds for two updates
        rebuildsMock.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Exactly(2));
        _output.WriteLine("✓ ReviewContextChanged fired exactly twice");

        var context = provider.GetCurrent();
        Assert.NotNull(context);
        _output.WriteLine("✓ ReviewContext reflects latest update");
    }

    #endregion

    #region TEST 4: No Mutation = No Rebuild

    [Fact]
    public void NoMutationShouldNotTriggerRebuild()
    {
        _output.WriteLine("=== TEST 4: No mutations within batch = no rebuild ===");

        var (repository, coordinator, provider, rebuildsMock) = SetupProvider();

        // Act: Batch with no mutations
        coordinator.BeginUpdate();
        _output.WriteLine("Batch started, no artifacts modified");
        // NotifyMutation() is NOT called
        coordinator.EndUpdate();

        // Assert: No rebuild occurred
        rebuildsMock.Verify(x => x(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Never);
        _output.WriteLine("✓ ReviewContextChanged was not fired");

        var context = provider.GetCurrent();
        Assert.Null(context);
        _output.WriteLine("✓ ReviewContext remains uninitialized");
    }

    #endregion

    #region TEST 5: RestoreWorkspaceAsync Triggers Rebuild

    [Fact]
    public async Task RestoreWorkspaceAsyncShouldTriggerRebuild()
    {
        _output.WriteLine("=== TEST 5: RestoreWorkspaceAsync triggers ReviewContextProvider rebuild ===");

        var repository = new WorkspaceArtifactRepository();
        var coordinator = new WorkspaceUpdateCoordinator();

        // Create mocks for analysis services
        var constitutionService = new Mock<IConstitutionAnalysisService>();
        constitutionService
            .Setup(x => x.Parse(It.IsAny<string>()))
            .Returns((string _) => new ConstitutionDocument());

        var planService = new Mock<IPlanAnalysisService>();
        planService
            .Setup(x => x.Parse(It.IsAny<string>()))
            .Returns((string _) => new PlanDocument());

        var dataModelService = new Mock<IDataModelAnalysisService>();
        dataModelService
            .Setup(x => x.Parse(It.IsAny<string>()))
            .Returns((string _) => new DataModelDocument());

        var provider = new ReviewContextProvider(
            repository,
            coordinator,
            constitutionService.Object,
            planService.Object,
            dataModelService.Object,
            new MockLogger<ReviewContextProvider>());

        var stateManager = new Mock<IWorkspaceStateManager>();

        // Create a real SampleProjectsApiService with a stub HTTP handler (no projects)
        var handler = new EmptySampleProjectsHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var sampleProjects = new SampleProjectsApiService(client);

        var restoreService = new WorkspaceSessionRestoreService(
            repository, stateManager.Object, provider, sampleProjects, new MockLogger<WorkspaceSessionRestoreService>());

        var rebuilds = new Mock<EventHandler>();
        provider.ReviewContextChanged += rebuilds.Object;

        // Arrange: Create a SavedWorkspaceDto with artifacts
        var workspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Test Workspace",
            ProjectName = "Test Project",
            Artifacts = new List<SavedWorkspaceArtifactDto>
            {
                new() { ArtifactType = "Constitution", Content = "constitution", FileName = "constitution.md" },
                new() { ArtifactType = "Specification", Content = "specification", FileName = "spec.md" }
            }
        };

        // Act: Restore workspace
        await restoreService.RestoreWorkspaceAsync(workspace);

        // Assert: Rebuild was triggered
        _output.WriteLine("✓ RestoreWorkspaceAsync completed");
        rebuilds.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Once);
        _output.WriteLine("✓ ReviewContextChanged fired once");

        var context = provider.GetCurrent();
        Assert.NotNull(context);
        // Mocks return empty documents, so no requirements
        Assert.Empty(context.GetRequirements());
        _output.WriteLine("✓ ReviewContext built from restored artifacts (mocks empty)");
    }

    #endregion

    #region TEST 6: Provider Gracefully Handles Missing Artifacts

    [Fact]
    public void ProviderShouldBuildFromPartialArtifacts()
    {
        _output.WriteLine("=== TEST 6: Provider builds gracefully from partial artifacts ===");

        var (repository, coordinator, provider, rebuildsMock) = SetupProvider();

        // Act: Load only some artifacts
        coordinator.BeginUpdate();
        repository.Set(WorkspaceArtifactType.Constitution, "constitution");
        // Skip other artifacts
        coordinator.NotifyMutation();
        coordinator.EndUpdate();

        // Assert: Rebuild succeeded
        rebuildsMock.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Once);
        _output.WriteLine("✓ Rebuild completed with partial artifacts");

        var context = provider.GetCurrent();
        Assert.NotNull(context);
        _output.WriteLine("✓ ReviewContext is available");

        // Both are present but Specification is empty (not loaded)
        Assert.NotNull(context.Constitution);
        Assert.NotNull(context.Specification);
        Assert.Empty(context.Specification.Requirements); // No requirements loaded
        _output.WriteLine("✓ Partial artifacts handled gracefully");
    }

    #endregion

    #region TEST 7: Provider Handles Rebuild Errors Gracefully

    [Fact]
    public void ProviderShouldHandleRebuildErrorsGracefully()
    {
        _output.WriteLine("=== TEST 7: Provider handles parse errors gracefully ===");

        var (repository, coordinator, provider, rebuildsMock) = SetupProvider();

        // Act: Load malformed artifact
        coordinator.BeginUpdate();
        repository.Set(WorkspaceArtifactType.Constitution, "");
        repository.Set(WorkspaceArtifactType.Specification, "");
        coordinator.NotifyMutation();
        coordinator.EndUpdate();

        // Assert: Rebuild completed despite errors
        rebuildsMock.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Once);
        _output.WriteLine("✓ Rebuild completed despite empty artifacts");

        var context = provider.GetCurrent();
        // Context should exist even if models are empty
        Assert.NotNull(context);
        _output.WriteLine("✓ ReviewContext is available (graceful degradation)");
    }

    #endregion

    #region TEST 8: Concurrent EndUpdate Calls Only Fire Event Once

    [Fact]
    public void ConcurrentEndUpdateCallsShouldFireEventOnce()
    {
        _output.WriteLine("=== TEST 8: Multiple EndUpdate calls only fire event at depth 0 ===");

        var (repository, coordinator, provider, rebuildsMock) = SetupProvider();

        // Act: Multiple BeginUpdate/EndUpdate pairs
        coordinator.BeginUpdate();
        coordinator.BeginUpdate();
        repository.Set(WorkspaceArtifactType.Constitution, "constitution");
        coordinator.NotifyMutation();
        coordinator.EndUpdate();
        // At depth 1, should not fire event yet
        _output.WriteLine("EndUpdate at depth 1 - no event should fire");
        rebuildsMock.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Never);

        coordinator.EndUpdate();
        // At depth 0, should fire event
        _output.WriteLine("EndUpdate at depth 0 - event should fire");
        rebuildsMock.Verify(h => h(It.IsAny<object>(), It.IsAny<EventArgs>()), Times.Once);
        _output.WriteLine("✓ Event fired exactly once at depth 0");
    }

    #endregion
}

/// <summary>
/// Mock logger that does nothing. Used in tests where logging isn't being validated.
/// </summary>
internal class MockLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

/// <summary>
/// Stub HTTP handler for SampleProjectsApiService that returns empty projects.
/// Used in tests where Sample Project classification is being tested.
/// </summary>
internal sealed class EmptySampleProjectsHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath.Trim('/');

        if (request.Method == HttpMethod.Get && path == "api/sample-projects")
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new List<object>());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
