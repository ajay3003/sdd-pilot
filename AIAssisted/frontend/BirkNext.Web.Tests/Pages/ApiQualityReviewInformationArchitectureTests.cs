using BirkNext.ApiReview;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

public sealed partial class ApiQualityReviewLandingUITests
{
    private static ApiReviewReport LimitedEvidence(ApiReviewRunRequest request)
    {
        var report = StubReport(request, true);
        return report with
        {
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
                }] : t.Operations,
                GraphQlOperationMatches = t.Target.ApiType == ApiReviewTargetType.GraphQl
                    ? Enumerable.Range(1, 6).Select(i => new ApiReviewGraphQlOperationMatch($"HentGenerelleTildelingerForRolle{i}", null, ApiReviewCheckResult.NotTested, "Schema unavailable.")).ToList() : [],
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
        panel.TextContent.Should().Contain("6 observed operations").And.Contain("0 / 6 matched to runtime schema");
        page.FindAll("[data-testid=aqr-gql-operations] tbody tr").Should().HaveCount(6)
            .And.OnlyContain(r => r.TextContent.Contains("Not tested"));
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
        page.Find("[data-check-id=rest-payload]").TextContent.Should().Contain("Pass").And.Contain("warning > 1048576 bytes");
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
