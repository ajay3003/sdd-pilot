using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Graph impact (impact/1): "if the contract changes, what depends on it?" Direct / Downstream / Potential from relation evidence, tests only via
/// VERIFIES (established = must retest, candidate = should retest), test gaps for unverified requirements, no alias traversal, rejected relations
/// ignored, unresolved relations counted as Unknown, cycle-safe and depth-bounded.
/// </summary>
public sealed class CanonicalImpactTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    static async Task<CanonicalInterpretationService> Graph()
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"impact-{Guid.NewGuid():N}").Options);
        var ids = new Dictionary<string, Guid>();
        void Entity(CanonicalEntityType type, string id, string name)
        {
            ids[id] = Guid.NewGuid();
            db.Entities.Add(new CanonicalEntityRecord { Id = ids[id], Scope = "proj", EntityType = type.ToString(), IdentityKey = id, DisplayName = name });
        }
        void Relation(string source, CanonicalRelationType type, string target, RelationResolution resolution, RelationReviewState review = RelationReviewState.NotReviewed) =>
            db.Relations.Add(new CanonicalRelationRecord
            {
                Id = Guid.NewGuid(), Scope = "proj", SourceEntityId = ids[source], TargetEntityId = ids[target], RelationType = type.ToString(),
                RelationKey = $"{source}-{type}-{target}", Resolution = resolution.ToString(), ReviewState = review.ToString(),
            });

        Entity(CanonicalEntityType.Contract, "fravaer", "Fravaersmelding");
        Entity(CanonicalEntityType.UserStory, "us1", "Register absence");
        Entity(CanonicalEntityType.Scenario, "sc1", "Absence over 10 days");
        Entity(CanonicalEntityType.Requirement, "fr1", "FR-001 absence is reported");
        Entity(CanonicalEntityType.Feature, "f-candidate", "Candidate dependent");
        Entity(CanonicalEntityType.Feature, "f-unresolved", "Unresolved dependent");
        Entity(CanonicalEntityType.Feature, "f-rejected", "Rejected dependent");
        Entity(CanonicalEntityType.Contract, "fravaer-alias", "Fraværsmelding (alias)");
        Entity(CanonicalEntityType.Test, "t-explicit", "AbsenceTests.Registers");
        Entity(CanonicalEntityType.Test, "t-candidate", "AbsenceLongTests.Over10Days");
        Entity(CanonicalEntityType.Test, "t-unrelated", "Unrelated.Uses");

        Relation("us1", CanonicalRelationType.DependsOn, "fravaer", RelationResolution.Explicit);            // Direct
        Relation("us1", CanonicalRelationType.Contains, "sc1", RelationResolution.Derived);                  // Downstream (forward)
        Relation("fr1", CanonicalRelationType.DerivedFrom, "us1", RelationResolution.Explicit);              // Downstream, unverified
        Relation("f-candidate", CanonicalRelationType.DependsOn, "fravaer", RelationResolution.Candidate);   // Potential
        Relation("f-unresolved", CanonicalRelationType.DependsOn, "fravaer", RelationResolution.Unresolved); // Unknown
        Relation("f-rejected", CanonicalRelationType.DependsOn, "fravaer", RelationResolution.Explicit, RelationReviewState.Rejected);
        Relation("fravaer-alias", CanonicalRelationType.AliasOf, "fravaer", RelationResolution.Explicit);    // terminology, never impact
        Relation("fravaer", CanonicalRelationType.DependsOn, "us1", RelationResolution.Explicit);            // cycle back to the root
        Relation("t-explicit", CanonicalRelationType.Verifies, "us1", RelationResolution.Explicit);
        Relation("t-candidate", CanonicalRelationType.Verifies, "sc1", RelationResolution.Candidate);
        Relation("t-unrelated", CanonicalRelationType.Uses, "fravaer", RelationResolution.Explicit);         // a test is impacted only via VERIFIES
        await db.SaveChangesAsync();
        return new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System);
    }

    [Fact]
    public async Task A_contract_change_impacts_its_dependents_with_paths_and_retest_needs_from_relation_evidence()
    {
        var view = (await (await Graph()).ImpactAsync("proj", "Contract|fravaer", default))!;

        view.RootName.Should().Be("Fravaersmelding");
        view.Rules.Should().Be("impact/1");
        var levels = view.Entities.ToDictionary(e => e.Name, e => e.Level);
        levels.Should().BeEquivalentTo(new Dictionary<string, ImpactLevel>
        {
            ["Register absence"] = ImpactLevel.Direct,
            ["Absence over 10 days"] = ImpactLevel.Downstream,
            ["FR-001 absence is reported"] = ImpactLevel.Downstream,
            ["Candidate dependent"] = ImpactLevel.Potential,
        }, "rejected and alias relations never impact, and the cycle back to the root is not revisited");
        view.Entities.Single(e => e.Name == "Absence over 10 days").Path.Select(p => p.Relation).Should().Equal(["DependsOn", "Contains"], "every entity carries its path");

        view.Tests.Select(t => (t.Name, t.Retest)).Should().Equal([("AbsenceTests.Registers", RetestLevel.MustRetest), ("AbsenceLongTests.Over10Days", RetestLevel.ShouldRetest)],
            "an established VERIFIES = must retest, a candidate VERIFIES = should retest, and a test that only USES the contract is not impacted");
        view.Tests[0].Why.Should().Contain("Fravaersmelding").And.Contain("verified by AbsenceTests.Registers");
        view.MissingTests.Select(m => m.Name).Should().Equal(["FR-001 absence is reported"], "only the impacted requirement nothing verifies is a test gap");
        view.Unknown.Should().Be(1);
        view.Limitations.Should().ContainSingle(l => l.Contains("unresolved"));
        view.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task The_walk_is_depth_bounded_and_an_unknown_entity_is_not_found()
    {
        var service = await Graph();
        var shallow = (await service.ImpactAsync("proj", "Contract|fravaer", default, maxDepth: 1))!;
        shallow.Entities.Select(e => e.Name).Should().BeEquivalentTo(["Register absence", "Candidate dependent"]);

        (await service.ImpactAsync("proj", "Contract|missing", default)).Should().BeNull();
        (await service.ImpactAsync("proj", "NotAType|fravaer", default)).Should().BeNull();
        (await service.ImpactAsync("other", "Contract|fravaer", default)).Should().BeNull("impact never crosses canonical scopes");
    }

    [Fact]
    public async Task An_unverified_testable_root_is_its_own_test_gap()
    {
        var view = (await (await Graph()).ImpactAsync("proj", "Requirement|fr1", default))!;
        view.Entities.Should().BeEmpty("nothing depends on the requirement");
        view.MissingTests.Select(m => m.Name).Should().Equal(["FR-001 absence is reported"]);
    }
}
