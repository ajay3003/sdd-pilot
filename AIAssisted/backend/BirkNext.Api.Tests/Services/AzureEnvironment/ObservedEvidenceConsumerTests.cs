using System.Text.Json;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.AzureEnvironment;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.AzureEnvironment.AzureEnvironmentFixtures;

namespace BirkNext.Api.Tests.Services.AzureEnvironment;

/// <summary>
/// Consumers of the shared observed evidence. Integration Quality Review looks configured values up in the stored Azure snapshot and records
/// observed lines beside configured and declared — never changing domain state, outcomes or the catalog. Lookups normalize hosts the same way
/// the declared comparison does, and say when absence cannot be established.
/// </summary>
public sealed class ObservedEvidenceConsumerTests
{
    private static IntegrationCatalog Catalog() => new()
    {
        EnvironmentId = "orders-dev",
        Platforms =
        [
            new IntegrationPlatform
            {
                Id = "eh", Name = "Orders Event Hubs", Kind = IntegrationKind.EventHub, NamespaceFqdn = "evhns-orders-dev.servicebus.windows.net",
                RuntimeEvidence = new IntegrationRuntimeEvidenceSettings { ExpectedConsumerGroup = "fulfillment-dev", CheckpointBlobEndpoint = "https://stordersdevckpt.blob.core.windows.net/",
                    CheckpointContainerName = "checkpoints", ApplicationInsightsResourceName = "appi-legacy" },
            },
            new IntegrationPlatform
            {
                Id = "sb", Name = "Orders Service Bus", Kind = IntegrationKind.ServiceBus, Namespace = "sbns-orders-dev",
                ServiceBusTopology = new() { Source = "Seeded audit", Entities = [new() { EntityType = ServiceBusEntityType.Topic, Name = "order-placed" }, new() { EntityType = ServiceBusEntityType.Queue, Name = "dead-orders" }] },
            },
        ],
        Integrations = [new IntegrationDefinition { Id = "orders", PlatformId = "eh", Kind = IntegrationKind.EventHub, EndpointOrTopic = "order-events" }],
    };

    private static IntegrationReviewResult Result(IntegrationCatalog catalog) => new()
    {
        ConfigurationSnapshot = catalog,
        Domains =
        [
            new IntegrationDomainResult { Domain = IntegrationReviewDomain.Configuration, StateLabel = "Assessed" },
            new IntegrationDomainResult { Domain = IntegrationReviewDomain.Connectivity, StateLabel = "Not assessed" },
            new IntegrationDomainResult { Domain = IntegrationReviewDomain.Contract, StateLabel = "Not assessed" },
        ],
    };

    [Fact]
    public async Task Iqr_records_observed_state_per_configured_value_without_changing_states_or_the_catalog()
    {
        var snapshot = await Collector(Full()).CollectAsync(Request(), default);
        var catalog = Catalog();
        var before = JsonSerializer.Serialize(catalog);
        var input = Result(catalog);
        var result = IqrObservedAzureReview.Augment(input, snapshot);

        JsonSerializer.Serialize(result.ConfigurationSnapshot).Should().Be(before);
        var rows = result.ObservedAzureComparisons.ToDictionary(c => c.Field, c => c.Lookup);
        rows["Event Hubs namespace"].Should().Match<ObservedResourceLookup>(l => l.State == ObservedLookupState.Observed && l.Resource!.ResourceKind == InfrastructureResourceKind.EventHubNamespace);
        rows["Event Hub"].State.Should().Be(ObservedLookupState.Observed);
        rows["Consumer group"].State.Should().Be(ObservedLookupState.Observed);
        rows["Checkpoint storage account"].State.Should().Be(ObservedLookupState.Observed);
        rows["Checkpoint container"].State.Should().Be(ObservedLookupState.Observed);
        rows["Application Insights"].State.Should().Be(ObservedLookupState.NotObserved);
        rows["Service Bus topic order-placed"].State.Should().Be(ObservedLookupState.Observed);
        rows["Service Bus queue dead-orders"].State.Should().Be(ObservedLookupState.NotObserved);
        result.ObservedAzureComparisons.Should().OnlyContain(c => c.Lookup.SnapshotId == snapshot.Id);

        result.Domains.Select(d => d.StateLabel).Should().Equal(input.Domains.Select(d => d.StateLabel), "observed evidence never changes a domain's state");
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Configuration).Observed.Should().Contain(o => o.StartsWith("Configured vs. observed in Azure (snapshot 2026-10-02 09:00 UTC)"));
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Connectivity).Missing.Should().Contain(m => m.Contains("does not prove the integration can connect"));
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Contract).Observed.Should().BeEmpty();
        result.WhatWasTested.Should().Contain(w => w.Contains("not re-queried"));
    }

    [Fact]
    public async Task Lookups_say_when_absence_cannot_be_established()
    {
        var fake = Full();
        fake.Overrides.Add((u => u.Contains("/topics", StringComparison.OrdinalIgnoreCase), FakeAzureManagementClient.Status(403)));
        var snapshot = await Collector(fake).CollectAsync(Request(), default);
        AzureEnvironmentEvidenceProvider.Lookup(snapshot, InfrastructureResourceKind.ServiceBusTopic, "order-placed", "sbns-orders-dev").State.Should().Be(ObservedLookupState.UnableToVerify);
        AzureEnvironmentEvidenceProvider.Lookup(snapshot, InfrastructureResourceKind.StorageAccount, "https://stordersdevckpt.blob.core.windows.net/", null).State.Should().Be(ObservedLookupState.Observed);
        AzureEnvironmentEvidenceProvider.Lookup(snapshot, InfrastructureResourceKind.StorageAccount, null, null).State.Should().Be(ObservedLookupState.UnableToVerify);
        AzureEnvironmentEvidenceProvider.Lookup(null, InfrastructureResourceKind.StorageAccount, "x", null).State.Should().Be(ObservedLookupState.NoSnapshot);
    }

    [Fact]
    public void A_run_without_an_Azure_snapshot_is_unchanged()
    {
        var input = Result(Catalog());
        IqrInfrastructureComparison.CompareObserved(input.ConfigurationSnapshot, null).Should().BeEmpty();
        IqrObservedAzureReview.Augment(input, new AzureEnvironmentSnapshot { Id = Guid.NewGuid() }).ObservedAzureComparisons.Should().OnlyContain(c => c.Lookup.State != ObservedLookupState.Observed);
    }
}
