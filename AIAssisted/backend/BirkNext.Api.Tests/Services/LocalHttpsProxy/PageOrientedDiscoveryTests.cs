using System.Text.Json;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>
/// Page-oriented network classification: every browser-observed exchange is assigned a conservative category (REST, GraphQL,
/// WebSocket, Authentication, Static, Telemetry, Other) and correlated to a page via the safe Referer (query string stripped), or to
/// Shared/background when correlation is not confident. An SPA HTML document is never REST. No credential, body or query is retained.
/// </summary>
public sealed class PageOrientedDiscoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static NetworkRequestMetadata Meta(string method, string target, string? respCt = "application/json", int status = 200,
        bool bearer = false, string? reqCt = null, bool ws = false, GraphQlOperationType gql = GraphQlOperationType.None, string? gqlName = null,
        string? referer = null, string host = "m2lbdev.bufetat.no", int port = 443) =>
        new()
        {
            Host = host, Port = port, Method = method, Target = target, RequestContentType = reqCt, ResponseStatus = status,
            ResponseContentType = respCt, BearerObserved = bearer, IsWebSocket = ws, GraphQlOperationType = gql, GraphQlOperationName = gqlName, Referer = referer
        };

    [Fact]
    public void AuthenticatedJsonGetIsRestCorrelatedToTheRefererPage()
    {
        var e = NetworkTrafficClassifier.Classify(Meta("GET", "/api/children?page=2", bearer: true, referer: "https://m2lbdev.bufetat.no/barn/123?tab=x"), Now);
        Assert.Equal(ObservedTrafficCategory.Rest, e.Category);
        Assert.Equal("https://m2lbdev.bufetat.no", e.Origin);
        Assert.Equal("/api/children", e.Path);          // query stripped
        Assert.True(e.AuthObserved);
        Assert.Equal("https://m2lbdev.bufetat.no", e.PageOrigin);
        Assert.Equal("/barn/123", e.PagePath);          // page from Referer, query stripped
    }

    [Fact]
    public void HtmlDocumentGetIsItsOwnPageAndNeverRest()
    {
        var e = NetworkTrafficClassifier.Classify(Meta("GET", "/operasjonskatalog", respCt: "text/html; charset=utf-8", referer: "https://m2lbdev.bufetat.no/home"), Now);
        Assert.NotEqual(ObservedTrafficCategory.Rest, e.Category);
        Assert.Equal(ObservedTrafficCategory.OtherHttp, e.Category);
        Assert.Equal("https://m2lbdev.bufetat.no", e.PageOrigin);
        Assert.Equal("/operasjonskatalog", e.PagePath);   // the document IS the page, not correlated to the Referer
    }

    [Theory]
    [InlineData("/static/app.js", "application/javascript")]
    [InlineData("/assets/logo.svg", "image/svg+xml")]
    [InlineData("/favicon.ico", "image/x-icon")]
    public void StaticAssetsAreCategorizedStatic(string path, string ct)
    {
        Assert.Equal(ObservedTrafficCategory.StaticAsset, NetworkTrafficClassifier.Classify(Meta("GET", path, respCt: ct), Now).Category);
    }

    [Fact]
    public void TelemetryAndAuthArePreservedAsDistinctCategories()
    {
        Assert.Equal(ObservedTrafficCategory.Telemetry, NetworkTrafficClassifier.Classify(Meta("POST", "/v2/track/collect", reqCt: "application/json"), Now).Category);
        Assert.Equal(ObservedTrafficCategory.Authentication, NetworkTrafficClassifier.Classify(Meta("GET", "/connect/token"), Now).Category);
    }

    [Fact]
    public void WebSocketIsCategorizedWithWssSchemeAndWsMethod()
    {
        var e = NetworkTrafficClassifier.Classify(Meta("GET", "/notifications", respCt: null, status: 101, ws: true, referer: "https://m2lbdev.bufetat.no/brukertilgang"), Now);
        Assert.Equal(ObservedTrafficCategory.WebSocket, e.Category);
        Assert.Equal("wss", e.Scheme);
        Assert.Equal("WS", e.Method);
        Assert.Equal("/brukertilgang", e.PagePath);
    }

    [Fact]
    public void GraphQlPostIsCorrelatedToItsPageAndKeepsOperationMetadata()
    {
        var e = NetworkTrafficClassifier.Classify(Meta("POST", "/internal/gql", reqCt: "application/json", bearer: true, gql: GraphQlOperationType.Query, gqlName: "SearchBarn", referer: "https://m2lbdev.bufetat.no/sok"), Now);
        Assert.Equal(ObservedTrafficCategory.GraphQl, e.Category);
        Assert.Equal("/internal/gql", e.Path);            // learned, not assumed /graphql
        Assert.Equal(GraphQlOperationType.Query, e.OperationType);
        Assert.Equal("SearchBarn", e.OperationName);
        Assert.Equal("/sok", e.PagePath);
    }

    [Fact]
    public void RequestWithoutARefererGoesToSharedBackground()
    {
        var e = NetworkTrafficClassifier.Classify(Meta("GET", "/api/config", bearer: true, referer: null), Now);
        Assert.Null(e.PageOrigin);
        Assert.Null(e.PagePath);
    }

    [Fact]
    public void ObservedNetworkEndpointCarriesNoSecret()
    {
        var e = NetworkTrafficClassifier.Classify(Meta("GET", "/api/children?token=leak", bearer: true, referer: "https://m2lbdev.bufetat.no/barn/1?secret=leak"), Now);
        var json = JsonSerializer.Serialize(e);
        foreach (var forbidden in new[] { "Bearer ", "eyJ", "Authorization", "Cookie", "token=leak", "secret=leak", "leak" })
            Assert.DoesNotContain(forbidden, json);
        var props = typeof(ObservedNetworkEndpoint).GetProperties().Select(p => p.Name);
        foreach (var secret in new[] { "Token", "Cookie", "Authorization", "Body", "Secret", "Password", "Query" })
            Assert.DoesNotContain(props, p => p.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegistryCollapsesRepeatsPerPageButKeepsDifferentPagesSeparate()
    {
        var registry = new ObservedNetworkRegistry();
        for (var i = 0; i < 4; i++)
            registry.Record(NetworkTrafficClassifier.Classify(Meta("GET", "/api/children", bearer: true, referer: "https://m2lbdev.bufetat.no/barn/1"), Now.AddSeconds(i)));
        registry.Record(NetworkTrafficClassifier.Classify(Meta("GET", "/api/children", bearer: true, referer: "https://m2lbdev.bufetat.no/barn/2"), Now));
        var snapshot = registry.Snapshot();
        Assert.Equal(2, snapshot.Count);   // same endpoint, two different pages → two entries
        Assert.Equal(4, snapshot.Single(e => e.PagePath == "/barn/1").Count);
    }
}
