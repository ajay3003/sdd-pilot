using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class GraphQlSchemaArtifactPresentationTests
{
    private static ApiReviewTarget Gql(string id, params ApiReviewOperation[] ops) => new()
    {
        TargetId = id, EnvironmentId = "dev", ApiType = ApiReviewTargetType.GraphQl, Host = "api-dev.example.test", BasePath = "/" + id, ServiceName = id, Selected = true, Operations = ops.ToList(),
    };

    private static ApiReviewOperation Op(string name, string? document = "query{roles{id}}", GraphQlDocumentOmission omission = GraphQlDocumentOmission.None) =>
        new() { Method = "POST", OperationType = GraphQlOperationType.Query, OperationName = name, Document = document, DocumentOmission = omission, ObservedCount = 1 };

    [Fact]
    public void Contracts_AllSelectedTargetsWithAnArtifact_IsArtifactFallback()
    {
        var targets = new[] { Gql("a", Op("A")) };
        var artifacts = new Dictionary<string, GraphQlSchemaArtifact> { ["a"] = FakeGraphQlSchemaArtifactApi.Artifact("dev", "a") };
        var model = ApiReviewPresentation.Contracts(targets, ["a"], null, null, artifacts);
        var row = model.Rows.Single(r => r.Label == "GraphQL");
        row.State.Should().Be(ApiReviewContractState.ArtifactFallback);
        row.Detail.Should().Contain("Runtime introspection is attempted first").And.Contain("m2lb-schema.graphql");
        model.Details.Should().Contain(d => d.StartsWith("GraphQL schema artifact: m2lb-schema.graphql · sha256 "));
        ApiReviewContractStates.PreRunSummary(row.State).Should().Be("Runtime schema first · configured SDL fallback");
    }

    [Fact]
    public void Contracts_AnArtifactForAnotherTarget_DoesNotCount()
    {
        var artifacts = new Dictionary<string, GraphQlSchemaArtifact> { ["other"] = FakeGraphQlSchemaArtifactApi.Artifact("dev", "other") };
        ApiReviewPresentation.Contracts([Gql("a", Op("A"))], ["a"], null, null, artifacts).Rows.Single(r => r.Label == "GraphQL").State.Should().Be(ApiReviewContractState.RuntimeSchema);
    }

    [Fact]
    public void Readiness_NamesTheFallback_AndCountsDocumentsThatWereNotKept()
    {
        var target = Gql("a", Op("A"), Op("P", null, GraphQlDocumentOmission.PersistedQueryHashOnly), Op("Big", null, GraphQlDocumentOmission.ExceededRetentionLimit));
        var item = ApiReviewGraphQlCompatibilityPresentation.Readiness([target], true,
            new Dictionary<string, GraphQlSchemaArtifact> { ["a"] = FakeGraphQlSchemaArtifactApi.Artifact("dev", "a") }).Single();
        item.State.Should().Be(ApiReviewReadinessItemState.Ok);
        item.Label.Should().Be("3 observed frontend operations (1 with a captured document, 1 persisted-query hash only, 1 over the retained-size limit) · runtime introspection will be attempted first; configured SDL m2lb-schema.graphql is available as fallback — compatibility can be assessed");
    }

    [Fact]
    public void Readiness_NoArtifactAfterARefusal_PointsToWhereOneIsAdded()
    {
        var item = ApiReviewGraphQlCompatibilityPresentation.Readiness([Gql("a", Op("A"))], true).Single();
        item.State.Should().Be(ApiReviewReadinessItemState.Warning);
        item.Label.Should().Contain("add a trusted SDL under Review details → GraphQL schema artifacts");
    }

    [Fact]
    public void SourceLabel_ConfiguredSdl_IsNeverCalledRuntime()
    {
        var used = new ApiReviewGraphQlCompatibility { SchemaSource = GraphQlSchemaSource.ConfiguredArtifact, ConfiguredArtifact = new GraphQlSchemaArtifactSnapshot { FileName = "s.graphql", ContentHash = new string('b', 64), UsedForCompatibility = true } };
        ApiReviewGraphQlCompatibilityPresentation.SchemaSourceLabel(used).Should().Be("Configured SDL");
        var runtimeWon = new ApiReviewGraphQlCompatibility { SchemaSource = GraphQlSchemaSource.RuntimeIntrospection, ConfiguredArtifact = used.ConfiguredArtifact with { UsedForCompatibility = false } };
        ApiReviewGraphQlCompatibilityPresentation.SchemaSourceLabel(runtimeWon).Should().Be("Runtime GraphQL schema");
        ApiReviewGraphQlCompatibilityPresentation.ArtifactLabel(runtimeWon).Should().Contain("available as fallback (not used)").And.Contain("bbbbbbbbbbbb").And.NotContain(new string('b', 13));
    }

    [Fact]
    public void Export_CarriesSourceArtifactProvenanceAndRuntimeOutcome_NeverTheSdl()
    {
        var report = new ApiReviewReport
        {
            Targets =
            [
                new ApiReviewTargetResult
                {
                    Target = Gql("a"), Status = ApiReviewTargetStatus.Completed,
                    GraphQlCompatibility = new ApiReviewGraphQlCompatibility
                    {
                        SchemaSource = GraphQlSchemaSource.ConfiguredArtifact, RuntimeSchemaOutcome = "Rejected — HTTP 400",
                        ConfiguredArtifact = new GraphQlSchemaArtifactSnapshot { ArtifactId = "x", FileName = "m2lb-schema.graphql", ContentHash = new string('c', 64), UpdatedAt = DateTimeOffset.UtcNow, UsedForCompatibility = true },
                        Operations =
                        [
                            new GraphQlOperationCompatibilityResult { OperationName = "A", OperationType = GraphQlOperationType.Query, Status = GraphQlCompatibilityStatus.Compatible, DeprecatedUsage = ["`Role.navn` is deprecated."] },
                            new GraphQlOperationCompatibilityResult { OperationName = "P", OperationType = GraphQlOperationType.Query, NotAssessedReason = "Operation text unavailable; only a persisted-query hash was observed." },
                        ],
                    },
                },
            ],
        };
        var html = new ReportExportService().ExportApiReview(report, "Test");
        html.Should().Contain("Configured SDL").And.Contain("Runtime introspection").And.Contain("Rejected — HTTP 400")
            .And.Contain("m2lb-schema.graphql · sha256 cccccccccccc").And.Contain("persisted-query hash").And.Contain("Observation: `Role.navn` is deprecated.");
        html.Should().NotContain("type Query");
    }
}
