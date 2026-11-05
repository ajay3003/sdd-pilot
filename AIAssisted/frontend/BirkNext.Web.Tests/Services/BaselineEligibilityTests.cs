using BirkNext.SourceEvidence;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>Master hardening (NEXT43, B2): the shared baseline rule for Test Coverage &amp; Overlap, Code Risk and other change reviews. M2LB-language
/// and M2LB have different repository identities: never matched by similar names, never hidden, comparable only by an explicit declaration.</summary>
public sealed class BaselineEligibilityTests
{
    static ReviewSourceSnapshot S(string repo, int daysAgo, string basis = "Import repository") => new()
    {
        SnapshotId = Guid.NewGuid(), RepositoryKey = repo.ToLowerInvariant(), Repository = repo, IdentityBasis = basis, ArchiveName = $"{repo}.zip",
        AnalyzedAt = DateTimeOffset.UnixEpoch.AddDays(-daysAgo),
    };

    [Fact]
    public void Same_identity_is_eligible_other_identities_are_listed_with_the_reason_and_need_confirmation()
    {
        var feature = S("M2LB-language", 0);
        var featureEarlier = S("M2LB-language", 1);
        var main = S("M2LB", 2);

        var candidates = BaselineEligibility.For([feature, featureEarlier, main], feature);

        candidates.Select(c => (c.Snapshot.Repository, c.Eligible)).Should().Equal(("M2LB-language", true), ("M2LB", false));
        candidates[1].Reason.Should().Contain("Different repository identity: M2LB").And.Contain("similar names are never treated as the same repository");
        BaselineEligibility.Unavailable([feature, main], feature).Should().BeNull("another snapshot exists, even if it needs confirmation");
        BaselineEligibility.Unavailable([feature], feature).Should().Contain("No other analyzed snapshot exists");
    }

    [Fact]
    public void Validation_records_how_the_baseline_relates_and_refuses_an_unconfirmed_other_identity()
    {
        var feature = new SourceRepositoryIdentity("m2lb-language", "M2LB-language", "Import repository");
        var main = new SourceRepositoryIdentity("m2lb", "M2LB", "Import repository");

        BaselineEligibility.Validate(feature, feature, false).Relation!.Kind.Should().Be(BaselineRelationKind.SameRepositoryIdentity);
        var (refusedRelation, error) = BaselineEligibility.Validate(feature, main, false);
        refusedRelation.Should().BeNull();
        error.Should().Contain("Confirm that both are versions of the same repository");
        var (declared, none) = BaselineEligibility.Validate(feature, main, true);
        none.Should().BeNull();
        declared!.Kind.Should().Be(BaselineRelationKind.DeclaredByReviewer);
        declared.Explanation.Should().Contain("Declared by the reviewer").And.Contain("Not detected");
    }

    [Fact]
    public void The_default_current_snapshot_is_the_Target_Applications_main_package_not_the_package_imported_last()
    {
        var common = S("M2LB.Common", 0);
        var main = S("M2LB", 3);
        var package = new ProjectEvidencePackage("import-m2lb", "M2LB", "M2LB.zip", "sha", DateTimeOffset.UnixEpoch, main.SnapshotId, [], new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null);
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(null)).Returns(new ProjectEvidenceView("dev", "Development", null, package, [], [], [package], []));
        var services = new ServiceCollection().AddSingleton(evidence.Object).BuildServiceProvider();

        ReviewSourceDefaults.Current(services, [common, main]).Should().Be(main.SnapshotId);
        ReviewSourceDefaults.Current(new ServiceCollection().BuildServiceProvider(), [common, main]).Should().Be(common.SnapshotId, "without Project Evidence: the newest");
        ReviewSourceDefaults.Current(services, [common, main with { HasConsumerEvidence = false }]).Should().Be(common.SnapshotId,
            "a main package without the review's evidence is not forced as the default");
    }
}
