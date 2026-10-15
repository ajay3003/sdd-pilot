using System.Net;
using System.Net.Http.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Utilities;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationTopology;

/// <summary>The Altinn provider (configured → Discovered after a metadata read, host from the central endpoints) and the real host's wiring.</summary>
public sealed class TopologyIntegrationTests
{
    [Fact]
    public async Task AltinnProvider_IsConfiguredUntilMetadataIsRead_ThenDiscovered_WithItsContractAndCentralHost()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var context = new TopologyContext("env", null, "", new IntegrationCatalog(), [], new TopologyRequest(null));

        var configured = new TopologyBuilder();
        await new AltinnTopologyProvider(flows, kit.Policy).ContributeAsync(context, configured, default);
        var app = configured.Build("env", null, DateTimeOffset.UtcNow).Components.Single().Interfaces.Single();
        (app.Type, app.Status, app.Address).Should().Be((TopologyInterfaceType.ExternalApplication, TopologyStatus.Configured, AltinnTestKit.AppId));

        await kit.Contracts(flows).DiscoverPublicAsync("env", default);
        var discovered = new TopologyBuilder();
        await new AltinnTopologyProvider(flows, kit.Policy).ContributeAsync(context, discovered, default);
        var component = discovered.Build("env", null, DateTimeOffset.UtcNow).Components.Single();
        component.Location.Should().Be("https://buf.apps.tt02.altinn.no/", "the host comes from the central Altinn endpoint resolution");
        component.Interfaces.Single().Should().Match<TopologyInterface>(item => item.Status == TopologyStatus.Discovered && item.ContractReference == AltinnContractEvolution.ContractId(AltinnTestKit.AppId, "model"));
    }

    [Fact]
    public async Task TheRealHost_ServesTheTopology_AndDiscoveryNeedsThePermission()
    {
        await using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/integration-topology/topology-test-env");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<IntegrationTopologyView>();
        view!.Sources.Should().Contain(item => item.Source == TopologySource.TargetEnvironment && item.State == TopologySourceState.NotAvailable);
        view.Profiles.Should().Contain("skolenaervaer");
        (await client.PostAsJsonAsync("/api/integration-topology/topology-test-env/discover", new TopologyRequest(null))).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
