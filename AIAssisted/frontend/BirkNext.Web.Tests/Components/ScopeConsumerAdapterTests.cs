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

}
