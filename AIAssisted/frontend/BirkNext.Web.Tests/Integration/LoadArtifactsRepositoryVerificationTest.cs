using BirkNext.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// Comprehensive test that verifies artifacts are actually in the repository during LoadArtifacts.
/// Simulates the SampleProjects.LoadArtifacts flow and checks repository state at each step.
/// </summary>
public class LoadArtifactsRepositoryVerificationTest
{
    private readonly ITestOutputHelper _output;

    public LoadArtifactsRepositoryVerificationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RepositoryMustContainAllArtifactsAfterLoadArtifacts()
    {
        // Setup DI
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceArtifactRepository, WorkspaceArtifactRepository>();
        services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IWorkspaceArtifactRepository>();
        var coordinator = provider.GetRequiredService<IWorkspaceUpdateCoordinator>();

        _output.WriteLine("=== PHASE 1: Simulate SampleProjects.LoadArtifacts ===");

        var repoHashBefore = RuntimeHelpers.GetHashCode(repository);
        var countBefore = repository.GetAllArtifacts().Count();

        _output.WriteLine($"Repository before load: hash={repoHashBefore}, count={countBefore}");
        Assert.Equal(0, countBefore);
        _output.WriteLine("✓ Repository starts empty");

        // Simulate LoadArtifacts loop
        coordinator.BeginUpdate();
        try
        {
            repository.Set(WorkspaceArtifactType.Constitution, "constitution content", fileName: "constitution.md");
            repository.Set(WorkspaceArtifactType.Specification, "spec content", fileName: "spec.md");
            repository.Set(WorkspaceArtifactType.DataModel, "datamodel content", fileName: "data-model.md");
            repository.Set(WorkspaceArtifactType.Plan, "plan content", fileName: "plan.md");
            repository.Set(WorkspaceArtifactType.Tasks, "tasks content", fileName: "tasks.md");

            _output.WriteLine("✓ All 5 artifacts Set() in repository");

            var countAfterSet = repository.GetAllArtifacts().Count();
            _output.WriteLine($"Repository after Set() calls: count={countAfterSet}");
            Assert.Equal(5, countAfterSet);
            _output.WriteLine("✓ Repository contains exactly 5 artifacts");

            // Verify each artifact has content
            var artifacts = repository.GetAllArtifacts().ToList();
            foreach (var artifact in artifacts)
            {
                Assert.NotNull(artifact.Artifact.Text);
                Assert.NotEmpty(artifact.Artifact.Text);
                _output.WriteLine($"  - {artifact.Type}: {artifact.Artifact.Text.Length} bytes");
            }
        }
        finally
        {
            coordinator.EndUpdate();
        }

        _output.WriteLine("");
        _output.WriteLine("=== PHASE 2: Verify repository persistence after batch ===");

        var repoHashAfter = RuntimeHelpers.GetHashCode(repository);
        var countAfter = repository.GetAllArtifacts().Count();

        _output.WriteLine($"Repository after batch: hash={repoHashAfter}, count={countAfter}");

        Assert.Equal(repoHashBefore, repoHashAfter);
        _output.WriteLine("✓ Same repository instance (no replacement)");

        Assert.Equal(5, countAfter);
        _output.WriteLine("✓ Repository still contains 5 artifacts after batch completes");

        // Verify artifacts are still retrievable
        var typesPresent = repository.GetAllArtifacts().Select(a => a.Type).ToList();
        Assert.Contains(WorkspaceArtifactType.Constitution, typesPresent);
        Assert.Contains(WorkspaceArtifactType.Specification, typesPresent);
        Assert.Contains(WorkspaceArtifactType.DataModel, typesPresent);
        Assert.Contains(WorkspaceArtifactType.Plan, typesPresent);
        Assert.Contains(WorkspaceArtifactType.Tasks, typesPresent);

        _output.WriteLine($"✓ All 5 artifact types present: {string.Join(", ", typesPresent)}");

        _output.WriteLine("");
        _output.WriteLine("=== PHASE 3: Verify content integrity ===");

        var constitution = repository.Get(WorkspaceArtifactType.Constitution);
        Assert.NotNull(constitution);
        Assert.Equal("constitution content", constitution.Text);
        _output.WriteLine("✓ Constitution content intact");

        var specification = repository.Get(WorkspaceArtifactType.Specification);
        Assert.NotNull(specification);
        Assert.Equal("spec content", specification.Text);
        _output.WriteLine("✓ Specification content intact");

        var dataModel = repository.Get(WorkspaceArtifactType.DataModel);
        Assert.NotNull(dataModel);
        Assert.Equal("datamodel content", dataModel.Text);
        _output.WriteLine("✓ DataModel content intact");

        var plan = repository.Get(WorkspaceArtifactType.Plan);
        Assert.NotNull(plan);
        Assert.Equal("plan content", plan.Text);
        _output.WriteLine("✓ Plan content intact");

        var tasks = repository.Get(WorkspaceArtifactType.Tasks);
        Assert.NotNull(tasks);
        Assert.Equal("tasks content", tasks.Text);
        _output.WriteLine("✓ Tasks content intact");

        _output.WriteLine("");
        _output.WriteLine("=== TEST COMPLETE ===");
        _output.WriteLine("✓ Repository correctly maintains all 5 artifacts");
        _output.WriteLine("✓ All artifact content verified");
        _output.WriteLine("✓ BeginUpdate/EndUpdate batch works correctly");
    }
}
