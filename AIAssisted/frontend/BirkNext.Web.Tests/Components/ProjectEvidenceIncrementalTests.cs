using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Tests.Pages;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Regression for "packages imported after Project Evidence was configured are not selectable". Root causes: the Source Analysis setup was
/// cached per environment for the app's lifetime (a later package's snapshot — its contracts, infrastructure and shared-library facts — was
/// never read, so it was not eligible for those roles), and saving the checklist unbound packages whose capability was not visible at that
/// moment. Imported packages are the inventory; bindings stay explicit and survive later imports.
/// </summary>
public sealed class ProjectEvidenceIncrementalTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly ActiveTargetEnvironmentTests.Storage _js = new();
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly List<SourceSetupSnapshot> _backend = [];
    private readonly ProjectEvidenceService _service;
    private int _reads;

    public ProjectEvidenceIncrementalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _settings.LoadAsync(_js).GetAwaiter().GetResult();
        _settings.SelectActiveProfile("dev");
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { _reads++; return new ActiveEventApiResult<SourceSetupView>(new SourceSetupView { Snapshots = [.. _backend] }, null); });
        _service = new ProjectEvidenceService(_settings, _repository, journeys.Object, _js);
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton<IProjectEvidenceService>(_service);
    }

    private FrontendAnalysisProfile Profile(string id) => _settings.Settings.Profiles.Single(p => p.Id == id);

    private void Import(string id, string archive, bool contracts = false, bool library = false, bool documents = false, DateTimeOffset? at = null)
    {
        var snapshot = Guid.NewGuid();
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord
        {
            ImportId = id, ProjectName = archive[..^4], ArchiveFileName = archive, ArchiveSha256 = "sha-" + id, ImportedAt = at ?? DateTimeOffset.UtcNow,
            SourceSnapshotId = snapshot, SourceState = "Created",
        });
        if (documents) _repository.AddArtifactRevision(WorkspaceArtifactType.Specification, "# Feature Specification: X\n", $"{id}/spec.md", $"{id}/spec.md", "import:" + id, "ProjectImport", select: true);
        _backend.Add(new SourceSetupSnapshot(snapshot, archive, "sha-" + id, DateTimeOffset.UtcNow, "Ready")
        {
            ImportId = id, ServiceProjectCount = library ? 0 : 1, SharedProjectCount = library ? 2 : 0,
            Areas = [new("Application code", "Complete"), new("Contracts", contracts ? "Complete" : "NotDetected"), new("Infrastructure as Code", "NotDetected")],
        });
    }

    private async Task<ProjectEvidenceView> ConfigureAsync(string main = "a")
    {
        (await _service.SelectProjectAsync("dev", main)).Should().BeTrue();
        return await _service.GetAsync("dev");
    }

    [Fact]
    public async Task PackageImportedLater_BecomesSelectableForItsRoles_WithoutRefresh_AndExistingBindingsStay()
    {
        Import("a", "M2LB.zip", contracts: true);
        var before = await ConfigureAsync();
        before.Row(ProjectEvidenceRoles.Contracts)!.Bound.Select(b => b.ImportId).Should().Equal("a");

        // Later, more packages are imported (Import Project) while the page's cache still holds the first read.
        Import("c", "M2LB-Common.zip", contracts: true, library: true);
        Import("d", "Partner-contracts.zip", contracts: true);
        var after = await _service.GetAsync("dev");

        after.Packages.Select(p => p.ImportId).Should().BeEquivalentTo(["a", "c", "d"]);
        after.Row(ProjectEvidenceRoles.Contracts)!.Candidates.Select(c => c.Package.ImportId).Should().BeEquivalentTo(["a", "c", "d"], "their snapshots are read: contracts are visible");
        after.Row(ProjectEvidenceRoles.SupportingSource)!.Candidates.Should().Contain(c => c.Package.ImportId == "c");
        after.Packages.Single(p => p.ImportId == "c").Classification.Should().Be("Supporting/shared source", "the shared library is not misread as an application");
        after.Row(ProjectEvidenceRoles.ApplicationSource)!.Bound.Select(b => b.ImportId).Should().Equal(["a"], "importing never rebinds");
        after.Row(ProjectEvidenceRoles.Contracts)!.Bound.Select(b => b.ImportId).Should().Equal(["a"], "a new candidate is not bound silently");

        (await _service.BindAsync("dev", ProjectEvidenceRoles.Contracts, "d")).Should().BeTrue();
        (await _service.GetAsync("dev")).Row(ProjectEvidenceRoles.Contracts)!.Bound.Select(b => b.ImportId).Should().BeEquivalentTo(["a", "d"], "adding keeps the existing source");
    }

    [Fact]
    public async Task ImportOrder_DoesNotChangeWhatIsAvailable()
    {
        Import("c", "M2LB-Common.zip", contracts: true, library: true, at: DateTimeOffset.UtcNow.AddHours(-2));
        Import("a", "M2LB.zip", contracts: true, at: DateTimeOffset.UtcNow.AddHours(-1));
        var view = await ConfigureAsync();
        var capabilities = view.Packages.OrderBy(p => p.ImportId).Select(p => (p.ImportId, string.Join(",", p.Capabilities.Select(c => c.Capability)))).ToList();
        capabilities.Should().Equal(("a", "ApplicationSource,ContractSchema"), ("c", "SupportingSource,ContractSchema"));
    }

    [Fact]
    public async Task SavingTheChecklist_NeverDropsABindingWhoseCapabilityIsNotVisibleAtTheMoment()
    {
        Import("a", "M2LB.zip", contracts: true);
        var view = await ConfigureAsync();
        var project = Profile("dev").Project!;
        // Re-analysis (or a not yet refreshed read) no longer reports contracts for the bound package.
        _backend[0] = _backend[0] with { Areas = [new("Application code", "Complete"), new("Contracts", "NotDetected")] };
        _service.Invalidate();
        view = await _service.GetAsync("dev");

        var checklist = ProjectSetupPlanner.Checklist("M2LB", view.Main, view.Packages, project);
        var contracts = checklist.Single(g => g.Role.RoleKey == ProjectEvidenceRoles.Contracts);
        contracts.Items.Should().ContainSingle(i => i.Package.ImportId == "a" && i.Selected && i.Bound, "a bound package stays listed and selected");
        var decisions = checklist.Select(g => new ProjectSetupPlanner.RoleDecision(g.Role.RoleKey, [.. g.Items.Where(i => i.Selected).Select(i => i.Package.ImportId)], [])).ToList();
        ProjectSetupPlanner.Apply(project, view.Packages, decisions, DateTimeOffset.UtcNow);

        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Contracts).Sources.Select(s => s.ImportId).Should().Equal(["a"], "saving unrelated changes keeps the binding");
        (await _service.GetAsync("dev")).Row(ProjectEvidenceRoles.Contracts)!.State.Should().Be(EvidenceRoleState.Partial, "bound, but no longer satisfied: Partial, never Ready");
    }

    [Fact]
    public async Task Invalidate_ReReadsAndNotifies_AndAMissingSnapshotIsNotReReadOnEveryCall()
    {
        Import("a", "M2LB.zip");
        await ConfigureAsync();
        var notified = 0;
        _service.Changed += () => notified++;
        var reads = _reads;
        await _service.GetAsync("dev");
        _reads.Should().Be(reads, "a covering cache is reused");

        _service.Invalidate();
        notified.Should().Be(1);
        await _service.GetAsync("dev");
        _reads.Should().Be(reads + 1);

        // A record names a snapshot the backend no longer has: one re-read, not one per call.
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "gone", ArchiveFileName = "gone.zip", ArchiveSha256 = "sha-gone", SourceSnapshotId = Guid.NewGuid(), SourceState = "Created" });
        await _service.GetAsync("dev");
        await _service.GetAsync("dev");
        await _service.GetAsync("dev");
        _reads.Should().Be(reads + 2);
    }

    [Fact]
    public async Task RemovingABinding_KeepsThePackage_AndBindingsArePerTargetEnvironment()
    {
        Import("a", "M2LB.zip", contracts: true);
        Import("c", "M2LB-Common.zip", contracts: true, library: true);
        await ConfigureAsync();
        (await _service.BindAsync("dev", ProjectEvidenceRoles.SupportingSource, "c")).Should().BeTrue();
        (await _service.SelectProjectAsync("qa", "a")).Should().BeTrue();

        (await _service.GetAsync("qa")).Row(ProjectEvidenceRoles.SupportingSource)!.Bound.Should().BeEmpty("another environment keeps its own bindings");
        (await _service.UnbindAsync("dev", ProjectEvidenceRoles.SupportingSource, "c")).Should().BeTrue();
        var dev = await _service.GetAsync("dev");
        dev.Row(ProjectEvidenceRoles.SupportingSource)!.Bound.Should().BeEmpty();
        dev.Packages.Should().Contain(p => p.ImportId == "c", "unbinding never deletes the imported package");
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(2);
    }

    [Fact]
    public async Task Pane_ListsEveryImportedSource_WithItsBindings_SearchAndRefresh_AndExplainsExcludedCandidates()
    {
        Import("a", "M2LB.zip", contracts: true, documents: true);
        Import("t", "Docs-only.zip");
        _backend.RemoveAt(1);
        _repository.SddLifecycle.ProjectImports[1].SourceSnapshotId = null;
        _repository.SddLifecycle.ProjectImports[1].SourceState = "NotDetected";
        TargetApplications.Set(Profile("dev"), new TargetApplicationIdentity { Name = "M2LB", ImportId = "a", SetAt = DateTimeOffset.UtcNow }, DateTimeOffset.UtcNow);
        await ConfigureAsync();
        var cut = Render<TargetEnvironmentProjectPane>(p => p.Add(x => x.EnvironmentId, "dev"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tep-source-row]").Should().HaveCount(2));
        cut.Find("[data-testid=tep-source-total]").TextContent.Should().StartWith("2");
        cut.Find("[data-testid=tep-source-row][data-import=a] [data-testid=tep-source-binding]").TextContent.Should().Contain("Application source").And.Contain("Contracts and schemas");
        cut.Find("[data-testid=tep-source-row][data-import=t] [data-testid=tep-source-binding]").TextContent.Should().Be("Available · not bound");
        cut.Find("[data-testid=tep-source-row][data-import=t] [data-testid=tep-source-analysis]").TextContent.Should().Be("Not analyzed");

        cut.Find("[data-testid=tep-source-search]").Input("docs-only");
        cut.FindAll("[data-testid=tep-source-row]").Select(r => r.GetAttribute("data-import")).Should().Equal("t");
        cut.Find("[data-testid=tep-source-search]").Input("");

        cut.Find($"[data-testid=tep-role][data-role={ProjectEvidenceRoles.SupportingSource}] [data-testid=tep-role-action]").Click();
        cut.Find("[data-testid=tep-picker-excluded][data-import=t]").TextContent.Should().Contain("not analyzed").And.Contain("Source Analysis snapshot");

        var reads = _reads;
        cut.Find("[data-testid=tep-refresh-sources]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-notice]").TextContent.Should().Contain("Sources refreshed: 2 imported. Bindings are unchanged."));
        _reads.Should().Be(reads + 1);
    }

    [Fact]
    public async Task ReadinessSummary_ReconcilesWithTheRoleRows()
    {
        Import("a", "M2LB.zip");
        await ConfigureAsync();
        TargetApplications.Set(Profile("dev"), new TargetApplicationIdentity { Name = "M2LB", ImportId = "a", SetAt = DateTimeOffset.UtcNow }, DateTimeOffset.UtcNow);
        var view = await _service.GetAsync("dev");
        var cut = Render<TargetEnvironmentProjectPane>(p => p.Add(x => x.EnvironmentId, "dev"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-readiness]"));

        var required = view.Rows.Where(r => r.IsRequired).ToList();
        cut.Find("[data-testid=tep-readiness-ready] strong").TextContent.Should().Be(required.Count(r => r.IsReady).ToString());
        cut.Find("[data-testid=tep-readiness-partial] strong").TextContent.Should().Be(required.Count(r => r.State == EvidenceRoleState.Partial).ToString());
        cut.Find("[data-testid=tep-readiness-missing] strong").TextContent.Should().Be(required.Count(r => !r.IsResolved).ToString());
        cut.FindAll("[data-testid=tep-role]").Count(r => r.GetAttribute("data-state") is "Ready").Should().Be(view.Rows.Count(r => r.IsReady));
    }
}
