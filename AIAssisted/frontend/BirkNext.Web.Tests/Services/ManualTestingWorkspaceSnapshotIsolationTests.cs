using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// DEF-V12-04 (found 2026-11-28 by the isolated browser journeys STALE-01/STALE-02): a documents-only import (no source snapshot of its own) took the
/// Target Environment project's main snapshot for «Finn og forstå». When that project is another application, the server read the OTHER application's
/// specification and stored its scenarios under the shown import's project key. The environment project's packages now stand in only for the same
/// application (the same project in the Project list), or when that project's own import is shown.
/// </summary>
public sealed class ManualTestingWorkspaceSnapshotIsolationTests
{
    private static readonly Guid Source = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ProjectEvidencePackage Package(string importId, Guid? snapshot) =>
        new(importId, importId, $"{importId}.zip", new string('a', 64), DateTimeOffset.UtcNow, snapshot, [], new Dictionary<WorkspaceArtifactType, int>(), 1, [], [], [], null);

    private static IServiceProvider Services(string shownImport, string? shownApplication, string? mainApplication)
    {
        var main = Package("dorado", Source);
        var view = new ProjectEvidenceView("dev", "Development", new EnvironmentProject { ProjectName = "Dorado", ProjectImportId = "dorado" }, main, [], [],
            [main, Package("cirrus", null)], []);
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(view);
        var explorer = new Mock<IArtifactExplorerContext>();
        explorer.SetupGet(e => e.CurrentScope).Returns($"import:{shownImport}");
        var lifecycle = new SddLifecycleState
        {
            ProjectImports =
            [
                new SddProjectImportRecord { ImportId = "dorado", ApplicationId = mainApplication },
                new SddProjectImportRecord { ImportId = "cirrus", ApplicationId = shownApplication },
            ],
        };
        var session = new Mock<IWorkspaceSessionService>();
        session.SetupGet(s => s.SddLifecycle).Returns(lifecycle);
        return new ServiceCollection().AddSingleton(evidence.Object).AddSingleton(explorer.Object).AddSingleton(session.Object).BuildServiceProvider();
    }

    [Fact]
    public void A_documents_only_import_of_another_application_never_borrows_the_environment_projects_snapshot()
    {
        var services = Services("cirrus", "app-cirrus", "app-dorado");
        ManualTestingWorkspace.ResolveSnapshot(services).Id.Should().BeNull("Cirrus has no source; Dorado's is another application's");
        ManualTestingWorkspace.MemberSnapshots(services).Should().BeEmpty();
    }

    [Fact]
    public void The_same_application_or_the_environment_projects_own_import_still_uses_its_snapshot()
    {
        ManualTestingWorkspace.ResolveSnapshot(Services("cirrus", "app-dorado", "app-dorado")).Id.Should().Be(Source, "both imports are one project in the Project list");
        ManualTestingWorkspace.ResolveSnapshot(Services("dorado", "app-cirrus", "app-dorado")).Id.Should().Be(Source, "the shown import is the project's own");
        ManualTestingWorkspace.MemberSnapshots(Services("cirrus", "app-dorado", "app-dorado")).Should().Equal(Source);
    }

    [Fact]
    public void Without_a_recorded_project_for_the_shown_import_nothing_is_borrowed()
    {
        ManualTestingWorkspace.ResolveSnapshot(Services("cirrus", null, null)).Id.Should().BeNull("no recorded grouping is never «the same application»");
    }
}
