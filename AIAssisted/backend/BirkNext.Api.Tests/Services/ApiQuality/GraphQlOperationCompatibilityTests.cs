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
/// GraphQL client/server compatibility: observed frontend operation documents (Endpoint Discovery, normalized and literal-redacted)
/// validated against the best trusted schema of their own endpoint. Compatibility is kept apart from schema drift, runtime execution,
/// authentication and authorization; "could not assess" is Not assessed, never Failed and never "0 compatible".
/// </summary>
public sealed class GraphQlOperationCompatibilityTests
{
    private const string Endpoint = "https://api-dev.example.test/api/graphql";

    private const string Sdl = """
        scalar UUID
        type Query { roles: [Role!]! user(id: UUID!): User search(term: String!, kind: Kind = ALL, filter: RoleFilter): [Role!]! node: Node }
        type Mutation { renameRole(id: ID!, name: String!): Role }
        type Subscription { roleChanged: Role }
        type Role implements Node { id: ID! name: String! kind: Kind }
        type User implements Node { id: ID! name: String! roles: [Role!]! }
        interface Node { id: ID! }
        enum Kind { ALL GENERAL CHILD }
        input RoleFilter { kind: Kind! nameContains: String }
        """;

    private static GraphQlNormalizedContract Schema(string sdl = Sdl) => GraphQlSdlSchema.FromSdl(sdl, out var error) ?? throw new InvalidOperationException(error);

    private static ApiReviewOperation Op(string document, string? name = null, GraphQlOperationType type = GraphQlOperationType.Query, int count = 1, bool historical = false)
    {
        var normalized = GraphQlDocumentNormalizer.Normalize(document)!;
        return new ApiReviewOperation { Method = "POST", Path = "/api/graphql", OperationType = type, OperationName = name, Document = normalized.Document, DocumentHash = normalized.Hash, ObservedCount = count, Historical = historical };
    }

    private static GraphQlOperationCompatibilityResult Assess(ApiReviewOperation op, GraphQlNormalizedContract? schema = null) =>
        GraphQlOperationCompatibility.Assess(schema ?? Schema(), GraphQlSchemaSource.RuntimeIntrospection, "test", DateTimeOffset.UtcNow, [op], Endpoint).Operations.Single();

    private static void Incompatible(string document, string code, GraphQlOperationType type = GraphQlOperationType.Query)
    {
        var result = Assess(Op(document, type: type));
        Assert.Equal(GraphQlCompatibilityStatus.Incompatible, result.Status);
        Assert.Contains(result.Issues, i => i.Code == code);
    }

    // ── 53–57, basic rules ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompatibleOperation_IsCompatible() =>
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, Assess(Op("query GetRoles { roles { id name } }", "GetRoles")).Status);

    [Fact]
    public void RemovedField_IsIncompatible_WithTheFieldAndType()
    {
        var result = Assess(Op("query GetRoles { roles { id navn } }", "GetRoles"));
        Assert.Equal(GraphQlCompatibilityStatus.Incompatible, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("FIELD_NOT_FOUND", issue.Code);
        Assert.Equal("The field `navn` does not exist on the type `Role`.", issue.Message);
    }

    [Fact] public void MissingRequiredArgument_IsIncompatible() => Incompatible("query { user { id } }", "REQUIRED_ARGUMENT_MISSING");
    [Fact] public void UnknownArgument_IsIncompatible() => Incompatible("query { roles(first: 3) { id } }", "ARGUMENT_NOT_FOUND");
    [Fact] public void VariableTypeMismatch_IsIncompatible() => Incompatible("query GetUser($id: String!) { user(id: $id) { name } }", "TYPE_MISMATCH");
    [Fact] public void NullableVariableForNonNullArgument_IsIncompatible() => Incompatible("query GetUser($id: UUID) { user(id: $id) { name } }", "TYPE_MISMATCH");
    [Fact] public void UndefinedVariable_IsIncompatible() => Incompatible("query { user(id: $id) { name } }", "VARIABLE_NOT_DEFINED");
    [Fact] public void UnknownVariableType_IsIncompatible() => Incompatible("query Q($id: Guid!) { user(id: $id) { name } }", "UNKNOWN_TYPE");
    [Fact] public void LeafWithSelection_IsIncompatible() => Incompatible("query { roles { name { x } } }", "SELECTION_NOT_ALLOWED");
    [Fact] public void CompositeWithoutSelection_IsIncompatible() => Incompatible("query { roles }", "SELECTION_SET_REQUIRED");
    [Fact] public void InvalidEnumLiteral_IsIncompatible() => Incompatible("query { search(term: \"x\", kind: SPECIAL) { id } }", "ENUM_VALUE_INVALID");
    [Fact] public void MissingRequiredInputField_IsIncompatible() => Incompatible("query { search(term: \"x\", filter: { nameContains: \"a\" }) { id } }", "REQUIRED_INPUT_FIELD_MISSING");
    [Fact] public void UnsupportedRootOperation_IsIncompatible() =>
        Assert.Contains(Assess(Op("subscription { roleChanged { id } }", type: GraphQlOperationType.Subscription), Schema(Sdl.Replace("type Subscription { roleChanged: Role }", ""))).Issues,
            i => i.Code == "OPERATION_TYPE_NOT_SUPPORTED");

    [Fact]
    public void ValidVariablesDefaultsAliasesAndDirectives_AreCompatible() =>
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, Assess(Op(
            "query S($t: String!, $k: Kind = GENERAL, $f: RoleFilter, $skip: Boolean!) { hits: search(term: $t, kind: $k, filter: $f) { id title: name @skip(if: $skip) __typename } }")).Status);

    // ── 57. Fragments ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FragmentsAreValidatedAsPartOfTheDocument()
    {
        Assert.Equal(GraphQlCompatibilityStatus.Compatible,
            Assess(Op("query GetUser($id: UUID!) { user(id: $id) { ...UserSummary } } fragment UserSummary on User { id name roles { id } }")).Status);
        // A field removed inside the fragment is caught, not just in the operation body.
        var broken = Assess(Op("query GetUser($id: UUID!) { user(id: $id) { ...UserSummary } } fragment UserSummary on User { id displayName }"));
        Assert.Contains(broken.Issues, i => i.Code == "FIELD_NOT_FOUND" && i.Message.Contains("displayName"));
        Incompatible("query { roles { ...Missing } }", "UNKNOWN_FRAGMENT");
        Incompatible("query { roles { ...OnUser } } fragment OnUser on User { id }", "INVALID_FRAGMENT_TYPE");
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, Assess(Op("query { node { id ... on Role { name } ... on User { roles { id } } } }")).Status);
    }

    // ── 58. Mutation: validated, never executed ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mutation_IsContractValidated() =>
        Assert.Equal(GraphQlCompatibilityStatus.Compatible,
            Assess(Op("mutation Rename($id: ID!, $n: String!) { renameRole(id: $id, name: $n) { id name } }", "Rename", GraphQlOperationType.Mutation)).Status);

    [Fact]
    public void Subscription_IsValidatedStructurally() =>
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, Assess(Op("subscription { roleChanged { id } }", type: GraphQlOperationType.Subscription)).Status);

    // ── 59. Schema unavailable ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SchemaUnavailable_AllObservedAreNotAssessed_CountStays()
    {
        var ops = Enumerable.Range(1, 6).Select(i => Op($"query Op{i} {{ roles {{ id }} }}", $"Op{i}")).ToList();
        var compatibility = GraphQlOperationCompatibility.Assess(null, GraphQlSchemaSource.RuntimeIntrospection, "x", DateTimeOffset.UtcNow, ops, Endpoint);
        Assert.Equal(6, compatibility.Observed);
        Assert.Equal(0, compatibility.Assessed);
        Assert.Equal(0, compatibility.Compatible);
        Assert.Equal(0, compatibility.Incompatible);
        Assert.Equal(6, compatibility.NotAssessed);
        Assert.Equal(GraphQlSchemaSource.None, compatibility.SchemaSource);
        Assert.Equal(GraphQlOperationCompatibility.NoSchemaReason, compatibility.NotAssessedReason);
        Assert.Empty(GraphQlOperationCompatibility.Findings(compatibility, "gql-1"));
    }

    [Fact]
    public void MissingOrMalformedDocument_IsNotAssessed_AndDoesNotStopTheOthers()
    {
        var ops = new List<ApiReviewOperation>
        {
            new() { OperationType = GraphQlOperationType.Query, OperationName = "NameOnly" },
            new() { OperationType = GraphQlOperationType.Query, OperationName = "Broken", Document = "query Broken { roles {", DocumentHash = "x" },
            Op("query Good { roles { id } }", "Good"),
        };
        var compatibility = GraphQlOperationCompatibility.Assess(Schema(), GraphQlSchemaSource.RuntimeIntrospection, "x", DateTimeOffset.UtcNow, ops, Endpoint);
        Assert.Equal(GraphQlOperationCompatibility.NoDocumentReason, compatibility.Operations[0].NotAssessedReason);
        Assert.Equal(GraphQlOperationCompatibility.UnparseableReason, compatibility.Operations[1].NotAssessedReason);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, compatibility.Operations[2].Status);
    }

    // ── Findings: one per operation, severity from the contract policy ──────────────────────────────────────────────

    [Fact]
    public void IncompatibleOperation_IsOneFindingWithEveryViolation_HistoricalIsLow()
    {
        var compatibility = GraphQlOperationCompatibility.Assess(Schema(), GraphQlSchemaSource.RuntimeIntrospection, "x", DateTimeOffset.UtcNow,
            [Op("query GetRoles { roles { id navn displayName } }", "GetRoles", count: 12), Op("query Old { roles { legacy } }", "Old", historical: true)], Endpoint);
        var findings = GraphQlOperationCompatibility.Findings(compatibility, "gql-1");
        var current = Assert.Single(findings, f => f.Endpoint == "Query GetRoles");
        Assert.Equal(GraphQlOperationCompatibility.RuleId, current.RuleId);
        Assert.Equal(ApiReviewSeverity.High, current.Severity);
        Assert.Equal(ApiReviewFindingType.Contract, current.Type);
        Assert.Equal(2, current.Evidence.Count);
        Assert.Equal("Observed GraphQL operation is incompatible with current schema", current.Title);
        Assert.Equal(ApiReviewSeverity.Low, Assert.Single(findings, f => f.Endpoint == "Query Old").Severity);
        Assert.True(compatibility.Operations.Single(o => o.OperationName == "Old").Historical);
    }

    // ── 60. SDL artifact ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sdl_BuildsRootsInterfacesUnionsEnumsInputsAndExtensions()
    {
        var schema = Schema(Sdl + "\nextend type Role { code: String }\nunion SearchResult = Role | User\nschema { query: Query mutation: Mutation }");
        Assert.Equal("Query", schema.QueryTypeName);
        Assert.Equal("Mutation", schema.MutationTypeName);
        Assert.Contains(schema.Types.Single(t => t.Name == "Role").Fields, f => f.Name == "code");
        Assert.Equal(["Role", "User"], schema.Types.Single(t => t.Name == "Node").PossibleTypes!.OrderBy(x => x));
        Assert.Equal(["Role", "User"], schema.Types.Single(t => t.Name == "SearchResult").PossibleTypes);
        Assert.Null(GraphQlSdlSchema.FromSdl("type Foo {", out var error));
        Assert.NotNull(error);
    }

    // ── Normalization: identity and redaction ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normalizer_RedactsLiterals_KeepsStructure_IgnoresFormatting()
    {
        var a = GraphQlDocumentNormalizer.Normalize("query  Q { search(term: \"Ola Nordmann 01019912345\", kind: CHILD) { id } # note\n }")!;
        var b = GraphQlDocumentNormalizer.Normalize("query Q{search(term:\"someone else\",kind:CHILD){id}}")!;
        Assert.Equal(a.Hash, b.Hash);
        Assert.DoesNotContain("Nordmann", a.Document);
        Assert.DoesNotContain("0101", a.Document);
        Assert.Contains("kind:CHILD", a.Document);
        Assert.Equal("query Q{search(term:\"\"kind:CHILD){id}}", a.Document);   // commas are insignificant tokens in GraphQL
        Assert.NotEqual(a.Hash, GraphQlDocumentNormalizer.Normalize("query Q{search(term:\"x\"){id name}}")!.Hash);
        Assert.Null(GraphQlDocumentNormalizer.Normalize("query {"));
    }

    [Fact]
    public void Capture_KeepsTheRedactedDocument_NeverTheVariables()
    {
        var (type, name, document) = GraphQlBodyInspector.ClassifyWithDocument(
            "{\"operationName\":\"GetUser\",\"query\":\"query GetUser($id: UUID!) { user(id: $id) { name } }\",\"variables\":{\"id\":\"secret-user-id\"}}");
        Assert.Equal(GraphQlOperationType.Query, type);
        Assert.Equal("GetUser", name);
        Assert.Equal("query GetUser($id:UUID!){user(id:$id){name}}", document!.Document);
        Assert.DoesNotContain("secret", document.Document);
    }

    // ── 61–62. Dedupe and variants ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DocumentVariants_MergePerHash_AndStayBounded()
    {
        var at = DateTimeOffset.UtcNow;
        ObservedGraphQlDocument Doc(string hash, int count = 1) => new() { Hash = hash, Document = "q", Count = count, FirstObservedAt = at, LastObservedAt = at };
        var merged = new List<ObservedGraphQlDocument>();
        for (var i = 0; i < 20; i++) merged = ObservedGraphQlDocuments.Add(merged, [Doc("a")]);
        Assert.Equal(20, Assert.Single(merged).Count);
        merged = ObservedGraphQlDocuments.Add(merged, [Doc("b")]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(20, ObservedGraphQlDocuments.Union(merged, [Doc("a", 7)]).Single(d => d.Hash == "a").Count);
        for (var i = 0; i < 20; i++) merged = ObservedGraphQlDocuments.Add(merged, [Doc($"v{i}")]);
        Assert.Equal(ObservedGraphQlDocuments.MaxVariants, merged.Count);
    }

    [Fact]
    public void SameNameDifferentDocuments_AreTwoResults()
    {
        var compatibility = GraphQlOperationCompatibility.Assess(Schema(), GraphQlSchemaSource.RuntimeIntrospection, "x", DateTimeOffset.UtcNow,
            [Op("query GetUser($id: UUID!) { user(id: $id) { name } }", "GetUser"), Op("query GetUser($id: UUID!) { user(id: $id) { name nickname } }", "GetUser")], Endpoint);
        Assert.Equal(2, compatibility.Observed);
        Assert.NotEqual(compatibility.Operations[0].OperationId, compatibility.Operations[1].OperationId);
        Assert.Equal(1, compatibility.Compatible);
        Assert.Equal(1, compatibility.Incompatible);
    }

    // ── Engine: schema source, cross-endpoint, drift vs compatibility, auth, safe probe, no mutation executed ────────

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        public readonly List<HttpRequestMessage> Requests = [];
        public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body is not null) Bodies.Add(body);
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Typename = "{\"data\":{\"__typename\":\"Query\"}}";
    private const string IntrospectionRejected = "{\"errors\":[{\"message\":\"Introspection is not allowed for the current request.\"}]}";

    private static ApiReviewEngine Engine(Fixture fixture) =>
        new(new HttpClient(fixture), new NoGateway(), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), NullLogger<ApiReviewEngine>.Instance);

    private static ApiReviewTarget Target(string path, string? contractSource, params ApiReviewOperation[] ops) => new()
    {
        TargetId = "gql" + path, EnvironmentId = "dev", ApiType = ApiReviewTargetType.GraphQl, Host = "api-dev.example.test", BasePath = path, ServiceName = "GraphQL",
        Source = ApiReviewTargetSource.DiscoveredTraffic, Confidence = ObservedEndpointConfidence.Verified, Selected = true, ContractSource = contractSource,
        Operations = ops.Select(o => o with { Path = path }).ToList(),
    };

    private static ApiReviewRunRequest Request(params ApiReviewTarget[] targets) => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "M2LB DEV", EnvironmentType = "Development", AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManagedEdgeCdp },
        Identity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.ManagedEdgeCdp, "dev", null), Targets = targets.ToList(),
        Policy = new ApiReviewPolicy { ErrorHandlingProbes = false },
    };

    [Fact]
    public async Task IntrospectionRejected_NoArtifact_SixObservedNotAssessed_NoFailure()
    {
        var fixture = new Fixture { Respond = (_, body) => Json(body?.Contains("__schema") == true ? IntrospectionRejected : Typename) };
        var ops = Enumerable.Range(1, 6).Select(i => Op($"query HentOp{i} {{ roles {{ id }} }}", $"HentOp{i}")).ToArray();
        var report = await Engine(fixture).RunAsync(Request(Target("/api/graphql", null, ops)));

        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiReviewTargetStatus.Completed, target.Status);
        var compatibility = target.GraphQlCompatibility!;
        Assert.Equal(6, compatibility.Observed);
        Assert.Equal(6, compatibility.NotAssessed);
        Assert.Equal(GraphQlSchemaSource.None, compatibility.SchemaSource);
        Assert.Contains(target.Checks, c => c.CheckId == "gql-compatibility" && c.Result == ApiReviewCheckResult.NotTested);
        Assert.Equal(6, report.Coverage.GraphQlOperationsObserved);
        // The safe probe is its own row and is never counted as an observed business operation.
        Assert.Single(target.Operations, o => o.Display == "query { __typename }" && o.Executed);
        Assert.Equal(6, target.Operations.Count(o => o.Compatibility == GraphQlCompatibilityStatus.NotAssessed && !o.Executed));
        Assert.DoesNotContain(compatibility.Operations, o => o.Display.Contains("__typename"));
        Assert.DoesNotContain(report.Findings, f => f.RuleId == GraphQlOperationCompatibility.RuleId);
    }

    [Fact]
    public async Task IntrospectionRejected_ConfiguredSdl_IsTheSchemaSource()
    {
        var fixture = new Fixture { Respond = (req, body) =>
            req.RequestUri!.AbsolutePath == "/schema.graphql" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sdl) }
            : Json(body?.Contains("__schema") == true ? IntrospectionRejected : Typename) };
        var report = await Engine(fixture).RunAsync(Request(Target("/api/graphql", "https://api-dev.example.test/schema.graphql",
            Op("query GetRoles { roles { id name } }", "GetRoles"), Op("query Broken { roles { navn } }", "Broken"))));

        var compatibility = report.Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlSchemaSource.ConfiguredArtifact, compatibility.SchemaSource);
        Assert.Equal(1, compatibility.Compatible);
        Assert.Equal(1, compatibility.Incompatible);
        Assert.Contains(report.Findings, f => f.RuleId == GraphQlOperationCompatibility.RuleId && f.Endpoint == "Query Broken");
    }

    [Fact]
    public async Task EachEndpointValidatesAgainstItsOwnSchema()
    {
        const string sdlA = "type Query { roles: [Role!]! } type Role { id: ID! }";
        const string sdlB = "type Query { children: [Child!]! } type Child { id: ID! }";
        var fixture = new Fixture { Respond = (req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/a.graphql" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sdlA) },
            "/b.graphql" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sdlB) },
            _ => Json(body?.Contains("__schema") == true ? IntrospectionRejected : Typename),
        } };
        var report = await Engine(fixture).RunAsync(Request(
            Target("/a/graphql", "https://api-dev.example.test/a.graphql", Op("query { roles { id } }", "A")),
            Target("/b/graphql", "https://api-dev.example.test/b.graphql", Op("query { children { id } }", "B"))));
        Assert.All(report.Targets, t => Assert.Equal(GraphQlCompatibilityStatus.Compatible, t.GraphQlCompatibility!.Operations.Single().Status));
    }

    [Fact]
    public async Task ObservedMutation_IsValidated_NeverSent()
    {
        var fixture = new Fixture { Respond = (req, body) =>
            req.RequestUri!.AbsolutePath == "/schema.graphql" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sdl) }
            : Json(body?.Contains("__schema") == true ? IntrospectionRejected : Typename) };
        var report = await Engine(fixture).RunAsync(Request(Target("/api/graphql", "https://api-dev.example.test/schema.graphql",
            Op("mutation Rename($id: ID!, $n: String!) { renameRole(id: $id, name: $n) { id } }", "Rename", GraphQlOperationType.Mutation))));

        var row = report.Targets.Single().Operations.Single(o => o.OperationType == GraphQlOperationType.Mutation);
        Assert.False(row.Executed);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, row.Compatibility);
        Assert.StartsWith("Contract validation only — mutation was not executed.", row.Note);
        Assert.DoesNotContain(fixture.Bodies, b => b.Contains("renameRole") || System.Text.RegularExpressions.Regex.IsMatch(b, @"""query"":""\s*mutation"));
    }

    [Fact]
    public async Task RuntimeAuthorizationFailure_DoesNotMakeACompatibleOperationIncompatible()
    {
        var fixture = new Fixture { Respond = (req, body) =>
            req.RequestUri!.AbsolutePath == "/schema.graphql" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sdl) }
            : Json(body?.Contains("__schema") == true ? IntrospectionRejected : "{\"errors\":[{\"message\":\"Not authorized\"}]}", HttpStatusCode.Forbidden) };
        var report = await Engine(fixture).RunAsync(Request(Target("/api/graphql", "https://api-dev.example.test/schema.graphql",
            Op("query GetRoles { roles { id } }", "GetRoles") with { LastStatus = 403 })));
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, report.Targets.Single().GraphQlCompatibility!.Operations.Single().Status);
    }

    [Fact]
    public async Task DriftAndCompatibility_AreSeparateDimensions()
    {
        // A: the schema changed (hash differs from the baseline) but the observed operation still validates.
        var schema = IntrospectionFromSdl("type Query { roles: [Role!]! } type Role { id: ID! name: String! }");
        var fixture = new Fixture { Respond = (_, body) => Json(body?.Contains("__schema") == true ? schema : Typename) };
        var request = Request(Target("/api/graphql", null, Op("query { roles { id } }", "GetRoles"))) with
        {
            Baselines = [new ApiReviewBaseline { TargetId = "gql/api/graphql", RecordedAt = DateTimeOffset.UtcNow.AddDays(-1), GraphQlRootFields = ["roles"], GraphQlSchemaHash = "previous" }],
        };
        var a = await Engine(fixture).RunAsync(request);
        Assert.Contains(a.Findings, f => f.Type == ApiReviewFindingType.Drift);
        Assert.Equal(GraphQlCompatibilityStatus.Compatible, a.Targets.Single().GraphQlCompatibility!.Operations.Single().Status);

        // B: no baseline (drift not assessed) but the observed operation is invalid.
        var b = await Engine(fixture).RunAsync(Request(Target("/api/graphql", null, Op("query { roles { navn } }", "GetRoles"))));
        Assert.DoesNotContain(b.Targets.Single().Checks, c => c.CheckId == "gql-drift");
        Assert.Equal(GraphQlCompatibilityStatus.Incompatible, b.Targets.Single().GraphQlCompatibility!.Operations.Single().Status);
    }

    [Fact]
    public async Task RemovedRootField_NamesTheObservedOperationsItAffects()
    {
        var schema = IntrospectionFromSdl("type Query { roles: [Role!]! } type Role { id: ID! }");
        var fixture = new Fixture { Respond = (_, body) => Json(body?.Contains("__schema") == true ? schema : Typename) };
        var request = Request(Target("/api/graphql", null, Op("query GetUser { user(id: \"1\") { id } }", "GetUser"))) with
        {
            Baselines = [new ApiReviewBaseline { TargetId = "gql/api/graphql", RecordedAt = DateTimeOffset.UtcNow.AddDays(-1), GraphQlRootFields = ["roles", "user"], GraphQlSchemaHash = "previous" }],
        };
        var report = await Engine(fixture).RunAsync(request);
        var compatibility = report.Targets.Single().GraphQlCompatibility!;
        Assert.Equal(GraphQlCompatibilityStatus.Incompatible, compatibility.Operations.Single().Status);
        Assert.Contains("Removed root field `user` — used by Query GetUser", compatibility.SchemaChangeImpact);
    }

    /// <summary>An introspection result for an SDL, so engine tests exercise the runtime-introspection path.</summary>
    private static string IntrospectionFromSdl(string sdl)
    {
        var contract = Schema(sdl);
        object TypeRef(GraphQlTypeRef? t) => t is null ? null! : new { kind = t.Kind == "NAMED" ? KindOf(t.Name!) : t.Kind, name = t.Kind == "NAMED" ? t.Name : null, ofType = t.OfType is null ? null : TypeRef(t.OfType) };
        string KindOf(string name) => contract.Types.FirstOrDefault(x => x.Name == name)?.Kind ?? "SCALAR";
        var types = contract.Types.Select(t => new
        {
            kind = t.Kind, name = t.Name, description = (string?)null,
            fields = t.Kind is "OBJECT" or "INTERFACE" ? t.Fields.Select(f => new { name = f.Name, description = (string?)null, args = f.Arguments.Select(a => new { name = a.Name, description = (string?)null, type = TypeRef(a.Type), defaultValue = a.DefaultValue }).ToArray(), type = TypeRef(f.Type), isDeprecated = false, deprecationReason = (string?)null }).ToArray() : null,
            inputFields = (object?)null, interfaces = Array.Empty<object>(), enumValues = (object?)null, possibleTypes = (object?)null,
        }).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new { data = new { __schema = new { queryType = new { name = "Query" }, mutationType = (object?)null, subscriptionType = (object?)null, types, directives = Array.Empty<object>() } } });
    }

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new() { Method = identity.Method };
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no gateway");
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no gateway");
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no gateway");
    }
}
