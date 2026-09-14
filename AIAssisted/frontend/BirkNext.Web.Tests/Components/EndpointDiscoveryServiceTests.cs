using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Endpoint Discovery store folds live proxy-observed endpoints into safe per-Target-Environment, page-oriented history and supports
/// delete page / refresh analysis / delete all. Refresh keeps the page entry, starts a new generation and only accepts traffic observed
/// after the refresh boundary. It persists only non-secret metadata, keeps environments isolated, and never deletes configured integrations.
/// </summary>
public sealed class EndpointDiscoveryServiceTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UtcNow;

    private static ObservedNetworkEndpoint Ep(ObservedTrafficCategory cat, string path, string method = "GET", string? pageOrigin = "https://app.test", string? pagePath = "/a", bool auth = true, int count = 1, GraphQlOperationType op = GraphQlOperationType.None, DateTimeOffset? at = null) =>
        new()
        {
            Category = cat, Scheme = "https", Host = "api.test", Port = 443, Path = path, Method = method, AuthObserved = auth,
            LastStatus = 200, Source = EndpointDiscoverySource.AuthenticatedProxyTraffic, Confidence = ObservedEndpointConfidence.Verified,
            Count = count, FirstObservedAt = at ?? T, LastObservedAt = at ?? T, PageOrigin = pageOrigin, PagePath = pagePath,
            OperationType = cat == ObservedTrafficCategory.GraphQl && op == GraphQlOperationType.None ? GraphQlOperationType.Query : op
        };

    private sealed class FakeJs : IJSRuntime
    {
        public string? Stored;
        public int Writes;
        public int SetItemCalls;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Do<TValue>(identifier, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => Do<TValue>(identifier, args);
        private ValueTask<TValue> Do<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "birkNextStorage.setItem") { SetItemCalls++; return new(default(TValue)!); }
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
    public async Task RefreshKeepsPageStartsNewGenerationAndOnlyPostBoundaryTrafficRepopulates()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        // Old traffic observed before the refresh.
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a", at: T.AddMinutes(-10))]);
        var id = svc.GetSnapshot("dev").Pages.Single().Identity;

        await svc.RefreshPageAsync(js, "dev", id);
        var page = svc.GetSnapshot("dev").Pages.Single();
        page.Should().NotBeNull("the page entry is kept, never deleted/recreated");
        page.Endpoints.Should().BeEmpty();
        page.AnalysisGeneration.Should().Be(2);
        page.RefreshedAtUtc.Should().NotBeNull();
        page.IsWaitingForFreshTraffic.Should().BeTrue();

        // The still-live proxy keeps re-reporting the OLD (pre-boundary) observation: it must NOT reappear.
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a", at: T.AddMinutes(-10))]);
        svc.GetSnapshot("dev").Pages.Single().Endpoints.Should().BeEmpty("old cached traffic must not repopulate a refreshed page");

        // Fresh traffic observed after the refresh boundary repopulates the page.
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a", at: DateTimeOffset.UtcNow.AddMinutes(5))]);
        var repopulated = svc.GetSnapshot("dev").Pages.Single();
        repopulated.Endpoints.Should().ContainSingle();
        repopulated.IsWaitingForFreshTraffic.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAffectsOnlyTheSelectedPageAndLeavesEveryOtherPageExactlyUnchanged()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        var old = T.AddMinutes(-10);
        string[] pages = ["/dashboard", "/children", "/placements", "/users", "/reports"];
        foreach (var p in pages)
            await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api" + p, pagePath: p, at: old)]);

        var before = svc.GetSnapshot("dev").Pages.ToDictionary(p => p.PagePath, p => (p.Endpoints.Count, p.LastObservedAt, p.AnalysisGeneration));
        var childrenId = svc.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/children").Identity;
        await svc.RefreshPageAsync(js, "dev", childrenId);

        var snap = svc.GetSnapshot("dev");
        var children = snap.Pages.Single(p => p.PagePath == "/children");
        children.Endpoints.Should().BeEmpty();
        children.AnalysisGeneration.Should().Be(2);
        children.IsWaitingForFreshTraffic.Should().BeTrue();
        foreach (var p in pages.Where(p => p != "/children"))
        {
            var page = snap.Pages.Single(x => x.PagePath == p);
            (page.Endpoints.Count, page.LastObservedAt, page.AnalysisGeneration).Should().Be(before[p], $"{p} must be untouched");
        }
    }

    [Fact]
    public async Task RefreshingOnePageKeepsAndDoesNotResetSharedEndpointEvidenceOnAnotherPage()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        var old = T.AddMinutes(-10);
        // Endpoint X (same host+path+method) used by page A and page B.
        await svc.MergeObservedAsync(js, "dev",
        [
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/a", at: old),
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/b", at: old)
        ]);
        var idA = svc.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/a").Identity;
        await svc.RefreshPageAsync(js, "dev", idA);

        var snap = svc.GetSnapshot("dev");
        snap.Pages.Single(p => p.PagePath == "/a").Endpoints.Should().BeEmpty("A's copy of X is reset for the new generation");
        snap.Pages.Single(p => p.PagePath == "/b").Endpoints.Should().ContainSingle(e => e.Path == "/gql", "B's copy of X is untouched");
    }

    [Fact]
    public async Task RefreshKeepsThePageWhileDeleteRemovesIt()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a", at: T.AddMinutes(-10))]);
        var id = svc.GetSnapshot("dev").Pages.Single().Identity;

        await svc.RefreshPageAsync(js, "dev", id);
        svc.GetSnapshot("dev").Pages.Should().ContainSingle("refresh keeps the page");

        await svc.DeletePageAsync(js, "dev", id);
        svc.GetSnapshot("dev").Pages.Should().BeEmpty("delete removes the page");
    }

    [Fact]
    public async Task RefreshPersistsOnlyTheDiscoveryStoreAndNeverTheTargetEnvironmentConfig()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a", at: T.AddMinutes(-10))]);
        var id = svc.GetSnapshot("dev").Pages.Single().Identity;
        js.SetItemCalls = 0;
        await svc.RefreshPageAsync(js, "dev", id);
        js.SetItemCalls.Should().Be(0, "refresh must not persist Target Environment configuration");
        js.Writes.Should().BeGreaterThan(0, "refresh persists only the safe discovery store");
    }

    [Fact]
    public async Task RapidDoubleRefreshDoesNotDuplicateOrCorruptThePage()
    {
        var js = new FakeJs();
        var svc = new EndpointDiscoveryService();
        await svc.MergeObservedAsync(js, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a", at: T.AddMinutes(-10))]);
        var id = svc.GetSnapshot("dev").Pages.Single().Identity;
        await svc.RefreshPageAsync(js, "dev", id);
        await svc.RefreshPageAsync(js, "dev", id);
        var snap = svc.GetSnapshot("dev");
        snap.Pages.Should().ContainSingle("no duplicate page is created");
        snap.Pages.Single().Endpoints.Should().BeEmpty();
        snap.Pages.Single().AnalysisGeneration.Should().Be(3, "each refresh advances the generation");
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
