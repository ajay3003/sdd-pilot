using System.Text.Json;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.LocalHttpsProxy;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// REST/GraphQL integration proposals from Endpoint Discovery, and the merge that keeps a
/// rediscovered integration from becoming a duplicate of a configured one.
/// </summary>
public class IntegrationDiscoveryProposalTests
{
    private readonly IntegrationDiscoveryProposalService _proposals = new();
    private readonly IntegrationConfigurationMerger _merger = new();

    private static ObservedNetworkEndpoint Observed(
        ObservedTrafficCategory category = ObservedTrafficCategory.Rest,
        string host = "m2lbqa.example.test",
        string path = "/api/orders",
        int port = 443,
        string scheme = "https") =>
        new()
        {
            Category = category, Scheme = scheme, Host = host, Port = port, Path = path,
            Method = "GET", Source = EndpointDiscoverySource.AuthenticatedProxyTraffic
        };

    // ── What becomes a proposal ──────────────────────────────────────────────

    [Fact]
    public void RestEndpoint_IsProposed()
    {
        var proposal = Assert.Single(_proposals.Propose([Observed()]));

        Assert.Equal(IntegrationType.REST, proposal.Type);
        Assert.Equal(IntegrationResourceKind.RestEndpoint, proposal.ResourceKind);
        Assert.Equal("https://m2lbqa.example.test/api/orders", proposal.Endpoint);
        Assert.Equal(IntegrationConfigurationSource.EndpointDiscovery, proposal.ConfigurationSource);
    }

    [Fact]
    public void GraphQlEndpoint_IsProposed()
    {
        var proposal = Assert.Single(_proposals.Propose(
            [Observed(ObservedTrafficCategory.GraphQl, path: "/api/person/graphql")]));

        Assert.Equal(IntegrationType.GraphQL, proposal.Type);
        Assert.Equal(IntegrationResourceKind.GraphQlEndpoint, proposal.ResourceKind);
    }

    [Theory]
    [InlineData(ObservedTrafficCategory.StaticAsset)]
    [InlineData(ObservedTrafficCategory.Telemetry)]
    [InlineData(ObservedTrafficCategory.Authentication)]
    [InlineData(ObservedTrafficCategory.WebSocket)]
    [InlineData(ObservedTrafficCategory.OtherHttp)]
    [InlineData(ObservedTrafficCategory.Unknown)]
    public void InfrastructureTraffic_IsNotAnIntegration(ObservedTrafficCategory category) =>
        Assert.Empty(_proposals.Propose([Observed(category)]));

    [Fact]
    public void TrafficOutsideApplicationOrigins_IsNotProposed()
    {
        var observations = new[]
        {
            Observed(host: "m2lbqa.example.test"),
            Observed(host: "security-infra.microsoft.example", path: "/api/telemetry")
        };

        var proposals = _proposals.Propose(observations, ["https://m2lbqa.example.test"]);

        Assert.Single(proposals);
        Assert.Contains("m2lbqa.example.test", Assert.Single(proposals).Endpoint!);
    }

    [Fact]
    public void WithoutKnownApplicationOrigins_AllApiTrafficIsProposed() =>
        Assert.Equal(2, _proposals.Propose(
            [Observed(host: "a.example.test"), Observed(host: "b.example.test")]).Count);

    // ── What proposals must not claim ────────────────────────────────────────

    [Fact]
    public void ProducerAndConsumer_RemainUnknown()
    {
        // The proxy is transparent: it observes that a request happened, not which services sit
        // on either side. Nothing here can establish a relationship.
        var proposal = Assert.Single(_proposals.Propose(
            [Observed(ObservedTrafficCategory.GraphQl, path: "/api/person/graphql")]));

        Assert.Null(proposal.LogicalProducerService);
        Assert.Null(proposal.LogicalConsumerService);
    }

    [Fact]
    public void ConsumerIsNotInferredFromPathName()
    {
        // "/api/person/graphql" must not yield Consumer = "Person".
        var proposals = _proposals.Propose(
        [
            Observed(ObservedTrafficCategory.GraphQl, path: "/api/person/graphql"),
            Observed(ObservedTrafficCategory.GraphQl, path: "/api/tjeneste/graphql"),
            Observed(ObservedTrafficCategory.GraphQl, path: "/api/hendelse/graphql")
        ]);

        Assert.All(proposals, p => Assert.Null(p.LogicalConsumerService));
        Assert.DoesNotContain(proposals, p => p.LogicalConsumerService == "Person");
        Assert.DoesNotContain(proposals, p => p.LogicalConsumerService == "Tjeneste");
    }

    [Fact]
    public void ProposalCarriesNoRuntimeEvidence()
    {
        // Configuration discovery and observed traffic are different claims. A proposal says the
        // endpoint exists, not that anything was measured against it.
        var proposal = Assert.Single(_proposals.Propose([Observed()]));
        var analyzer = new IntegrationPerformanceAnalyzer();

        Assert.Equal(PerformanceEvidenceState.Unavailable, analyzer.Analyze([]).EvidenceState);
        Assert.Equal(IntegrationConfigurationSource.EndpointDiscovery, proposal.ConfigurationSource);
    }

    // ── Endpoint safety ──────────────────────────────────────────────────────

    [Fact]
    public void ProposedEndpoint_IsCanonicalAndCarriesNoSecrets()
    {
        var proposal = Assert.Single(_proposals.Propose(
            [Observed(host: "M2LBQA.Example.Test", path: "/api/orders/")]));

        Assert.Equal("https://m2lbqa.example.test/api/orders", proposal.Endpoint);

        var json = JsonSerializer.Serialize(proposal);
        Assert.DoesNotContain("?", json);
        Assert.DoesNotContain("#", json);
        Assert.DoesNotContain("@", json);
    }

    [Fact]
    public void NonDefaultPort_IsRetained() =>
        Assert.Equal("https://m2lbqa.example.test:8443/api/orders",
            Assert.Single(_proposals.Propose([Observed(port: 8443)])).Endpoint);

    [Fact]
    public void RepeatedObservationsOfOneEndpoint_ProposeOneIntegration() =>
        Assert.Single(_proposals.Propose([Observed(), Observed(), Observed()]));

    // ── Merge and de-duplication ─────────────────────────────────────────────

    private static IntegrationConfigDto Manual(
        string name, IntegrationType type, string endpoint) =>
        new()
        {
            Id = "manual-1", Name = name, Type = type, Endpoint = endpoint,
            ConfigurationSource = IntegrationConfigurationSource.Manual, Enabled = true
        };

    [Fact]
    public void ManualAndRediscoveredEndpoint_StayOneIntegration()
    {
        var existing = new[] { Manual("My Person API", IntegrationType.GraphQL,
            "https://m2lbqa.example.test/api/person/graphql") };

        var discovered = _proposals.Propose(
            [Observed(ObservedTrafficCategory.GraphQl, path: "/api/person/graphql")]);

        var merged = _merger.Merge("QA", existing, discovered);

        Assert.Single(merged);
        // The name a person chose is kept; discovery does not rename their integration.
        Assert.Equal("My Person API", merged[0].Name);
    }

    [Fact]
    public void DifferentDisplayNames_DoNotCreateDuplicates()
    {
        var existing = new[] { Manual("Totally Different Name", IntegrationType.REST,
            "https://m2lbqa.example.test/api/orders") };

        Assert.Single(_merger.Merge("QA", existing, _proposals.Propose([Observed()])));
    }

    [Fact]
    public void DifferentPaths_RemainSeparateIntegrations()
    {
        var existing = new[] { Manual("Orders", IntegrationType.REST,
            "https://m2lbqa.example.test/api/orders") };

        var discovered = _proposals.Propose([Observed(path: "/api/invoices")]);

        Assert.Equal(2, _merger.Merge("QA", existing, discovered).Count);
    }

    [Fact]
    public void ManualRelationship_SurvivesRediscovery()
    {
        var manual = Manual("Person", IntegrationType.GraphQL,
            "https://m2lbqa.example.test/api/person/graphql");
        manual.LogicalProducerService = "M2LB Frontend";
        manual.LogicalConsumerService = "Person";

        var merged = _merger.Merge("QA", [manual], _proposals.Propose(
            [Observed(ObservedTrafficCategory.GraphQl, path: "/api/person/graphql")]));

        // Discovery has nothing to say about relationships and must not erase what was entered.
        Assert.Equal("M2LB Frontend", merged[0].LogicalProducerService);
        Assert.Equal("Person", merged[0].LogicalConsumerService);
    }

    [Fact]
    public void DiscoveryFillsFieldsThatWereEmpty()
    {
        var sparse = new IntegrationConfigDto
        {
            Id = "i1", Name = "Orders", Type = IntegrationType.REST,
            Endpoint = "https://m2lbqa.example.test/api/orders",
            ConfigurationSource = IntegrationConfigurationSource.Unknown, Enabled = true
        };

        var merged = _merger.Merge("QA", [sparse], _proposals.Propose([Observed()]));

        Assert.Equal(IntegrationResourceKind.RestEndpoint, merged[0].ResourceKind);
        Assert.Equal(IntegrationConfigurationSource.EndpointDiscovery, merged[0].ConfigurationSource);
    }

    [Fact]
    public void SuggestionDoesNotOverwriteAManualValue()
    {
        var manual = new IntegrationConfigDto
        {
            Id = "i1", Name = "BiRK Person CDC", Type = IntegrationType.EventHub,
            Endpoint = "my-namespace", Resource = "m2lb-cdc-qa.birk.dbo.person",
            LogicalConsumerService = "MyOwnAdapter",
            ConfigurationSource = IntegrationConfigurationSource.Manual, Enabled = true
        };

        var suggested = KnownIntegrationTemplates.ForEnvironment("QA")
            .Single(t => t.Resource == "m2lb-cdc-qa.birk.dbo.person")
            .ToIntegration("suggested-1");
        suggested.Endpoint = "my-namespace";

        var merged = _merger.Merge("QA", [manual], [suggested]);

        Assert.Single(merged);
        Assert.Equal("MyOwnAdapter", merged[0].LogicalConsumerService);
        Assert.Equal(IntegrationConfigurationSource.Manual, merged[0].ConfigurationSource);
    }

    [Fact]
    public void SuggestionStillFillsGapsOnAManualRecord()
    {
        var manual = new IntegrationConfigDto
        {
            Id = "i1", Name = "BiRK Person CDC", Type = IntegrationType.EventHub,
            Endpoint = "my-namespace", Resource = "m2lb-cdc-qa.birk.dbo.person",
            ConfigurationSource = IntegrationConfigurationSource.Manual, Enabled = true
        };

        var suggested = KnownIntegrationTemplates.ForEnvironment("QA")
            .Single(t => t.Resource == "m2lb-cdc-qa.birk.dbo.person")
            .ToIntegration("suggested-1");
        suggested.Endpoint = "my-namespace";

        var merged = _merger.Merge("QA", [manual], [suggested]);

        // The consumer was empty, so filling it adds information without overriding anyone.
        Assert.Equal("PersonBiRKAdapter", merged[0].LogicalConsumerService);
        Assert.Equal(IntegrationConfigurationSource.Manual, merged[0].ConfigurationSource);
    }

    // ── Identity stability across merging ────────────────────────────────────

    [Fact]
    public void MergingDoesNotMoveBaselineKey()
    {
        var manual = Manual("Person", IntegrationType.GraphQL,
            "https://m2lbqa.example.test/api/person/graphql");

        var before = IntegrationBaselineIdentity.Compute("QA", manual);

        var merged = _merger.Merge("QA", [manual], _proposals.Propose(
            [Observed(ObservedTrafficCategory.GraphQl, path: "/api/person/graphql")]));

        Assert.Equal(before, IntegrationBaselineIdentity.Compute("QA", merged[0]));
    }

    [Fact]
    public void EnvironmentsDoNotMergeIntoEachOther()
    {
        var qa = Manual("Orders", IntegrationType.REST, "https://m2lbqa.example.test/api/orders");

        // Same structural endpoint, different environment: still one record per environment set,
        // because the merge is always performed within one environment.
        var merged = _merger.Merge("QA", [qa], _proposals.Propose([Observed()]));
        Assert.Single(merged);

        Assert.NotEqual(
            IntegrationBaselineIdentity.Compute("QA", merged[0]),
            IntegrationBaselineIdentity.Compute("Production", merged[0]));
    }
}
