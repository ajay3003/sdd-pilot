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

    // ── Performance metadata (BirkNext Performance Quality) ─────────────────────────────────────────

    [Fact]
    public void SamplesRecordWhetherTheResponseWasEncoded_AsAPresenceFlagOnly()
    {
        var registry = new ObservedNetworkRegistry();
        registry.Record(NetworkTrafficClassifier.Classify(Meta("GET", "/api/children", bearer: true, referer: "https://m2lbdev.bufetat.no/barn/1") with { DurationMs = 40, ResponseBytes = 900, ResponseEncoded = true }, Now));
        registry.Record(NetworkTrafficClassifier.Classify(Meta("GET", "/api/children", bearer: true, referer: "https://m2lbdev.bufetat.no/barn/1") with { DurationMs = 50, ResponseBytes = 4096, ResponseEncoded = false }, Now.AddSeconds(1)));
        var samples = registry.Snapshot().Single().Samples;
        Assert.Equal([false, true], samples.Select(s => s.ResponseEncoded));   // most recent first
        Assert.Equal(4096, samples[0].ResponseBytes);
    }

    [Fact]
    public void RegistryAccumulatesLatencySamplesStatusesAndCacheMetadataWithoutHeaderValues()
    {
        var registry = new ObservedNetworkRegistry();
        var durations = new[] { 120.0, 480.0, 1300.0, 90.0 };
        var statuses = new[] { 200, 200, 500, 304 };
        for (var i = 0; i < durations.Length; i++)
            registry.Record(NetworkTrafficClassifier.Classify(Meta("GET", "/api/children", bearer: true, referer: "https://m2lbdev.bufetat.no/barn/1", status: statuses[i]) with
            {
                DurationMs = durations[i], CacheDirectives = "private, max-age=60, x-custom=\"secret value\"", HasEtag = true, HasLastModified = false, ResponseBytes = 2048 + i,
            }, Now.AddSeconds(i)));

        var e = registry.Snapshot().Single();
        Assert.Equal(4, e.Count);
        Assert.Equal(4, e.Samples.Count);
        Assert.Equal(90, e.Samples[0].DurationMs);                 // most recent first
        Assert.Equal(304, e.Samples[0].Status);
        Assert.Equal(2051, e.Samples[0].ResponseBytes);
        Assert.Equal(90, e.MinDurationMs);
        Assert.Equal(1300, e.MaxDurationMs);
        Assert.Equal(1990, e.TotalDurationMs);
        Assert.Equal(90, e.LastDurationMs);
        Assert.Equal(1, e.ErrorCount);
        Assert.Equal(0, e.AuthRejectedCount);
        Assert.Equal(1, e.NotModifiedCount);
        Assert.Equal("private, max-age=60", e.CacheDirectives);   // unknown extension with a value is dropped
        Assert.True(e.HasEtag);
        Assert.False(e.HasLastModified);
        var json = JsonSerializer.Serialize(e);
        Assert.DoesNotContain("secret value", json);
        Assert.DoesNotContain("x-custom", json);
    }

    [Fact]
    public void RegistrySamplesAreBoundedMostRecentFirst()
    {
        var registry = new ObservedNetworkRegistry();
        for (var i = 0; i < ObservedNetworkPerformanceLimits.MaxSamplesPerEndpoint + 25; i++)
            registry.Record(NetworkTrafficClassifier.Classify(Meta("POST", "/gql", reqCt: "application/json", gql: GraphQlOperationType.Query, gqlName: "GetChildren", referer: "https://m2lbdev.bufetat.no/sok") with { DurationMs = i }, Now.AddMilliseconds(i)));
        var e = registry.Snapshot().Single();
        Assert.Equal(ObservedNetworkPerformanceLimits.MaxSamplesPerEndpoint + 25, e.Count);
        Assert.Equal(ObservedNetworkPerformanceLimits.MaxSamplesPerEndpoint, e.Samples.Count);
        Assert.Equal(ObservedNetworkPerformanceLimits.MaxSamplesPerEndpoint + 24, e.Samples[0].DurationMs);
        Assert.Equal(0, e.MinDurationMs);
    }

    [Fact]
    public void ExchangeWithoutTimingProducesNoSample()
    {
        var e = NetworkTrafficClassifier.Classify(Meta("GET", "/api/x", referer: "https://m2lbdev.bufetat.no/a"), Now);
        Assert.Empty(e.Samples);
        Assert.Null(e.LastDurationMs);
        Assert.Equal(0, e.TotalDurationMs);
        Assert.Null(e.CacheDirectives);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("no-store", "no-store")]
    [InlineData("public, max-age=31536000, immutable", "public, max-age=31536000, immutable")]
    [InlineData("MAX-AGE=\"600\", must-revalidate, foo=bar, no-cache", "max-age=600, must-revalidate, no-cache")]
    [InlineData("s-maxage=-5, private", "private")]
    public void CacheControlIsReducedToRecognisedDirectivesOnly(string? header, string? expected)
    {
        Assert.Equal(expected, CacheHeaderMetadata.NormalizeCacheControl(header));
    }

    [Fact]
    public void AuthenticationRejectionsAreCountedSeparatelyFromOtherErrors()
    {
        var registry = new ObservedNetworkRegistry();
        foreach (var status in new[] { 401, 403, 500, 200 })
            registry.Record(NetworkTrafficClassifier.Classify(Meta("GET", "/api/me", bearer: true, status: status, referer: "https://m2lbdev.bufetat.no/a") with { DurationMs = 10 }, Now));
        var e = registry.Snapshot().Single();
        Assert.Equal(3, e.ErrorCount);
        Assert.Equal(2, e.AuthRejectedCount);
    }

    // ── A served JSON document is not an API ─────────────────────────────────
    // The live defect: /appsettings.json returns application/json, so the JSON rule made it a Verified REST endpoint and
    // API Quality Review offered a configuration file as a REST API target called "Appsettings.json API".

    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/appsettings.Dev.json")]
    [InlineData("/appsettings.Production.json")]
    [InlineData("/manifest.json")]
    [InlineData("/site.webmanifest")]
    [InlineData("/_framework/blazor.boot.json")]
    [InlineData("/i18n/nb-NO.json")]
    public void ServedJsonDocumentsAreNotRestApis(string path)
    {
        var endpoint = NetworkTrafficClassifier.Classify(Meta("GET", path, respCt: "application/json"), Now);

        Assert.NotEqual(ObservedTrafficCategory.Rest, endpoint.Category);
        // Still observed, and still application traffic in Endpoint Discovery: configuration exposure is reviewed there.
        Assert.Equal(ObservedTrafficCategory.OtherHttp, endpoint.Category);
        Assert.Equal(path, endpoint.Path);
    }

    /// <summary>A JSON response is evidence of a media type, never of an API. The route is what decides.</summary>
    [Fact]
    public void JsonContentTypeAloneDoesNotMakeARestApi()
    {
        Assert.Equal(ObservedTrafficCategory.OtherHttp,
            NetworkTrafficClassifier.Classify(Meta("GET", "/config.json", respCt: "application/json"), Now).Category);
        Assert.Equal(ObservedTrafficCategory.Rest,
            NetworkTrafficClassifier.Classify(Meta("GET", "/api/autorisasjon", respCt: "application/json"), Now).Category);
    }

    /// <summary>A real API route that happens to end in .json is still an API route.</summary>
    [Theory]
    [InlineData("/api/users.json")]
    [InlineData("/v1/roles.json")]
    public void AnApiRouteEndingInJsonStaysRest(string path) =>
        Assert.Equal(ObservedTrafficCategory.Rest,
            NetworkTrafficClassifier.Classify(Meta("GET", path, respCt: "application/json"), Now).Category);

    /// <summary>A POST to a .json path is not a served document; only safe reads are.</summary>
    [Fact]
    public void AWriteToAJsonPathIsNotTreatedAsADocument() =>
        Assert.Equal(ObservedTrafficCategory.Rest,
            NetworkTrafficClassifier.Classify(Meta("POST", "/submit.json", reqCt: "application/json", respCt: "application/json"), Now).Category);

    /// <summary>The M2LB targets that must survive the fix, including GraphQL precedence over everything else.</summary>
    [Fact]
    public void TheRealM2lbApiTargetsAreStillClassifiedAsApis()
    {
        Assert.Equal(ObservedTrafficCategory.Rest,
            NetworkTrafficClassifier.Classify(Meta("GET", "/api/autorisasjon", respCt: "application/json"), Now).Category);

        var graphQl = Meta("POST", "/api/autorisasjon/graphql", reqCt: "application/json", gql: GraphQlOperationType.Query, gqlName: "GetRoles");
        Assert.Equal(ObservedTrafficCategory.GraphQl, NetworkTrafficClassifier.Classify(graphQl, Now).Category);
    }
}
