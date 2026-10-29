using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Project setup planning: logical projects are systems, never evidence packages; suggestions are explained and only strong evidence is
/// preselected; decisions apply to the one canonical binding model.
/// </summary>
public sealed class ProjectSetupPlannerTests
{
    private static ProjectEvidencePackage Package(string id, string archive, params ProjectEvidenceCapability[] capabilities) => new(
        id, archive[..^4], archive, "sha-" + id, DateTimeOffset.UtcNow, capabilities.Any(c => c is not ProjectEvidenceCapability.Documentation) ? Guid.NewGuid() : null,
        [.. capabilities.Select(c => new PackageCapability(c, PackageCapabilityStatus.Ready, c.ToString()))],
        new Dictionary<WorkspaceArtifactType, int> { [WorkspaceArtifactType.Specification] = capabilities.Contains(ProjectEvidenceCapability.Documentation) ? 2 : 0 },
        capabilities.Contains(ProjectEvidenceCapability.Documentation) ? 2 : 0, [], [], [], "Ready");

    private static readonly ProjectEvidencePackage M2lb = Package("a", "M2LB.zip", ProjectEvidenceCapability.ApplicationSource, ProjectEvidenceCapability.Documentation, ProjectEvidenceCapability.ContractSchema);
    private static readonly ProjectEvidencePackage Tiltak = Package("t", "Tiltak 2 - Sikkert digitalt økosystem.zip", ProjectEvidenceCapability.Documentation);
    // An infrastructure repository with a few Markdown documents (as the real Terraform-iac-m2lb.zip): still infrastructure evidence.
    private static readonly ProjectEvidencePackage Iac = Package("i", "Terraform-iac-m2lb.zip", ProjectEvidenceCapability.Infrastructure, ProjectEvidenceCapability.Documentation);
    private static readonly ProjectEvidencePackage Ops = Package("o", "Terraform-BasicOpsInfra_M2LBM01.zip", ProjectEvidenceCapability.Infrastructure);
    private static readonly ProjectEvidencePackage Common = Package("c", "M2LB.Common.zip", ProjectEvidenceCapability.SupportingSource);
    private static readonly ProjectEvidencePackage Shared = Package("s", "SharedPlatform.zip", ProjectEvidenceCapability.Infrastructure);
    private static readonly IReadOnlyList<ProjectEvidencePackage> All = [M2lb, Tiltak, Iac, Ops, Common, Shared];

    [Fact]
    public void Only_systems_are_project_candidates_infrastructure_and_shared_libraries_are_evidence()
    {
        var options = ProjectSetupPlanner.Options(All, ["Skolenærvær"]);
        options.Select(o => (o.Name, o.Group)).Should().Equal(
            ("M2LB", ProjectSetupPlanner.SuggestedGroup),
            ("Tiltak 2 - Sikkert digitalt økosystem", ProjectSetupPlanner.OtherGroup),
            ("Skolenærvær", ProjectSetupPlanner.KnownGroup));
        ProjectSetupPlanner.Search(options, "m2lb").Select(o => o.Name).Should().Equal(["M2LB"], "Terraform-iac-m2lb is not matched as a project name");
    }

    [Fact]
    public void The_checklist_preselects_strong_evidence_only_and_explains_every_suggestion()
    {
        var groups = ProjectSetupPlanner.Checklist("M2LB", M2lb, All, null).ToDictionary(g => g.Role.RoleKey);

        groups[ProjectEvidenceRoles.ApplicationSource].Items.Should().ContainSingle(i => i.Package == M2lb && i.Selected && i.Strength == EvidenceMatchStrength.Strong);
        groups[ProjectEvidenceRoles.Contracts].Items.Should().ContainSingle(i => i.Package == M2lb && i.Selected);
        var infra = groups[ProjectEvidenceRoles.Infrastructure].Items;
        infra.Select(i => i.Package.ArchiveFileName).Should().BeEquivalentTo(["Terraform-iac-m2lb.zip", "Terraform-BasicOpsInfra_M2LBM01.zip"]);
        infra.Should().OnlyContain(i => !i.Selected && i.Strength == EvidenceMatchStrength.Possible && i.Reason.Contains("name similarity only"),
            "a name resemblance is a possible match — never preselected without stronger evidence");
        groups[ProjectEvidenceRoles.Infrastructure].Addable.Should().ContainSingle(p => p == Shared, "unrelated packages can still be added by hand");
        groups[ProjectEvidenceRoles.SupportingSource].Items.Should().Contain(i => i.Package == Common && !i.Selected);
    }

    [Fact]
    public void Expected_evidence_matches_only_packages_that_name_it()
    {
        var adapter = Package("x", "SkoleAdapter.zip", ProjectEvidenceCapability.ApplicationSource);
        ProjectSetupPlanner.NamesExpected(adapter, "SkoleAdapter source").Should().BeTrue();
        ProjectSetupPlanner.NamesExpected(M2lb, "SkoleAdapter source").Should().BeFalse();
        ProjectSetupPlanner.NamesExpected(M2lb, "source").Should().BeFalse("generic words never match anything");
    }

    [Fact]
    public void Decisions_apply_to_the_canonical_bindings_and_remember_declines()
    {
        var project = new EnvironmentProject { ProjectName = "M2LB", Evidence = [.. ProjectEvidenceRoles.Defaults()] };
        var now = DateTimeOffset.UtcNow;

        ProjectSetupPlanner.Apply(project, All,
        [
            new(ProjectEvidenceRoles.ApplicationSource, ["a"], []),
            new(ProjectEvidenceRoles.Infrastructure, ["i", "o"], []),
            new(ProjectEvidenceRoles.Contracts, [], ["a"]),
        ], now).Should().BeTrue();

        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Infrastructure).Sources.Select(s => s.ImportId).Should().BeEquivalentTo(["i", "o"]);
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources.Single().BoundBy.Should().Be("ProjectSelection");
        project.Declined.Should().ContainSingle(d => d.RoleKey == ProjectEvidenceRoles.Contracts && d.ImportId == "a");

        // Deselect one infrastructure package; select the declined contracts again: the decline is cleared.
        ProjectSetupPlanner.Apply(project, All, [new(ProjectEvidenceRoles.Infrastructure, ["i"], ["o"]), new(ProjectEvidenceRoles.Contracts, ["a"], [])], now);
        project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.Infrastructure).Sources.Select(s => s.ImportId).Should().Equal(["i"]);
        project.Declined.Should().ContainSingle(d => d.RoleKey == ProjectEvidenceRoles.Infrastructure && d.ImportId == "o");

        var again = ProjectSetupPlanner.Checklist("M2LB", M2lb, All, project).Single(g => g.Role.RoleKey == ProjectEvidenceRoles.Infrastructure);
        again.Items.Single(i => i.Package == Ops).Selected.Should().BeFalse("a remembered deselection is never reselected");
    }

    [Fact]
    public void An_infrastructure_only_snapshot_without_areas_is_never_an_application()
    {
        var lifecycle = new SddLifecycleState();
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "i", ArchiveFileName = "Terraform-iac-m2lb.zip", ProjectName = "Terraform-iac-m2lb", SourceSnapshotId = Guid.NewGuid(), SourceState = "Created" });
        var setup = new BirkNext.Integrations.SourceSetupView
        {
            Snapshots = [new BirkNext.Integrations.SourceSetupSnapshot(lifecycle.ProjectImports[0].SourceSnapshotId!.Value, "Terraform-iac-m2lb.zip", "sha", DateTimeOffset.UtcNow, "Ready")
                { ImportId = "i", InfrastructureFormats = ["Terraform"] }],
        };
        var package = ProjectEvidencePackages.Of(lifecycle.ProjectImports[0], lifecycle, setup);
        package.IsApplicationCandidate.Should().BeFalse();
        ProjectSetupPlanner.IsLogicalProjectCandidate(package).Should().BeFalse();
    }
}
