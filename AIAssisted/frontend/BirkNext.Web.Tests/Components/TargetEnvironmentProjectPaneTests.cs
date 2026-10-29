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

    /// <summary>Project setup through the checklist: choose the project, keep the defaults (optionally check more), Save.</summary>
    private void Configure(IRenderedComponent<TargetEnvironmentProjectPane> cut, string project = "M2LB", Action? adjust = null)
    {
        cut.Find($"[data-testid=psp-option][data-name='{project}']").Click();
        adjust?.Invoke();
        cut.Find("[data-testid=psp-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be(project));
    }

    private static AngleSharp.Dom.IElement Item(IRenderedComponent<TargetEnvironmentProjectPane> cut, string role, string import) =>
        cut.Find($"[data-testid=psp-role][data-role={role}] [data-testid=psp-item][data-import={import}]");

    [Fact]
    public void NotConfigured_OffersLogicalProjectsOnly_NeverAnInfrastructurePackage()
    {
        var cut = RenderPane();

        cut.Find("[data-testid=psp]").TextContent.Should().Contain("Project not configured").And.Contain("Choose the logical application/system");
        cut.FindAll("[data-testid=tep-roles]").Should().BeEmpty("evidence lists are not shown before a project is chosen");
        cut.Find("#psp-search").GetAttribute("role").Should().Be("combobox");
        cut.FindAll("[data-testid=psp-option]").Select(o => o.GetAttribute("data-name")).Should().Equal(["M2LB", "M2LB-docs"],
            "the application first, then the documentation project; Terraform-iac is evidence, never a project");
        cut.Find("[data-testid=psp-search]").Input("docs");
        cut.FindAll("[data-testid=psp-option]").Select(o => o.GetAttribute("data-name")).Should().Equal(["M2LB-docs"]);
        cut.Find("[data-testid=psp-search]").Input("terraform");
        cut.FindAll("[data-testid=psp-option]").Should().BeEmpty("package file names are not project names");
    }

    [Fact]
    public void ChoosingTheProject_ShowsTheChecklist_StrongPreselected_PossibleNot_AndSaveBindsReferencesOnly()
    {
        var cut = RenderPane();
        cut.Find("[data-testid=psp-option][data-name=M2LB]").Click();

        var app = Item(cut, ProjectEvidenceRoles.ApplicationSource, "a");
        app.GetAttribute("data-strength").Should().Be("Strong");
        app.QuerySelector("[data-testid=psp-check]")!.HasAttribute("checked").Should().BeTrue("strong evidence is selected by default");
        app.QuerySelector("[data-testid=psp-why]")!.TextContent.Should().Contain("The project's own package provides it");
        var docs = Item(cut, ProjectEvidenceRoles.Documentation, "b");
        docs.GetAttribute("data-strength").Should().Be("Possible");
        docs.QuerySelector("[data-testid=psp-check]")!.HasAttribute("checked").Should().BeFalse("a name resemblance is never preselected");
        docs.QuerySelector("[data-testid=psp-why]")!.TextContent.Should().Contain("name similarity only");
        cut.FindAll($"[data-testid=psp-role][data-role={ProjectEvidenceRoles.Infrastructure}] [data-testid=psp-item]").Should().BeEmpty();

        docs.QuerySelector("[data-testid=psp-check]")!.Change(true);
        cut.Find($"[data-testid=psp-role][data-role={ProjectEvidenceRoles.Infrastructure}] [data-testid=psp-add-package]").Change("i");
        cut.Find("[data-testid=psp-summary]").TextContent.Should().Contain("Infrastructure: 1 selected").And.Contain("Project documentation: 1 selected");
        cut.Find("[data-testid=psp-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB"));
        var rows = cut.FindAll("[data-testid=tep-role]");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.ApplicationSource).GetAttribute("data-state").Should().Be("Ready");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Documentation).GetAttribute("data-state").Should().Be("Ready");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Infrastructure).GetAttribute("data-state").Should().Be("Ready");
        cut.Find("[data-testid=tep-advanced-title]").TextContent.Should().Be("Advanced evidence bindings", "the checklist and the table show the same bindings");
        _js.Json.Should().Contain("\"importId\":\"b\"").And.NotContain("FR-001", "only references are stored");
        _repository.SddLifecycle.CurrentProjectImportId.Should().BeNull("configuring the environment's project never changes the current project");
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(3, "no package is created or copied");
    }

    [Fact]
    public void A_deselected_suggestion_is_remembered_and_never_silently_reselected()
    {
        Configure(RenderPane());
        var project = _settings.Settings.Profiles.Single(p => p.Id == "dev").Project!;
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources.Should().ContainSingle(s => s.ImportId == "a");

        // Deselect the strong application source suggestion of the own package, save, reopen: it stays unselected and says why.
        var edit = RenderPane();
        edit.Find("[data-testid=tep-edit-evidence]").Click();
        Item(edit, ProjectEvidenceRoles.ApplicationSource, "a").QuerySelector("[data-testid=psp-check]")!.Change(false);
        edit.Find("[data-testid=psp-save]").Click();
        edit.WaitForAssertion(() => project.Declined.Should().ContainSingle(d => d.RoleKey == ProjectEvidenceRoles.ApplicationSource && d.ImportId == "a"));
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources.Should().BeEmpty();

        var reopened = RenderPane();
        reopened.Find("[data-testid=tep-edit-evidence]").Click();
        var item = Item(reopened, ProjectEvidenceRoles.ApplicationSource, "a");
        item.QuerySelector("[data-testid=psp-check]")!.HasAttribute("checked").Should().BeFalse("an explicit deselection is not undone by the next suggestion");
        item.QuerySelector("[data-testid=psp-badge]")!.TextContent.Should().Be("Deselected earlier");
    }

    [Fact]
    public void Expected_evidence_is_a_missing_requirement_until_a_matching_package_is_imported_and_no_package_is_created()
    {
        var cut = RenderPane();
        Configure(cut, adjust: () =>
        {
            cut.Find("[data-testid=psp-expected-label]").Input("SkoleAdapter source");
            cut.Find("[data-testid=psp-expected-capability]").Change(nameof(ProjectEvidenceCapability.SupportingSource));
            cut.Find("[data-testid=psp-expected-add]").Click();
        });
        var row = cut.FindAll("[data-testid=tep-role]").Single(r => r.TextContent.Contains("SkoleAdapter source"));
        row.GetAttribute("data-state").Should().Be("Missing");
        cut.Find("[data-testid=tep-problems]").TextContent.Should().Contain("required roles missing", "the expected source and the unselected documentation");
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(3, "expected evidence never creates a package");

        // A later import that names it becomes its candidate (shown, never bound silently); unrelated packages never are.
        Import("s", "SkoleAdapter.zip", Guid.NewGuid());
        var later = RenderPane();
        var expected = later.FindAll("[data-testid=tep-role]").Single(r => r.TextContent.Contains("SkoleAdapter source"));
        expected.GetAttribute("data-state").Should().Be("Suggested");
        expected.QuerySelector("[data-testid=tep-role-action]")!.Click();
        later.FindAll("[data-testid=tep-picker-candidate]").Select(c => c.GetAttribute("data-import")).Should().Equal(["s"]);
    }

    [Fact]
    public void A_project_can_be_created_by_name_before_any_evidence_exists()
    {
        var cut = RenderPane();
        cut.Find("[data-testid=psp-create-manual]").Click();
        cut.Find("[data-testid=psp-manual-name]").Input("Skolenærvær");
        cut.Find("[data-testid=psp-manual-description]").Change("School attendance reporting");
        cut.Find("[data-testid=psp-manual-create]").Click();
        cut.Find("[data-testid=psp-chosen]").TextContent.Should().Contain("Skolenærvær").And.Contain("Manual");
        cut.Find("[data-testid=psp-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be("Skolenærvær"));
        var project = _settings.Settings.Profiles.Single(p => p.Id == "dev").Project!;
        project.Manual.Should().BeTrue();
        project.ProjectImportId.Should().BeNull("a manual project is not a package");
        project.Description.Should().Be("School attendance reporting");
        cut.Find("[data-testid=tep-role][data-role=application-source]").GetAttribute("data-state").Should().NotBe("Ready", "evidence is incomplete until bound");
    }

    [Fact]
    public void Changing_the_project_asks_first_and_never_deletes_evidence()
    {
        var cut = RenderPane();
        Configure(cut);
        cut.Find("[data-testid=tep-change-project]").Click();
        cut.Find("[data-testid=psp-option][data-name=M2LB-docs]").Click();
        cut.Find("[data-testid=psp-save]").Click();

        cut.Find("[data-testid=psp-switch-confirm]").TextContent.Should().Contain("from M2LB to M2LB-docs").And.Contain("Imports, Source Analysis snapshots and documents stay");
        cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB", "nothing is applied before confirmation");
        cut.Find("[data-testid=psp-switch-confirm-button]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().Be("M2LB-docs"));
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(3);
    }

    [Fact]
    public void BoundAndReadyAreSeparate_ProblemsAreSummarised_AndMultiSourceRowsCollapseWithOrigin()
    {
        Import("i2", "Terraform-dns.zip", Guid.NewGuid(), infrastructureOnly: true);
        Import("i3", "Terraform-net.zip", Guid.NewGuid(), infrastructureOnly: true);
        var cut = RenderPane();
        Configure(cut);
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
    public void DeepLinkFocus_OpensThatRolesPicker()
    {
        Configure(RenderPane());

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
