using System.Text.Json;
using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Configuration provenance and resource kind.
///
/// The central guarantee is that provenance is metadata about how configuration arrived, not part
/// of what the integration *is*. Promoting a suggestion to a manual entry, or having discovery
/// confirm a manual one, must leave historical continuity untouched.
/// </summary>
public class IntegrationConfigurationSourceTests
{
    private static IntegrationConfigDto Rest(
        IntegrationConfigurationSource source = IntegrationConfigurationSource.Manual) =>
        new()
        {
            Id = "i1", Name = "Person GraphQL", Type = IntegrationType.GraphQL,
            Endpoint = "https://m2lbqa.example.test/api/person/graphql",
            Resource = "person",
            ConfigurationSource = source
        };

    // ── Provenance must not affect identity ──────────────────────────────────

    [Fact]
    public void ProvenanceChange_DoesNotChangeBaselineKey()
    {
        var suggested = Rest(IntegrationConfigurationSource.CodeSuggested);
        var manual = Rest(IntegrationConfigurationSource.Manual);
        var discovered = Rest(IntegrationConfigurationSource.EndpointDiscovery);
        var unknown = Rest(IntegrationConfigurationSource.Unknown);

        var key = IntegrationBaselineIdentity.Compute("QA", suggested);

        Assert.Equal(key, IntegrationBaselineIdentity.Compute("QA", manual));
        Assert.Equal(key, IntegrationBaselineIdentity.Compute("QA", discovered));
        Assert.Equal(key, IntegrationBaselineIdentity.Compute("QA", unknown));
    }

    [Fact]
    public void ResourceKindChange_DoesNotChangeBaselineKey()
    {
        // ResourceKind refines what Resource means; it does not change which entity is addressed.
        var a = Rest();
        a.ResourceKind = IntegrationResourceKind.Unknown;

        var b = Rest();
        b.ResourceKind = IntegrationResourceKind.GraphQlEndpoint;

        Assert.Equal(
            IntegrationBaselineIdentity.Compute("QA", a),
            IntegrationBaselineIdentity.Compute("QA", b));
    }

    [Fact]
    public void StructuralEndpointChange_DoesChangeBaselineKey()
    {
        var original = Rest();
        var moved = Rest();
        moved.Endpoint = "https://m2lbqa.example.test/api/tjeneste/graphql";

        Assert.NotEqual(
            IntegrationBaselineIdentity.Compute("QA", original),
            IntegrationBaselineIdentity.Compute("QA", moved));
    }

    [Fact]
    public void ProvenanceIsSeparateFromRelationshipSource()
    {
        // A manually entered integration can still have its relationship derived from messaging
        // metadata. Collapsing the two would lose one of the facts.
        var integration = new IntegrationConfigDto
        {
            Id = "m1", Name = "BiRK Person CDC", Type = IntegrationType.EventHub,
            ConfigurationSource = IntegrationConfigurationSource.Manual,
            ProducerConsumerSource = RelationshipSource.MessagingMetadata
        };

        Assert.Equal(IntegrationConfigurationSource.Manual, integration.ConfigurationSource);
        Assert.Equal(RelationshipSource.MessagingMetadata, integration.ProducerConsumerSource);
    }

    // ── Backward compatibility ───────────────────────────────────────────────

    [Fact]
    public void LegacyConfigWithoutProvenance_ReadsAsUnknown()
    {
        const string legacy = """
            {"id":"i1","name":"Orders","type":0,"endpoint":"https://api.example.test/orders",
             "resource":"orders","authType":0,"enabled":true}
            """;

        var integration = JsonSerializer.Deserialize<IntegrationConfigDto>(legacy);

        Assert.NotNull(integration);

        // Absent provenance must not be read as a claim that someone entered this manually,
        // nor that discovery found it.
        Assert.Equal(IntegrationConfigurationSource.Unknown, integration!.ConfigurationSource);
        Assert.Equal(IntegrationResourceKind.Unknown, integration.ResourceKind);
    }

    [Fact]
    public void LegacyConfig_KeepsWorkingAndKeepsItsIdentity()
    {
        const string legacy = """
            {"id":"i1","name":"Orders","type":0,"endpoint":"https://api.example.test/orders",
             "resource":"orders","authType":0,"enabled":true}
            """;

        var legacyIntegration = JsonSerializer.Deserialize<IntegrationConfigDto>(legacy)!;

        var sameStructureToday = new IntegrationConfigDto
        {
            Id = "different-guid", Name = "Renamed Orders", Type = IntegrationType.REST,
            Endpoint = "https://api.example.test/orders", Resource = "orders",
            ConfigurationSource = IntegrationConfigurationSource.EndpointDiscovery,
            ResourceKind = IntegrationResourceKind.RestEndpoint
        };

        // Adding provenance to an existing integration must not detach it from its history.
        Assert.Equal(
            IntegrationBaselineIdentity.Compute("QA", legacyIntegration),
            IntegrationBaselineIdentity.Compute("QA", sameStructureToday));
    }

    [Fact]
    public void ProvenanceAndResourceKind_RoundTrip()
    {
        var original = Rest(IntegrationConfigurationSource.CodeSuggested);
        original.ResourceKind = IntegrationResourceKind.ServiceBusSubscription;

        var round = JsonSerializer.Deserialize<IntegrationConfigDto>(
            JsonSerializer.Serialize(original));

        Assert.Equal(IntegrationConfigurationSource.CodeSuggested, round!.ConfigurationSource);
        Assert.Equal(IntegrationResourceKind.ServiceBusSubscription, round.ResourceKind);
    }

    [Fact]
    public void UnknownIsTheZeroValue()
    {
        // Both default to Unknown, so a missing field can never deserialize into a positive claim.
        Assert.Equal(0, (int)IntegrationConfigurationSource.Unknown);
        Assert.Equal(0, (int)IntegrationResourceKind.Unknown);
    }
}
