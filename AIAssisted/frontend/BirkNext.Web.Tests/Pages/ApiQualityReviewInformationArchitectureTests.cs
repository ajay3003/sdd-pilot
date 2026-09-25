using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

public sealed partial class ApiQualityReviewLandingUITests
{
    // The reported run: six observed business operations, one safe __typename query executed, runtime schema unavailable.
    private static readonly string[] ObservedNames = ["HentAlleOperasjoner", "HentGenerelleTildelingerForRolle", "HentNødinnganger", "HentOrganisasjonstre", "HentAlleGenerelleRoller", "HentAlleBarnespesifikkeRoller"];

    private static List<ApiReviewOperation> ObservedGraphQlOperations => ObservedNames.Select(n => new ApiReviewOperation
    {
        Method = "POST", Path = "/api/autorisasjon/graphql", OperationType = GraphQlOperationType.Query, OperationName = n, ObservedCount = 2, AuthObserved = true,
    }).ToList();

    private static List<ApiReviewOperationResult> EngineShapedGraphQlOperations() =>
    [
        new() { Display = "query { __typename }", Method = "POST", Path = "/api/autorisasjon/graphql", Executed = true, StatusCode = 200, ContentType = "application/graphql-response+json", ElapsedMs = 90, Result = ApiReviewCheckResult.Pass },
        .. ObservedNames.Select(n => new ApiReviewOperationResult { Display = $"Query {n}", Method = "POST", Path = "/api/autorisasjon/graphql", Executed = false, Result = ApiReviewCheckResult.NotTested, Note = "Observed operation; schema unavailable so it could not be matched." }),
    ];

    private static ApiReviewReport LimitedEvidence(ApiReviewRunRequest request)
    {
        var report = StubReport(request, true);
        return report with
        {
            // As the engine computes it: observed = the GraphQL target's observed operations; nothing matched without a schema.
            Coverage = report.Coverage with { GraphQlOperationsObserved = ObservedNames.Length, GraphQlOperationsMatched = 0 },
            Targets = report.Targets.Select(t => t with
            {
                Contract = t.Target.ApiType == ApiReviewTargetType.Rest ? null : new()
                {
                    Kind = "GraphQL schema", Available = false, IntrospectionEnabled = false,
                    Status = ApiReviewCheckResult.NotApplicable, Note = "HTTP 400; introspection disabled or rejected."
                },
                Operations = t.Target.ApiType == ApiReviewTargetType.Rest ? [new()
                {
                    Display = "GET /api/autorisasjon/roller", Executed = true, StatusCode = 200, ContentLength = 141,
                    Result = ApiReviewCheckResult.Warning,
                    Checks = [new() { CheckId = "rest-cache-control", Result = ApiReviewCheckResult.Warning },
                        new() { CheckId = "drift-shape", Area = ApiReviewFindingType.Drift, Result = ApiReviewCheckResult.Pass, Detail = "0 change(s) vs baseline." },
                        new() { CheckId = "rest-payload", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Pass, Detail = "141 bytes." }]
                }] : EngineShapedGraphQlOperations(),
                // Engine shape without any schema: every observed operation is Not assessed — compatibility could not run.
                GraphQlOperationMatches = [],
                GraphQlCompatibility = t.Target.ApiType == ApiReviewTargetType.Rest ? null : new ApiReviewGraphQlCompatibility
                {
                    SchemaSource = GraphQlSchemaSource.None, NotAssessedReason = "No GraphQL schema was available for validation.",
                    Operations = ObservedNames.Select(n => new GraphQlOperationCompatibilityResult
                    {
                        OperationName = n, OperationType = GraphQlOperationType.Query, ObservationCount = 3, Status = GraphQlCompatibilityStatus.NotAssessed,
                        NotAssessedReason = "No GraphQL schema was available for validation.",
                    }).ToList(),
                },
                Target = t.Target.ApiType == ApiReviewTargetType.GraphQl ? t.Target with { Operations = ObservedGraphQlOperations } : t.Target,
                Checks = [new() { CheckId = "sec-tls", Area = ApiReviewFindingType.Security, Title = "TLS", Result = ApiReviewCheckResult.Pass, Detail = "HTTPS" },
                    new() { CheckId = "errors-leak", Area = ApiReviewFindingType.Errors, Title = "No internal details in error responses", Result = ApiReviewCheckResult.Pass, Detail = "No indicators." }]
            }).ToList()
        };
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("graphql")]
    [InlineData("contracts")]
    [InlineData("security")]
    [InlineData("errors")]
    [InlineData("performance")]
    [InlineData("findings")]
    public async Task DomainTabsHaveNoGlobalStack_AndLinkReturnsToSingleOwner(string tab)
    {
        Register(Context(), true, AutorisasjonEndpoints(), LimitedEvidence);
        var page = await RenderAndRun();
        string[] ids = ["aqr-coverage", "aqr-limitations", "aqr-readonly", "aqr-targets-disclosure", "aqr-access-details"];
        foreach (var id in ids) page.FindAll($"[data-testid={id}]").Should().ContainSingle();
        page.Find("[data-testid=aqr-reviewed-help]").TextContent.Should().Contain("available automated scope completed");
        page.Find("[data-testid=aqr-service-status]").ClassList.Should().NotContain("aqr-pill-ready");
        page.Find($"[data-testid=aqr-tab-{tab}]").Click();
        page.Find($"[data-testid=aqr-tab-{tab}]").GetAttribute("aria-selected").Should().Be("true");
        foreach (var id in ids) page.FindAll($"[data-testid={id}]").Should().BeEmpty();
        page.Find("[data-testid=aqr-review-details-link]").Click();
        foreach (var id in ids) page.FindAll($"[data-testid={id}]").Should().ContainSingle();
        page.Find("[data-testid=aqr-manual-review]").TextContent.Should().Contain("not findings");
    }

    [Fact]
    public async Task RejectedIntrospectionIsUnavailable_ObservedOperationsStayNotTested()
    {
        Register(Context(), true, AutorisasjonEndpoints(), LimitedEvidence);
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-graphql]").Click();
        var panel = page.Find("[data-testid=aqr-tabpanel]");
        panel.TextContent.Should().Contain("Unavailable").And.Contain("HTTP 400").And.NotContain("Not applicable");
        // Six listed operations are six observed operations, and compatibility that could not run is not "0 compatible".
        page.Find("[data-testid=aqr-gql-counts]").TextContent.Should().Be("6 observed operations · compatibility not assessed — schema unavailable");
        panel.TextContent.Should().NotContain("0 observed operations").And.NotContain("0 / 6").And.NotContain("0 compatible");
        // Runtime and contract are separate columns: never executed, and not assessed.
        page.FindAll("[data-testid=aqr-gql-operations] tbody tr").Should().HaveCount(6)
            .And.OnlyContain(r => r.TextContent.Contains("Not executed (observed only)") && r.TextContent.Contains("Not assessed"));
        // The safe __typename query is the review's own request, listed once and never counted as an observed operation.
        page.FindAll("[data-testid=aqr-operation-row]").Select(r => r.TextContent).Should().ContainSingle(r => r.Contains("query { __typename }"));
        page.FindAll("[data-testid=aqr-operation-row]").Should().HaveCount(1);
        // Overview says the same thing.
        page.Find("[data-testid=aqr-tab-overview]").Click();
        page.Find("[data-testid=aqr-coverage-row][data-coverage='GraphQL']").TextContent
            .Should().Contain("6 observed operations").And.Contain("Compatibility not assessed — schema unavailable").And.NotContain("0 / 6");
    }

    [Fact]
    public async Task ContractsSeparateMissingEvidenceFromCompletedDrift()
    {
        Register(Context(), true, AutorisasjonEndpoints(), LimitedEvidence);
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-contracts]").Click();
        var panel = page.Find("[data-testid=aqr-tabpanel]");
        panel.TextContent.Should().Contain("No contract configured").And.Contain("Unavailable")
            .And.Contain("Drift checks").And.Contain("1 completed").And.Contain("No changes detected")
            .And.NotContain("Pass 1");
    }

    [Fact]
    public async Task SuccessfulExecutionIsIndependentOfReviewWarnings()
    {
        Register(Context(), true, AutorisasjonEndpoints(), LimitedEvidence);
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-rest]").Click();
        var row = page.Find("[data-testid=aqr-operation-row]");
        row.Children[1].TextContent.Should().Contain("Successful");
        row.Children[2].TextContent.Should().Be("200");
        row.Children[6].TextContent.Should().Contain("Warnings: 1").And.NotContain("Pass");
    }

    [Fact]
    public async Task ObservationWordingAndPolicyThresholdsReachDomainTables()
    {
        Register(Context(), true, AutorisasjonEndpoints(), LimitedEvidence);
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-security]").Click();
        page.Find("[data-check-id=sec-tls]").TextContent.Should().Contain("HTTPS observed").And.Contain("does not assess full TLS");
        page.Find("[data-testid=aqr-security-note]").TextContent.Should().Contain("does not establish that the API is secure");
        page.Find("[data-testid=aqr-tab-errors]").Click();
        page.Find("[data-check-id=errors-leak]").TextContent.Should().Contain("No internal-detail indicators observed").And.NotContain("No internal details in error responses");
        page.Find("[data-testid=aqr-tab-performance]").Click();
        // The REST Payload setting (500 KB), not the larger GraphQL one it used to share.
        page.Find("[data-check-id=rest-payload]").TextContent.Should().Contain("Pass").And.Contain("warning > 512,000 bytes (500 KB)").And.NotContain("1048576").And.NotContain("1,048,576");
        page.Find("[data-testid=aqr-performance-note]").TextContent.Should().Contain("backend gateway to the API").And.Contain("Not end-user");
    }

    [Fact]
    public void SetupShowsTargetsOpen_ExplainsCredentialLifetime_AndAttemptsSchema()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-targets-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("true"));
        page.Find("[data-testid=aqr-memory-only-help]").TextContent.Should().Contain("backend runtime").And.Contain("not exposed to the review result");
        page.Find("[data-testid=aqr-target][data-type=GraphQl] [data-testid=aqr-target-contract]").TextContent.Should().Be("Schema retrieval pending");
    }
}
