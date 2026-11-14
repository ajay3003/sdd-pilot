using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;
using BirkNext.Scopes;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Shared scopes in Delivery and Impact: a Delivery scope item maps to the canonical key the interpretation gives the same document item (type,
/// module, feature, id — never a title), and an impact trace names the scopes that hold an impacted entity without changing any revision.
/// </summary>
public sealed class ScopeConsumerAdapterTests : BunitContext
{
    private static ScopeItem Item(ScopeArtifactType type, string id, string module = "SkoleService", string feature = "001-skoleservice") =>
        new($"k:{id}", type, id, $"{id} title", default, "", module, feature, "Feature", null, null, default, default, "", default, null, default, default, false, null, null);

    [Fact]
    public void Delivery_items_map_to_canonical_keys_by_identity_and_items_without_a_counterpart_map_to_nothing()
    {
        FunctionalQualityScopePanel.CanonicalKeyOf(Item(ScopeArtifactType.Requirement, "FR-001"))
            .Should().Be(CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, CorrelationRules.Scoped("SkoleService", "001-skoleservice", "FR-001")));
        FunctionalQualityScopePanel.CanonicalKeyOf(Item(ScopeArtifactType.UserStory, "US1"))!.Should().StartWith("UserStory|");
        FunctionalQualityScopePanel.CanonicalKeyOf(Item(ScopeArtifactType.Scenario, "S1"))!.Should().StartWith("Scenario|");
        FunctionalQualityScopePanel.CanonicalKeyOf(Item(ScopeArtifactType.AcceptanceCriterion, "AC-1")).Should().BeNull("acceptance criteria follow their parent item");
        FunctionalQualityScopePanel.CanonicalKeyOf(Item(ScopeArtifactType.Requirement, "FR-001", feature: "002-other"))
            .Should().NotBe(FunctionalQualityScopePanel.CanonicalKeyOf(Item(ScopeArtifactType.Requirement, "FR-001")), "the same display id in another feature is another requirement");
    }

    [Fact]
    public void An_impact_trace_lists_the_scopes_naming_impacted_items_and_marks_candidate_only_members()
    {
        var registry = new Mock<IScopeRegistryApiService>();
        var a = Guid.NewGuid();
        registry.Setup(r => r.MembershipAsync("import:m2lb", It.IsAny<ScopeMemberKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, ScopeMemberKind kind, CancellationToken _) => (kind == ScopeMemberKind.System
                ? [new ScopeMembershipSummary(a, "Person flow", ScopeState.Approved, 2, ["Service|m2lb.person.api", "System|person"])]
                : [], (string?)null));
        Services.AddSingleton(registry.Object);
        Services.AddSingleton(WorkspaceSnapshots.Projection(WorkspaceSnapshots.Loaded("Imported", "import:m2lb", "M2LB")).Object);
        var impact = new CanonicalImpactView
        {
            Scope = "dev", RootKey = "Service|m2lb.person.api", RootName = "M2LB.Person.Api", RootType = "Service",
            Entities = [new("System|person", "System", "Person", ImpactLevel.Potential, 1, [])],
        };

        var cut = Render<CanonicalImpactSummary>(p => p.Add(c => c.Impact, impact));
        cut.WaitForAssertion(() => cut.Find("[data-testid=cis-scope]").TextContent.Should().Contain("Person flow").And.Contain("Approved, revision 2"));
        cut.Find("[data-testid=cis-scope]").TextContent.Should().Contain("the changed item").And.Contain("candidate relation only — not confirmed");
        cut.Find("[data-testid=cis-scopes]").TextContent.Should().Contain("approved revisions are not changed");
    }
}
