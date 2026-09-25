using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// API Quality Review → GraphQL / Contracts with client/server compatibility: the GraphQL tab keeps Runtime and Contract in separate
/// columns, an incompatible operation opens to its violations and recommended action, Contracts states the five counts apart from
/// drift, historical evidence is marked, and the export carries the same result.
/// </summary>
public sealed partial class ApiQualityReviewLandingUITests
{
    private static ApiReviewReport CompatibilityEvidence(ApiReviewRunRequest request)
    {
        var report = LimitedEvidence(request);
        return report with
        {
            Targets = report.Targets.Select(t => t.Target.ApiType != ApiReviewTargetType.GraphQl ? t : t with
            {
                GraphQlCompatibility = new ApiReviewGraphQlCompatibility
                {
                    SchemaSource = GraphQlSchemaSource.RuntimeIntrospection, SchemaRetrievedAt = new DateTimeOffset(2026, 9, 25, 11, 40, 0, TimeSpan.Zero),
                    Operations =
                    [
                        .. ObservedNames.Take(5).Select(n => new GraphQlOperationCompatibilityResult { OperationName = n, OperationType = GraphQlOperationType.Query, Status = GraphQlCompatibilityStatus.Compatible, ObservationCount = 24, DocumentHash = n }),
                        new GraphQlOperationCompatibilityResult
                        {
                            OperationName = "HentAlleGenerelleRoller", OperationType = GraphQlOperationType.Query, Status = GraphQlCompatibilityStatus.Incompatible, ObservationCount = 12,
                            DocumentHash = "incompat", LastObservedAt = new DateTimeOffset(2026, 9, 25, 11, 42, 0, TimeSpan.Zero),
                            Issues = [new GraphQlValidationIssue("FIELD_NOT_FOUND", "The field `navn` does not exist on the type `GenerellRolle`.")],
                        },
                        new GraphQlOperationCompatibilityResult { OperationName = "GammelSok", OperationType = GraphQlOperationType.Query, Status = GraphQlCompatibilityStatus.Incompatible, ObservationCount = 2, Historical = true, DocumentHash = "old",
                            Issues = [new GraphQlValidationIssue("FIELD_NOT_FOUND", "The field `sok` does not exist on the type `Query`.")] },
                    ],
                    SchemaChangeImpact = ["Removed root field `sok` — used by Query GammelSok"],
                },
            }).ToList(),
        };
    }

    [Fact]
    public async Task GraphQlTab_RuntimeAndContractAreSeparateColumns_IncompatibleOpensToItsIssues()
    {
        Register(Context(), true, AutorisasjonEndpoints(), CompatibilityEvidence);
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-graphql]").Click();

        page.Find("[data-testid=aqr-gql-counts]").TextContent.Should().Be("5 of 7 observed operations compatible · 2 incompatible");
        page.FindAll("[data-testid=aqr-gql-operations] thead th").Select(h => h.TextContent).Should().Contain(["Runtime", "Contract"]);
        var rows = page.FindAll("[data-testid=aqr-gql-operation-row]");
        rows.Should().HaveCount(7);
        rows.Should().OnlyContain(r => r.QuerySelector("[data-testid=aqr-gql-runtime]")!.TextContent == "Not executed (observed only)");
        var incompatible = rows.Single(r => r.GetAttribute("data-compatibility") == "Incompatible" && r.GetAttribute("data-historical") == "false");
        incompatible.QuerySelector("[data-testid=aqr-gql-contract]")!.TextContent.Should().Be("Incompatible");
        incompatible.QuerySelector("[data-testid=aqr-gql-issues-incompat-toggle]")!.Click();
        var detail = page.FindAll("[data-testid=aqr-gql-operation-row]").Single(r => r.GetAttribute("data-compatibility") == "Incompatible" && r.GetAttribute("data-historical") == "false").TextContent;
        detail.Should().Contain("FIELD_NOT_FOUND").And.Contain("`navn` does not exist on the type `GenerellRolle`")
            .And.Contain("Endpoint Discovery · 12 observations").And.Contain("Runtime GraphQL schema").And.Contain("Update the frontend operation");
        // Historical evidence is marked, never presented as current.
        rows.Single(r => r.GetAttribute("data-historical") == "true").QuerySelector("[data-testid=aqr-gql-historical]").Should().NotBeNull();
        // Compatible rows stay compact: no issue disclosure.
        rows.Where(r => r.GetAttribute("data-compatibility") == "Compatible").Should().OnlyContain(r => r.QuerySelector(".disclosure") == null);
    }

    [Fact]
    public async Task ContractsTab_CompatibilityCountsAreSeparateFromDrift_WithClientImpact()
    {
        Register(Context(), true, AutorisasjonEndpoints(), CompatibilityEvidence);
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-contracts]").Click();

        var card = page.Find("[data-testid=aqr-gql-compatibility]");
        card.QuerySelector("[data-testid=aqr-gql-schema-source]")!.TextContent.Should().StartWith("Runtime GraphQL schema");
        card.QuerySelector("[data-testid=aqr-gql-observed]")!.TextContent.Should().Be("7 (6 current · 1 historical-only)");
        card.QuerySelector("[data-testid=aqr-gql-assessed]")!.TextContent.Should().Be("7");
        card.QuerySelector("[data-testid=aqr-gql-compatible]")!.TextContent.Should().Be("5");
        card.QuerySelector("[data-testid=aqr-gql-incompatible]")!.TextContent.Should().Be("2");
        card.QuerySelector("[data-testid=aqr-gql-not-assessed]")!.TextContent.Should().Be("0");
        card.QuerySelector("[data-testid=aqr-gql-coverage]")!.TextContent.Should().Be("Complete");
        page.FindAll("[data-testid=aqr-gql-incompatible-detail]").Should().HaveCount(2);
        page.Find("[data-testid=aqr-gql-client-impact]").TextContent.Should().Contain("Removed root field `sok` — used by Query GammelSok");
        var text = page.Find("[data-testid=aqr-tabpanel]").TextContent;
        text.IndexOf("GraphQL client/server compatibility", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Drift comparison", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContractsTab_WithoutASchema_SaysNotAssessed_NeverZeroCompatible()
    {
        Register(Context(), true, AutorisasjonEndpoints(), LimitedEvidence);
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-contracts]").Click();
        var card = page.Find("[data-testid=aqr-gql-compatibility]");
        card.QuerySelector("[data-testid=aqr-gql-observed]")!.TextContent.Should().Be("6");
        card.QuerySelector("[data-testid=aqr-gql-assessed]")!.TextContent.Should().Be("0");
        card.QuerySelector("[data-testid=aqr-gql-compatible]")!.TextContent.Should().Be("—");
        card.QuerySelector("[data-testid=aqr-gql-not-assessed]")!.TextContent.Should().Be("6");
        card.QuerySelector("[data-testid=aqr-gql-schema-source]")!.TextContent.Should().Be("None");
        card.QuerySelector("[data-testid=aqr-gql-coverage]")!.TextContent.Should().Be("Unavailable — no schema");
        card.QuerySelector("[data-testid=aqr-gql-not-assessed-reason]")!.TextContent.Should().Contain("No GraphQL schema was available for validation.");
    }

    [Fact]
    public void Export_CarriesCompatibilityApartFromDrift()
    {
        var snapshot = new BirkNext.Web.Models.EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, AutorisasjonEndpoints(), DateTimeOffset.UtcNow);
        var request = new ApiReviewRunRequest { Targets = ApiReviewTargetResolver.Resolve(Context(), snapshot) };
        var html = new ReportExportService().ExportApiReview(CompatibilityEvidence(request), "Test");
        html.Should().Contain("GraphQL client/server compatibility").And.Contain("Runtime GraphQL schema")
            .And.Contain("5 compatible · 2 incompatible").And.Contain("FIELD_NOT_FOUND: The field `navn` does not exist on the type `GenerellRolle`.")
            .And.Contain("GammelSok (historical)").And.Contain("Schema drift client impact");
    }
}
