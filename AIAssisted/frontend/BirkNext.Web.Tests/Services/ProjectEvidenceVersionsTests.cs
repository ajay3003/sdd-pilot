using BirkNext.Integrations;
using BirkNext.SourceBaselines;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The reported Target Environments → Project Evidence state with five M2LB ZIP imports ("Application source not selected · candidate: M2LB",
/// four suggestions to review): several imports of one logical source are versions, not competing application sources. With the shared source
/// context the decision is one explicit choice (the analysis baseline); once chosen, that version is the application source and the others are
/// kept as alternative versions — never a silent pick, never an approval of all candidates.
/// </summary>
public sealed class ProjectEvidenceVersionsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly List<SourceSetupSnapshot> _snapshots = [];
    private SourceSetupView Setup => new() { Snapshots = [.. _snapshots] };
    private SddLifecycleState Lifecycle => _repository.SddLifecycle;
    private readonly Dictionary<string, Guid> _snapshotOf = new();

    private void Import(string project, string archive, int minutes, int services, int shared, string code, string iac = "NotDetected")
    {
        var id = "import-" + archive.ToLowerInvariant().Replace(' ', '-').Replace('.', '-').Replace("(", "").Replace(")", "");
        var snapshot = new SourceSetupSnapshot(Guid.NewGuid(), archive, "sha-" + id, T0.AddMinutes(minutes), "Partial")
        {
            ImportId = id, ServiceProjectCount = services, SharedProjectCount = shared, InfrastructureFormats = iac == "NotDetected" ? [] : ["Terraform"],
            Areas = [new("Application code", code), new("Infrastructure as Code", iac), new("Contracts", "NotDetected")],
        };
        _snapshots.Add(snapshot);
        _snapshotOf[archive] = snapshot.SnapshotId;
        Lifecycle.ProjectImports.Add(new SddProjectImportRecord
        {
            ImportId = id, ProjectName = project, ArchiveFileName = archive, ArchiveSha256 = "sha-" + id, ImportedAt = T0.AddMinutes(minutes),
            SourceState = "Created", SourceSnapshotId = snapshot.SnapshotId,
        });
    }

    private static readonly string[] Versions = ["M2LB (3).zip", "M2LB (4).zip", "M2LB (5).zip", "M2LB (6).zip", "M2LB (7).zip"];

    private void Workspace()
    {
        for (var i = 0; i < Versions.Length; i++) Import("M2LB", Versions[i], i, services: 12, shared: 20, code: "Partial");
        Import("M2LB.Common", "M2LB.Common.zip", 10, services: 0, shared: 1, code: "Partial");
        Import("Terraform-iac-m2lb", "Terraform-iac-m2lb.zip", 11, services: 0, shared: 1, code: "NotDetected", iac: "Partial");
        ApplicationContexts.Ensure(Lifecycle, T0);
        ApplicationContexts.Reconcile(Lifecycle, Setup);
    }

    /// <summary>The backend's view: one logical source "m2lb" with five different contents; <paramref name="baseline"/> selected or not.
    /// Without a baseline nothing is authoritative (BASELINE_NOT_SELECTED): no version is used for analysis; the newest is only a preview.</summary>
    private SourceContextView Context(string? baseline)
    {
        var snapshots = Versions.Select((v, i) => new SourceSnapshotView(_snapshotOf[v], v, "sha", "content-" + i, T0.AddMinutes(i), "Partial", SourceBranchKind.Unknown, null, null,
            null, v == baseline, i == Versions.Length - 1, v == baseline, 1, 10, [], 0)).Reverse().ToList();
        var m2lb = new LogicalSourceView("m2lb", "M2LB", "Archive file name", baseline is null ? SourceBaselineState.NotSelected : SourceBaselineState.Selected,
            baseline is null ? null : _snapshotOf[baseline], baseline is null ? null : _snapshotOf[baseline], "", 5, 5, snapshots,
            baseline is null ? "BASELINE_NOT_SELECTED" : "BASELINE_SELECTED", baseline is null ? _snapshotOf[Versions[^1]] : null);
        return new SourceContextView([m2lb], 1, 5, baseline is null ? 1 : 0, "fp");
    }

    private static FrontendAnalysisProfile Profile() => new() { Id = "dev", Name = "Development", TargetApplication = new TargetApplicationIdentity { Name = "M2LB", SetAt = T0 } };

    private IReadOnlyList<PackageAssociation> Associations(FrontendAnalysisProfile profile, SourceContextView? context) =>
        ProjectEvidenceAssociation.Of(TargetApplications.Of(profile), profile.Project, Lifecycle, ProjectEvidencePackages.All(Lifecycle, Setup), Setup, null, context);

    private IEnumerable<PackageAssociation> M2lb(IReadOnlyList<PackageAssociation> list) => list.Where(a => a.Package.DisplayName == "M2LB");

    [Fact]
    public void Reproduction_without_the_source_context_five_versions_compete_and_no_application_source_is_selected()
    {
        Workspace();
        var associations = Associations(Profile(), null);
        M2lb(associations).Should().NotContain(a => a.State == EvidenceAssociationState.Associated && a.Role == PackageEvidenceRole.PrimaryApplicationSource,
            "the reported state: no application source");
        M2lb(associations).Count(a => a.State == EvidenceAssociationState.NeedsReview).Should().BeGreaterThan(1, "several review items for one decision");
    }

    [Fact]
    public void Without_a_baseline_the_versions_become_one_explicit_decision_and_none_is_picked()
    {
        Workspace();
        var profile = Profile();
        var associations = Associations(profile, Context(null));
        var review = M2lb(associations).Where(a => a.State == EvidenceAssociationState.NeedsReview).ToList();
        review.Should().ContainSingle().Which.Detail.Should().Contain("5 versions of M2LB").And.Contain("no analysis baseline").And.Contain("Source versions").And.Contain("does not pick a branch");
        M2lb(associations).Where(a => a.State != EvidenceAssociationState.NeedsReview).Should().HaveCount(4)
            .And.OnlyContain(a => a.State == EvidenceAssociationState.NotAssociated && a.AlternativeVersion);
        M2lb(associations).Should().NotContain(a => a.State == EvidenceAssociationState.Associated, "nothing is associated automatically");
        associations.Single(a => a.Package.DisplayName == "M2LB.Common").State.Should().Be(EvidenceAssociationState.Associated, "supporting sources are unaffected");
        associations.Single(a => a.Package.DisplayName == "Terraform-iac-m2lb").State.Should().Be(EvidenceAssociationState.Associated);
        ProjectEvidenceAssociation.Apply(profile, associations, T0);
        profile.Project!.ProjectImportId.Should().BeNull("no application source is chosen for the person");
    }

    [Fact]
    public void A_selected_baseline_is_the_application_source_alternatives_are_kept_and_the_main_package_follows_a_baseline_switch()
    {
        Workspace();
        var profile = Profile();
        var withFive = Associations(profile, Context("M2LB (5).zip"));
        var baseline = M2lb(withFive).Single(a => a.State == EvidenceAssociationState.Associated);
        baseline.Package.ArchiveFileName.Should().Be("M2LB (5).zip");
        baseline.Role.Should().Be(PackageEvidenceRole.PrimaryApplicationSource);
        baseline.AnalysisBaseline.Should().BeTrue();
        baseline.Detail.Should().Contain("not a deployment claim");
        M2lb(withFive).Where(a => a != baseline).Should().HaveCount(4)
            .And.OnlyContain(a => a.State == EvidenceAssociationState.NotAssociated && a.AlternativeVersion && a.Detail.Contains("M2LB (5).zip"));
        withFive.Should().NotContain(a => a.State == EvidenceAssociationState.NeedsReview, "the decision is made");

        ProjectEvidenceAssociation.Apply(profile, withFive, T0).Should().BeTrue();
        profile.Project!.ProjectImportId.Should().Be("import-m2lb-5-zip");
        profile.Project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources.Select(s => s.ArchiveFileName).Should().Equal("M2LB (5).zip");

        ProjectEvidenceAssociation.Apply(profile, Associations(profile, Context("M2LB (7).zip")), T0.AddMinutes(5)).Should().BeTrue();
        profile.Project.ProjectImportId.Should().Be("import-m2lb-7-zip", "an automatic main package follows the new analysis baseline");
        profile.Project.Evidence.Single(r => r.RoleKey == ProjectEvidenceRoles.ApplicationSource).Sources.Select(s => s.ArchiveFileName).Should().Equal("M2LB (7).zip");
    }

    [Fact]
    public void A_manual_main_package_is_never_moved_by_a_baseline()
    {
        Workspace();
        var profile = Profile();
        ProjectEvidenceAssociation.Apply(profile, Associations(profile, Context("M2LB (5).zip")), T0);
        profile.Project!.Manual = true;
        ProjectEvidenceAssociation.Apply(profile, Associations(profile, Context("M2LB (7).zip")), T0.AddMinutes(5));
        profile.Project.ProjectImportId.Should().Be("import-m2lb-5-zip");
    }
}
