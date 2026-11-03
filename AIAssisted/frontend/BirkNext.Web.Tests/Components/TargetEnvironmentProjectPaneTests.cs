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

    private FrontendAnalysisProfile DevProfile => _settings.Settings.Profiles.Single(p => p.Id == "dev");

    /// <summary>Two imported application sources and a host naming neither: nothing decides the application, so it stays unchosen (Needs review).</summary>
    private void AmbiguousApplications()
    {
        Import("t", "Tiltak.zip", Guid.NewGuid());
        DevProfile.TargetUrl = "https://app-dev.example.org";
    }

    /// <summary>The Target Application (as the Target Application tab sets it): the M2LB application package by default.</summary>
    private void Application(string name = "M2LB", string? importId = "a") =>
        TargetApplications.Set(DevProfile, new TargetApplicationIdentity { Name = name, ImportId = importId, Manual = importId is null, SetAt = DateTimeOffset.UtcNow }, DateTimeOffset.UtcNow);

    /// <summary>The Project pane; by default the environment's Target Application is M2LB (set first unless <paramref name="withApplication"/> is false).</summary>
    private IRenderedComponent<TargetEnvironmentProjectPane> RenderPane(string? focus = null, bool withApplication = true)
    {
        if (withApplication && TargetApplications.Of(DevProfile) is null) Application();
        return Render<TargetEnvironmentProjectPane>(p => p.Add(x => x.EnvironmentId, "dev").Add(x => x.Focus, focus));
    }

    /// <summary>Project evidence through the checklist: keep the defaults (optionally check more), Save.</summary>
    private void Configure(IRenderedComponent<TargetEnvironmentProjectPane> cut, Action? adjust = null)
    {
        adjust?.Invoke();
        cut.Find("[data-testid=psp-save]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tep-roles]").Should().NotBeEmpty());
    }

    // ── One guided workflow: the application is detected, its evidence associated ─────────────────────────────────────────

    [Fact]
    public void A_unique_application_source_and_an_agreeing_target_host_detect_M2LB_without_manual_creation()
    {
        DevProfile.TargetUrl = "https://m2lbdev.bufetat.no";
        var panel = Render<TargetApplicationIdentityPanel>(p => p.Add(x => x.EnvironmentId, "dev"));

        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-name]").TextContent.Should().Be("M2LB"));
        panel.Find("[data-testid=tai-provenance]").TextContent.Should().Be("Detected automatically · Very High confidence");
        panel.FindAll("[data-testid=tai-reason]").Select(r => r.TextContent).Should().Contain(["Imported primary application source: M2LB.zip", "Target host m2lbdev.bufetat.no names M2LB"]);
        panel.FindAll("[data-testid=tai-empty]").Should().BeEmpty("no manual creation is needed");
        DevProfile.TargetApplication!.Should().Match<TargetApplicationIdentity>(t => t.Origin == TargetApplicationOrigins.Detected && t.ImportId == "a" && !t.Manual);
        _settings.Settings.ActiveProfileId.Should().Be("dev", "detection never changes activation");
        panel.Find("[data-testid=tai-change-impact]").TextContent.Should().Contain("re-evaluates evidence associations").And.Contain("remain untouched");
    }

    [Fact]
    public void An_existing_manual_M2LB_is_confirmed_by_detection_in_place_never_duplicated()
    {
        DevProfile.TargetUrl = "https://m2lbdev.bufetat.no";
        TargetApplications.Set(DevProfile, TargetApplications.Manual("M2LB", "added before detection", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        var original = DevProfile.TargetApplication!;

        var panel = Render<TargetApplicationIdentityPanel>(p => p.Add(x => x.EnvironmentId, "dev"));
        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-provenance]").TextContent.Should().StartWith("Confirmed by detection"));
        panel.Find("[data-testid=tai-provenance]").TextContent.Should().EndWith("originally added manually");
        DevProfile.TargetApplication.Should().BeSameAs(original, "the same entity is reused and its provenance upgraded");
        (original.Origin, original.Description, original.ConfirmedByDetectionAt is not null).Should().Be((TargetApplicationOrigins.Manual, "added before detection", true));
        DevProfile.ArchivedProjects.Should().BeNull("nothing was archived or duplicated");
    }

    [Fact]
    public void An_explicit_different_application_wins_over_detection_and_the_disagreement_is_shown_for_review()
    {
        DevProfile.TargetUrl = "https://m2lbdev.bufetat.no";
        TargetApplications.Set(DevProfile, TargetApplications.Manual("Skolenærvær", null, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        var panel = Render<TargetApplicationIdentityPanel>(p => p.Add(x => x.EnvironmentId, "dev"));

        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-detection-differs]").TextContent.Should().Contain("detection points to M2LB"));
        DevProfile.TargetApplication!.Name.Should().Be("Skolenærvær", "explicit user decisions override automatic inference");
    }

    [Fact]
    public void The_project_tab_is_compact_by_default_and_keeps_rules_and_the_package_catalog_under_Advanced()
    {
        DevProfile.TargetUrl = "https://m2lbdev.bufetat.no";
        var cut = RenderPane(withApplication: false);
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-application-provenance]").TextContent.Should().Contain("Detected automatically"));
        cut.Find("[data-testid=tep-needs-review-count]").TextContent.Should().Be("0");
        if (cut.FindAll("[data-testid=tep-roles]").Count > 0)
        {
            cut.FindAll("[data-testid=tep-requirement]").Should().OnlyContain(select => select.Closest("[data-testid=tep-advanced]") != null, "requirement editing is advanced");
            cut.FindAll("[data-testid=tep-clear-project]").Should().OnlyContain(button => button.Closest("[data-testid=tep-advanced]") != null, "removal is advanced");
            cut.Find("[data-testid=tep-edit-evidence]").TextContent.Should().Be("Review evidence");
        }
        cut.FindAll("[data-testid=pce-pattern]").Should().OnlyContain(input => input.Closest("[data-testid=tep-advanced]") != null, "expected filename patterns are advanced");
    }

    private static AngleSharp.Dom.IElement Item(IRenderedComponent<TargetEnvironmentProjectPane> cut, string role, string import) =>
        cut.Find($"[data-testid=psp-role][data-role={role}] [data-testid=psp-item][data-import={import}]");

    /// <summary>The reported case: the Target Application tab has a URL and a target check ("Target URL: Configured"), but no application was
    /// chosen. The Project tab says exactly that — never a bare "Not configured" that contradicts the configured URL.</summary>
    [Fact]
    public void AConfiguredTargetUrlWithoutAChosenApplication_IsNamedAsSuch_WithTheOneActionThatFixesIt()
    {
        AmbiguousApplications();
        var cut = RenderPane(withApplication: false);

        cut.Find("[data-testid=tep-target-application]").TextContent.Should().StartWith("Application not chosen");
        cut.Find("[data-testid=tep-target-url-only]").TextContent.Should().Contain("https://app-dev.example.org is configured, but the application (for example M2LB) is not chosen");
        cut.Find("[data-testid=psp-url-configured]").TextContent.Should().Contain("The target URL (https://app-dev.example.org) is configured, but no application is chosen yet");
        cut.Find("[data-testid=psp-configure-target-application]").GetAttribute("href").Should().Contain("tab=target");

        // Ambiguous evidence is shown for one-click review — never chosen silently, never a form to fill in.
        var panel = Render<TargetApplicationIdentityPanel>(p => p.Add(x => x.EnvironmentId, "dev"));
        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-detected]").GetAttribute("data-state").Should().Be(TargetApplicationInference.NeedsReview));
        panel.FindAll("[data-testid=tai-candidate]").Select(c => c.GetAttribute("data-name")).Should().BeEquivalentTo(["M2LB", "Tiltak"]);
        DevProfile.TargetApplication.Should().BeNull("two application sources and an unrelated host decide nothing");
        panel.Find("[data-testid=tai-candidate][data-name=M2LB] [data-testid=tai-use-candidate]").Click();
        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-provenance]").TextContent.Should().Be("Chosen by you"));
        DevProfile.TargetApplication!.Origin.Should().Be(TargetApplicationOrigins.Chosen);
    }

    [Fact]
    public void NoTargetApplication_ProjectShowsThePrerequisite_NoProjectSearch_NoManualProject()
    {
        AmbiguousApplications();
        var cut = RenderPane(withApplication: false);

        cut.Find("[data-testid=psp]").TextContent.Should().Contain("Target Application required").And.Contain("after choosing the application/system this environment represents");
        cut.Find("[data-testid=psp-configure-target-application]").GetAttribute("href").Should().Contain("tab=target").And.Contain("profile=dev");
        cut.FindAll("[data-testid=psp-search]").Should().BeEmpty("Project never chooses the application");
        cut.FindAll("[data-testid=psp-create-manual]").Should().BeEmpty();
        cut.FindAll("[data-testid=psp-checklist]").Should().BeEmpty("no package checklist before the application is known");
        cut.FindAll("[data-testid=tep-roles]").Should().BeEmpty();
        cut.Find("[data-testid=tep-target-application]").TextContent.Should().StartWith("Application not chosen");
        cut.Find("[data-testid=tep-project]").TextContent.Should().Be("Waiting for Target Application");
        // Imported evidence stays visible with detected roles and labelled analysis badges; nothing says bound / not bound.
        cut.FindAll("[data-testid=tep-source-role]").Should().NotBeEmpty().And.OnlyContain(r => r.TextContent == "Detected");
        cut.FindAll("[data-testid=tep-analysis-badge]").Should().OnlyContain(b => b.TextContent.Contains(':'), "every analysis status names its dimension");
        cut.Find("[data-testid=tep-sources-table]").TextContent.Should().NotContain("not bound").And.NotContain("Partial · Partial").And.NotContain("Partial · Failed");
    }

    [Fact]
    public void TargetApplication_OffersApplicationsOnly_NeverAnInfrastructurePackage()
    {
        AmbiguousApplications();
        var panel = Render<TargetApplicationIdentityPanel>(p => p.Add(x => x.EnvironmentId, "dev"));

        panel.WaitForAssertion(() => panel.FindAll("[data-testid=tai-option]").Select(o => o.GetAttribute("data-name")).Should().Equal(["M2LB", "Tiltak", "M2LB-docs"],
            "applications first, then the documentation project; Terraform-iac is evidence, never an application"));
        panel.Find("[data-testid=tai-search]").Input("terraform");
        panel.FindAll("[data-testid=tai-option]").Should().BeEmpty("package file names are not application names");
        panel.Find("[data-testid=tai-search]").Input("");
        panel.Find("[data-testid=tai-option][data-name=M2LB]").Click();

        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-name]").TextContent.Should().Be("M2LB"));
        DevProfile.TargetApplication!.ImportId.Should().Be("a");
        DevProfile.Project.Should().BeNull("choosing the application binds no evidence by itself");
        _settings.Settings.ActiveProfileId.Should().Be("dev", "unchanged — setting the application never changes activation");
    }

    [Fact]
    public void TargetApplicationSet_ShowsTheChecklist_StrongPreselected_PossibleNot_AndSaveBindsReferencesOnly()
    {
        var cut = RenderPane();
        cut.Find("#psp-title").TextContent.Should().Be("Project evidence for M2LB");
        cut.Find("[data-testid=psp-suggestions-lead]").TextContent.Should().Contain("Strong matches are selected by default");

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

        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-project]").TextContent.Should().StartWith("Ready"));
        cut.Find("[data-testid=tep-target-application]").TextContent.Should().StartWith("M2LB");
        cut.Find("[data-testid=tep-application-provenance]").TextContent.Should().Contain("Confirmed by detection", "the chosen M2LB is the one detection points to — confirmed in place");
        var rows = cut.FindAll("[data-testid=tep-role]");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.ApplicationSource).GetAttribute("data-state").Should().Be("Ready");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Documentation).GetAttribute("data-state").Should().Be("Ready");
        rows.Single(r => r.GetAttribute("data-role") == ProjectEvidenceRoles.Infrastructure).GetAttribute("data-state").Should().Be("Ready");
        cut.Find("[data-testid=tep-advanced-title]").TextContent.Should().StartWith("Required evidence", "the checklist and the role cards show the same associations");
        cut.Find("[data-testid=tep-decision]").GetAttribute("data-decision").Should().Be("Ready");
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
    public void A_manual_application_is_added_on_Target_Application_then_its_evidence_is_bound_on_Project()
    {
        AmbiguousApplications();
        var panel = Render<TargetApplicationIdentityPanel>(p => p.Add(x => x.EnvironmentId, "dev"));
        // Manual is the exception: under Advanced, not the normal path.
        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-advanced] summary").TextContent.Should().Be("Advanced — add an application manually"));
        panel.Find("[data-testid=tai-manual-name]").Input("Skolenærvær");
        panel.Find("[data-testid=tai-manual-description]").Change("School attendance reporting");
        panel.Find("[data-testid=tai-manual-save]").Click();
        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-current]").TextContent.Should().Contain("Skolenærvær").And.Contain("Added manually"));

        var cut = RenderPane(withApplication: false);
        cut.Find("#psp-title").TextContent.Should().Be("Project evidence for Skolenærvær");
        cut.Find("[data-testid=psp-save]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tep-roles]").Should().NotBeEmpty());
        var project = _settings.Settings.Profiles.Single(p => p.Id == "dev").Project!;
        project.Manual.Should().BeTrue();
        project.ProjectImportId.Should().BeNull("a manual project is not a package");
        project.Description.Should().Be("School attendance reporting");
        cut.Find("[data-testid=tep-role][data-role=application-source]").GetAttribute("data-state").Should().NotBe("Ready", "evidence is incomplete until bound");
    }

    [Fact]
    public void Changing_the_Target_Application_asks_first_archives_its_evidence_and_never_deletes_anything()
    {
        Configure(RenderPane());
        var m2lbEvidence = DevProfile.Project!;
        var panel = Render<TargetApplicationIdentityPanel>(p => p.Add(x => x.EnvironmentId, "dev"));
        panel.WaitForAssertion(() => panel.Find("[data-testid=tai-change]"));
        panel.Find("[data-testid=tai-change]").Click();
        panel.Find("[data-testid=tai-option][data-name=M2LB-docs]").Click();

        panel.Find("[data-testid=tai-confirm]").TextContent.Should().Contain("archived").And.Contain("not deleted").And.Contain("re-evaluated for M2LB-docs")
            .And.Contain("Imported packages, Source Analysis, Test Data and Connected Systems are not changed");
        DevProfile.TargetApplication!.Name.Should().Be("M2LB", "nothing is applied before confirmation");
        panel.Find("[data-testid=tai-confirm-yes]").Click();

        panel.WaitForAssertion(() => DevProfile.TargetApplication!.Name.Should().Be("M2LB-docs"));
        DevProfile.Project.Should().BeNull("M2LB evidence is not reused for another application");
        DevProfile.ArchivedProjects.Should().ContainSingle().Which.Should().BeSameAs(m2lbEvidence);
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(3, "no import is deleted");
        RenderPane().Find("#psp-title").TextContent.Should().Be("Project evidence for M2LB-docs");

        // Choosing M2LB again restores its earlier evidence decisions.
        TargetApplications.Set(DevProfile, new TargetApplicationIdentity { Name = "M2LB", ImportId = "a" }, DateTimeOffset.UtcNow).Should().BeTrue();
        DevProfile.Project.Should().BeSameAs(m2lbEvidence);
        DevProfile.ArchivedProjects.Should().BeNull();
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
        var origin = cut.Find("[data-testid=tep-role][data-role=application-source] [data-testid=tep-binding-origin]");
        (origin.TextContent.StartsWith("Confirmed", StringComparison.Ordinal), origin.GetAttribute("data-origin")).Should().Be((true, "Bound from Project setup"), "an explicit badge; the full origin stays available");

        cut.Find("[data-testid=tep-role][data-role=infrastructure] [data-testid=tep-role-action]").Click();
        for (var i = 0; i < 3; i++) cut.FindAll("[data-testid=tep-bind]")[0].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tep-role][data-role=infrastructure] [data-testid=tep-source-count]").TextContent.Should().Be("3 sources"));
        var infra = cut.Find("[data-testid=tep-role][data-role=infrastructure]");
        infra.QuerySelectorAll("[data-testid=tep-bound-source]").Length.Should().Be(3, "two visible, the rest behind View all");
        infra.QuerySelector("[data-testid=tep-more-sources] summary")!.TextContent.Should().Contain("+1 more").And.Contain("View all");
        infra.QuerySelectorAll("[data-testid=tep-binding-origin]").Select(o => o.GetAttribute("data-origin")).Should().OnlyContain(o => o == "Confirmed by you");
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
