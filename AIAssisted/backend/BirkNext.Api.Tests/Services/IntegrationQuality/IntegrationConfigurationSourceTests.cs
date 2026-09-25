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
