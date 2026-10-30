using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Target Environment → Project: one logical project, several bound evidence packages. Packages are judged on the capabilities they contain
/// (a documentation-only or source-only package is not deficient); unbound roles are Suggested or Need decision, never the newest; explorers
/// and Recommended Workflow read the bound evidence set, not whichever package was imported or selected last.
/// </summary>
public sealed class ProjectEvidenceBindingTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ActiveTargetEnvironmentTests.Storage _js = new();
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly List<SourceSetupSnapshot> _snapshots = [];

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    public ProjectEvidenceBindingTests()
    {
        _samples.Repository = _repository;
        _settings.LoadAsync(_js).GetAwaiter().GetResult();
        _settings.SelectActiveProfile("dev");
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples, environments: _settings);
    }

    private const string Constitution = "# M2LB Constitution\n\n## Core Principles\n\n### P-01: Privacy\nData MUST be minimised.\n";
    private const string Spec = "# Feature Specification: Person\n\n## Requirements\n\n- **FR-001**: System MUST search persons.\n";
    private const string Plan = "# Implementation Plan: Person\n\n## Summary\n\nPerson search.\n";
    private const string DataModel = "# Data Model: Person\n\n## Entities\n\n### Person\n- Id\n";

    private SourceSetupView Setup => new() { Snapshots = [.. _snapshots] };

    private SddProjectImportRecord Import(string id, string archive, int daysAfter, params (WorkspaceArtifactType Role, string Path, string Text)[] documents) =>
        Import(id, archive, daysAfter, null, documents);

    private SddProjectImportRecord Import(string id, string archive, int daysAfter, SourceSetupSnapshot? snapshot, params (WorkspaceArtifactType Role, string Path, string Text)[] documents)
    {
        var record = new SddProjectImportRecord
        {
            ImportId = id, ProjectName = archive.Replace(".zip", ""), ArchiveFileName = archive, ArchiveSha256 = "sha-" + id, ImportedAt = T0.AddDays(daysAfter),
            ArtifactDocumentCount = documents.Length, SourceState = snapshot is null ? "NotDetected" : "Created", SourceSnapshotId = snapshot?.SnapshotId,
        };
        _repository.SddLifecycle.ProjectImports.Add(record);
        foreach (var (role, path, text) in documents)
            _repository.AddArtifactRevision(role, text, path, path, "import:" + id, "ProjectImport", select: true);
        if (snapshot is not null) _snapshots.Add(snapshot with { ImportId = id });
        return record;
    }

    private static SourceSetupSnapshot App(string archive, int services = 1, int shared = 0, string code = "Complete", string contracts = "NotDetected", string iac = "NotDetected") =>
        new(Guid.NewGuid(), archive, "sha", T0, code == "NotDetected" ? "Failed" : "Ready")
        {
            Areas = [new("Application code", code), new("Architecture", "Complete"), new("Infrastructure as Code", iac), new("Configuration", "NotDetected"),
                new("CI/CD", "NotDetected"), new("Contracts", contracts)],
            ServiceProjectCount = services, SharedProjectCount = shared,
        };

    private ProjectEvidencePackage Package(SddProjectImportRecord record) => ProjectEvidencePackages.Of(record, _repository.SddLifecycle, Setup);
    private IReadOnlyList<ProjectEvidencePackage> Packages => ProjectEvidencePackages.All(_repository.SddLifecycle, Setup);
    private FrontendAnalysisProfile Dev => _settings.Settings.Profiles.Single(p => p.Id == "dev");

    /// <summary>Package A = source only (main application), package B = documentation only. Returns the project bound to A.</summary>
    private EnvironmentProject SourceAndDocs(bool bindDocs = true)
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        var docs = Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Constitution, ".specify/memory/constitution.md", Constitution),
            (WorkspaceArtifactType.Specification, "specs/001-person/spec.md", Spec), (WorkspaceArtifactType.Plan, "specs/001-person/plan.md", Plan),
            (WorkspaceArtifactType.DataModel, "specs/001-person/data-model.md", DataModel));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);
        if (bindDocs) ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(docs), T0).Should().BeTrue();
        Dev.Project = project;
        return project;
    }

    // ── Package capabilities ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DocumentationOnlyPackage_IsDocumentationSource_ReadyAndNotDeficient()
    {
        var docs = Package(Import("b", "M2LB-docs.zip", 0, (WorkspaceArtifactType.Constitution, "constitution.md", Constitution), (WorkspaceArtifactType.Specification, "spec.md", Spec)));

        docs.Classification.Should().Be("Documentation source");
        docs.Status.Should().Be(PackageCapabilityStatus.Ready, "a documentation-only package is judged on its documents, not on missing code");
        docs.Capability(ProjectEvidenceCapability.Documentation)!.Fact.Should().Be("2 supported documents");
        docs.DocumentRoleSummary.Should().Be("Constitution · Specification");
        docs.NotProvided.Should().Contain("No application source");
        docs.IsApplicationCandidate.Should().BeFalse();
        docs.SnapshotId.Should().BeNull("documentation evidence never gets a fake Source Analysis snapshot");
    }

    [Fact]
    public void SourceOnlyPackage_IsApplicationSource_ReadyWithoutDocuments()
    {
        var app = Package(Import("a", "M2LB.zip", 0, App("M2LB.zip")));

        app.Classification.Should().Be("Application source");
        app.Status.Should().Be(PackageCapabilityStatus.Ready, "a source-only package is not partial for lacking Markdown");
        app.NotProvided.Should().Equal("No authored project documents");
        app.IsApplicationCandidate.Should().BeTrue();
        app.ClassificationReason.Should().StartWith("Application candidate");
    }

    [Fact]
    public void InfrastructureOnly_ContractOnly_LibraryOnly_AreSupportingEvidence_AndNeverSatisfyApplicationOrDocumentation()
    {
        var iac = Package(Import("i", "Terraform-iac.zip", 0, App("Terraform-iac.zip", services: 0, code: "NotDetected", iac: "Complete")));
        var contracts = Package(Import("c", "contracts.zip", 0, App("contracts.zip", services: 0, code: "NotDetected", contracts: "Complete")));
        var library = Package(Import("l", "Common.zip", 0, App("Common.zip", services: 0, shared: 3)));

        iac.Classification.Should().Be("Infrastructure source");
        contracts.Classification.Should().Be("Contract/schema source");
        library.Classification.Should().Be("Supporting/shared source");
        library.ClassificationReason.Should().Contain("no web/service host project");
        foreach (var package in new[] { iac, contracts, library })
        {
            package.IsApplicationCandidate.Should().BeFalse();
            ProjectEvidencePackages.Satisfies(package, ProjectEvidenceCapability.ApplicationSource).Should().BeFalse();
            ProjectEvidencePackages.Satisfies(package, ProjectEvidenceCapability.Documentation).Should().BeFalse();
        }
        ProjectEvidencePackages.Satisfies(iac, ProjectEvidenceCapability.Infrastructure).Should().BeTrue();
        ProjectEvidencePackages.Satisfies(contracts, ProjectEvidenceCapability.ContractSchema).Should().BeTrue();
        ProjectEvidencePackages.Satisfies(library, ProjectEvidenceCapability.SupportingSource).Should().BeTrue();
    }

    [Fact]
    public void MixedPackage_IsSourcePlusDocuments_AndPartialOnlyForIncompleteExtractionOfWhatItContains()
    {
        var mixed = Package(Import("m", "M2LB.zip", 0, App("M2LB.zip", contracts: "Complete"), (WorkspaceArtifactType.Specification, "spec.md", Spec)));
        mixed.Classification.Should().Be("Source + documents");
        mixed.Has(ProjectEvidenceCapability.ContractSchema).Should().BeTrue();
        mixed.Status.Should().Be(PackageCapabilityStatus.Ready);

        var partial = Package(Import("p", "Other.zip", 0, App("Other.zip", code: "Partial")));
        partial.Status.Should().Be(PackageCapabilityStatus.Partial, "the application code it contains was only partly analyzed");
    }

    // ── Resolution: suggested, needs decision, never newest ─────────────────────────────────────────────────────────────

    [Fact]
    public void UnboundRole_OneCompatiblePackageIsSuggested_SeveralNeedDecision_NoneIsMissingOrNotProvided()
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Specification, "spec.md", Spec));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);

        var view = ProjectEvidenceResolver.Resolve("dev", "Development", project, Packages, Setup);
        view.Row(ProjectEvidenceRoles.ApplicationSource)!.State.Should().Be(EvidenceRoleState.Ready, "the chosen main package's own source is bound with the project");
        view.Row(ProjectEvidenceRoles.ApplicationSource)!.Role.Sources.Single().SnapshotId.Should().NotBeNull("source evidence binds the exact snapshot");
        view.Documentation!.State.Should().Be(EvidenceRoleState.Suggested, "one compatible package is shown, never bound silently");
        view.Documentation.Bound.Should().BeEmpty();
        view.Row(ProjectEvidenceRoles.Infrastructure)!.State.Should().Be(EvidenceRoleState.NotProvided, "optional and absent is neutral");
        view.Row(ProjectEvidenceRoles.Infrastructure)!.Blocks.Should().BeFalse();

        Import("c", "M2LB-docs-newer.zip", 30, (WorkspaceArtifactType.Plan, "plan.md", Plan));
        view = ProjectEvidenceResolver.Resolve("dev", "Development", project, Packages, Setup);
        view.Documentation!.State.Should().Be(EvidenceRoleState.NeedsDecision, "the newest package is never chosen automatically");
        view.Documentation.Candidates.Select(c => c.Package.ArchiveFileName).Should().Equal("M2LB-docs.zip", "M2LB-docs-newer.zip");
        view.Documentation.Blocks.Should().BeTrue();
        view.Summary.Should().Contain("1 unresolved").And.StartWith("1 / 2 required bound · 1 ready");
    }

    // ── Bound vs ready, role-specific readiness ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RequiredRolePartial_IsBoundButNotReady_AndTheTopSummaryNeverSaysReady()
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip", code: "Partial"));
        var docs = Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Specification, "spec.md", Spec));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(docs), T0);
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", project, Packages, Setup);

        var source = view.Row(ProjectEvidenceRoles.ApplicationSource)!;
        source.State.Should().Be(EvidenceRoleState.Partial);
        source.IsBound.Should().BeTrue();
        source.IsReady.Should().BeFalse("bound is not ready");
        source.PartialDetail.Should().Contain("M2LB.zip: present").And.Contain("incomplete").And.Contain("partial");
        view.Documentation!.State.Should().Be(EvidenceRoleState.Ready, "a documentation-only package is ready for the documentation role");
        view.RequiredBound.Should().Be(2);
        view.RequiredReady.Should().Be(1);
        view.RequiredReadyAll.Should().BeFalse();
        var required = ProjectEvidenceResolver.Dimensions(view).Single(d => d.Key == "required");
        required.Value.Should().Be("Partial", "every required role has a source, but one does not satisfy its role");
        required.Detail.Should().Be("2 / 2 bound · 1 ready · 1 partial");
        view.Problems.Should().Equal("1 required role partial");
    }

    [Fact]
    public void ApplicationSourceRole_IsReady_EvenWhenTheSamePackagesContractsAreIncomplete()
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip", contracts: "Partial"));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);
        Package(app).Status.Should().Be(PackageCapabilityStatus.Partial, "the package as a whole has an incomplete capability");
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", project, Packages, Setup);
        view.Row(ProjectEvidenceRoles.ApplicationSource)!.State.Should().Be(EvidenceRoleState.Ready, "role readiness is role-specific, never package-global");
        view.Row(ProjectEvidenceRoles.Contracts)!.State.Should().Be(EvidenceRoleState.Partial);
    }

    [Fact]
    public void SingleSourceRoleWithTwoBoundSources_IsAConflict_NeverChosenSilently()
    {
        var project = SourceAndDocs();
        var other = Import("x", "Other.zip", 5, App("Other.zip"));
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources.Add(ProjectEvidenceResolver.Reference(Package(other), ProjectEvidenceCapability.ApplicationSource, T0));
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", project, Packages, Setup);
        view.Row(ProjectEvidenceRoles.ApplicationSource)!.State.Should().Be(EvidenceRoleState.Conflict);
        view.Row(ProjectEvidenceRoles.ApplicationSource)!.StatusLabel.Should().Be("Conflict · review required");
        view.Problems.Should().Contain("1 role has a conflict");
        view.RequiredReadyAll.Should().BeFalse();
    }

    [Fact]
    public void WithoutAProject_OneApplicationCandidateIsSuggested_SeveralNeedSelection_NeverTheNewest()
    {
        Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Specification, "spec.md", Spec));
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", null, Packages, Setup);
        view.SuggestedProject!.ImportId.Should().Be("a", "a docs-only package is never the project candidate");
        Dev.Project.Should().BeNull("a suggestion is never persisted");

        Import("c", "Tiltak2.zip", 9, App("Tiltak2.zip"));
        view = ProjectEvidenceResolver.Resolve("dev", "Development", null, Packages, Setup);
        view.SuggestedProject.Should().BeNull("several candidates need a decision");
        view.ProjectCandidates.Select(p => p.ImportId).Should().BeEquivalentTo(["a", "c"]);
    }

    [Fact]
    public void PreviewChange_ShowsTheImpact_WithoutChangingTheProject()
    {
        var project = SourceAndDocs();
        var other = Import("x", "Other.zip", 5, App("Other.zip"));
        var before = System.Text.Json.JsonSerializer.Serialize(project);
        var impact = ProjectEvidenceResolver.PreviewChange(project, Package(other), Packages, T0);
        System.Text.Json.JsonSerializer.Serialize(project).Should().Be(before, "nothing is applied before the person confirms");
        impact.Should().Contain("Project documentation: binding retained.").And.Contain("Imported packages, Source Analysis snapshots and document history are unchanged.");
    }

    [Fact]
    public void RequiredRoleWithoutAnyCompatiblePackage_IsMissing()
    {
        var docs = Import("b", "M2LB-docs.zip", 0, (WorkspaceArtifactType.Specification, "spec.md", Spec));
        var project = ProjectEvidenceResolver.CreateProject(Package(docs), T0);
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", project, Packages, Setup);

        view.Row(ProjectEvidenceRoles.ApplicationSource)!.State.Should().Be(EvidenceRoleState.Missing);
        view.Documentation!.State.Should().Be(EvidenceRoleState.Ready);
    }

    [Fact]
    public void Bind_RejectsAPackageThatDoesNotProvideTheRole_AndSingleRolesReplaceWhileMultipleRolesAggregate()
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        var iac = Import("i", "Terraform-iac.zip", 0, App("Terraform-iac.zip", services: 0, code: "NotDetected", iac: "Complete"));
        var d1 = Import("b", "docs-1.zip", 0, (WorkspaceArtifactType.Constitution, "c.md", Constitution));
        var d2 = Import("c", "docs-2.zip", 0, (WorkspaceArtifactType.Plan, "p.md", Plan));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);

        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(iac), T0).Should().BeFalse("infrastructure is not documentation");
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.ApplicationSource, Package(d1), T0).Should().BeFalse("a docs-only package is never application source");
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(d1), T0).Should().BeTrue();
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(d2), T0).Should().BeTrue();
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Documentation).Sources.Select(s => s.ImportId).Should().Equal("b", "c");
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Documentation).Sources.Should().OnlyContain(s => s.SnapshotId == null);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Infrastructure, Package(iac), T0).Should().BeTrue();
    }

    [Fact]
    public void ChangeProject_KeepsUserBindings_DropsThePreviousProjectsOwnEvidence_AndDeletesNothing()
    {
        var project = SourceAndDocs();
        var other = Import("z", "Other.zip", 5, App("Other.zip"));
        var revisions = _repository.SddLifecycle.Revisions.Count;

        var impact = ProjectEvidenceResolver.ChangeProject(project, Package(other), Packages, T0.AddDays(6));

        project.ProjectImportId.Should().Be("z");
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources.Select(s => s.ImportId).Should().Equal("z");
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Documentation).Sources.Select(s => s.ImportId).Should().Equal("b");
        impact.Should().Contain(line => line.Contains("unchanged"));
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(3);
        _repository.SddLifecycle.Revisions.Should().HaveCount(revisions);
    }

    // ── Explorers read the bound evidence set ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkspaceArtifactType.Constitution)]
    [InlineData(WorkspaceArtifactType.Specification)]
    [InlineData(WorkspaceArtifactType.Plan)]
    [InlineData(WorkspaceArtifactType.DataModel)]
    public async Task SourceOnlyMainProject_ExplorersReadTheBoundDocumentationPackage_WithProvenance(WorkspaceArtifactType role)
    {
        SourceAndDocs();
        _repository.SddLifecycle.CurrentProjectImportId = "a";

        var state = await _explorers.GetStateAsync(role);

        state.Status.Should().Be(ExplorerArtifactStatus.Loaded, "the application package has no Markdown, the bound documentation package does");
        state.Selected!.EvidenceSource.Should().Be("M2LB-docs.zip");
        state.Selected.EvidenceImportId.Should().Be("b");
        state.Evidence!.FromEnvironment.Should().BeTrue();
        state.Evidence.ProjectName.Should().Be("M2LB");
        state.Evidence.Sources.Select(s => s.ArchiveFileName).Should().Equal("M2LB-docs.zip");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a")]
    [InlineData("b")]
    public async Task SwitchingTheCurrentImport_DoesNotEmptyOrChangeTheExplorers(string? currentImport)
    {
        SourceAndDocs();
        _repository.SddLifecycle.CurrentProjectImportId = currentImport;

        _explorers.CurrentScope.Should().Be("import:a", "the Target Environment Project decides, not the last imported/selected package");
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Selected!.EvidenceSource.Should().Be("M2LB-docs.zip");
        _repository.SddLifecycle.CurrentProjectImportId.Should().Be(currentImport, "the user's current project is never changed");
    }

    [Fact]
    public async Task DocumentationRoleUnbound_IsSourceUnresolved_NotAnEmptyExplorer()
    {
        SourceAndDocs(bindDocs: false);

        var state = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Empty);
        state.SourceUnresolved.Should().BeTrue();
        state.Evidence!.UnboundDocumentPackages.Should().Equal("M2LB-docs.zip");
    }

    [Fact]
    public async Task BoundDocumentationWithoutTheRole_IsMissingArtifact_NotUnresolved()
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        var docs = Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Specification, "spec.md", Spec));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(docs), T0);
        Dev.Project = project;

        var plan = await _explorers.GetStateAsync(WorkspaceArtifactType.Plan);

        plan.Status.Should().Be(ExplorerArtifactStatus.Empty);
        plan.SourceUnresolved.Should().BeFalse("the documentation source is bound; it just holds no Plan");
        plan.Evidence!.SourceCount.Should().Be(1);
    }

    [Fact]
    public async Task TwoDocumentationPackages_Aggregate_EachArtifactKeepsItsSource()
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        var d1 = Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Constitution, "constitution.md", Constitution), (WorkspaceArtifactType.Specification, "spec.md", Spec));
        var d2 = Import("c", "M2LB-plans.zip", 2, (WorkspaceArtifactType.Plan, "plan.md", Plan), (WorkspaceArtifactType.DataModel, "data-model.md", DataModel));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(d1), T0);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(d2), T0);
        Dev.Project = project;

        (await _explorers.GetStateAsync(WorkspaceArtifactType.Constitution)).Selected!.EvidenceSource.Should().Be("M2LB-docs.zip");
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Selected!.EvidenceSource.Should().Be("M2LB-docs.zip");
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Plan)).Selected!.EvidenceSource.Should().Be("M2LB-plans.zip");
        (await _explorers.GetStateAsync(WorkspaceArtifactType.DataModel)).Selected!.EvidenceSource.Should().Be("M2LB-plans.zip");
    }

    [Fact]
    public async Task SameRoleInTwoBoundPackages_NeedsDecision_IdenticalDocumentIsOneArtifact()
    {
        var app = Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        var d1 = Import("b", "docs-old.zip", 1, (WorkspaceArtifactType.Specification, "docs/spec-a.md", Spec), (WorkspaceArtifactType.Constitution, "constitution.md", Constitution));
        var d2 = Import("c", "docs-new.zip", 9, (WorkspaceArtifactType.Specification, "docs/spec-b.md", Spec + "\n- **FR-002**: More.\n"), (WorkspaceArtifactType.Constitution, "constitution.md", Constitution));
        var project = ProjectEvidenceResolver.CreateProject(Package(app), T0);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(d1), T0);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.Documentation, Package(d2), T0);
        Dev.Project = project;

        var spec = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired, "two specifications from two packages: a decision, never the newest");
        spec.Artifacts.Select(a => a.EvidenceSource).Should().BeEquivalentTo(["docs-old.zip", "docs-new.zip"]);

        var constitution = await _explorers.GetStateAsync(WorkspaceArtifactType.Constitution);
        constitution.Artifacts.Should().ContainSingle("the byte-identical constitution at the same path is one document");
        constitution.Status.Should().Be(ExplorerArtifactStatus.Loaded);
    }

    [Fact]
    public async Task WithoutAnEnvironmentProject_TheCurrentImportIsTheFallback_AndOtherImportsWithDocumentsAreReported()
    {
        Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Specification, "spec.md", Spec));
        _repository.SddLifecycle.CurrentProjectImportId = "a";

        var state = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);

        _explorers.CurrentScope.Should().Be("import:a");
        state.Status.Should().Be(ExplorerArtifactStatus.Empty);
        state.Evidence!.FromEnvironment.Should().BeFalse("no environment binding is claimed");
        state.Evidence.UnboundDocumentPackages.Should().Equal("M2LB-docs.zip");
    }

    // ── Recommended Workflow ────────────────────────────────────────────────────────────────────────────────────────────

    private static CurrentWorkspaceSnapshot ImportedWithout(ExplorerProjectEvidence evidence) =>
        WorkspaceSnapshots.Loaded("M2LB", "import:a", "M2LB") with { Evidence = evidence };

    private static ExplorerProjectEvidence Evidence(bool fromEnvironment, bool unresolved, params string[] elsewhere) =>
        new(fromEnvironment, "dev", "Development", "M2LB", unresolved ? [] : [new("b", "M2LB-docs", "M2LB-docs.zip", T0, false)], unresolved, elsewhere);

    [Fact]
    public void DocumentsInput_IsNeverNotProvided_WhenEvidenceExists()
    {
        ProjectInputPresentation.Documents(ImportedWithout(Evidence(true, true, "M2LB-docs.zip"))).StatusLabel.Should().Be("Source unresolved");
        ProjectInputPresentation.Documents(ImportedWithout(Evidence(false, false, "M2LB-docs.zip"))).StatusLabel.Should().Be("Available in another import");
        var none = ProjectInputPresentation.Documents(ImportedWithout(Evidence(false, false)));
        none.StatusLabel.Should().Be("No supported project documents found");
        none.ActionLabel.Should().Be("Import project documents");
        ProjectInputPresentation.Documents(ImportedWithout(Evidence(true, true))).Route.Should().Contain("tab=project").And.Contain("focus=documentation");
    }

    private static async Task<WorkflowReadiness> ReadinessAsync(CurrentWorkspaceSnapshot workspace, ProjectEvidenceView? evidence = null)
    {
        var projection = WorkspaceSnapshots.Projection(workspace);
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(x => x.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" });
        var applicability = new ProjectApplicabilityState(Mock.Of<ITechnologyCoverageApiService>(), contexts.Object, projection.Object, Mock.Of<IFrontendAnalysisSettingsService>());
        var api = new Mock<IRecommendedWorkflowApiService>();
        api.Setup(a => a.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IReadOnlyList<ArtifactRevisionRef>>()))
            .ReturnsAsync([new WorkflowStepViewModel { Number = 1, Key = "LoadSampleProject", Title = "Load project artifacts", StepType = "ArtifactLoad", RequiredArtifacts = [] }]);
        var service = new Mock<IProjectEvidenceService>();
        service.Setup(s => s.GetAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(evidence ?? new ProjectEvidenceView(null, null, null, null, [], [], [], []));
        return await new WorkflowReadinessService(projection.Object, applicability, api.Object, NullLogger<WorkflowReadinessService>.Instance, evidence: service.Object).GetReadinessAsync();
    }

    [Fact]
    public async Task Workflow_ImportedDocumentsWithoutEnvironment_NeverRecommendsChoosingATargetEnvironment()
    {
        // Document-only user: a docs package imported, no Target Environment. Document reviews proceed; the environment is not the next step.
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:b", "M2LB-docs", WorkspaceArtifactType.Specification, WorkspaceArtifactType.Tasks) with { Evidence = Evidence(false, false) };

        var readiness = await ReadinessAsync(workspace);

        readiness.NextRecommendedAction?.Key.Should().NotBe(WorkflowReadinessService.ChooseTargetEnvironmentKey);
        readiness.NextRecommendedAction?.Key.Should().NotStartWith(WorkflowPaths.RuntimeSetupKeyPrefix);
        readiness.EvidenceProfile.Should().Be(WorkflowEvidenceProfile.DocumentsOnly);
        readiness.PathOf(WorkflowPathKind.Documents)!.State.Should().Be(WorkflowPathState.Ready);
    }

    [Fact]
    public async Task Workflow_ImportedSourceOnlyPackageWithoutEnvironment_OffersPaths_NotAMandatoryEnvironment()
    {
        // The screenshot state: an imported package, no supported documents, no Target Environment.
        var readiness = await ReadinessAsync(ImportedWithout(Evidence(false, false)));

        readiness.NextRecommendedAction!.Key.Should().Be(WorkflowPaths.ChoosePathKey);
        readiness.NextRecommendedAction.Title.Should().Be("Choose what you want to review");
        readiness.PathOf(WorkflowPathKind.Documents)!.PrimaryLabel.Should().Be("Import project documents");
        readiness.PathOf(WorkflowPathKind.Runtime)!.PrimaryLabel.Should().Be("Choose Target Environment", "the environment is required on the runtime path only");
    }

    [Fact]
    public async Task Workflow_DocumentsInAnotherImport_RecommendsBinding_NotLoadProjectArtifacts()
    {
        var readiness = await ReadinessAsync(ImportedWithout(Evidence(true, true, "M2LB-docs.zip")));

        readiness.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.BindProjectEvidenceKey);
        readiness.NextRecommendedAction.Title.Should().Be("Bind project documentation source");
        readiness.NextRecommendedAction.Route.Should().Contain("focus=documentation");
        readiness.Steps.Should().NotContain(s => s.Title == "Load project artifacts", "BirkNext already holds the documents");
    }

    [Fact]
    public async Task Workflow_RequiredRoleMissing_RecommendsBindingThatRole()
    {
        SourceAndDocs();
        Import("i", "Terraform-iac.zip", 3, App("Terraform-iac.zip", services: 0, code: "NotDetected", iac: "Complete"));
        Dev.Project!.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Infrastructure).Requirement = ProjectEvidenceRequirement.Required;
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", Dev.Project, Packages, Setup);
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:a", "M2LB", WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification) with
            { Evidence = Evidence(true, false) };

        var readiness = await ReadinessAsync(workspace, view);

        readiness.NextRecommendedAction!.Title.Should().Be("Complete project evidence: bind Infrastructure");
        readiness.NextRecommendedAction.Description.Should().Contain("required roles ready. Missing: Infrastructure");
        readiness.NextRecommendedAction.Description.Should().Contain("Terraform-iac.zip");
        readiness.NextRecommendedAction.Route.Should().Contain("focus=infrastructure");
    }

    [Fact]
    public async Task Workflow_ImportsWithoutAProject_RecommendsConfiguringProjectContext_WithTheSuggestion()
    {
        Import("a", "M2LB.zip", 0, App("M2LB.zip"));
        Import("b", "M2LB-docs.zip", 1, (WorkspaceArtifactType.Specification, "spec.md", Spec));
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", null, Packages, Setup);
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:a", "M2LB", WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification) with { Evidence = Evidence(false, false) };

        var readiness = await ReadinessAsync(workspace, view);

        readiness.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.ConfigureProjectKey);
        readiness.NextRecommendedAction.Title.Should().Be("Configure Project");
        readiness.NextRecommendedAction.Description.Should().Contain("Select the logical system").And.Contain("confirm the evidence packages").And.Contain("Suggested: M2LB");
        readiness.NextRecommendedAction.Route.Should().Contain("tab=project").And.Contain("profile=dev");
    }

    [Fact]
    public async Task Workflow_AllRequiredEvidenceResolved_DoesNotRecommendImportOrBinding()
    {
        SourceAndDocs();
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", Dev.Project, Packages, Setup);
        view.BlockingLabels.Should().BeEmpty();
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:a", "M2LB", WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification) with
            { Evidence = Evidence(true, false) };

        var readiness = await ReadinessAsync(workspace, view);

        readiness.NextRecommendedAction?.Key.Should().NotBe(WorkflowReadinessService.BindProjectEvidenceKey);
        readiness.NextRecommendedAction?.Key.Should().NotBe(WorkflowReadinessService.ImportProjectDocumentsKey);
    }

    [Fact]
    public void RestoringAWorkspace_NeverCapturesThePreviousImportsSessionDocumentsIntoTheCurrentImport()
    {
        // Root cause of a source-only package showing another package's documents: the saved session copies (from the import that was current
        // before) were captured as revisions of the restored current import. Only the import itself populates its scope.
        Import("b", "M2LB-docs.zip", 0, (WorkspaceArtifactType.Specification, "specs/001-person/spec.md", Spec));
        Import("a", "M2LB.zip", 1, App("M2LB.zip"));
        _repository.SddLifecycle.CurrentProjectImportId = "a";
        var saved = System.Text.Json.JsonSerializer.Serialize(_repository.SddLifecycle);

        _repository.ResetSddLifecycle();
        _repository.Set(WorkspaceArtifactType.Specification, Spec, "specs/001-person/spec.md", "specs/001-person/spec.md");
        _repository.Set(WorkspaceArtifactType.SchemaStudioDrafts, "{}", "schema-studio-drafts.json");
        _repository.RestoreSddLifecycle(saved);

        _repository.SddLifecycle.Revisions.Should().NotContain(r => r.WorkspaceScope == "import:a", "M2LB.zip has no documents of its own");
        _repository.Get(WorkspaceArtifactType.Specification).Should().BeNull("session copies follow the current import's own revisions");
        _repository.Get(WorkspaceArtifactType.SchemaStudioDrafts).Should().NotBeNull("session-only Schema Studio stores are kept");
        Package(_repository.SddLifecycle.ProjectImports.Single(i => i.ImportId == "a")).Classification.Should().Be("Application source");
    }

    // ── Persistence: references only, omitted when absent ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectIsStoredOnTheProfileAsReferences_AndOmittedWhenNotConfigured()
    {
        await _settings.SaveAsync(_js);
        _js.Json.Should().NotContain("\"project\"", "existing profiles serialize exactly as before");

        SourceAndDocs();
        await _settings.SaveAsync(_js);
        _js.Json.Should().Contain("\"project\"").And.Contain("\"importId\":\"b\"");
        _js.Json.Should().NotContain(Spec.Split('\n')[0], "documents are never copied into the Target Environment");

        var copy = _settings.DuplicateProfile("dev");
        copy.Project!.Evidence.SelectMany(r => r.Sources).Select(s => s.ImportId).Should().Contain(["a", "b"], "duplication copies binding intent (references) only");
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(2);
    }
}
