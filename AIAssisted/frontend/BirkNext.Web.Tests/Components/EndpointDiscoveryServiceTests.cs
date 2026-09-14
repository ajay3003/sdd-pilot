using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Endpoint Discovery store folds live proxy-observed endpoints into safe per-Target-Environment, page-oriented history and supports
/// delete page / clear page / re-analyze / delete all. It persists only non-secret metadata, keeps environments isolated, and never
/// deletes configured backend integrations when browser analyses are cleared.
/// </summary>
public sealed class EndpointDiscoveryServiceTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UtcNow;

    private static ObservedNetworkEndpoint Ep(ObservedTrafficCategory cat, string path, string method = "GET", string? pageOrigin = "https://app.test", string? pagePath = "/a", bool auth = true, int count = 1, GraphQlOperationType op = GraphQlOperationType.None) =>
        new()
        {
            Category = cat, Scheme = "https", Host = "api.test", Port = 443, Path = path, Method = method, AuthObserved = auth,
            LastStatus = 200, Source = EndpointDiscoverySource.AuthenticatedProxyTraffic, Confidence = ObservedEndpointConfidence.Verified,
            Count = count, FirstObservedAt = T, LastObservedAt = T, PageOrigin = pageOrigin, PagePath = pagePath,
            OperationType = cat == ObservedTrafficCategory.GraphQl && op == GraphQlOperationType.None ? GraphQlOperationType.Query : op
        };

    private sealed class FakeJs : IJSRuntime
    {
        public string? Stored;
        public int Writes;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Do<TValue>(identifier, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => Do<TValue>(identifier, args);
        private ValueTask<TValue> Do<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "birkNextStorage.setDiscovery") { Stored = args?[0] as string; Writes++; return new(default(TValue)!); }
            if (identifier == "birkNextStorage.getDiscovery") return new((TValue)(object?)Stored!);
            return new(default(TValue)!);
        }
    }

    // ── page creation & dedup (section 44) ───────────────────────────────────

    [Fact]
    public async Task FirstObservationCreatesOnePageAndRepeatsCollapse()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/children", pagePath: "/barn/1")]);
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/children", pagePath: "/barn/1", count: 3)]);
        var snap = svc.GetSnapshot("dev");
        snap.Pages.Should().HaveCount(1);
        snap.Pages[0].PagePath.Should().Be("/barn/1");
        snap.Pages[0].Endpoints.Should().HaveCount(1);
        snap.Pages[0].Endpoints[0].Count.Should().Be(3);   // collapsed, count is the max seen
    }

    [Fact]
    public async Task DifferentNavigationCreatesASecondPage()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/barn/1"), Ep(ObservedTrafficCategory.Rest, "/api/b", pagePath: "/barn/2")]);
        svc.GetSnapshot("dev").Pages.Should().HaveCount(2);
    }

    [Fact]
    public async Task UncorrelatedTrafficGoesToSharedNotAGuessedPage()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/config", pageOrigin: null, pagePath: null)]);
        var snap = svc.GetSnapshot("dev");
        snap.Pages.Should().BeEmpty();
        snap.Shared.Should().HaveCount(1);
    }

    // ── delete / clear / re-analyze (sections 48-51) ─────────────────────────

    [Fact]
    public async Task DeletingOnePageKeepsSharedEndpointsOnOtherPages()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        // Endpoint X used by both page A and page B.
        await svc.MergeObservedAsync(js, "dev",
        [
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/a"),
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/b")
        ]);
        var idA = svc.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/a").Identity;
        await svc.DeletePageAsync(js, "dev", idA);
        var snap = svc.GetSnapshot("dev");
        snap.Pages.Should().ContainSingle().Which.PagePath.Should().Be("/b");
        snap.Pages[0].Endpoints.Should().ContainSingle(e => e.Path == "/gql");   // B's X survives
    }

    [Fact]
    public async Task ClearPageKeepsThePageWithZeroEndpoints()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a")]);
        var id = svc.GetSnapshot("dev").Pages.Single().Identity;
        await svc.ClearPageAsync(js, "dev", id);
        var snap = svc.GetSnapshot("dev");
        snap.Pages.Should().ContainSingle();
        snap.Pages[0].Endpoints.Should().BeEmpty();
        // New traffic repopulates the same page.
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a")]);
        svc.GetSnapshot("dev").Pages.Single().Endpoints.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteAllRemovesBrowserAnalysesButNeverTouchesConfiguredIntegrations()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev",
        [
            Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a"),
            Ep(ObservedTrafficCategory.Rest, "/api/x", pageOrigin: null, pagePath: null)
        ]);
        await svc.DeleteAllAsync(js, "dev");
        var snap = svc.GetSnapshot("dev");
        snap.Pages.Should().BeEmpty();
        snap.Shared.Should().BeEmpty();

        // Backend integrations are computed from configuration and are unaffected by browser-analysis deletion.
        var profile = new FrontendAnalysisProfile { Id = "dev" };
        profile.Integrations.Add(new IntegrationConfig { Name = "M2LB Events", Type = IntegrationType.RabbitMQ, Resource = "309-rmq05", Enabled = true });
        EndpointDiscoveryService.BackendIntegrationsFor(profile).Should().ContainSingle().Which.Name.Should().Be("M2LB Events");
    }

    // ── persistence & restart (section 52) ───────────────────────────────────

    [Fact]
    public async Task PersistsAndReloadsSafeMetadataWithoutAnyCredential()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/sok")]);
        js.Stored.Should().NotBeNullOrEmpty();
        foreach (var forbidden in new[] { "Bearer ", "eyJ", "Authorization", "Cookie" })
            js.Stored!.Should().NotContain(forbidden);

        // A fresh service (a restart) reloads from the same storage.
        var reloaded = new EndpointDiscoveryService();
        await reloaded.LoadAsync(js);
        var snap = reloaded.GetSnapshot("dev");
        snap.Pages.Should().ContainSingle();
        snap.Pages[0].Endpoints.Single().OperationType.Should().Be(GraphQlOperationType.Query);
        snap.Pages[0].PagePath.Should().Be("/sok");
    }

    // ── environment isolation (section 54) ───────────────────────────────────

    [Fact]
    public async Task EnvironmentsAreIsolated()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a")]);
        svc.GetSnapshot("qa").Pages.Should().BeEmpty();
        svc.GetSnapshot("dev").Pages.Should().ContainSingle();
    }

    // ── backend integrations mapping & secret safety (sections 24, 43) ───────

    [Fact]
    public void BackendIntegrationsExcludeBrowserVisibleTypesAndNeverExposeConnectionStrings()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev" };
        profile.Integrations.Add(new IntegrationConfig { Name = "Rabbit", Type = IntegrationType.RabbitMQ, Resource = "rmq-host", Enabled = true });
        profile.Integrations.Add(new IntegrationConfig { Name = "Events", Type = IntegrationType.EventHub, Resource = "Endpoint=sb://x;SharedAccessKey=secret", Enabled = true });
        profile.Integrations.Add(new IntegrationConfig { Name = "PublicRest", Type = IntegrationType.REST, Endpoint = "https://api.test", Enabled = true });
        var integrations = EndpointDiscoveryService.BackendIntegrationsFor(profile);
        integrations.Select(i => i.Name).Should().BeEquivalentTo(["Rabbit", "Events"]);   // REST is browser-visible, excluded
        integrations.Single(i => i.Name == "Rabbit").Protocol.Should().Be("AMQP");
        integrations.Single(i => i.Name == "Events").Resource.Should().BeNull();           // connection string never surfaced
        integrations.Should().OnlyContain(i => i.RuntimeObserved == false);
    }
}
