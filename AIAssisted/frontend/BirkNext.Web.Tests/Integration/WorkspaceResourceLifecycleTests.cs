using System.Runtime.CompilerServices;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// BirkNext self-regression for workspace resource lifecycle (load A → load B → load A … N times). Lifecycle correctness, not a generic leak verdict:
/// revisions are captured for new content only, the provider holds one current ReviewContext, and superseded contexts become collectable.
/// </summary>
public sealed class WorkspaceResourceLifecycleTests
{
    private const string SpecA = "# Spec A\n\n## Requirements\n\n- **FR-001**: The system MUST register an item.\n";
    private const string SpecB = "# Spec B\n\n## Requirements\n\n- **FR-002**: The system MUST archive an item.\n";

    private static (WorkspaceArtifactRepository Repository, ReviewContextProvider Provider) Setup()
    {
        var repository = new WorkspaceArtifactRepository();
        var constitution = new Mock<IConstitutionAnalysisService>();
        constitution.Setup(x => x.Parse(It.IsAny<string>())).Returns((string _) => new ConstitutionDocument());
        var plan = new Mock<IPlanAnalysisService>();
        plan.Setup(x => x.Parse(It.IsAny<string>())).Returns((string _) => new PlanDocument());
        var data = new Mock<IDataModelAnalysisService>();
        data.Setup(x => x.Parse(It.IsAny<string>())).Returns((string _) => new DataModelDocument());
        return (repository, new ReviewContextProvider(repository, new WorkspaceUpdateCoordinator(), constitution.Object, plan.Object, data.Object, new MockLogger<ReviewContextProvider>()));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> CycleAsync(WorkspaceArtifactRepository repository, ReviewContextProvider provider, int cycles)
    {
        var contexts = new List<WeakReference>();
        for (var i = 0; i < cycles; i++)
        {
            repository.Set(WorkspaceArtifactType.Specification, i % 2 == 0 ? SpecA : SpecB, "spec.md");
            await provider.RebuildAsync();
            contexts.Add(new WeakReference(provider.GetCurrent()));
        }
        return contexts;
    }

    [Fact]
    public async Task RepeatedWorkspaceSwitching_StoresEachRevisionOnce_AndKeepsTheLifecycleBounded()
    {
        var (repository, provider) = Setup();
        await CycleAsync(repository, provider, 4);
        var afterFew = JsonSerializer.Serialize(repository.SddLifecycle).Length;
        await CycleAsync(repository, provider, 60);
        var revisions = repository.SddLifecycle.Revisions.Where(r => r.Role == "Specification").ToList();
        revisions.Should().HaveCount(2, "A and B are each stored once; switching back re-selects the existing revision");
        revisions.Count(r => r.IsCurrentSelection).Should().Be(1);
        revisions.Single(r => r.IsCurrentSelection).Content.Should().Be(SpecB);
        JsonSerializer.Serialize(repository.SddLifecycle).Length.Should().Be(afterFew, "64 switches persist no more lifecycle data than 4");
        repository.Set(WorkspaceArtifactType.Specification, SpecA + "\n- **FR-003**: new", "spec.md");
        repository.SddLifecycle.Revisions.Count(r => r.Role == "Specification").Should().Be(3, "genuinely new content is still captured");
    }

    [Fact]
    public async Task RepeatedWorkspaceSwitching_RetainsOnlyTheCurrentReviewContext()
    {
        var (repository, provider) = Setup();
        var contexts = await CycleAsync(repository, provider, 40);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var alive = contexts.Take(contexts.Count - 1).Count(w => w.IsAlive);
        alive.Should().Be(0, "superseded ReviewContexts are not retained by the provider, repository or event subscriptions");
        provider.GetCurrent().Should().NotBeNull();
        provider.Dispose();
    }
}
