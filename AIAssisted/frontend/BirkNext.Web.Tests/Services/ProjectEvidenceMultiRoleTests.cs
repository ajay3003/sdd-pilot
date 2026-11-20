using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Project Evidence multi-role audit (2026-11-20, Tiltak 2 + M2LB): a source has detected capabilities; each Target Application assigns it one or
/// more reviewed roles in its own Target Environment's bindings; the application source may have several contributing sources with one primary;
/// and an import that was added to the wrong project can become its own project without re-importing. Synthetic packages.
/// </summary>
public sealed class ProjectEvidenceMultiRoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static ProjectEvidencePackage Package(string id, string name, params (ProjectEvidenceCapability Capability, PackageCapabilityStatus Status)[] caps) =>
        new(id, name, name + ".zip", "sha-" + id, Now, Guid.NewGuid(), [.. caps.Select(c => new PackageCapability(c.Capability, c.Status, "fact"))],
            new Dictionary<WorkspaceArtifactType, int>(), caps.Any(c => c.Capability == ProjectEvidenceCapability.Documentation) ? 3 : 0, [], [], [], "Analyzed");

    private static readonly ProjectEvidencePackage M2lb = Package("m2lb", "M2LB", (ProjectEvidenceCapability.ApplicationSource, PackageCapabilityStatus.Ready),
        (ProjectEvidenceCapability.Documentation, PackageCapabilityStatus.Ready), (ProjectEvidenceCapability.ContractSchema, PackageCapabilityStatus.Partial),
        (ProjectEvidenceCapability.GeneratedDocumentation, PackageCapabilityStatus.Ready));
    private static readonly ProjectEvidencePackage Tiltak = Package("t2", "Tiltak 2", (ProjectEvidenceCapability.ApplicationSource, PackageCapabilityStatus.Partial),
        (ProjectEvidenceCapability.ContractSchema, PackageCapabilityStatus.Ready));

    private static ProjectEvidenceView View(EnvironmentProject project) =>
        ProjectEvidenceResolver.Resolve("env", "Dev", project, [M2lb, Tiltak], null);

    [Fact]
    public void One_source_holds_several_roles_and_the_application_source_takes_a_primary_and_contributing_sources()
    {
        var project = ProjectEvidenceResolver.CreateProject(M2lb, Now);
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Cardinality.Should().Be(ProjectEvidenceCardinality.Multiple);
        ProjectEvidenceResolver.Bind(project, ProjectEvidenceRoles.ApplicationSource, Tiltak, Now).Should().BeTrue();

        var view = View(project);
        var row = view.Row(ProjectEvidenceRoles.ApplicationSource)!;
        row.Bound.Select(b => b.ImportId).Should().BeEquivalentTo(["m2lb", "t2"], "a second application source contributes; it no longer replaces the first");
        row.State.Should().NotBe(EvidenceRoleState.Conflict);
        SourceRoleAssignments.ApplicationSourceSummary(view).Should().Be("Application source: M2LB (primary) + 1 contributing");
        ProjectEvidenceResolver.BoundSnapshot(project, ProjectEvidenceRoles.ApplicationSource).Should().Be(M2lb.SnapshotId, "consumers read the primary's snapshot");

        var m2lb = SourceRoleAssignments.Sources(view).First();
        m2lb.Package.ImportId.Should().Be("m2lb", "the primary application source is listed first");
        m2lb.Roles.Select(r => r.Label).Should().Contain(["Application source (primary)", "Project documentation", "Contracts and schemas", "Generated documentation"]);
        m2lb.ReviewStatus.Should().Be("Confirmed by you", "the project selection is a person's choice");
        m2lb.Completeness.Label.Should().Be("Partial — contracts and schemas incomplete", "a confirmed role with partial evidence is valid, and both facts are shown");
    }

    [Fact]
    public void A_role_the_source_cannot_provide_is_shown_but_never_offered()
    {
        var view = View(ProjectEvidenceResolver.CreateProject(M2lb, Now));
        var options = SourceRoleAssignments.Options(view, Tiltak);
        options.Single(o => o.Row.Role.RoleKey == ProjectEvidenceRoles.Documentation).Should().Match<RoleOption>(o => !o.Supported && o.Reason!.Contains("Not detected in Tiltak 2"));
        options.Single(o => o.Row.Role.RoleKey == ProjectEvidenceRoles.ApplicationSource).Supported.Should().BeTrue();
    }

    [Fact]
    public void Saved_single_source_application_evidence_is_upgraded_once_and_named_expected_evidence_is_left_alone()
    {
        var project = ProjectEvidenceResolver.CreateProject(M2lb, Now);
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Cardinality = ProjectEvidenceCardinality.Single;
        project.Evidence.Add(ProjectEvidenceRoles.Role("expected-skoleadapter", "SkoleAdapter source", ProjectEvidenceCapability.ApplicationSource,
            ProjectEvidenceRequirement.Required, ProjectEvidenceCardinality.Single, "Expected", ProjectEvidenceRoleOrigin.Expected));

        ProjectEvidenceRoles.Upgrade(project).Should().BeTrue();
        ProjectEvidenceRoles.Upgrade(project).Should().BeFalse("only once");
        project.Evidence.Single(r => r.RoleKey == "expected-skoleadapter").Cardinality.Should().Be(ProjectEvidenceCardinality.Single);
    }

    [Fact]
    public void Roles_are_per_Target_Environment_so_one_application_never_changes_another()
    {
        var forM2lb = ProjectEvidenceResolver.CreateProject(M2lb, Now);
        var forTiltak = ProjectEvidenceResolver.CreateProject(Tiltak, Now);
        ProjectEvidenceResolver.Bind(forTiltak, ProjectEvidenceRoles.SupportingSource, M2lb, Now);

        SourceRoleAssignments.RolesOf(View(forM2lb), "m2lb").Should().Contain(r => r.Primary);
        SourceRoleAssignments.RolesOf(View(forTiltak), "m2lb").Select(r => r.Row.Role.RoleKey).Should().Equal(ProjectEvidenceRoles.SupportingSource);
        SourceRoleAssignments.RolesOf(View(forTiltak), "t2").Should().Contain(r => r.Primary);
        ProjectEvidenceResolver.Unbind(forTiltak, ProjectEvidenceRoles.SupportingSource, "m2lb");
        SourceRoleAssignments.RolesOf(View(forM2lb), "m2lb").Should().HaveCountGreaterThan(1, "M2LB's own roles are untouched");
    }

    // ── Belongs to project ───────────────────────────────────────────────────────────────────────────────────────

    private static SddLifecycleState Workspace()
    {
        var app = new SddApplicationContext { Id = "app-t2", Name = "Tiltak 2", NameBasis = "Manual", CreatedAt = Now };
        return new SddLifecycleState
        {
            Applications = [app], CurrentApplicationId = app.Id,
            ProjectImports =
            [
                new SddProjectImportRecord { ImportId = "t2", ProjectName = "Tiltak 2", ArchiveFileName = "Tiltak 2.zip", ApplicationId = app.Id, ImportedAt = Now, SourceSnapshotId = Guid.NewGuid() },
                new SddProjectImportRecord { ImportId = "m2lb", ProjectName = "M2LB", ArchiveFileName = "M2LB.zip", ApplicationId = app.Id, ImportedAt = Now.AddMinutes(5), SourceSnapshotId = Guid.NewGuid() },
            ],
        };
    }

    [Fact]
    public void A_package_imported_into_the_wrong_project_becomes_its_own_project_without_re_importing()
    {
        var lifecycle = Workspace();
        var snapshot = lifecycle.ProjectImports[1].SourceSnapshotId;

        var (application, problem) = ApplicationContexts.MoveImport(lifecycle, "m2lb", null, "M2LB", Now);

        problem.Should().BeNull();
        application!.Name.Should().Be("M2LB");
        lifecycle.Applications.Select(a => a.Name).Should().BeEquivalentTo(["Tiltak 2", "M2LB"], "the Project list now has two entries");
        lifecycle.ProjectImports.Should().HaveCount(2, "nothing is re-imported or removed");
        lifecycle.ProjectImports[1].SourceSnapshotId.Should().Be(snapshot);
        ApplicationContexts.Imports(lifecycle, "app-t2").Select(i => i.ImportId).Should().Equal("t2");
    }

    [Fact]
    public void Moving_refuses_duplicates_and_removes_an_emptied_project()
    {
        var lifecycle = Workspace();
        ApplicationContexts.MoveImport(lifecycle, "m2lb", null, "tiltak 2", Now).Problem.Should().Contain("already exists");
        ApplicationContexts.MoveImport(lifecycle, "m2lb", "app-t2", null, Now).Problem.Should().Contain("already belongs");
        ApplicationContexts.MoveImport(lifecycle, "nope", null, "X", Now).Problem.Should().Contain("no longer imported");

        var (m2lb, _) = ApplicationContexts.MoveImport(lifecycle, "m2lb", null, "M2LB", Now);
        var (back, problem) = ApplicationContexts.MoveImport(lifecycle, "m2lb", "app-t2", null, Now);
        problem.Should().BeNull();
        back!.Id.Should().Be("app-t2");
        lifecycle.Applications.Should().NotContain(a => a.Id == m2lb!.Id, "a project left without packages is removed");
    }
}
