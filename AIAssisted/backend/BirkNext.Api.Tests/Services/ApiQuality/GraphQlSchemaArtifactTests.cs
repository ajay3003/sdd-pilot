using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Trusted GraphQL schema artifacts: configured per (Target Environment, API target), validated on upload, used only as the fallback when
/// runtime introspection is unavailable, and snapshotted into each result so a historical run never reads the current artifact.
/// </summary>
public sealed class GraphQlSchemaArtifactTests
{
    private const string Sdl = """
        type Query { roles: [Role!]! user(id: ID!): User }
        type Role { id: ID! name: String! navn: String @deprecated(reason: "Use name.") kind: Kind }
        type User { id: ID! roles: [Role!]! }
        enum Kind { ALL GENERAL LEGACY @deprecated }
        """;
    private const string SdlWithoutName = "type Query { roles: [Role!]! } type Role { id: ID! }";

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static GraphQlSchemaArtifactService Store(AppDbContext db) => new(db, NullLogger<GraphQlSchemaArtifactService>.Instance);

    private static GraphQlSchemaArtifactUpload Upload(string content, string env = "dev", string target = "gql/api/graphql", string file = "m2lb-schema.graphql") =>
        new() { EnvironmentId = env, TargetId = target, TargetUrl = "https://api-dev.example.test/api/graphql", FileName = file, Content = content };

    // ── 41–43, validation ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidSdl_IsStoredWithHashAndTargetBinding()
    {
        await using var db = Db();
        var (artifact, error) = await Store(db).SaveAsync(Upload(Sdl));
        Assert.Null(error);
        Assert.NotNull(artifact);
        Assert.Equal(GraphQlSchemaArtifactValidation.Valid, artifact!.ValidationStatus);
        Assert.Equal(GraphQlSchemaArtifactService.Hash(Sdl), artifact.ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", artifact.ContentHash);
        Assert.Equal(12, artifact.ShortHash.Length);
        Assert.Equal(("dev", "gql/api/graphql"), (artifact.EnvironmentId, artifact.TargetId));
        Assert.Equal("Query", artifact.QueryType);
        Assert.True(artifact.TypeCount >= 4);
    }

    [Theory]
    [InlineData("type Query { roles: [Role!]! ", "Not valid GraphQL")]
    [InlineData("query GetRoles { roles { id } }", "operation document")]
    [InlineData("fragment F on Role { id }", "operation document")]
    [InlineData("type Query { a: Int } query X { a }", "mixes operations")]
    [InlineData("   ", "empty")]
    [InlineData("hello world, not a schema", "Not valid GraphQL")]
    [InlineData("type Role { id: ID! }", "no query root")]
    public void InvalidContent_IsRejectedWithAReason(string content, string reason)
    {
        var result = GraphQlSchemaArtifactService.Validate("schema.graphql", content);
        Assert.False(result.Valid);
        Assert.Null(result.Schema);
        Assert.Contains(reason, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema.graphql", true)]
    [InlineData("schema.graphqls", true)]
    [InlineData("SCHEMA.GQL", true)]
    [InlineData("schema.json", false)]
    [InlineData("schema.txt", false)]
    [InlineData(null, false)]
    public void OnlyGraphQlSchemaExtensionsAreAccepted(string? fileName, bool accepted) =>
        Assert.Equal(accepted, GraphQlSchemaArtifactService.Validate(fileName, Sdl).Valid);

    [Fact]
    public void BinaryAndOversizeContent_IsRejected()
    {
        Assert.Contains("binary", GraphQlSchemaArtifactService.Validate("s.graphql", "type Query { a: Int }\0\u0001").Reason);
        var huge = "# " + new string('x', GraphQlSchemaArtifactRules.MaxBytes) + "\ntype Query { a: Int }";
        Assert.Contains("limit", GraphQlSchemaArtifactService.Validate("s.graphql", huge).Reason);
    }

    [Fact]
    public async Task InvalidUpload_IsNeverStored_AndAClientPathIsReducedToTheFileName()
    {
        await using var db = Db();
        var store = Store(db);
        var (rejected, error) = await store.SaveAsync(Upload("query X { roles { id } }"));
        Assert.Null(rejected);
        Assert.Contains("operation document", error);
        Assert.Empty(await store.ListAsync("dev"));

        var (saved, _) = await store.SaveAsync(Upload(Sdl, file: @"C:\Users\someone\repo\schema\m2lb.graphql"));
        Assert.Equal("m2lb.graphql", saved!.FileName);
    }

    [Fact]
    public async Task ListedMetadata_NeverCarriesTheSdlText()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(Sdl));
        var json = JsonSerializer.Serialize(await store.ListAsync("dev"));
        Assert.DoesNotContain("type Query", json);
        Assert.DoesNotContain("\"content\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── 44–50, source selection in the engine ─────────────────────────────────────────────────────────────────────

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body is not null) Bodies.Add(body);
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private const string Typename = "{\"data\":{\"__typename\":\"Query\"}}";
    private static Fixture IntrospectionRejected() => new() { Respond = (_, body) => body?.Contains("__schema") == true ? Json("{\"errors\":[{\"message\":\"Introspection is not allowed.\"}]}", HttpStatusCode.BadRequest) : Json(Typename) };

    private static ApiReviewEngine Engine(Fixture fixture, IGraphQlSchemaArtifactStore store) =>
        new(new HttpClient(fixture), new NoGateway(), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), NullLogger<ApiReviewEngine>.Instance, store);

    private static ApiReviewOperation Op(string document, string name, GraphQlDocumentOmission omission = GraphQlDocumentOmission.None)
    {
        var normalized = omission == GraphQlDocumentOmission.None ? GraphQlDocumentNormalizer.Normalize(document)! : null;
        return new ApiReviewOperation { Method = "POST", Path = "/api/graphql", OperationType = GraphQlOperationType.Query, OperationName = name, Document = normalized?.Document, DocumentHash = normalized?.Hash ?? "persisted-abc", DocumentOmission = omission, ObservedCount = 1 };
    }

    private static ApiReviewTarget Target(string path, params ApiReviewOperation[] ops) => new()
    {
        TargetId = "gql" + path, EnvironmentId = "dev", ApiType = ApiReviewTargetType.GraphQl, Host = "api-dev.example.test", BasePath = path, ServiceName = "GraphQL",
        Source = ApiReviewTargetSource.DiscoveredTraffic, Confidence = ObservedEndpointConfidence.Verified, Selected = true, Operations = ops.Select(o => o with { Path = path }).ToList(),
    };

    private static ApiReviewRunRequest Request(string env, params ApiReviewTarget[] targets) => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = env, Name = "M2LB " + env.ToUpperInvariant(), EnvironmentType = "Development", AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManagedEdgeCdp },
        Identity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.ManagedEdgeCdp, env, null), Targets = targets.ToList(),
        Policy = new ApiReviewPolicy { ErrorHandlingProbes = false },
    };

    private static ApiReviewOperation[] SixM2lbOperations() =>
    [
        Op("query HentRoller { roles { id name } }", "HentRoller"),
        Op("query HentBruker($id: ID!) { user(id: $id) { id roles { id } } }", "HentBruker"),
        Op("query HentKind { roles { kind } }", "HentKind"),
        Op("query HentGammelt { roles { navn } }", "HentGammelt"),
        Op("query HentFjernet { roles { slettet } }", "HentFjernet"),
        Op("query Persisted { roles { id } }", "Persisted", GraphQlDocumentOmission.PersistedQueryHashOnly),
    ];

    [Fact]
    public async Task M2lbShape_IntrospectionRejected_ConfiguredSdlIsTheFallback()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(Sdl));
        var fixture = IntrospectionRejected();

        var report = await Engine(fixture, store).RunAsync(Request("dev", Target("/api/graphql", SixM2lbOperations())));
        var target = report.Targets.Single();
        var compatibility = target.GraphQlCompatibility!;

        Assert.Equal(GraphQlSchemaSource.ConfiguredArtifact, compatibility.SchemaSource);
        Assert.StartsWith("Configured SDL m2lb-schema.graphql", compatibility.SchemaSourceDetail);
        Assert.StartsWith("Rejected", compatibility.RuntimeSchemaOutcome);
        Assert.True(compatibility.ConfiguredArtifact!.UsedForCompatibility);
        Assert.Equal(GraphQlSchemaArtifactService.Hash(Sdl), compatibility.ConfiguredArtifact.ContentHash);
        Assert.Equal(6, compatibility.Observed);
        Assert.Equal(5, compatibility.Assessed);
        Assert.Equal(4, compatibility.Compatible);
        Assert.Equal(1, compatibility.Incompatible);
        Assert.Equal(1, compatibility.NotAssessed);
        Assert.Equal(GraphQlOperationCompatibility.PersistedQueryReason, compatibility.Operations.Single(o => o.OperationName == "Persisted").NotAssessedReason);
        // The safe probe stays outside the observed count and no observed operation is ever sent.
        Assert.DoesNotContain(compatibility.Operations, o => o.Display.Contains("__typename"));
        Assert.DoesNotContain(fixture.Bodies, b => b.Contains("HentRoller") || b.Contains("slettet"));
        Assert.Contains(report.Findings, f => f.RuleId == GraphQlOperationCompatibility.RuleId && f.Endpoint == "Query HentFjernet");
    }

    [Fact]
    public async Task DeprecatedUsage_IsCompatibleWithAnObservation_RemovedFieldIsIncompatible()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(Sdl));
        var report = await Engine(IntrospectionRejected(), store).RunAsync(Request("dev", Target("/api/graphql",
            Op("query HentGammelt { roles { navn kind } }", "HentGammelt"),
            Op("query MedEnum { roles(kind: LEGACY) { id } }", "MedEnum"),
            Op("query HentFjernet { roles { slettet } }", "HentFjernet"))));
        var operations = report.Targets.Single().GraphQlCompatibility!.Operations;

        var deprecated = operations.Single(o => o.OperationName == "HentGammelt");
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, deprecated.Status);
        Assert.Contains("`Role.navn` is deprecated — Use name.", deprecated.DeprecatedUsage);
        Assert.Equal(GraphQlCompatibilityStatus.Incompatible, operations.Single(o => o.OperationName == "HentFjernet").Status);
        Assert.DoesNotContain(report.Findings, f => f.RuleId == GraphQlOperationCompatibility.RuleId && f.Endpoint == "Query HentGammelt");
    }

    [Fact]
    public async Task RuntimeIntrospectionWins_ArtifactIsRetainedAsFallbackMetadata()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(SdlWithoutName));   // older artifact that lacks Role.name
        var runtime = IntrospectionFromSdl("type Query { roles: [Role!]! } type Role { id: ID! name: String! }");
        var fixture = new Fixture { Respond = (_, body) => Json(body?.Contains("__schema") == true ? runtime : Typename) };

        var compatibility = (await Engine(fixture, store).RunAsync(Request("dev", Target("/api/graphql", Op("query { roles { name } }", "Roller"))))).Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlSchemaSource.RuntimeIntrospection, compatibility.SchemaSource);
        Assert.Equal("Retrieved", compatibility.RuntimeSchemaOutcome);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, compatibility.Operations.Single().Status);
        Assert.False(compatibility.ConfiguredArtifact!.UsedForCompatibility);
    }

    [Fact]
    public async Task NoRuntimeSchemaAndNoArtifact_OperationsStayObserved_NotAssessed()
    {
        await using var db = Db();
        var compatibility = (await Engine(IntrospectionRejected(), Store(db)).RunAsync(Request("dev", Target("/api/graphql", SixM2lbOperations())))).Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlSchemaSource.None, compatibility.SchemaSource);
        Assert.Equal(6, compatibility.Observed);
        Assert.Equal(6, compatibility.NotAssessed);
        Assert.Null(compatibility.ConfiguredArtifact);
    }

    [Fact]
    public async Task InvalidStoredArtifact_IsNotUsed_AndIsTheNotAssessedReason()
    {
        await using var db = Db();
        var metadata = new GraphQlSchemaArtifact { Id = "a1", EnvironmentId = "dev", TargetId = "gql/api/graphql", FileName = "broken.graphql", ContentHash = "x" };
        db.GraphQlSchemaArtifacts.Add(new GraphQlSchemaArtifactRecord { EnvironmentId = "dev", TargetId = "gql/api/graphql", Id = "a1", ContentHash = "x", Content = "type Query {", DocumentJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();

        var report = await Engine(IntrospectionRejected(), Store(db)).RunAsync(Request("dev", Target("/api/graphql", Op("query { roles { id } }", "Roller"))));
        var compatibility = report.Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlSchemaSource.None, compatibility.SchemaSource);
        Assert.StartsWith("Configured schema artifact invalid", compatibility.Operations.Single().NotAssessedReason);
        Assert.NotNull(compatibility.ConfiguredArtifactProblem);
        Assert.False(compatibility.ConfiguredArtifact!.UsedForCompatibility);
    }

    [Fact]
    public async Task ReplacingTheArtifact_DoesNotReinterpretAnEarlierResult()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(Sdl));
        var engine = Engine(IntrospectionRejected(), store);
        var target = Target("/api/graphql", Op("query { roles { name } }", "Roller"));

        var first = (await engine.RunAsync(Request("dev", target))).Targets.Single().GraphQlCompatibility!;
        await store.SaveAsync(Upload(SdlWithoutName, file: "m2lb-schema-v2.graphql"));
        var second = (await engine.RunAsync(Request("dev", target))).Targets.Single().GraphQlCompatibility!;

        Assert.Equal(GraphQlSchemaArtifactService.Hash(Sdl), first.ConfiguredArtifact!.ContentHash);
        Assert.Equal("m2lb-schema.graphql", first.ConfiguredArtifact.FileName);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, first.Operations.Single().Status);
        Assert.Equal(GraphQlSchemaArtifactService.Hash(SdlWithoutName), second.ConfiguredArtifact!.ContentHash);
        Assert.Equal(GraphQlCompatibilityStatus.Incompatible, second.Operations.Single().Status);
        Assert.Equal(first.ConfiguredArtifact.ArtifactId, second.ConfiguredArtifact.ArtifactId);   // same binding, new version
    }

    [Fact]
    public async Task RemovingTheArtifact_MakesFutureRunsNotAssessed()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(Sdl));
        Assert.True(await store.DeleteAsync("dev", "gql/api/graphql"));
        var compatibility = (await Engine(IntrospectionRejected(), store).RunAsync(Request("dev", Target("/api/graphql", Op("query { roles { id } }", "Roller"))))).Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlSchemaSource.None, compatibility.SchemaSource);
        Assert.False(await store.DeleteAsync("dev", "gql/api/graphql"));
    }

    [Fact]
    public async Task EachTargetUsesOnlyItsOwnArtifact()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload("type Query { roles: [Role!]! } type Role { id: ID! }", target: "gql/a/graphql", file: "a.graphql"));
        await store.SaveAsync(Upload("type Query { children: [Child!]! } type Child { id: ID! }", target: "gql/b/graphql", file: "b.graphql"));
        var report = await Engine(IntrospectionRejected(), store).RunAsync(Request("dev",
            Target("/a/graphql", Op("query { roles { id } }", "A"), Op("query { children { id } }", "AskingForB")),
            Target("/b/graphql", Op("query { children { id } }", "B"))));

        var a = report.Targets.Single(t => t.Target.TargetId == "gql/a/graphql").GraphQlCompatibility!;
        Assert.Equal("a.graphql", a.ConfiguredArtifact!.FileName);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, a.Operations.Single(o => o.OperationName == "A").Status);
        Assert.Equal(GraphQlCompatibilityStatus.Incompatible, a.Operations.Single(o => o.OperationName == "AskingForB").Status);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, report.Targets.Single(t => t.Target.TargetId == "gql/b/graphql").GraphQlCompatibility!.Operations.Single().Status);
    }

    [Fact]
    public async Task ADevArtifactNeverAppliesToQa()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(Sdl, env: "dev"));
        var compatibility = (await Engine(IntrospectionRejected(), store).RunAsync(Request("qa", Target("/api/graphql", Op("query { roles { id } }", "Roller"))))).Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlSchemaSource.None, compatibility.SchemaSource);
        Assert.Null(compatibility.ConfiguredArtifact);
        Assert.Empty(await store.ListAsync("qa"));
    }

    // ── 51–52, documents that were observed but not kept ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(GraphQlDocumentOmission.PersistedQueryHashOnly, "persisted-query hash")]
    [InlineData(GraphQlDocumentOmission.ExceededRetentionLimit, "retained-query size limit")]
    public void OperationWithoutItsDocument_IsNotAssessedWithItsOwnReason_EvenWithASchema(GraphQlDocumentOmission omission, string reason)
    {
        var schema = GraphQlSdlSchema.FromSdl(Sdl, out _)!;
        var result = GraphQlOperationCompatibility.Assess(schema, GraphQlSchemaSource.ConfiguredArtifact, "test", DateTimeOffset.UtcNow, [Op("", "X", omission)], "https://api-dev.example.test/api/graphql");
        var operation = result.Operations.Single();
        Assert.Equal(GraphQlCompatibilityStatus.NotAssessed, operation.Status);
        Assert.Contains(reason, operation.NotAssessedReason);
        Assert.Equal(1, result.Observed);
    }

    [Fact]
    public void OversizeDocument_IsMarkedByTheNormalizer_NotSilentlyDropped()
    {
        var huge = "query Big { " + string.Join(" ", Enumerable.Range(0, 600).Select(i => $"alias{i}WithALongDescriptiveName: roles {{ id }}")) + " }";
        var outcome = GraphQlDocumentNormalizer.NormalizeWithOutcome(huge);
        Assert.Null(outcome.Document);
        Assert.Equal(GraphQlDocumentOmission.ExceededRetentionLimit, outcome.Omission);
        Assert.Matches("^[0-9a-f]{16}$", outcome.Hash);
        Assert.Equal(GraphQlDocumentOmission.None, GraphQlDocumentNormalizer.NormalizeWithOutcome("query { roles }").Omission);
        Assert.Equal(GraphQlDocumentOmission.None, GraphQlDocumentNormalizer.NormalizeWithOutcome("not graphql {{{").Omission);
    }

    [Fact]
    public void CapturedOversizeBody_KeepsAnOperationWithTheMarker_AndNoDocumentText()
    {
        var query = "query Big { " + string.Join(" ", Enumerable.Range(0, 600).Select(i => $"alias{i}WithALongDescriptiveName: roles {{ id }}")) + " }";
        var (type, name, document) = GraphQlBodyInspector.ClassifyWithDocument(JsonSerializer.Serialize(new { query, operationName = "Big" }));
        Assert.Equal(GraphQlOperationType.Query, type);
        Assert.Equal("Big", name);
        Assert.Equal(GraphQlDocumentOmission.ExceededRetentionLimit, document!.Omission);
        Assert.Equal("", document.Document);
        Assert.Null(GraphQlDocumentNormalizer.Normalize(query));   // the kept-document API is unchanged: nothing over the limit is kept
    }

    [Fact]
    public async Task BlockedTarget_StillAssessesAgainstTheConfiguredArtifact()
    {
        await using var db = Db();
        var store = Store(db);
        await store.SaveAsync(Upload(Sdl));
        var fixture = new Fixture { Respond = (_, _) => throw new HttpRequestException("unreachable") };
        var compatibility = (await Engine(fixture, store).RunAsync(Request("dev", Target("/api/graphql", Op("query { roles { id } }", "Roller"))))).Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlSchemaSource.ConfiguredArtifact, compatibility.SchemaSource);
        Assert.StartsWith("Not attempted", compatibility.RuntimeSchemaOutcome);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, compatibility.Operations.Single().Status);
    }

    private static string IntrospectionFromSdl(string sdl)
    {
        var contract = GraphQlSdlSchema.FromSdl(sdl, out var error) ?? throw new InvalidOperationException(error);
        object TypeRef(GraphQlTypeRef? t) => t is null ? null! : new { kind = t.Kind == "NAMED" ? KindOf(t.Name!) : t.Kind, name = t.Kind == "NAMED" ? t.Name : null, ofType = t.OfType is null ? null : TypeRef(t.OfType) };
        string KindOf(string name) => contract.Types.FirstOrDefault(x => x.Name == name)?.Kind ?? "SCALAR";
        var types = contract.Types.Select(t => new
        {
            kind = t.Kind, name = t.Name, description = (string?)null,
            fields = t.Kind is "OBJECT" or "INTERFACE" ? t.Fields.Select(f => new { name = f.Name, description = (string?)null, args = f.Arguments.Select(a => new { name = a.Name, description = (string?)null, type = TypeRef(a.Type), defaultValue = a.DefaultValue }).ToArray(), type = TypeRef(f.Type), isDeprecated = false, deprecationReason = (string?)null }).ToArray() : null,
            inputFields = (object?)null, interfaces = Array.Empty<object>(), enumValues = (object?)null, possibleTypes = (object?)null,
        }).ToList();
        return JsonSerializer.Serialize(new { data = new { __schema = new { queryType = new { name = "Query" }, mutationType = (object?)null, subscriptionType = (object?)null, types, directives = Array.Empty<object>() } } });
    }

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new() { Method = identity.Method };
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no gateway");
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no gateway");
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no gateway");
    }
}
