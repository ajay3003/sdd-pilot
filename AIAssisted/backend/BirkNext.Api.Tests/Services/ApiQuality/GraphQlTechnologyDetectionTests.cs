using System.Net;
using System.Text;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Evidence-based GraphQL technology metadata: Strawberry Shake Confirmed only from the deployed build manifest, Hot Chocolate Likely at most
/// from two independent runtime fingerprints of the review's own requests. Never a gate, never a finding, never changes compatibility.
/// </summary>
public sealed class GraphQlTechnologyDetectionTests
{
    private const string Frontend = "https://m2lbdev.example.test/";
    private const string HcIntrospectionRefusal = "{\"errors\":[{\"message\":\"Introspection is not allowed for the current request.\",\"extensions\":{\"code\":\"HC0046\"}}]}";
    private const string HcUnknownField = "{\"errors\":[{\"message\":\"The field `__birkNextUnknownFieldProbe` does not exist on the type `Query`.\",\"extensions\":{\"code\":\"HC0020\"}}]}";
    private const string GraphQlJsUnknownField = "{\"errors\":[{\"message\":\"Cannot query field \\\"x\\\" on type \\\"Query\\\".\",\"extensions\":{\"code\":\"GRAPHQL_VALIDATION_FAILED\"}}]}";
    private const string Index = "<!DOCTYPE html><html><head><base href=\"/app/\" /></head><body></body></html>";
    private const string BootWithStrawberry = "{\"resources\":{\"assembly\":{\"M2LB.Web.wasm\":\"sha256-a\",\"StrawberryShake.Core.wasm\":\"sha256-b\",\"StrawberryShake.Transport.Http.wasm\":\"sha256-c\"},\"fingerprinting\":{\"StrawberryShake.Core.abc12345.wasm\":\"StrawberryShake.Core.wasm\"}}}";
    private const string BootWithoutStrawberry = "{\"resources\":{\"assembly\":{\"M2LB.Web.wasm\":\"sha256-a\",\"System.Net.Http.Json.wasm\":\"sha256-b\"}}}";

    // ── Server: Hot Chocolate ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoIndependentHotChocolateFingerprints_AreLikely_NeverConfirmed()
    {
        var finding = GraphQlServerFingerprints.Classify([.. GraphQlServerFingerprints.From(HcIntrospectionRefusal), .. GraphQlServerFingerprints.From(HcUnknownField)]);
        Assert.Equal(GraphQlTechnologies.HotChocolate, finding.Technology);
        Assert.Equal(GraphQlTechnologyConfidence.Likely, finding.Confidence);
        Assert.Equal(GraphQlTechnologyEvidenceSource.RuntimeResponse, finding.Source);
        Assert.Contains(finding.Evidence, e => e.Contains("HC0046"));
        Assert.Contains(finding.Evidence, e => e.Contains("Introspection refusal uses Hot Chocolate's message"));
        Assert.Equal(3, finding.Evidence.Count);   // code, introspection message, unknown-field template — the code counted once
    }

    [Fact]
    public void AGenericGraphQlServer_IsNotDetected()
    {
        var finding = GraphQlServerFingerprints.Classify(GraphQlServerFingerprints.From(GraphQlJsUnknownField));
        Assert.Null(finding.Technology);
        Assert.Equal(GraphQlTechnologyConfidence.NotDetected, finding.Confidence);
        Assert.Equal(GraphQlTechnologyEvidenceSource.None, finding.Source);
    }

    [Fact]
    public void OneFingerprintAlone_IsNotEnough()
    {
        var finding = GraphQlServerFingerprints.Classify(GraphQlServerFingerprints.From("{\"errors\":[{\"message\":\"x\",\"extensions\":{\"code\":\"HC0001\"}}]}"));
        Assert.Equal(GraphQlTechnologyConfidence.NotDetected, finding.Confidence);
        Assert.Contains("at least two independent fingerprints", finding.Note);
    }

    [Fact]
    public void Fingerprints_CarryNoMessageTextOrValues()
    {
        var prints = GraphQlServerFingerprints.From("{\"errors\":[{\"message\":\"The field `secretCustomerField` does not exist on the type `Person`.\",\"extensions\":{\"code\":\"HC0020\"}}]}");
        Assert.DoesNotContain(prints, p => p.Contains("secretCustomerField") || p.Contains("Person"));
        Assert.Empty(GraphQlServerFingerprints.From("not json"));
    }

    // ── Client: Strawberry Shake from the deployed build manifest ────────────────────────────────────────────────

    private sealed class Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static HttpResponseMessage Text(string body, string type, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static Fixture Frontend_(string? boot, HttpStatusCode indexStatus = HttpStatusCode.OK) => new(req => req.RequestUri!.AbsolutePath switch
    {
        "/" => indexStatus == HttpStatusCode.OK ? Text(Index, "text/html") : new HttpResponseMessage(indexStatus),
        "/app/_framework/blazor.boot.json" when boot is not null => Text(boot, "application/json"),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    [Fact]
    public async Task StrawberryShakeAssembliesInTheDeployedManifest_AreConfirmed()
    {
        var fixture = Frontend_(BootWithStrawberry);
        var finding = await GraphQlClientTechnologyDetector.DetectAsync(new HttpClient(fixture), Frontend, CancellationToken.None);
        Assert.Equal(GraphQlTechnologies.StrawberryShake, finding.Technology);
        Assert.Equal(GraphQlTechnologyConfidence.Confirmed, finding.Confidence);
        Assert.Equal(GraphQlTechnologyEvidenceSource.DeployedFrontendArtifact, finding.Source);
        Assert.Contains("StrawberryShake.Core, StrawberryShake.Transport.Http", finding.Evidence.Single());
        Assert.All(fixture.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Contains(fixture.Requests, r => r.RequestUri!.AbsolutePath == "/app/_framework/blazor.boot.json");   // honours <base href>
    }

    [Fact]
    public async Task AManifestWithoutStrawberryShake_IsNotDetected_NotInferredFromBlazorPlusGraphQl()
    {
        var finding = await GraphQlClientTechnologyDetector.DetectAsync(new HttpClient(Frontend_(BootWithoutStrawberry)), Frontend, CancellationToken.None);
        Assert.Null(finding.Technology);
        Assert.Equal(GraphQlTechnologyConfidence.NotDetected, finding.Confidence);
        Assert.Equal(GraphQlTechnologyEvidenceSource.DeployedFrontendArtifact, finding.Source);
    }

    [Fact]
    public async Task AFrontendBehindSignIn_IsNotDetected_WithTheReason()
    {
        var finding = await GraphQlClientTechnologyDetector.DetectAsync(new HttpClient(Frontend_(null, HttpStatusCode.Redirect)), Frontend, CancellationToken.None);
        Assert.Equal(GraphQlTechnologyConfidence.NotDetected, finding.Confidence);
        Assert.Contains("index HTTP 302", finding.Note);
    }

    // ── Engine: metadata only ─────────────────────────────────────────────────────────────────────────────────────

    private const string Sdl = "type Query { roles: [Role!]! } type Role { id: ID! name: String! }";

    private static ApiReviewEngine Engine(HttpMessageHandler handler, IGraphQlSchemaArtifactStore store) =>
        new(new HttpClient(handler), new NoGateway(), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), NullLogger<ApiReviewEngine>.Instance, store);

    private sealed class Store : IGraphQlSchemaArtifactStore
    {
        public Task<IReadOnlyList<GraphQlSchemaArtifact>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GraphQlSchemaArtifact>>([]);
        public Task<(GraphQlSchemaArtifact? Artifact, string? Error)> SaveAsync(GraphQlSchemaArtifactUpload upload, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string environmentId, string targetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResolvedGraphQlSchemaArtifact?> ResolveAsync(string environmentId, string targetId, CancellationToken ct = default) =>
            Task.FromResult<ResolvedGraphQlSchemaArtifact?>(new(new GraphQlSchemaArtifact { Id = "a", FileName = "m2lb.graphql", ContentHash = new string('a', 64) }, GraphQlSdlSchema.FromSdl(Sdl, out _), null));
    }

    private static Fixture Site(bool strawberry) => new(req => req.RequestUri!.Host == "m2lbdev.example.test"
        ? req.RequestUri.AbsolutePath switch
        {
            "/" => Text(Index, "text/html"),
            "/app/_framework/blazor.boot.json" => Text(strawberry ? BootWithStrawberry : BootWithoutStrawberry, "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }
        : req.Content is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
        : req.Content.ReadAsStringAsync().Result switch
        {
            var b when b.Contains("__schema") => Text(HcIntrospectionRefusal, "application/json", HttpStatusCode.BadRequest),
            var b when b.Contains("__birkNextUnknownFieldProbe") => Text(HcUnknownField, "application/json", HttpStatusCode.BadRequest),
            _ => Text("{\"data\":{\"__typename\":\"Query\"}}", "application/graphql-response+json"),
        });

    private static ApiReviewRunRequest Request() => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "M2LB DEV", EnvironmentType = "Development", TargetUrl = Frontend },
        Identity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.ManagedEdgeCdp, "dev", null), FrontendOrigin = "https://m2lbdev.example.test",
        Policy = new ApiReviewPolicy { ErrorHandlingProbes = true },
        Targets =
        [
            new ApiReviewTarget
            {
                TargetId = "gql", EnvironmentId = "dev", ApiType = ApiReviewTargetType.GraphQl, Host = "api-dev.example.test", BasePath = "/graphql", ServiceName = "GraphQL",
                Source = ApiReviewTargetSource.DiscoveredTraffic, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
                Operations = [Op("query HentRoller { roles { id name } }", "HentRoller"), Op("query Brutt { roles { navn } }", "Brutt"),
                    Op("mutation Endre { renameRole { id } }", "Endre", GraphQlOperationType.Mutation)],
            },
        ],
    };

    private static ApiReviewOperation Op(string document, string name, GraphQlOperationType type = GraphQlOperationType.Query)
    {
        var normalized = GraphQlDocumentNormalizer.Normalize(document)!;
        return new ApiReviewOperation { Method = "POST", Path = "/graphql", OperationType = type, OperationName = name, Document = normalized.Document, DocumentHash = normalized.Hash, ObservedCount = 2 };
    }

    [Fact]
    public async Task M2lbShape_TechnologyIsReported_AsMetadataWithProvenance()
    {
        var report = await Engine(Site(strawberry: true), new Store()).RunAsync(Request());
        var target = report.Targets.Single();
        var technology = target.GraphQlTechnology!;
        Assert.Equal((GraphQlTechnologies.HotChocolate, GraphQlTechnologyConfidence.Likely, GraphQlTechnologyEvidenceSource.RuntimeResponse),
            (technology.Server.Technology, technology.Server.Confidence, technology.Server.Source));
        Assert.Equal((GraphQlTechnologies.StrawberryShake, GraphQlTechnologyConfidence.Confirmed, GraphQlTechnologyEvidenceSource.DeployedFrontendArtifact),
            (technology.Client.Technology, technology.Client.Confidence, technology.Client.Source));
        Assert.Equal(GraphQlSchemaSource.ConfiguredArtifact, target.GraphQlCompatibility!.SchemaSource);   // runtime refused → SDL fallback
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("Hot Chocolate") || f.Title.Contains("Strawberry"));
    }

    [Fact]
    public async Task Technology_NeverChangesCompatibility_OnlyTheConfirmedClientWording()
    {
        var withClient = await Engine(Site(strawberry: true), new Store()).RunAsync(Request());
        var without = await Engine(Site(strawberry: false), new Store()).RunAsync(Request());

        static string Shape(ApiReviewReport r) => string.Join("|", r.Targets.Single().GraphQlCompatibility!.Operations.Select(o => $"{o.OperationName}:{o.Status}:{string.Join(",", o.Issues.Select(i => i.Code))}"));
        Assert.Equal(Shape(without), Shape(withClient));
        Assert.Equal(without.Findings.Select(f => (f.RuleId, f.Severity)), withClient.Findings.Select(f => (f.RuleId, f.Severity)));

        var confirmed = withClient.Findings.Single(f => f.RuleId == GraphQlOperationCompatibility.RuleId && f.Endpoint == "Query Brutt");
        var generic = without.Findings.Single(f => f.RuleId == GraphQlOperationCompatibility.RuleId && f.Endpoint == "Query Brutt");
        Assert.Contains("regenerate the Strawberry Shake client", confirmed.Recommendation);
        Assert.DoesNotContain("Strawberry", generic.Recommendation);
        Assert.Contains("Regenerate generated client code if applicable", generic.Recommendation);
    }

    [Fact]
    public async Task ObservedMutation_IsContractValidated_NeverSent()
    {
        var site = Site(strawberry: true);
        var target = (await Engine(site, new Store()).RunAsync(Request())).Targets.Single();
        var mutation = target.Operations.Single(o => o.OperationType == GraphQlOperationType.Mutation);
        Assert.False(mutation.Executed);
        Assert.DoesNotContain(site.Bodies, b => b.Contains("renameRole"));
        Assert.DoesNotContain(target.GraphQlCompatibility!.Operations, o => o.Display.Contains("__typename"));
        Assert.Equal(3, target.GraphQlCompatibility.Observed);
    }

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new() { Method = identity.Method };
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
