using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Deterministic association of imported evidence with the Target Application (no manual Bind for obvious sources): the user's M2LB
/// workspace — M2LB (primary), M2LB.Common (supporting), Terraform-iac-m2lb (infrastructure + docs), Tiltak 2 (MU, another system) —
/// is associated automatically except the uncertain package; conflicts and explicit user decisions are never overridden.
/// </summary>
public sealed class ProjectEvidenceAssociationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly List<SourceSetupSnapshot> _snapshots = [];
    private SourceSetupView Setup => new() { Snapshots = [.. _snapshots] };
    private SddLifecycleState Lifecycle => _repository.SddLifecycle;

    private SddProjectImportRecord Import(string name, int minutes, int services, int shared, string code, string iac = "NotDetected", int docs = 0)
    {
        var id = "import-" + name.ToLowerInvariant().Replace(' ', '-').Replace('.', '-');
        var snapshot = new SourceSetupSnapshot(Guid.NewGuid(), name + ".zip", "sha-" + id, T0, code == "NotDetected" ? "Failed" : "Partial")
        {
            ImportId = id, ServiceProjectCount = services, SharedProjectCount = shared, InfrastructureFormats = iac == "NotDetected" ? [] : ["Terraform"],
            Areas = [new("Application code", code), new("Infrastructure as Code", iac), new("Contracts", "NotDetected")],
        };
        _snapshots.Add(snapshot);
        var record = new SddProjectImportRecord
        {
            ImportId = id, ProjectName = name, ArchiveFileName = name + ".zip", ArchiveSha256 = "sha-" + id, ImportedAt = T0.AddMinutes(minutes),
            SourceState = "Created", SourceSnapshotId = snapshot.SnapshotId, ArtifactDocumentCount = docs,
        };
        Lifecycle.ProjectImports.Add(record);
        for (var i = 0; i < docs; i++)
            _repository.AddArtifactRevision(WorkspaceArtifactType.Specification, $"# Feature Specification: F{i}\n\n- **FR-001**: System MUST work.\n",
                $"specs/00{i}-f/spec.md", $"specs/00{i}-f/spec.md", "import:" + id, "ProjectImport", select: true);
        return record;
    }

    private void UserWorkspace()
    {
        Import("M2LB", 8, services: 16, shared: 23, code: "Partial", docs: 3);
        Import("M2LB.Common", 14, services: 0, shared: 1, code: "Partial", docs: 1);
        Import("Terraform-iac-m2lb", 15, services: 0, shared: 1, code: "NotDetected", iac: "Partial", docs: 2);
        Import("Tiltak 2", 16, services: 4, shared: 6, code: "Partial");
        ApplicationContexts.Ensure(Lifecycle, T0);
        ApplicationContexts.Reconcile(Lifecycle, Setup);
    }

    private static FrontendAnalysisProfile Profile(string? application) => new()
    {
        Id = "dev", Name = "Development",
        TargetApplication = application is null ? null : new TargetApplicationIdentity { Name = application, SetAt = T0 },
    };

    private IReadOnlyList<PackageAssociation> Associations(FrontendAnalysisProfile profile) =>
        ProjectEvidenceAssociation.Of(TargetApplications.Of(profile), profile.Project, Lifecycle, ProjectEvidencePackages.All(Lifecycle, Setup), Setup);

    private static PackageAssociation For(IReadOnlyList<PackageAssociation> list, string name) => list.Single(a => a.Package.DisplayName == name);

    [Fact]
    public void WithoutTargetApplication_RolesAreDetected_NothingIsAssessedOrPersisted()
    {
        UserWorkspace();
        var profile = Profile(null);
        var associations = Associations(profile);
        associations.Should().OnlyContain(a => a.State == EvidenceAssociationState.WaitingForTargetApplication);
        For(associations, "M2LB").Role.Should().Be(PackageEvidenceRole.PrimaryApplicationSource);
        For(associations, "M2LB.Common").Role.Should().Be(PackageEvidenceRole.SupportingSource);
        ProjectEvidenceAssociation.Apply(profile, associations, T0).Should().BeFalse();
        profile.Project.Should().BeNull();
    }

    [Fact]
    public void TargetApplicationM2lb_AssociatesStrongMatchesAutomatically_AndOnlyTheUncertainSourceNeedsReview()
    {
        UserWorkspace();
        var profile = Profile("M2LB");
        var associations = Associations(profile);

        For(associations, "M2LB").Should().Match<PackageAssociation>(a => a.State == EvidenceAssociationState.Associated && a.Role == PackageEvidenceRole.PrimaryApplicationSource);
        For(associations, "M2LB.Common").Should().Match<PackageAssociation>(a => a.State == EvidenceAssociationState.Associated && a.Role == PackageEvidenceRole.SupportingSource);
        For(associations, "Terraform-iac-m2lb").Should().Match<PackageAssociation>(a => a.State == EvidenceAssociationState.Associated && a.Role == PackageEvidenceRole.Infrastructure);
        For(associations, "Tiltak 2").Should().Match<PackageAssociation>(a => a.State == EvidenceAssociationState.NeedsReview && a.Detail.Contains("another component or integration"));

        ProjectEvidenceAssociation.Apply(profile, associations, T0).Should().BeTrue();
        var project = profile.Project!;
        string[] Sources(string role) => [.. project.Evidence.Single(r => r.RoleKey == role).Sources.Select(s => s.ArchiveFileName)];
        project.ProjectImportId.Should().Be("import-m2lb");
        Sources(ProjectEvidenceRoles.ApplicationSource).Should().Equal("M2LB.zip");
        Sources(ProjectEvidenceRoles.SupportingSource).Should().Equal("M2LB.Common.zip");
        Sources(ProjectEvidenceRoles.Infrastructure).Should().Equal("Terraform-iac-m2lb.zip");
        Sources(ProjectEvidenceRoles.Documentation).Should().BeEquivalentTo(["M2LB.zip", "M2LB.Common.zip", "Terraform-iac-m2lb.zip"], "every associated package's documents");
        project.Evidence.SelectMany(r => r.Sources).Should().OnlyContain(s => s.BoundBy == ProjectEvidenceAssociation.Detected);
        project.Evidence.SelectMany(r => r.Sources).Should().NotContain(s => s.ArchiveFileName == "Tiltak 2.zip", "uncertain evidence is never associated automatically");

        ProjectEvidenceAssociation.Apply(profile, Associations(profile), T0.AddHours(1)).Should().BeFalse("recomputing an unchanged workspace changes nothing");
        var view = ProjectEvidenceResolver.Resolve("dev", "Development", project, ProjectEvidencePackages.All(Lifecycle, Setup), Setup) with
            { Associations = Associations(profile), TargetApplication = profile.TargetApplication };
        view.Row(ProjectEvidenceRoles.ApplicationSource)!.IsResolved.Should().BeTrue();
        view.Row(ProjectEvidenceRoles.Documentation)!.IsResolved.Should().BeTrue();
        view.NeedsReviewCount.Should().Be(1);
    }

    [Fact]
    public void ExplicitDecisionsSurviveRefresh_AndARemovedEvidenceSetIsNotRecreated()
    {
        UserWorkspace();
        var profile = Profile("M2LB");
        ProjectEvidenceAssociation.Apply(profile, Associations(profile), T0);
        var project = profile.Project!;

        // The user confirms MU as integration-ish supporting evidence and removes Terraform from Infrastructure.
        var packages = ProjectEvidencePackages.All(Lifecycle, Setup);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.SupportingSource, packages.Single(p => p.DisplayName == "Tiltak 2"), T0).Should().BeTrue();
        ProjectEvidenceResolver.Unbind(project, ProjectEvidenceRoles.Infrastructure, "import-terraform-iac-m2lb");
        project.Declined.Add(new ProjectEvidenceDecline { RoleKey = ProjectEvidenceRoles.Infrastructure, ImportId = "import-terraform-iac-m2lb", At = T0 });

        ProjectEvidenceAssociation.Apply(profile, Associations(profile), T0.AddHours(1));
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Infrastructure).Sources.Should().BeEmpty("a declined automatic association stays declined");
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.SupportingSource).Sources.Should().Contain(s => s.ArchiveFileName == "Tiltak 2.zip" && s.BoundBy == "User");
        For(Associations(profile), "Tiltak 2").Should().Match<PackageAssociation>(a => a.State == EvidenceAssociationState.Associated && a.UserConfirmed);

        // "Remove project evidence" archives it: never recreated behind the user's back.
        project.ArchivedAt = T0; profile.ArchivedProjects = [project]; profile.Project = null;
        ProjectEvidenceAssociation.Apply(profile, Associations(profile), T0.AddHours(2)).Should().BeFalse();
        profile.Project.Should().BeNull();
    }

    [Fact]
    public void TwoApplicationSourcesNamedLikeTheTargetApplication_AreAConflict_NeverASilentChoice()
    {
        Import("M2LB", 1, services: 16, shared: 2, code: "Partial");
        var second = Import("M2LB", 2, services: 16, shared: 2, code: "Partial");
        second.ImportId = "import-m2lb-2"; second.ArchiveFileName = "M2LB (1).zip"; _snapshots[^1] = _snapshots[^1] with { ImportId = "import-m2lb-2" };
        ApplicationContexts.Ensure(Lifecycle, T0);
        var profile = Profile("M2LB");
        var associations = Associations(profile);
        associations.Where(a => a.Role is PackageEvidenceRole.PrimaryApplicationSource or PackageEvidenceRole.RelatedApplicationSource)
            .Should().HaveCount(2).And.OnlyContain(a => a.State == EvidenceAssociationState.NeedsReview);
        ProjectEvidenceAssociation.Apply(profile, associations, T0);
        (profile.Project?.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources ?? []).Should().BeEmpty();
    }

    [Fact]
    public void ARoleThatNoLongerHolds_IsRemovedOnRefresh_WhileReimportedEvidenceIsReassociated()
    {
        UserWorkspace();
        var profile = Profile("M2LB");
        ProjectEvidenceAssociation.Apply(profile, Associations(profile), T0);
        // Re-analysis now classifies M2LB.Common as an application with a host project: it is no longer the supporting source.
        _snapshots[1] = _snapshots[1] with { ServiceProjectCount = 2 };
        ProjectEvidenceAssociation.Apply(profile, Associations(profile), T0.AddHours(1)).Should().BeTrue();
        profile.Project!.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.SupportingSource).Sources.Should().BeEmpty();
        For(Associations(profile), "M2LB.Common").State.Should().Be(EvidenceAssociationState.NeedsReview);
    }

    [Fact]
    public void DocumentsCard_NeverSendsTheTesterToANonExistentBindAction()
    {
        var evidence = new BirkNext.Web.Services.Explorers.ExplorerProjectEvidence(false, "dev", "Development", "Tiltak 2", [], false, ["M2LB.zip", "M2LB.Common.zip", "Terraform-iac-m2lb.zip"]);
        var workspace = WorkspaceSnapshots.Loaded("Tiltak 2", "import:t", "Tiltak 2", []) with { Evidence = evidence };

        var waiting = ProjectInputPresentation.Documents(workspace, Profile(null));
        waiting.StatusLabel.Should().Be("Available in imported evidence");
        waiting.ActionLabel.Should().Be("Configure Target Application");
        waiting.Route.Should().Contain("tab=target");
        waiting.Detail.Should().Contain("M2LB.zip, M2LB.Common.zip, Terraform-iac-m2lb.zip");

        var configured = ProjectInputPresentation.Documents(workspace, Profile("M2LB"));
        configured.ActionLabel.Should().Be("Review project evidence");
        foreach (var input in new[] { waiting, configured })
            (input.Detail + input.ActionLabel + input.StatusLabel).Should().NotContainEquivalentOf("bind", "the Project tab associates automatically; there is no Bind step to send the tester to");
    }
}
