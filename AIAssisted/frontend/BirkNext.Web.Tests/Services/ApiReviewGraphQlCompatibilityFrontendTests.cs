using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The frontend half of GraphQL client/server compatibility: Endpoint Discovery keeps each observed document variant (merged per
/// hash, bounded, trimmed at a refresh), the resolver turns variants into operations (37 observations of one document are one
/// operation; one name with two documents is two), retained history stays Historical, and the presentation never says
/// "0 compatible" for something it could not assess.
/// </summary>
public sealed class ApiReviewGraphQlCompatibilityFrontendTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static ObservedGraphQlDocument Doc(string hash, int count, DateTimeOffset? at = null) =>
        new() { Hash = hash, Document = $"query GetUser{{user(id:\"\"){{{hash}}}}}", Count = count, FirstObservedAt = at ?? T0, LastObservedAt = at ?? T0 };

    private static ObservedNetworkEndpoint Gql(string name, int count, params ObservedGraphQlDocument[] documents) => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "api-dev.bufetat.no", Port = 443,
        Path = "/api/graphql", Method = "POST", AuthObserved = true, LastStatus = 200, Count = count, FirstObservedAt = T0, LastObservedAt = T0.AddMinutes(1),
        OperationType = GraphQlOperationType.Query, OperationName = name, Confidence = ObservedEndpointConfidence.Verified, PageOrigin = Origin, PagePath = "/roller",
        GraphQlDocuments = documents.ToList(),
    };

    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/" };
        return new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = Origin + "/", ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "FP") };
    }

    private static EndpointDiscoverySnapshot Discovery(params ObservedNetworkEndpoint[] endpoints)
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, endpoints, T0);
        return snapshot;
    }

    private static List<ApiReviewOperation> Operations(EndpointDiscoverySnapshot snapshot) =>
        ApiReviewTargetResolver.Resolve(Context(), snapshot).Single(t => t.ApiType == ApiReviewTargetType.GraphQl).Operations;

    // ── Dedupe and variants ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneDocumentObservedManyTimes_IsOneOperationWithItsObservationCount()
    {
        var operation = Operations(Discovery(Gql("GetRoles", 20, Doc("a1b2", 20)))).Should().ContainSingle().Subject;
        operation.ObservedCount.Should().Be(20);
        operation.DocumentHash.Should().Be("a1b2");
        operation.Document.Should().NotBeNullOrEmpty();
        operation.Historical.Should().BeFalse();
    }

    [Fact]
    public void SameNameDifferentDocuments_AreTwoOperations()
    {
        var operations = Operations(Discovery(Gql("GetUser", 7, Doc("v1", 4), Doc("v2", 3))));
        operations.Should().HaveCount(2);
        operations.Select(o => o.DocumentHash).Should().BeEquivalentTo(["v1", "v2"]);
        operations.Should().OnlyContain(o => o.OperationName == "GetUser");
    }

    [Fact]
    public void AnOperationWithoutACapturedDocument_StaysOneNameOnlyOperation()
    {
        var operation = Operations(Discovery(Gql("HentAlleOperasjoner", 5))).Should().ContainSingle().Subject;
        operation.Document.Should().BeNull();
        operation.ObservedCount.Should().Be(5);
    }

    [Fact]
    public void RepeatedSnapshotsOfTheCumulativeRegistry_DoNotDoubleCount()
    {
        var snapshot = Discovery(Gql("GetRoles", 5, Doc("a", 5)));
        EndpointDiscoveryMerge.Merge(snapshot, [Gql("GetRoles", 8, Doc("a", 8), Doc("b", 1))], T0);
        var operations = Operations(snapshot);
        operations.Single(o => o.DocumentHash == "a").ObservedCount.Should().Be(8, "the live registry is cumulative; the larger count wins, never the sum");
        operations.Single(o => o.DocumentHash == "b").ObservedCount.Should().Be(1);
    }

    // ── Live vs historical ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VariantsOnlyInRetainedHistory_AreHistorical_NeverCurrent()
    {
        var snapshot = Discovery(Gql("GetUser", 3, Doc("current", 3)));
        snapshot.Pages[0].NetworkHistory.Add(new NetworkAnalysisCapture(1, T0.AddDays(-2), T0.AddDays(-2), null,
            [Gql("GetUser", 9, Doc("current", 6), Doc("old", 9, T0.AddDays(-2)))], null));
        var operations = Operations(snapshot);
        operations.Should().HaveCount(2);
        operations.Single(o => o.DocumentHash == "current").Historical.Should().BeFalse();
        var old = operations.Single(o => o.DocumentHash == "old");
        old.Historical.Should().BeTrue();
        old.ObservedCount.Should().Be(9);
    }

    [Fact]
    public void ARefreshDropsDocumentVariantsLastSeenBeforeTheBoundary()
    {
        var endpoint = Gql("GetUser", 3, Doc("before", 2, T0.AddHours(-1)), Doc("after", 1, T0.AddHours(1)));
        EndpointDiscoveryMerge.RestrictToGeneration(endpoint, T0).GraphQlDocuments.Select(d => d.Hash).Should().Equal("after");
    }

    // ── Presentation ────────────────────────────────────────────────────────────────────────────────────────────────

    private static ApiReviewGraphQlCompatibility Compatibility(GraphQlSchemaSource source, params GraphQlCompatibilityStatus[] statuses) => new()
    {
        SchemaSource = source,
        NotAssessedReason = source == GraphQlSchemaSource.None ? "No GraphQL schema was available for validation." : null,
        Operations = statuses.Select((s, i) => new GraphQlOperationCompatibilityResult { OperationName = $"Op{i}", OperationType = GraphQlOperationType.Query, Status = s, ObservationCount = 2 }).ToList(),
    };

    [Fact]
    public void SchemaUnavailable_IsNotAssessed_NeverZeroCompatible()
    {
        var c = Compatibility(GraphQlSchemaSource.None, Enumerable.Repeat(GraphQlCompatibilityStatus.NotAssessed, 6).ToArray());
        ApiReviewGraphQlCompatibilityPresentation.Summary(c).Should().Be("6 observed operations · compatibility not assessed — schema unavailable");
        ApiReviewGraphQlCompatibilityPresentation.Coverage(c).Should().Be("Unavailable — no schema");
        ApiReviewGraphQlCompatibilityPresentation.SchemaSourceLabel(c.SchemaSource).Should().Be("None");
    }

    [Fact]
    public void Assessed_SaysHowManyOfTheObservedAreCompatible()
    {
        var c = Compatibility(GraphQlSchemaSource.RuntimeIntrospection,
            GraphQlCompatibilityStatus.Compatible, GraphQlCompatibilityStatus.Compatible, GraphQlCompatibilityStatus.Compatible,
            GraphQlCompatibilityStatus.Compatible, GraphQlCompatibilityStatus.Compatible, GraphQlCompatibilityStatus.Incompatible);
        ApiReviewGraphQlCompatibilityPresentation.Summary(c).Should().Be("5 of 6 observed operations compatible · 1 incompatible");
        ApiReviewGraphQlCompatibilityPresentation.Coverage(c).Should().Be("Complete");
        ApiReviewGraphQlCompatibilityPresentation.SchemaSourceLabel(c.SchemaSource).Should().Be("Runtime GraphQL schema");
        ApiReviewGraphQlCompatibilityPresentation.Coverage(Compatibility(GraphQlSchemaSource.ConfiguredArtifact, GraphQlCompatibilityStatus.Compatible, GraphQlCompatibilityStatus.NotAssessed))
            .Should().Be("Partial — 1 of 2 assessed");
    }

    [Theory]
    [InlineData(GraphQlOperationType.Query, "Not executed (observed only)")]
    [InlineData(GraphQlOperationType.Mutation, "Not executed — contract validation only")]
    [InlineData(GraphQlOperationType.Subscription, "Not tested")]
    public void RuntimeIsNeverClaimedForObservedOperations(GraphQlOperationType type, string label) =>
        ApiReviewGraphQlCompatibilityPresentation.RuntimeLabel(type).Should().Be(label);

    // ── Pre-run readiness ──────────────────────────────────────────────────────────────────────────────────────────

    private static ApiReviewTarget Target(string? contractSource, params ApiReviewOperation[] operations) => new()
    {
        TargetId = "gql", ApiType = ApiReviewTargetType.GraphQl, Host = "api-dev.bufetat.no", BasePath = "/api/graphql", ContractSource = contractSource, Operations = operations.ToList(),
    };

    private static ApiReviewOperation Observed(bool withDocument) => new()
    {
        OperationType = GraphQlOperationType.Query, OperationName = "GetRoles", Document = withDocument ? "query GetRoles{roles{id}}" : null, DocumentHash = withDocument ? "h" : null,
    };

    [Fact]
    public void Readiness_SaysWhetherCompatibilityCanBeAssessed_NeverAResult()
    {
        ApiReviewGraphQlCompatibilityPresentation.Readiness([Target(null, Observed(true))], false).Single().Label
            .Should().Be("1 observed frontend operation · runtime schema will be retrieved during the review for compatibility");
        ApiReviewGraphQlCompatibilityPresentation.Readiness([Target("https://x/schema.graphql", Observed(true))], true).Single()
            .Should().Be(new ApiReviewReadinessItem("1 observed frontend operation · configured schema artifact available — compatibility can be assessed", ApiReviewReadinessItemState.Ok));
        ApiReviewGraphQlCompatibilityPresentation.Readiness([Target(null, Observed(true))], true).Single().State.Should().Be(ApiReviewReadinessItemState.Warning);
        var legacy = ApiReviewGraphQlCompatibilityPresentation.Readiness([Target(null, Observed(false), Observed(false))], false).Single();
        legacy.State.Should().Be(ApiReviewReadinessItemState.Missing);
        legacy.Label.Should().StartWith("2 observed frontend operations · operation documents not captured yet");
        ApiReviewGraphQlCompatibilityPresentation.Readiness([Target(null)], false).Should().BeEmpty();
    }
}
