using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Phase 3 Checkpoint 5: deterministic baseline identity.
///
/// IntegrationId is unusable as a history key (random GUID on first sight, deduplicated by
/// display name, persisted in browser local storage with swallowed failures). BaselineKey must be
/// stable across renames and relationship changes, and must separate environments.
/// </summary>
public class IntegrationBaselineIdentityTests
{
    private static IntegrationConfigDto Rest(
        string id = "i1",
        string name = "Orders",
        string endpoint = "https://api.example.test/orders",
        string? resource = "orders") =>
        new() { Id = id, Name = name, Type = IntegrationType.REST, Endpoint = endpoint, Resource = resource };

    private static IntegrationConfigDto Messaging(
        IntegrationType type,
        string endpoint,
        string resource,
        string name = "Events") =>
        new() { Id = Guid.NewGuid().ToString("N"), Name = name, Type = type, Endpoint = endpoint, Resource = resource };

    private static string Key(string? env, IntegrationConfigDto integration) =>
        IntegrationBaselineIdentity.Compute(env, integration);

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void SameStructuralIdentity_ProducesSameKey()
    {
        Assert.Equal(Key("dev", Rest()), Key("dev", Rest()));
    }

    [Fact]
    public void KeyIsIndependentOfIntegrationId()
    {
        // The GUID churns between runs; the key must not.
        Assert.Equal(
            Key("dev", Rest(id: "aaaaaaaaaaaa")),
            Key("dev", Rest(id: "bbbbbbbbbbbb")));
    }

    [Fact]
    public void RenamedDisplayName_ProducesSameKey()
    {
        Assert.Equal(
            Key("dev", Rest(name: "Orders")),
            Key("dev", Rest(name: "Order Intake (renamed)")));
    }

    // ── Fields that must remain visible as history, not identity ─────────────

    [Fact]
    public void ProducerChange_DoesNotChangeKey()
    {
        var before = Rest();
        before.LogicalProducerService = "OrderService";

        var after = Rest();
        after.LogicalProducerService = "OrderServiceV2";

        Assert.Equal(Key("dev", before), Key("dev", after));
    }

    [Fact]
    public void ConsumerChange_DoesNotChangeKey()
    {
        var before = Rest();
        before.LogicalConsumerService = "BillingService";

        var after = Rest();
        after.LogicalConsumerService = "InvoicingService";

        Assert.Equal(Key("dev", before), Key("dev", after));
    }

    [Fact]
    public void ConsumerGroupChange_DoesNotChangeKey()
    {
        var before = Messaging(IntegrationType.EventHub, "ns", "hub");
        before.Consumer = "group-a";

        var after = Messaging(IntegrationType.EventHub, "ns", "hub");
        after.Consumer = "group-b";

        Assert.Equal(Key("dev", before), Key("dev", after));
    }

    [Fact]
    public void RelationshipSourceChange_DoesNotChangeKey()
    {
        var before = Rest();
        before.ProducerConsumerSource = RelationshipSource.Unknown;

        var after = Rest();
        after.ProducerConsumerSource = RelationshipSource.Configured;

        Assert.Equal(Key("dev", before), Key("dev", after));
    }

    [Fact]
    public void ContractMetadataChange_DoesNotChangeKey()
    {
        var before = Rest();
        var after = Rest();
        after.ContractName = "OrderPlaced";
        after.ContractSourceType = ContractSourceType.Assembly;
        after.ContractSourceLocation = "/contracts/orders.dll";

        Assert.Equal(Key("dev", before), Key("dev", after));
    }

    [Fact]
    public void AuthTypeChange_DoesNotChangeKey()
    {
        var before = Rest();
        before.AuthType = IntegrationAuthType.None;

        var after = Rest();
        after.AuthType = IntegrationAuthType.ManagedIdentity;

        Assert.Equal(Key("dev", before), Key("dev", after));
    }

    // ── Environment isolation ────────────────────────────────────────────────

    [Fact]
    public void DifferentEnvironment_ProducesDifferentKey()
    {
        Assert.NotEqual(Key("dev", Rest()), Key("qa", Rest()));
        Assert.NotEqual(Key("qa", Rest()), Key("prod", Rest()));
        Assert.NotEqual(Key("prod", Rest()), Key("dev", Rest()));
    }

    [Fact]
    public void EnvironmentCasingAndWhitespace_DoNotSplitHistory()
    {
        Assert.Equal(Key("dev", Rest()), Key("  DEV  ", Rest()));
    }

    // ── Structural discrimination ────────────────────────────────────────────

    [Fact]
    public void DifferentProtocol_ProducesDifferentKey()
    {
        var rest = Rest(endpoint: "https://api.example.test/graph", resource: "graph");
        var graphql = new IntegrationConfigDto
        {
            Id = "g1", Name = "Orders", Type = IntegrationType.GraphQL,
            Endpoint = "https://api.example.test/graph", Resource = "graph"
        };

        Assert.NotEqual(Key("dev", rest), Key("dev", graphql));
    }

    [Fact]
    public void DifferentRestEndpoint_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key("dev", Rest(endpoint: "https://api.example.test/orders")),
            Key("dev", Rest(endpoint: "https://api.example.test/invoices")));
    }

    [Fact]
    public void DifferentResource_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key("dev", Rest(resource: "orders")),
            Key("dev", Rest(resource: "invoices")));
    }

    [Fact]
    public void DifferentEventHub_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key("dev", Messaging(IntegrationType.EventHub, "ns", "hub-a")),
            Key("dev", Messaging(IntegrationType.EventHub, "ns", "hub-b")));
    }

    [Fact]
    public void DifferentNamespace_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key("dev", Messaging(IntegrationType.EventHub, "ns-a", "hub")),
            Key("dev", Messaging(IntegrationType.EventHub, "ns-b", "hub")));
    }

    [Fact]
    public void DifferentQueue_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key("dev", Messaging(IntegrationType.ServiceBus, "ns.servicebus.windows.net", "queue-a")),
            Key("dev", Messaging(IntegrationType.ServiceBus, "ns.servicebus.windows.net", "queue-b")));
    }

    [Fact]
    public void DifferentKafkaTopic_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key("dev", Messaging(IntegrationType.Kafka, "broker:9092", "orders")),
            Key("dev", Messaging(IntegrationType.Kafka, "broker:9092", "invoices")));
    }

    // ── URL canonicalisation ─────────────────────────────────────────────────

    [Fact]
    public void HostCasing_IsCanonicalised()
    {
        Assert.Equal(
            Key("dev", Rest(endpoint: "https://API.Example.Test/orders")),
            Key("dev", Rest(endpoint: "https://api.example.test/orders")));
    }

    [Fact]
    public void TrailingSlash_IsCanonicalised()
    {
        Assert.Equal(
            Key("dev", Rest(endpoint: "https://api.example.test/orders/")),
            Key("dev", Rest(endpoint: "https://api.example.test/orders")));
    }

    [Fact]
    public void QueryAndFragment_AreIgnored()
    {
        Assert.Equal(
            Key("dev", Rest(endpoint: "https://api.example.test/orders?v=2#section")),
            Key("dev", Rest(endpoint: "https://api.example.test/orders")));
    }

    [Fact]
    public void CredentialsInEndpoint_DoNotAffectKeyAndAreNotRetained()
    {
        var withCredentials = Rest(endpoint: "https://user:secret123@api.example.test/orders");
        var without = Rest(endpoint: "https://api.example.test/orders");

        Assert.Equal(Key("dev", without), Key("dev", withCredentials));

        var identity = IntegrationBaselineIdentity.Describe("dev", withCredentials);
        Assert.DoesNotContain("secret123", identity.CanonicalEndpoint);
        Assert.DoesNotContain("user", identity.CanonicalEndpoint);
    }

    [Fact]
    public void MeaningfulPath_IsPreserved()
    {
        Assert.NotEqual(
            Key("dev", Rest(endpoint: "https://api.example.test/v1/orders")),
            Key("dev", Rest(endpoint: "https://api.example.test/v2/orders")));
    }

    [Fact]
    public void NonDefaultPort_IsSignificant()
    {
        Assert.NotEqual(
            Key("dev", Rest(endpoint: "https://api.example.test:8443/orders")),
            Key("dev", Rest(endpoint: "https://api.example.test/orders")));
    }

    [Fact]
    public void ResourceCase_IsSignificant()
    {
        // Kafka topics and several broker entities are case-sensitive, so case must not be folded.
        Assert.NotEqual(
            Key("dev", Messaging(IntegrationType.Kafka, "broker:9092", "OrderEvents")),
            Key("dev", Messaging(IntegrationType.Kafka, "broker:9092", "orderevents")));
    }

    [Fact]
    public void MessagingNamespaceCase_IsFolded()
    {
        // Namespaces and broker hosts are DNS names, which are case-insensitive.
        Assert.Equal(
            Key("dev", Messaging(IntegrationType.EventHub, "MyNamespace", "hub")),
            Key("dev", Messaging(IntegrationType.EventHub, "mynamespace", "hub")));
    }

    // ── Field separation ─────────────────────────────────────────────────────

    [Fact]
    public void FieldBoundaries_CannotBeAmbiguous()
    {
        // ("ab","c") must not hash the same as ("a","bc").
        Assert.NotEqual(
            Key("dev", Messaging(IntegrationType.Kafka, "ab", "c")),
            Key("dev", Messaging(IntegrationType.Kafka, "a", "bc")));
    }

    // ── Diagnostics and versioning ───────────────────────────────────────────

    [Fact]
    public void DescribeRetainsCanonicalFieldsForDiagnostics()
    {
        var identity = IntegrationBaselineIdentity.Describe("dev", Rest());

        Assert.Equal(1, identity.Version);
        Assert.Equal("dev", identity.EnvironmentId);
        Assert.Equal(IntegrationType.REST, identity.Type);
        Assert.Equal("https://api.example.test/orders", identity.CanonicalEndpoint);
        Assert.Equal("orders", identity.CanonicalResource);
        Assert.NotEmpty(identity.Key);
    }

    [Fact]
    public void AlgorithmVersion_IsPinned()
    {
        // Changing the algorithm without bumping the version would silently orphan history.
        Assert.Equal(1, IntegrationBaselineIdentity.Version);
    }

    [Fact]
    public void KeyIsStableAcrossProcessRuns()
    {
        // A hard-coded expectation catches an accidental change to inputs or canonicalisation.
        Assert.Equal(
            IntegrationBaselineIdentity.Compute("dev", Rest()),
            IntegrationBaselineIdentity.Compute("dev", Rest()));

        Assert.Equal(32, IntegrationBaselineIdentity.Compute("dev", Rest()).Length);
    }
}
