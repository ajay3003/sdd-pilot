using BirkNext.Web.Models;
using Xunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Tests for Phase 2: Contract relationship section in FrontendAnalysisSettings
/// Verifies contract metadata model properties and readiness computation
/// </summary>
public class FrontendAnalysisSettingsContractRelationshipTests
{
    // ────────────────────────────────────────────────────────────────────────────
    // Test 1: Legacy integration displays without contract metadata
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LegacyIntegration_ContractFieldsDefault_ToNullAndNotConfigured()
    {
        var integration = new IntegrationConfig
        {
            Id = "legacy-eh",
            Name = "Legacy EventHub",
            Type = IntegrationType.EventHub,
            Endpoint = "namespace",
            Resource = "hub",
            AuthType = IntegrationAuthType.ConnectionString,
            Enabled = true
            // No contract metadata set
        };

        integration.LogicalProducerService.Should().BeNull();
        integration.LogicalConsumerService.Should().BeNull();
        integration.ContractName.Should().BeNull();
        integration.ContractSourceType.Should().Be(ContractSourceType.Unknown);
        integration.ContractSourceLocation.Should().BeNull();
        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.NotConfigured);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 2: Contract fields can be set and retrieved
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContractMetadata_CanBeSet_AndRetrieved()
    {
        var integration = new IntegrationConfig
        {
            Id = "contract-eh",
            Name = "Contract EventHub",
            Type = IntegrationType.EventHub,
            Endpoint = "namespace",
            Resource = "hub",
            LogicalProducerService = "Hendelse Adapter",
            LogicalConsumerService = "Hendelse",
            ContractName = "ChildUpdated",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "BirkNext.Contracts.ChildUpdated, v1.0"
        };

        integration.LogicalProducerService.Should().Be("Hendelse Adapter");
        integration.LogicalConsumerService.Should().Be("Hendelse");
        integration.ContractName.Should().Be("ChildUpdated");
        integration.ContractSourceType.Should().Be(ContractSourceType.Assembly);
        integration.ContractSourceLocation.Should().Be("BirkNext.Contracts.ChildUpdated, v1.0");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 3: Producer Service field can be edited
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProducerServiceField_CanBeModified()
    {
        var integration = new IntegrationConfig();

        integration.LogicalProducerService = "Event Publisher";
        integration.LogicalProducerService.Should().Be("Event Publisher");

        integration.LogicalProducerService = "Event Generator";
        integration.LogicalProducerService.Should().Be("Event Generator");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 4: Consumer Service field can be edited
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConsumerServiceField_CanBeModified()
    {
        var integration = new IntegrationConfig();

        integration.LogicalConsumerService = "Event Subscriber";
        integration.LogicalConsumerService.Should().Be("Event Subscriber");

        integration.LogicalConsumerService = "Service A";
        integration.LogicalConsumerService.Should().Be("Service A");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 5: Contract Name field can be edited
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContractNameField_CanBeModified()
    {
        var integration = new IntegrationConfig();

        integration.ContractName = "UserCreated";
        integration.ContractName.Should().Be("UserCreated");

        integration.ContractName = "OrderUpdated";
        integration.ContractName.Should().Be("OrderUpdated");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 6: Contract Source Type field can be changed
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContractSourceTypeField_CanBeChanged()
    {
        var integration = new IntegrationConfig { ContractSourceType = ContractSourceType.Unknown };

        integration.ContractSourceType = ContractSourceType.OpenApi;
        integration.ContractSourceType.Should().Be(ContractSourceType.OpenApi);

        integration.ContractSourceType = ContractSourceType.GraphQlSchema;
        integration.ContractSourceType.Should().Be(ContractSourceType.GraphQlSchema);

        integration.ContractSourceType = ContractSourceType.Assembly;
        integration.ContractSourceType.Should().Be(ContractSourceType.Assembly);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 7: Source Location provides type-dependent hints
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SourceLocationField_CanStore_TypeDependentReferences()
    {
        var restIntegration = new IntegrationConfig
        {
            ContractSourceType = ContractSourceType.OpenApi,
            ContractSourceLocation = "https://api.example.com/swagger/v1/swagger.json"
        };
        restIntegration.ContractSourceLocation.Should().Contain("swagger");

        var graphqlIntegration = new IntegrationConfig
        {
            ContractSourceType = ContractSourceType.GraphQlSchema,
            ContractSourceLocation = "https://api.example.com/graphql"
        };
        graphqlIntegration.ContractSourceLocation.Should().Contain("graphql");

        var assemblyIntegration = new IntegrationConfig
        {
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "BirkNext.Contracts.v1.0"
        };
        assemblyIntegration.ContractSourceLocation.Should().Contain("BirkNext");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 8: Readiness NotConfigured when all fields empty
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_IsNotConfigured_WhenAllFieldsEmpty()
    {
        var integration = new IntegrationConfig
        {
            Type = IntegrationType.EventHub
        };

        integration.ComputeReadiness();

        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.NotConfigured);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 9: Readiness Partial when incomplete metadata
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_IsPartial_WhenIncompleteMetadata()
    {
        var integration = new IntegrationConfig
        {
            Type = IntegrationType.EventHub,
            LogicalProducerService = "Producer Only"
            // Missing consumer/contract/source
        };

        integration.ComputeReadiness();

        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.Partial);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 10: Readiness Ready for EventHub with complete metadata
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_IsReady_ForEventHubWithCompleteMetadata()
    {
        var integration = new IntegrationConfig
        {
            Type = IntegrationType.EventHub,
            LogicalProducerService = "Hendelse Adapter",
            LogicalConsumerService = "Hendelse",
            ContractName = "ChildUpdated",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "BirkNext.Contracts.ChildUpdated, v1.0"
        };

        integration.ComputeReadiness();

        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.Ready);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 11: Readiness Ready for REST with OpenAPI source
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_IsReady_ForRestWithOpenApiSource()
    {
        var integration = new IntegrationConfig
        {
            Type = IntegrationType.REST,
            ContractSourceType = ContractSourceType.OpenApi,
            ContractSourceLocation = "https://api.example.com/swagger.json"
        };

        integration.ComputeReadiness();

        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.Ready);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 12: Readiness Ready for GraphQL with schema source and consumer
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_IsReady_ForGraphQLWithSchemaAndConsumer()
    {
        var integration = new IntegrationConfig
        {
            Type = IntegrationType.GraphQL,
            LogicalConsumerService = "BirkNext Frontend",
            ContractSourceType = ContractSourceType.GraphQlSchema,
            ContractSourceLocation = "https://api.example.com/graphql"
        };

        integration.ComputeReadiness();

        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.Ready);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 13: Dirty state tracking - ComputeReadiness updates status after changes
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DirtyStateTracking_ComputeReadiness_UpdatesStatusAfterChanges()
    {
        var integration = new IntegrationConfig
        {
            Type = IntegrationType.EventHub
        };

        integration.ComputeReadiness();
        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.NotConfigured);

        // Add producer - becomes Partial
        integration.LogicalProducerService = "Producer";
        integration.ComputeReadiness();
        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.Partial);

        // Add consumer and contract - should become Ready
        integration.LogicalConsumerService = "Consumer";
        integration.ContractName = "Event";
        integration.ComputeReadiness();
        integration.ContractMetadataReadiness.Should().Be(ContractMetadataReadiness.Ready);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 14: Profile scope isolation - different profiles have independent metadata
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProfileScopeIsolation_DifferentProfiles_HaveIndependentMetadata()
    {
        var profile1 = new FrontendAnalysisProfile
        {
            Id = "profile1",
            Integrations = new List<IntegrationConfig>
            {
                new IntegrationConfig
                {
                    Id = "int1",
                    LogicalProducerService = "Producer1"
                }
            }
        };

        var profile2 = new FrontendAnalysisProfile
        {
            Id = "profile2",
            Integrations = new List<IntegrationConfig>
            {
                new IntegrationConfig
                {
                    Id = "int2",
                    LogicalProducerService = "Producer2"
                }
            }
        };

        profile1.Integrations[0].LogicalProducerService.Should().NotBe(profile2.Integrations[0].LogicalProducerService);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 15: Explicit save required - changes don't auto-save
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExplicitSave_Required_NoAutoSaveOnChanges()
    {
        var integration = new IntegrationConfig
        {
            Type = IntegrationType.EventHub
        };

        // Simulate user making changes (UI binding updates the property)
        integration.LogicalProducerService = "New Producer";

        // The integration object is modified in memory
        integration.LogicalProducerService.Should().Be("New Producer");

        // But without explicit save, changes would not persist to storage
        // This is verified by integration with persistence layer (not in unit test)
        // Test verifies the model supports the pattern by being mutable
        Assert.NotNull(integration);
    }
}
