using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The one project-scoped package catalog (Recommended Workflow + Target Environment → Project): custom package roles through a combo box,
/// protected predefined roles, recommendations matched against the authoritative import registry, never creating bindings or readiness;
/// projects never share catalog entries; older saved settings stay readable.
/// </summary>
public sealed class PackageCatalogTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly ActiveTargetEnvironmentTests.Storage _js = new();
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly PackageCatalogService _catalog;
    private readonly ProjectEvidenceService _evidence;
    private readonly List<SourceSetupSnapshot> _backend = [];

    public PackageCatalogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _settings.LoadAsync(_js).GetAwaiter().GetResult();
        _settings.SelectActiveProfile("dev");
        _catalog = new PackageCatalogService(_settings, _repository, _js);
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ActiveEventApiResult<SourceSetupView>(new SourceSetupView { Snapshots = [.. _backend] }, null));
        _evidence = new ProjectEvidenceService(_settings, _repository, journeys.Object, _js);
        Services.AddSingleton(_catalog);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
    }

    private void Import(string id, string archive, bool contracts = false)
    {
        var snapshot = Guid.NewGuid();
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord
        {
            ImportId = id, ProjectName = archive[..^4], ArchiveFileName = archive, ArchiveSha256 = "sha-" + id, ImportedAt = DateTimeOffset.UtcNow, SourceSnapshotId = snapshot, SourceState = "Created",
        });
        _backend.Add(new SourceSetupSnapshot(snapshot, archive, "sha-" + id, DateTimeOffset.UtcNow, "Ready")
        { ImportId = id, ServiceProjectCount = 1, Areas = [new("Application code", "Complete"), new("Contracts", contracts ? "Complete" : "NotDetected")] });
    }

    [Fact]
    public async Task CustomRoles_AddRenameRemove_DuplicatesRejected_PredefinedProtected()
    {
        (await _catalog.AddRoleAsync("M2LB", "Batch jobs")).Should().BeNull();
        (await _catalog.AddRoleAsync("M2LB", "batch JOBS")).Should().Contain("already exists");
        (await _catalog.AddRoleAsync("M2LB", "Main Application")).Should().Contain("already exists", "predefined names are taken");
        var role = _catalog.Catalog("M2LB").CustomRoles.Single();
        (await _catalog.RenameRoleAsync("M2LB", role.Id, "Batch & scheduling")).Should().BeNull();
        _catalog.RoleName("M2LB", role.Id).Should().Be("Batch & scheduling");
        (await _catalog.RenameRoleAsync("M2LB", "main-application", "Main")).Should().Contain("Predefined");
        (await _catalog.RemoveRoleAsync("M2LB", "documentation")).Should().Contain("Predefined");
        (await _catalog.RemoveRoleAsync("M2LB", role.Id)).Should().BeNull("an unused custom role can be removed");
        _catalog.Roles("M2LB").Should().HaveCount(PackageCatalogService.PredefinedRoles.Count);
    }

    [Fact]
    public async Task RoleInUse_IsNotRemovedWithoutReassignment_ThenReassignmentMovesItsPackages()
    {
        (await _catalog.SaveRecommendationAsync("M2LB", null, "Batch jobs", "M2LB.Batch.zip", "Batch", required: false)).Should().BeNull("typing a new role in the combo box creates it");
        var custom = _catalog.Catalog("M2LB").CustomRoles.Single();
        custom.Name.Should().Be("Batch");
        (await _catalog.RemoveRoleAsync("M2LB", custom.Id)).Should().Contain("used by 1 recommended package");
        (await _catalog.RemoveRoleAsync("M2LB", custom.Id, reassignTo: "shared-source")).Should().BeNull();
        _catalog.Catalog("M2LB").Packages.Single().RoleId.Should().Be("shared-source");
    }

    [Fact]
    public async Task Recommendation_IsMissingUntilItsZipIsImported_ThenImported_AndPatternsMatch()
    {
        (await _catalog.SaveRecommendationAsync("M2LB", null, "M2LB application", "M2LB.zip", "Main Application", true)).Should().BeNull();
        (await _catalog.SaveRecommendationAsync("M2LB", null, "Shared libraries", "M2LB.Common*", "shared-source", false)).Should().BeNull();
        (await _catalog.SaveRecommendationAsync("M2LB", null, "m2lb application", "x.zip", "documentation", true)).Should().Contain("already exists");
        (await _catalog.SaveRecommendationAsync("M2LB", null, "Folder", "src/M2LB.zip", "documentation", true)).Should().Contain("without a folder");

        _catalog.Matches("M2LB").Select(m => (m.Recommendation.DisplayName, m.Status)).Should().Equal(("M2LB application", RecommendationStatus.Missing), ("Shared libraries", RecommendationStatus.Missing));
        Import("a", "m2lb.zip");
        Import("c", "M2LB.Common-v2.zip");
        _catalog.Matches("M2LB").Should().OnlyContain(m => m.Status == RecommendationStatus.Imported, "case-insensitive file name and wildcard pattern");
        PackageCatalogService.Matches("M2LB", "M2LB.zip").Should().BeTrue("a pattern without .zip also matches the archive");
        PackageCatalogService.Matches("M2LB", "M2LB-docs.zip").Should().BeFalse();
    }

    [Fact]
    public async Task OrderAndEdit_KeepIdentity_AndEditingNeverTouchesImportsOrBindings()
    {
        Import("a", "M2LB.zip", contracts: true);
        (await _evidence.SelectProjectAsync("dev", "a")).Should().BeTrue();
        var bindingsBefore = JsonSerializer.Serialize(_settings.Settings.Profiles.Single(p => p.Id == "dev").Project);
        var readyBefore = (await _evidence.GetAsync("dev")).RequiredReadyAll;

        await _catalog.SaveRecommendationAsync("M2LB", null, "A", "A.zip", "documentation", true);
        await _catalog.SaveRecommendationAsync("M2LB", null, "B", "B.zip", "documentation", true);
        var b = _catalog.Catalog("M2LB").Packages.Single(p => p.DisplayName == "B");
        (await _catalog.MoveAsync("M2LB", b.Id, -1)).Should().BeNull();
        _catalog.Matches("M2LB").Select(m => m.Recommendation.DisplayName).Should().Equal("B", "A");
        (await _catalog.SaveRecommendationAsync("M2LB", b.Id, "B renamed", "B2.zip", "documentation", false)).Should().BeNull();
        _catalog.Catalog("M2LB").Packages.Single(p => p.Id == b.Id).DisplayName.Should().Be("B renamed");

        JsonSerializer.Serialize(_settings.Settings.Profiles.Single(p => p.Id == "dev").Project).Should().Be(bindingsBefore, "recommendations never create or change bindings");
        (await _evidence.GetAsync("dev")).RequiredReadyAll.Should().Be(readyBefore, "catalog changes never change readiness");
        _repository.SddLifecycle.ProjectImports.Should().ContainSingle();
    }

    [Fact]
    public async Task EndToEnd_RecommendImportDiscoverBind_AndLaterImportsKeepTheBinding()
    {
        Import("a", "M2LB.zip");
        await _evidence.SelectProjectAsync("dev", "a");
        await _catalog.SaveRecommendationAsync("M2LB", null, "Partner contracts", "Partner*.zip", "api-contracts", true);
        _catalog.Matches("M2LB").Single().Status.Should().Be(RecommendationStatus.Missing);

        Import("p", "Partner-contracts.zip", contracts: true);
        _catalog.Matches("M2LB").Single().Status.Should().Be(RecommendationStatus.Imported);
        var view = await _evidence.GetAsync("dev");
        view.Row(ProjectEvidenceRoles.Contracts)!.Candidates.Should().Contain(c => c.Package.ImportId == "p", "the new source is discoverable in Project Evidence");
        view.Row(ProjectEvidenceRoles.Contracts)!.Bound.Should().BeEmpty("a recommendation never binds");
        (await _evidence.BindAsync("dev", ProjectEvidenceRoles.Contracts, "p")).Should().BeTrue();

        Import("q", "Partner-contracts-v2.zip", contracts: true);
        (await _evidence.GetAsync("dev")).Row(ProjectEvidenceRoles.Contracts)!.Bound.Select(b => b.ImportId).Should().Equal(["p"], "a later import never changes the binding");
    }

    [Fact]
    public async Task Projects_NeverShareCatalogEntries()
    {
        await _catalog.SaveRecommendationAsync("M2LB", null, "M2LB application", "M2LB.zip", "Main Application", true);
        await _catalog.AddRoleAsync("Skolenærvær", "Adapter");
        _catalog.Matches("Skolenærvær").Should().BeEmpty();
        _catalog.Roles("M2LB").Should().NotContain(r => r.Name == "Adapter");
        _catalog.Matches("m2lb").Should().ContainSingle("the project key is not case-sensitive");
    }

    [Fact]
    public async Task OlderSavedSettings_WithoutCatalogs_StayReadable_AndCatalogsPersistAcrossReload()
    {
        var legacy = new ActiveTargetEnvironmentTests.Storage();
        var old = new FrontendAnalysisSettingsService();
        await old.LoadAsync(legacy);
        old.Settings.PackageCatalogs.Should().BeEmpty();

        await _catalog.SaveRecommendationAsync("M2LB", null, "M2LB application", "M2LB.zip", "Main Application", true);
        var reloaded = new FrontendAnalysisSettingsService();
        await reloaded.LoadAsync(_js);
        new PackageCatalogService(reloaded, _repository, _js).Matches("M2LB").Should().ContainSingle(m => m.Recommendation.DisplayName == "M2LB application");
    }

    [Fact]
    public async Task Editor_ComboBoxAddsACustomRole_AndRecommendedWorkflowCardShowsMissingThenImported()
    {
        TargetApplications.Set(_settings.Settings.Profiles.Single(p => p.Id == "dev"), new TargetApplicationIdentity { Name = "M2LB", SetAt = DateTimeOffset.UtcNow }, DateTimeOffset.UtcNow);
        var editor = Render<PackageCatalogEditor>(p => p.Add(x => x.Project, "M2LB"));
        editor.Find("[data-testid=pce-role]").GetAttribute("role").Should().Be("combobox");
        editor.FindAll("#pce-roles option").Select(o => o.GetAttribute("value")).Should().Contain(["Main Application", "Shared Source", "Documentation", "Infrastructure / Terraform", "API Contracts / Schemas", "Integration / Adapter", "Testing / Test Data", "Generated Documentation"]);
        editor.Find("[data-testid=pce-name]").Change("Skole adapter");
        editor.Find("[data-testid=pce-pattern]").Change("SkoleAdapter.zip");
        editor.Find("[data-testid=pce-role]").Change("Skole integration");
        editor.Find("[data-testid=pce-form]").Submit();
        editor.WaitForAssertion(() => editor.Find("[data-testid=pce-row] [data-testid=pce-row-role]").TextContent.Should().Be("Skole integration"));
        editor.Find("[data-testid=pce-role-item][data-predefined=false]").TextContent.Should().Contain("Skole integration").And.Contain("used by 1");

        var card = Render<RecommendedPackagesCard>();
        card.Find("[data-testid=rpc-item]").GetAttribute("data-status").Should().Be("Missing");
        card.Find("[data-testid=rpc-import-missing]").GetAttribute("href").Should().Be("project-import?import=1");
        card.Find("[data-testid=rpc-edit]").GetAttribute("href").Should().Contain("tab=project").And.Contain("profile=dev");

        Import("s", "SkoleAdapter.zip");
        card.Render();
        card.Find("[data-testid=rpc-item]").GetAttribute("data-status").Should().Be("Imported");
        card.Find("[data-testid=rpc-summary]").TextContent.Should().Be("1 of 1 imported");
    }
}
