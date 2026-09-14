using System.Text.Json;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>
/// Traffic-driven authenticated endpoint discovery: the classifier learns REST and GraphQL endpoints from observed authenticated
/// requests (never assuming <c>/health</c> or <c>/graphql</c>), refuses to verify SPA HTML documents or static assets as REST,
/// distinguishes a GraphQL query from a mutation/subscription by the request body, and never retains any secret. The registry
/// collapses repeated identical endpoints. All inputs are non-secret metadata only.
/// </summary>
public sealed class ObservedTrafficDiscoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ObservedRequestMetadata Meta(string method, string target, string? responseContentType = "application/json", int status = 200,
        bool bearer = true, string? requestContentType = null, GraphQlOperationType gqlOp = GraphQlOperationType.None, string? gqlName = null, string host = "api-dev.example.no", int port = 443) =>
        new()
        {
            Host = host, Port = port, Method = method, Target = target, RequestContentType = requestContentType,
            ResponseStatus = status, ResponseContentType = responseContentType, BearerObserved = bearer,
            GraphQlOperationType = gqlOp, GraphQlOperationName = gqlName
        };

    // ── REST discovery (section 27) ──────────────────────────────────────────

    [Fact]
    public void AuthenticatedJsonGetIsVerifiedRest()
    {
        var endpoint = ObservedTrafficClassifier.Classify(Meta("GET", "/api/children?page=2"), Now);
        Assert.NotNull(endpoint);
        Assert.Equal(ObservedEndpointType.Rest, endpoint!.EndpointType);
        Assert.Equal(ObservedEndpointConfidence.Verified, endpoint.Confidence);
        Assert.Equal("https://api-dev.example.no", endpoint.Origin);
        Assert.Equal("/api/children", endpoint.Path);          // query string is never captured
        Assert.Equal("GET", endpoint.Method);
        Assert.True(endpoint.BearerObserved);
        Assert.Equal(GraphQlOperationType.None, endpoint.OperationType);
    }

    [Fact]
    public void AuthenticatedHtmlResponseIsNeverVerifiedRest()
    {
        var endpoint = ObservedTrafficClassifier.Classify(Meta("GET", "/app/dashboard", responseContentType: "text/html; charset=utf-8", status: 200), Now);
        Assert.NotNull(endpoint);
        Assert.Equal(ObservedEndpointType.Rest, endpoint!.EndpointType);
        Assert.Equal(ObservedEndpointConfidence.Rejected, endpoint.Confidence);   // the SPA document, not an API
    }

    [Theory]
    [InlineData("/static/app.js")]
    [InlineData("/assets/logo.svg")]
    [InlineData("/favicon.ico")]
    [InlineData("/fonts/inter.woff2")]
    public void StaticAssetsAreRejected(string path)
    {
        var endpoint = ObservedTrafficClassifier.Classify(Meta("GET", path, responseContentType: "application/javascript"), Now);
        Assert.Equal(ObservedEndpointConfidence.Rejected, endpoint!.Confidence);
    }

    [Fact]
    public void RequestWithoutBearerIsNotAuthenticatedRestAndIsNotRecorded()
    {
        Assert.Null(ObservedTrafficClassifier.Classify(Meta("GET", "/api/children", bearer: false), Now));
    }

    [Fact]
    public void AuthenticatedNonJsonReadIsACandidateNotVerified()
    {
        var endpoint = ObservedTrafficClassifier.Classify(Meta("GET", "/api/report", responseContentType: "text/plain"), Now);
        Assert.Equal(ObservedEndpointConfidence.Candidate, endpoint!.Confidence);
    }

    // ── GraphQL discovery (section 28) ───────────────────────────────────────

    [Fact]
    public void AuthenticatedGraphQlQueryPostIsVerifiedQueryEndpoint()
    {
        var endpoint = ObservedTrafficClassifier.Classify(
            Meta("POST", "/api/graph", requestContentType: "application/json", gqlOp: GraphQlOperationType.Query, gqlName: "Me"), Now);
        Assert.Equal(ObservedEndpointType.GraphQl, endpoint!.EndpointType);
        Assert.Equal(ObservedEndpointConfidence.Verified, endpoint.Confidence);
        Assert.Equal(GraphQlOperationType.Query, endpoint.OperationType);
        Assert.Equal("Me", endpoint.OperationName);
        Assert.Equal("/api/graph", endpoint.Path);   // learned from traffic, not assumed to be /graphql
    }

    [Fact]
    public void GraphQlMutationDoesNotVerifyTheQueryCapability()
    {
        var endpoint = ObservedTrafficClassifier.Classify(
            Meta("POST", "/api/graph", requestContentType: "application/json", gqlOp: GraphQlOperationType.Mutation, gqlName: "CreateChild"), Now);
        Assert.Equal(ObservedEndpointType.GraphQl, endpoint!.EndpointType);
        Assert.Equal(GraphQlOperationType.Mutation, endpoint.OperationType);
        // The endpoint is a real GraphQL endpoint, but a query capability must NOT be verified from a mutation.
        var status = new LocalHttpsProxyStatus { ObservedEndpoints = [endpoint] };
        Assert.False(status.AuthenticatedGraphQlQueryObserved);
        Assert.Null(status.VerifiedGraphQlQueryEndpoint);
    }

    [Fact]
    public void GraphQlSubscriptionDoesNotVerifyTheQueryCapability()
    {
        var endpoint = ObservedTrafficClassifier.Classify(
            Meta("POST", "/api/graph", requestContentType: "application/json", gqlOp: GraphQlOperationType.Subscription), Now);
        var status = new LocalHttpsProxyStatus { ObservedEndpoints = [endpoint!] };
        Assert.False(status.AuthenticatedGraphQlQueryObserved);
    }

    [Fact]
    public void GetToAGraphQlPathIsNotClassifiedAsAVerifiedQuery()
    {
        // A GET (no parsed operation body) to a graphql-looking path is REST, never a verified GraphQL query.
        var endpoint = ObservedTrafficClassifier.Classify(Meta("GET", "/graphql", responseContentType: "application/json"), Now);
        Assert.Equal(ObservedEndpointType.Rest, endpoint!.EndpointType);
        var status = new LocalHttpsProxyStatus { ObservedEndpoints = [endpoint] };
        Assert.False(status.AuthenticatedGraphQlQueryObserved);
    }

    // ── GraphQL body inspector: query vs mutation vs subscription, malformed (section 28 E) ──

    [Theory]
    [InlineData("{\"query\":\"query Me { me { id } }\"}", GraphQlOperationType.Query, "Me")]
    [InlineData("{\"query\":\"mutation Add($n:String!){ add(name:$n){ id } }\",\"operationName\":\"Add\"}", GraphQlOperationType.Mutation, "Add")]
    [InlineData("{\"query\":\"subscription OnX { x }\"}", GraphQlOperationType.Subscription, "OnX")]
    [InlineData("{\"query\":\"{ me { id } }\"}", GraphQlOperationType.Query, null)]   // anonymous shorthand query
    [InlineData("  { \"query\" : \"  query   Foo  { a }\" }  ", GraphQlOperationType.Query, "Foo")]
    public void GraphQlBodyInspectorClassifiesOperations(string body, GraphQlOperationType expected, string? expectedName)
    {
        var (type, name) = GraphQlBodyInspector.Classify(body);
        Assert.Equal(expected, type);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"notquery\": 1 }")]
    [InlineData("{ \"query\": 123 }")]
    [InlineData("{ \"query\": \"\" }")]
    [InlineData("[1,2,3]")]
    [InlineData("{ broken json")]
    public void GraphQlBodyInspectorTreatsMalformedOrNonGraphQlAsNone(string body)
    {
        var (type, name) = GraphQlBodyInspector.Classify(body);
        Assert.Equal(GraphQlOperationType.None, type);
        Assert.Null(name);
    }

    [Fact]
    public void MalformedGraphQlPostDoesNotBecomeAVerifiedQuery()
    {
        // POST with JSON content type but no parseable query: classified as REST candidate at most, never a verified GraphQL query, no crash.
        var endpoint = ObservedTrafficClassifier.Classify(
            Meta("POST", "/api/graph", requestContentType: "application/json", gqlOp: GraphQlOperationType.None), Now);
        Assert.Equal(ObservedEndpointType.Rest, endpoint!.EndpointType);
        Assert.NotEqual(ObservedEndpointConfidence.Verified, endpoint.Confidence);
    }

    // ── no assumed paths (section 29) ────────────────────────────────────────

    [Fact]
    public void DiscoveryUsesTheRealObservedPathsNotHealthOrGraphql()
    {
        // The real authenticated REST endpoint is /api/v2/children, not /health; the real GraphQL endpoint is /internal/gql, not /graphql.
        var rest = ObservedTrafficClassifier.Classify(Meta("GET", "/api/v2/children"), Now)!;
        var gql = ObservedTrafficClassifier.Classify(Meta("POST", "/internal/gql", requestContentType: "application/json", gqlOp: GraphQlOperationType.Query), Now)!;
        var status = new LocalHttpsProxyStatus { ObservedEndpoints = [rest, gql] };
        Assert.Equal("https://api-dev.example.no/api/v2/children", status.VerifiedRestEndpoint!.Display);
        Assert.Equal("https://api-dev.example.no/internal/gql", status.VerifiedGraphQlQueryEndpoint!.Display);
        // A configured /health returning the SPA document is never a REST proof.
        var health = ObservedTrafficClassifier.Classify(Meta("GET", "/health", responseContentType: "text/html"), Now)!;
        Assert.Equal(ObservedEndpointConfidence.Rejected, health.Confidence);
    }

    // ── capability separation (section 30) ───────────────────────────────────

    [Fact]
    public void AuthContextAvailabilityIsSeparateFromRestAndGraphQlVerification()
    {
        // Credential available but no traffic observed yet: auth context available, REST/GraphQL not verified.
        var contextOnly = new LocalHttpsProxyStatus { AuthenticatedCredentialAvailable = true };
        Assert.True(contextOnly.AuthenticatedCredentialAvailable);
        Assert.False(contextOnly.AuthenticatedRestObserved);
        Assert.False(contextOnly.AuthenticatedGraphQlQueryObserved);

        var rest = ObservedTrafficClassifier.Classify(Meta("GET", "/api/children"), Now)!;
        var withRest = contextOnly with { ObservedEndpoints = [rest] };
        Assert.True(withRest.AuthenticatedRestObserved);
        Assert.False(withRest.AuthenticatedGraphQlQueryObserved);

        var gql = ObservedTrafficClassifier.Classify(Meta("POST", "/gql", requestContentType: "application/json", gqlOp: GraphQlOperationType.Query), Now)!;
        var withBoth = contextOnly with { ObservedEndpoints = [rest, gql] };
        Assert.True(withBoth.AuthenticatedRestObserved);
        Assert.True(withBoth.AuthenticatedGraphQlQueryObserved);
    }

    // ── duplicate collapsing (section 23) ────────────────────────────────────

    [Fact]
    public void RegistryCollapsesRepeatedIdenticalEndpointsAndCountsThem()
    {
        var registry = new ObservedEndpointRegistry();
        for (var i = 0; i < 5; i++)
            registry.Record(ObservedTrafficClassifier.Classify(Meta("GET", "/api/children"), Now.AddSeconds(i))!);
        registry.Record(ObservedTrafficClassifier.Classify(Meta("GET", "/api/parents"), Now)!);
        var snapshot = registry.Snapshot();
        Assert.Equal(2, snapshot.Count);
        var children = snapshot.Single(e => e.Path == "/api/children");
        Assert.Equal(5, children.Count);
    }

    [Fact]
    public void RegistryKeepsTheStrongestConfidenceAndMostInformativeOperation()
    {
        var registry = new ObservedEndpointRegistry();
        // Same GraphQL endpoint seen first as a mutation, later as a query: the query verification must win for the query capability.
        registry.Record(ObservedTrafficClassifier.Classify(Meta("POST", "/gql", requestContentType: "application/json", gqlOp: GraphQlOperationType.Mutation), Now)!);
        registry.Record(ObservedTrafficClassifier.Classify(Meta("POST", "/gql", requestContentType: "application/json", gqlOp: GraphQlOperationType.Query), Now)!);
        var status = new LocalHttpsProxyStatus { ObservedEndpoints = registry.Snapshot() };
        Assert.True(status.AuthenticatedGraphQlQueryObserved);
    }

    [Fact]
    public void RegistryIsBounded()
    {
        var registry = new ObservedEndpointRegistry(capacity: 3);
        for (var i = 0; i < 10; i++)
            registry.Record(ObservedTrafficClassifier.Classify(Meta("GET", $"/api/item/{i}"), Now)!);
        Assert.Equal(3, registry.Snapshot().Count);
    }

    // ── secret safety (section 31) ───────────────────────────────────────────

    [Fact]
    public void ObservedEndpointModelHasNoSecretFieldsAndSerializesWithoutCredentials()
    {
        var endpoint = ObservedTrafficClassifier.Classify(Meta("GET", "/api/children?token=shouldNotAppear"), Now)!;
        var json = JsonSerializer.Serialize(endpoint);
        // No credential VALUE and no query string (which can carry secrets) is ever present. "BearerObserved" is a safe boolean flag, not a token.
        foreach (var forbidden in new[] { "Bearer ", "eyJ", "Authorization", "Cookie", "token=shouldNotAppear", "shouldNotAppear" })
            Assert.DoesNotContain(forbidden, json);
        var properties = typeof(ObservedAuthenticatedEndpoint).GetProperties().Select(p => p.Name);
        // No field stores a secret value (a bearer/cookie/body value); BearerObserved is only a bool that a Bearer was present.
        foreach (var secret in new[] { "Token", "Cookie", "Authorization", "Body", "Secret", "Password", "Credential" })
            Assert.DoesNotContain(properties, p => p.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }
}
