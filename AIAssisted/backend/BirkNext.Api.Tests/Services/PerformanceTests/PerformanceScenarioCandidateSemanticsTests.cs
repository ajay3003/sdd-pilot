using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Scenario candidate semantics: one requirement model for every source (a normalised {id} always needs Test Data), observed values as hidden
/// evidence, traffic classification and deterministic ranking, observed vs correlated counts, GraphQL evidence states (unknown vs not required),
/// proposed GraphQL definitions only from a captured document, and logical contract groups. Generic fixtures only.
/// </summary>
public sealed class PerformanceScenarioCandidateSemanticsTests
{
    private const string Target = "https://app.example.test";
    private static readonly DateTimeOffset Seen = new(2026, 10, 28, 13, 6, 0, TimeSpan.Zero);

    private static ObservedNetworkEndpoint Rest(string method, string path, int count = 13, int status = 200, bool auth = true) => new()
    {
        Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = "app.example.test", Port = 443, Method = method, Path = path, Count = count, LastStatus = status,
        AuthObserved = auth, LastObservedAt = Seen,
    };

    private static ObservedNetworkEndpoint Other(string path) => Rest("GET", path) with { Category = ObservedTrafficCategory.OtherHttp, AuthObserved = false };

    private static ObservedNetworkEndpoint GraphQl(string name, string? document, GraphQlOperationType type = GraphQlOperationType.Query) => new()
    {
        Category = ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "app.example.test", Port = 443, Method = "POST", Path = "/graphql", Count = 7, LastStatus = 200,
        AuthObserved = true, LastObservedAt = Seen, OperationName = name, OperationType = type,
        GraphQlDocuments = document is null ? [] : [new ObservedGraphQlDocument { Hash = "h1", Document = document, LastObservedAt = Seen }],
    };

    private static SourceContract Users(string id = "users", string file = "Users/autodoc/openapi.yaml", string? producer = "Users.Api", params string[] paths) => new()
    {
        Id = id, Type = SourceContractType.OpenApi, Name = "Users API", File = file, Producer = producer,
        Operations = [.. (paths.Length == 0 ? ["/users/{userId}/children"] : paths).Select(p => new ContractOperation($"GET {p}", "GET", p, []))],
    };

    private static ScenarioCatalog Build(IReadOnlyList<ObservedNetworkEndpoint> observed, IReadOnlyList<SourceContract>? contracts = null, bool authenticated = false) =>
        PerformanceScenarioEngine.Build(contracts ?? [], contracts is null ? null : Guid.NewGuid(), contracts is null ? null : "app.zip", Target, "Development", observed, [], null, authenticated);

    // BM: a normalised runtime placeholder is a path input that needs Test Data — never "no Test Data needed".
    [Fact]
    public void LiveRestParameter_NormalisedId_KeepsItsTestDataRequirement_WithAHiddenObservedValue()
    {
        var op = Build([Rest("GET", "/users/123456/children")]).Operations.Single();
        op.Path.Should().Be("/users/{id}/children");
        var need = op.Needs.Should().ContainSingle().Subject;
        need.Should().Match<ScenarioDataNeed>(n => n.Variable == "id" && n.In == "path" && n.Required && n.Source == ScenarioNeedSources.Observed && n.ObservedValueAvailable);
        op.TestDataState.Should().Be(ScenarioTestDataStates.Required);
        op.Data.Single().State.Should().Be(ScenarioDataMatchState.Missing);
        op.Reasons.Should().NotContain(r => r.Contains("no Test Data needed") || r.Contains("not required"));
        JsonSerializer.Serialize(op).Should().NotContain("123456", "the observed value is evidence, never part of a candidate");
        JsonSerializer.Serialize(PerformanceScenarioEngine.ToStep(op)).Should().NotContain("123456").And.Contain("/users/{id}/children");
    }

    // BN: declared + observed correlate as one operation with the declared parameter name.
    [Fact]
    public void DeclaredAndObserved_CorrelateAsOneOperation_WithTheCanonicalDeclaredParameter()
    {
        var catalog = Build([Rest("GET", "/users/123456/children")], [Users()]);
        catalog.Operations.Should().ContainSingle();
        var op = catalog.Operations.Single();
        op.Evidence.Should().Be(ScenarioEvidence.DeclaredAndObserved);
        op.Path.Should().Be("/users/{userId}/children");
        op.Needs.Single().Should().Match<ScenarioDataNeed>(n => n.Variable == "userId" && n.Source == ScenarioNeedSources.Declared && n.ObservedValueAvailable);
        op.TestDataState.Should().Be(ScenarioTestDataStates.Required);
    }

    [Fact]
    public void ObservedValues_AreOnlyReturnedByTheExplicitReviewHelper_AndHiddenInText()
    {
        var observed = new[] { Rest("GET", "/users/123456/children"), Rest("GET", "/users/777777/children", count: 2, status: 404) };
        var op = Build(observed).Operations.Single();
        var evidence = PerformanceScenarioEngine.ObservedValues(op, "id", observed, Target)!;
        evidence.Values.Should().BeEquivalentTo(["123456", "777777"]);
        evidence.Observations.Should().Be(15);
        evidence.SuccessfulObservations.Should().Be(13);
        evidence.AuthenticatedTraffic.Should().BeTrue();
        evidence.ToString().Should().Contain("value hidden").And.NotContain("123456");
    }

    // BT/BU/W/X/Y: technical and diagnostic traffic is classified, ranked last and kept.
    [Fact]
    public void TechnicalAndHealthTraffic_IsClassified_RankedLast_AndStillPresent()
    {
        var ops = Build([Other("/"), Other("/appsettings.json"), Other("/appsettings.Dev.json"), Rest("GET", "/health/live"), Rest("GET", "/health/ready"), Rest("GET", "/api/orders")]).Operations;
        ops.Select(o => o.Path).Should().StartWith(["/api/orders"], "application operations rank first");
        ops.Where(o => o.Path is "/" or "/appsettings.json" or "/appsettings.Dev.json").Should().HaveCount(3).And.OnlyContain(o => o.Traffic == ScenarioTrafficKind.TechnicalBackground && o.Tier == 6);
        var health = ops.Where(o => o.Path!.StartsWith("/health")).ToList();
        health.Should().HaveCount(2).And.OnlyContain(o => o.Traffic == ScenarioTrafficKind.DiagnosticSmoke && o.Runnable, "diagnostic endpoints stay explicitly addable");
        ops.Select(o => o.Tier).Should().BeInAscendingOrder();
        PerformanceScenarioEngine.Classify("/_framework/blazor.boot.json").Should().Be(ScenarioTrafficKind.TechnicalBackground);
        PerformanceScenarioEngine.Classify("/api/config").Should().Be(ScenarioTrafficKind.TechnicalBackground);
        PerformanceScenarioEngine.Classify("/api/autorisasjon/v1/brukere/{id}/barn").Should().Be(ScenarioTrafficKind.Application);
    }

    // BV/CE: Live Discovery without a snapshot; observed endpoints vs correlated operations are distinct counts.
    [Fact]
    public void LiveDiscovery_WorksWithoutASnapshot_AndCountsAreDistinct()
    {
        var observed = new[] { Rest("GET", "/users/1/children"), Rest("GET", "/users/2/children"), Rest("GET", "/api/orders") };
        var observedOnly = Build(observed);
        observedOnly.Operations.Should().OnlyContain(o => o.Evidence == ScenarioEvidence.ObservedOnly);
        observedOnly.ObservedEndpoints.Should().Be(2, "two observed paths share one template");
        observedOnly.CorrelatedOperations.Should().Be(0);

        var correlated = Build(observed, [Users(), Users("users-copy", "frontend/users-openapi.yaml", null)]);
        correlated.ObservedEndpoints.Should().Be(2);
        correlated.CorrelatedOperations.Should().BeGreaterThan(0);
    }

    // BX: only an operation name — not runnable, Test Data unknown, document not captured.
    [Fact]
    public void GraphQl_OperationNameOnly_IsNotRunnable_WithUnknownTestData()
    {
        var op = Build([GraphQl("HentBarn", null)]).Operations.Single();
        op.Runnable.Should().BeFalse();
        op.ProposedDefinition.Should().BeFalse();
        op.NotRunnableReason.Should().Be("The operation name was observed, but the GraphQL query document was not retained. BirkNext cannot construct an executable test safely.");
        op.TestDataState.Should().Be(ScenarioTestDataStates.Unknown);
        op.GraphQl!.DocumentState.Should().Be(ScenarioGraphQlDocumentStates.NotCaptured);
        op.Reasons.Should().NotContain(r => r.Contains("no Test Data needed")).And.Contain(r => r.StartsWith("Test Data unknown"));
        PerformanceScenarioEngine.ProposedGraphQlStep(op).Should().BeNull("an operation is never reconstructed without its document");
    }

    // BY/BZ: a captured document is a proposed definition; its variables become Test Data requirements; nothing secret is copied.
    [Fact]
    public void GraphQl_CapturedDocument_ProposesADefinition_WithVariablesAsRequirements()
    {
        var op = Build([GraphQl("HentBarn", "query HentBarn($id: ID!, $limit: Int) { barn(id: $id, limit: $limit) { navn } }")]).Operations.Single();
        op.ProposedDefinition.Should().BeTrue();
        op.Level.Should().Be(ScenarioRecommendationLevel.ReviewRequired);
        op.Runnable.Should().BeFalse("it is created as its own definition after review, never added directly");
        op.Needs.Select(n => (n.Variable, n.In, n.Required, n.Source)).Should().Equal(("id", "variable", true, ScenarioNeedSources.CapturedQuery), ("limit", "variable", false, ScenarioNeedSources.CapturedQuery));
        op.TestDataState.Should().Be(ScenarioTestDataStates.Required);
        var step = PerformanceScenarioEngine.ProposedGraphQlStep(op)!;
        step.GraphQlQuery.Should().StartWith("query HentBarn($id: ID!");
        step.GraphQlVariablesJson.Should().Be("""{"id":"{id}","limit":"{limit}"}""");
        JsonSerializer.Serialize(step).Should().NotContainAny("Bearer", "Authorization", "Cookie");
    }

    // CA: only a document proving zero variables gives "Not required".
    [Fact]
    public void GraphQl_DocumentWithoutVariables_IsTheOnlyNotRequiredCase()
    {
        var op = Build([GraphQl("Versjon", "query Versjon { versjon }")]).Operations.Single();
        op.TestDataState.Should().Be(ScenarioTestDataStates.NotRequired);
        op.Needs.Should().BeEmpty();
        Build([GraphQl("Slett", "mutation Slett($id: ID!) { slett(id: $id) }", GraphQlOperationType.Mutation)]).Operations.Single()
            .Should().Match<ScenarioOperation>(o => !o.Runnable && !o.ProposedDefinition && o.NotRunnableReason!.Contains("queries only"));
    }

    [Fact]
    public void GraphQlVariables_AreReadFromTheOperationHeaderOnly()
    {
        PerformanceScenarioEngine.GraphQlVariables("query X($a: ID!, $b: [String!]) { f(a: $a) }").Should().Equal(("a", "ID!"), ("b", "[String!]"));
        PerformanceScenarioEngine.GraphQlVariables("{ f(a: 1) }").Should().BeEmpty();
    }

    // AC/AD/AE: logical contracts with canonical evidence; disagreeing variants need a decision.
    [Fact]
    public void ContractGroups_PreferTheCanonicalFile_AndNeverMergeDisagreeingVariants()
    {
        var canonical = Users();
        var copy = Users("users-snapshot", "Users/schema.snapshot.yaml", null);
        var groups = PerformanceScenarioEngine.ContractGroups([copy, canonical]);
        groups.Should().ContainSingle();
        groups[0].Canonical.File.Should().Be("Users/autodoc/openapi.yaml");
        groups[0].State.Should().Be(ScenarioContractGroup.Consistent);

        var drifted = Users("users-frontend", "frontend/users.yaml", null, "/users/{userId}/children", "/users/{userId}");
        PerformanceScenarioEngine.ContractGroups([canonical, drifted]).Single().State.Should().Be(ScenarioContractGroup.NeedsDecision);
        PerformanceScenarioEngine.ContractGroups([canonical, Users("orders", "Orders/openapi.yaml", "Orders.Api", "/orders")]).Should().HaveCount(2, "different APIs stay separate");
    }
}
