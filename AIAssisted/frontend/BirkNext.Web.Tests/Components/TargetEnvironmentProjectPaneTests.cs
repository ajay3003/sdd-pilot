using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.SampleProjects;
using BirkNext.Web.Tests.Services;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment → Project: empty state first, application candidates apart from supporting packages, compatible-source picker that
/// binds references only, distinct Suggested / Needs decision states; the Explorer host tells an unresolved documentation source from a
/// missing artifact.
/// </summary>
public sealed class TargetEnvironmentProjectPaneTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ActiveTargetEnvironmentTests.Storage _js = new();
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly List<SourceSetupSnapshot> _snapshots = [];

    public TargetEnvironmentProjectPaneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _samples.Repository = _repository;
        _settings.LoadAsync(_js).GetAwaiter().GetResult();
        _settings.SelectActiveProfile("dev");
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ActiveEventApiResult<SourceSetupView>(new SourceSetupView { Snapshots = [.. _snapshots] }, null));
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton<IProjectEvidenceService>(new ProjectEvidenceService(_settings, _repository, journeys.Object, _js));
        Services.AddSingleton<IArtifactExplorerContext>(new ArtifactExplorerContext(_repository, _samples, _samples, environments: _settings));
        Services.AddSingleton<ISampleProjectDocumentResolver>(_samples);
        Services.AddSingleton<ISampleProjectArtifactDiscovery>(_samples);

        Import("a", "M2LB.zip", Guid.NewGuid());
        Import("b", "M2LB-docs.zip", null, (WorkspaceArtifactType.Specification, "specs/001-person/spec.md", "# Feature Specification: Person\n\n- **FR-001**: Search.\n"));
        Import("i", "Terraform-iac.zip", Guid.NewGuid(), infrastructureOnly: true);
    }

    private void Import(string id, string archive, Guid? snapshot, params (WorkspaceArtifactType Role, string Path, string Text)[] documents) =>
        Import(id, archive, snapshot, false, documents);

    private void Import(string id, string archive, Guid? snapshot, bool infrastructureOnly, params (WorkspaceArtifactType Role, string Path, string Text)[] documents)
    {
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord
        {
            ImportId = id, ProjectName = archive[..^4], ArchiveFileName = archive, ArchiveSha256 = "sha-" + id, ImportedAt = DateTimeOffset.UtcNow,
            SourceSnapshotId = snapshot, SourceState = snapshot is null ? "NotDetected" : "Created", ArtifactDocumentCount = documents.Length,
        });
        foreach (var (role, path, text) in documents) _repository.AddArtifactRevision(role, text, path, path, "import:" + id, "ProjectImport", select: true);
        if (snapshot is { } s)
            _snapshots.Add(new SourceSetupSnapshot(s, archive, "sha-" + id, DateTimeOffset.UtcNow, infrastructureOnly ? "Failed" : "Ready")
            {
                ImportId = id, ServiceProjectCount = infrastructureOnly ? 0 : 1,
                Areas = [new("Application code", infrastructureOnly ? "NotDetected" : "Complete"), new("Infrastructure as Code", infrastructureOnly ? "Complete" : "NotDetected"), new("Contracts", "NotDetected")],
            });
    }

    private IRenderedComponent<TargetEnvironmentProjectPane> RenderPane(string? focus = null) =>
        Render<TargetEnvironmentProjectPane>(p => p.Add(x => x.EnvironmentId, "dev").Add(x => x.Focus, focus));

    [Fact]
    public void NotConfigured_ShowsEmptyStateFirst_ThenGroupsApplicationCandidatesApartFromSupportingPackages()
    {
        var cut = RenderPane();

        cut.Find("[data-testid=tep-empty]").TextContent.Should().Contain("Project not configured").And.Contain("Choose which application/system");
        cut.FindAll("[data-testid=tep-roles]").Should().BeEmpty("evidence lists are not shown before a project is chosen");

        cut.Find("[data-testid=tep-select-project]").Click();
        cut.Find("[data-testid=tep-project-candidates]").QuerySelectorAll("[data-testid=tep-package]").Select(e => e.GetAttribute("data-import")).Should().Equal("a");
        var supporting = cut.Find("[data-testid=tep-supporting-candidates]").QuerySelectorAll("[data-testid=tep-package]");
        supporting.Select(e => e.GetAttribute("data-classification")).Should().BeEquivalentTo(["Documentation source", "Infrastructure source"]);
        supporting.Single(e => e.GetAttribute("data-import") == "b").TextContent.Should().Contain("No application source").And.NotContain("Partial");
    }

    [Fact]
    public void SelectingTheApplication_BindsItsOwnSource_SuggestsTheDocsPackage_AndBindingIsAReferenceOnly()
    {
        var cut = RenderPane();
        cut.Find("[data-testid=tep-select-project]").Click();
        cut.Find("[data-testid=tep-project-candidates] [data-testid=tep-use-as-project]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB"));
        var rows = cut.FindAll("[data-testid=tep-role]");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.ApplicationSource).GetAttribute("data-state").Should().Be("Ready");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Documentation).GetAttribute("data-state").Should().Be("Suggested");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Infrastructure).GetAttribute("data-state").Should().Be("Suggested");

        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Documentation).QuerySelector("[data-testid=tep-role-action]")!.Click();
        var picker = cut.Find("[data-testid=tep-picker]");
        picker.QuerySelectorAll("[data-testid=tep-picker-candidate]").Select(c => c.GetAttribute("data-import")).Should().Equal("b");
        picker.QuerySelector("[data-testid=tep-picker-why]")!.TextContent.Should().Contain("Specification");
        cut.Find("[data-testid=tep-picker-incompatible]").TextContent.Should().Contain("2 other imported packages");
        cut.Find("[data-testid=tep-bind]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tep-role]").Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Documentation)
            .GetAttribute("data-state").Should().Be("Ready"));
        _js.Json.Should().Contain("\"importId\":\"b\"").And.NotContain("FR-001", "only references are stored");
        _repository.SddLifecycle.CurrentProjectImportId.Should().BeNull("selecting the environment's project never changes the current project");
    }

    [Fact]
    public void OneApplicationCandidate_IsProposed_NotPersisted_AndUseAppliesIt()
    {
        var cut = RenderPane();
        var suggested = cut.Find("[data-testid=tep-suggested-project]");
        suggested.TextContent.Should().Contain("Suggested project").And.Contain("M2LB").And.Contain("Not saved until you choose it");
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Project.Should().BeNull();
        cut.Find("[data-testid=tep-use-suggested]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB"));
    }

    [Fact]
    public void BoundAndReadyAreSeparate_ProblemsAreSummarised_AndMultiSourceRowsCollapseWithOrigin()
    {
        Import("i2", "Terraform-dns.zip", Guid.NewGuid(), infrastructureOnly: true);
        Import("i3", "Terraform-net.zip", Guid.NewGuid(), infrastructureOnly: true);
        var cut = RenderPane();
        cut.Find("[data-testid=tep-use-suggested]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-problems]"));
        cut.Find("[data-testid=tep-problems]").TextContent.Should().Contain("Needs attention").And.Contain("1 required role missing");
        cut.Find("[data-testid=tep-dimension][data-key=required]").TextContent.Should().Contain("Needs attention").And.Contain("1 / 2 bound · 1 ready");
        cut.Find("[data-testid=tep-role][data-role=application-source] [data-testid=tep-binding-origin]").TextContent.Should().Be("Bound from Project setup");

        cut.Find("[data-testid=tep-role][data-role=infrastructure] [data-testid=tep-role-action]").Click();
        for (var i = 0; i < 3; i++) cut.FindAll("[data-testid=tep-bind]")[0].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-role][data-role=infrastructure] [data-testid=tep-source-count]").TextContent.Should().Be("3 sources"));
        var infra = cut.Find("[data-testid=tep-role][data-role=infrastructure]");
        infra.QuerySelectorAll("[data-testid=tep-bound-source]").Length.Should().Be(3, "two visible, the rest behind View all");
        infra.QuerySelector("[data-testid=tep-more-sources] summary")!.TextContent.Should().Contain("+1 more").And.Contain("View all");
        infra.QuerySelectorAll("[data-testid=tep-binding-origin]").Select(o => o.TextContent).Should().OnlyContain(o => o == "Bound manually");
        infra.QuerySelectorAll("[data-testid=tep-role-capability]").Select(b => b.TextContent).Should().Equal("Infrastructure");

        // Remove one supporting source: only that reference goes.
        cut.FindAll("[data-testid=tep-unbind]")[0].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-role][data-role=infrastructure] [data-testid=tep-source-count]").TextContent.Should().Be("2 sources"));
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(5, "removing a binding never deletes an import");
    }

    [Fact]
    public void ChangingTheProject_ShowsTheImpactFirst_AndAppliesOnlyOnConfirm()
    {
        Import("x", "Tiltak2.zip", Guid.NewGuid());
        var cut = RenderPane();
        cut.Find("[data-testid=tep-select-project]").Click();
        cut.FindAll("[data-testid=tep-project-candidates] [data-testid=tep-use-as-project]").First(b => b.GetAttribute("aria-label")!.Contains("M2LB")).Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB"));

        cut.Find("[data-testid=tep-change-project]").Click();
        cut.FindAll("[data-testid=tep-project-candidates] [data-testid=tep-use-as-project]").First(b => b.GetAttribute("aria-label")!.Contains("Tiltak2")).Click();
        cut.Find("[data-testid=tep-change-preview]").TextContent.Should().Contain("Change project to Tiltak2?");
        cut.Find("[data-testid=tep-change-impact]").TextContent.Should().Contain("Imported packages, Source Analysis snapshots and document history are unchanged.");
        cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB", "nothing is applied before confirmation");
        cut.Find("[data-testid=tep-change-cancel]").Click();
        cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB");

        cut.FindAll("[data-testid=tep-project-candidates] [data-testid=tep-use-as-project]").First(b => b.GetAttribute("aria-label")!.Contains("Tiltak2")).Click();
        cut.Find("[data-testid=tep-change-confirm]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be("Tiltak2"));
    }

    [Fact]
    public void DeepLinkFocus_OpensThatRolesPicker()
    {
        var cut = RenderPane();
        cut.Find("[data-testid=tep-select-project]").Click();
        cut.Find("[data-testid=tep-project-candidates] [data-testid=tep-use-as-project]").Click();

        var focused = RenderPane(ProjectEvidenceRoles.Infrastructure);
        focused.Find($"#tep-picker-{ProjectEvidenceRoles.Infrastructure}").Should().NotBeNull();
        focused.Find("[data-testid=tep-picker-candidate]").GetAttribute("data-import").Should().Be("i");
    }

    [Fact]
    public void ExplorerHost_UnresolvedDocumentationSource_IsDistinctFromMissingArtifact()
    {
        var package = ProjectEvidencePackages.Of(_repository.SddLifecycle.ProjectImports[0], _repository.SddLifecycle, new SourceSetupView { Snapshots = [.. _snapshots] });
        var project = ProjectEvidenceResolver.CreateProject(package, DateTimeOffset.UtcNow);
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Project = project;
        Services.AddSingleton<IWorkspaceAutoSaveService>(Mock.Of<IWorkspaceAutoSaveService>());

        var unresolved = Render<BirkNext.Web.Components.Explorers.ArtifactExplorerHost>(p => p.Add(x => x.Role, WorkspaceArtifactType.Specification));
        unresolved.WaitForAssertion(() => unresolved.Find("[data-testid=artifact-explorer-empty]").GetAttribute("data-state").Should().Be("SourceUnresolved"));
        unresolved.Find("[data-testid=artifact-explorer-unresolved]").TextContent.Should().Contain("M2LB-docs.zip");
        unresolved.Find("[data-testid=artifact-explorer-open-project-setup]").GetAttribute("href").Should().Contain("tab=project").And.Contain("focus=documentation");

        var docs = ProjectEvidencePackages.Of(_repository.SddLifecycle.ProjectImports[1], _repository.SddLifecycle, null);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, docs, DateTimeOffset.UtcNow);
        var missing = Render<BirkNext.Web.Components.Explorers.ArtifactExplorerHost>(p => p.Add(x => x.Role, WorkspaceArtifactType.Plan));
        missing.WaitForAssertion(() => missing.Find("[data-testid=artifact-explorer-empty]").GetAttribute("data-state").Should().Be("NotFoundInBoundEvidence"));
        missing.Find("[data-testid=artifact-explorer-not-found]").TextContent.Should().Contain("No supported Plan artifact was found across the 1 source bound to");
    }
}
