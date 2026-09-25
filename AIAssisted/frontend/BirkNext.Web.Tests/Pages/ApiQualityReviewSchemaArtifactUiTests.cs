using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// API Quality Review with a trusted GraphQL schema artifact: configured per environment + target in Review details, stated in pre-run
/// readiness as the fallback (never a result), and — after a run — reported from the run's own snapshot, not the current artifact.
/// </summary>
public sealed partial class ApiQualityReviewLandingUITests
{
    private static ObservedNetworkEndpoint[] WithDocuments(ObservedNetworkEndpoint[] endpoints) => endpoints.Select(e => e.Category != ObservedTrafficCategory.GraphQl ? e : e with
    {
        GraphQlDocuments = [new ObservedGraphQlDocument { Hash = e.OperationName!.ToLowerInvariant(), Document = $"query {e.OperationName}{{roles{{id}}}}", Count = 3, FirstObservedAt = T0, LastObservedAt = T0 }],
    }).ToArray();

    private static ApiReviewReport ArtifactEvidence(ApiReviewRunRequest request, GraphQlSchemaArtifactSnapshot snapshot)
    {
        var report = LimitedEvidence(request);
        return report with
        {
            Targets = report.Targets.Select(t => t.Target.ApiType != ApiReviewTargetType.GraphQl ? t : t with
            {
                GraphQlCompatibility = new ApiReviewGraphQlCompatibility
                {
                    SchemaSource = GraphQlSchemaSource.ConfiguredArtifact, SchemaSourceDetail = $"Configured SDL {snapshot.FileName}", SchemaRetrievedAt = T0,
                    RuntimeSchemaOutcome = "Rejected — HTTP 400; introspection disabled or rejected.", ConfiguredArtifact = snapshot,
                    Operations =
                    [
                        new GraphQlOperationCompatibilityResult { OperationName = "HentRoller", OperationType = GraphQlOperationType.Query, Status = GraphQlCompatibilityStatus.Compatible, DocumentHash = "h1",
                            DeprecatedUsage = ["`Rolle.navn` is deprecated — Use name."] },
                        new GraphQlOperationCompatibilityResult { OperationName = "Persisted", OperationType = GraphQlOperationType.Query, Status = GraphQlCompatibilityStatus.NotAssessed, DocumentHash = "p1",
                            NotAssessedReason = "Operation text unavailable; only a persisted-query hash was observed." },
                    ],
                },
            }).ToList(),
        };
    }

    private static readonly GraphQlSchemaArtifactSnapshot SnapshotA = new()
    {
        ArtifactId = "a1", FileName = "m2lb-schema-2026-09-20.graphql", ContentHash = "aaaaaaaaaaaa1111111111111111111111111111111111111111111111111111", UpdatedAt = T0.AddDays(-5), UsedForCompatibility = true,
    };

    [Fact]
    public void PreRun_ConfiguredArtifact_IsStatedAsFallback_NeverAsAResult()
    {
        _schemaArtifacts.Stored[("dev", GqlId)] = FakeGraphQlSchemaArtifactApi.Artifact("dev", GqlId);
        Register(Context(), true, WithDocuments(AutorisasjonEndpoints()));
        var page = Render<ApiQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness-items]").TextContent
            .Should().Contain("runtime introspection will be attempted first; configured SDL m2lb-schema.graphql is available as fallback — compatibility can be assessed"));
        page.Find("[data-testid=aqr-domain][data-domain='graphql'] [data-testid=aqr-domain-limitation]").TextContent.Should().Be("Runtime schema retrieval will be attempted. Configured SDL available as fallback for compatibility.");
        page.Find("[data-testid=aqr-contract-row][data-protocol='GraphQL']").GetAttribute("data-state").Should().Be("ArtifactFallback");
        page.Markup.Should().NotContain("Compatible", "pre-run never shows a compatibility result");
    }

    [Fact]
    public void PreRun_NoArtifact_SaysWhereToAddOne()
    {
        Register(Context(), true, WithDocuments(AutorisasjonEndpoints()));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-schema-artifacts-disclosure]"));
        page.Find("[data-testid=aqr-schema-artifacts-disclosure] .disclosure-hint").TextContent.Should().Be("None configured");
        page.Find("[data-testid=aqr-schema-artifacts-disclosure] button").Click();
        page.FindAll("[data-testid=gsa-load-error]").Should().BeEmpty("a successful load shows no error");
        var target = page.Find("[data-testid=gsa-target]");
        target.GetAttribute("data-target-id").Should().Be(GqlId);
        target.QuerySelector("[data-testid=gsa-source]")!.TextContent.Should().Be("Runtime introspection only");
        target.QuerySelector("[data-testid=gsa-artifact]")!.TextContent.Trim().Should().Be("None");
        target.QuerySelector("label.gsa-file-button")!.TextContent.Trim().Should().Be("Add schema artifact");
    }

    [Fact]
    public void PreRun_ArtifactStoreUnavailable_IsStated_AndNothingIsAssumed()
    {
        _schemaArtifacts.ListFailure = new HttpRequestException("refused");
        Register(Context(), true, WithDocuments(AutorisasjonEndpoints()));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-schema-artifacts-disclosure]"));
        page.Find("[data-testid=aqr-schema-artifacts-disclosure] button").Click();
        page.Find("[data-testid=gsa-load-error]").TextContent.Should().Contain("could not be loaded");
        page.Find("[data-testid=aqr-contract-row][data-protocol='GraphQL']").GetAttribute("data-state").Should().NotBe("ArtifactFallback");
    }

    [Fact]
    public async Task PostRun_ContractsCard_ReportsTheRunsOwnSnapshot_NotTheCurrentArtifact()
    {
        // The current artifact (B) differs from what the run used (A): the result must show A.
        _schemaArtifacts.Stored[("dev", GqlId)] = FakeGraphQlSchemaArtifactApi.Artifact("dev", GqlId, "m2lb-schema-v2.graphql");
        Register(Context(), true, WithDocuments(AutorisasjonEndpoints()), r => ArtifactEvidence(r, SnapshotA));
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-contracts]").Click();

        var card = page.Find("[data-testid=aqr-gql-compatibility]");
        card.QuerySelector("[data-testid=aqr-gql-schema-source]")!.TextContent.Should().StartWith("Configured SDL");
        card.QuerySelector("[data-testid=aqr-gql-schema-source]")!.TextContent.Should().NotContain("Runtime");
        card.QuerySelector("[data-testid=aqr-gql-runtime-outcome]")!.TextContent.Should().StartWith("Rejected — HTTP 400");
        var artifact = card.QuerySelector("[data-testid=aqr-gql-artifact]")!.TextContent;
        artifact.Should().Contain("m2lb-schema-2026-09-20.graphql").And.Contain("aaaaaaaaaaaa").And.NotContain("v2");
        artifact.Should().NotContain(SnapshotA.ContentHash, "only the short hash is shown");
        card.QuerySelector("[data-testid=aqr-gql-deprecated-summary]")!.TextContent.Should().Contain("not an incompatibility");
        card.QuerySelector("[data-testid=aqr-gql-observed]")!.TextContent.Should().Be("2");
        card.QuerySelector("[data-testid=aqr-gql-not-assessed]")!.TextContent.Should().Be("1");
    }

    [Fact]
    public async Task PostRun_GraphQlTab_ShowsDeprecationAsObservation_AndPersistedQueryReason()
    {
        Register(Context(), true, WithDocuments(AutorisasjonEndpoints()), r => ArtifactEvidence(r, SnapshotA));
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-graphql]").Click();
        var rows = page.FindAll("[data-testid=aqr-gql-operation-row]");
        var compatible = rows.Single(r => r.GetAttribute("data-compatibility") == "Compatible");
        compatible.QuerySelector("[data-testid=aqr-gql-contract]")!.TextContent.Should().Be("Compatible");
        compatible.QuerySelector("[data-testid=aqr-gql-deprecated]")!.TextContent.Should().Contain("Observation: `Rolle.navn` is deprecated");
        rows.Single(r => r.GetAttribute("data-compatibility") == "NotAssessed").TextContent.Should().Contain("only a persisted-query hash was observed");
    }
}
