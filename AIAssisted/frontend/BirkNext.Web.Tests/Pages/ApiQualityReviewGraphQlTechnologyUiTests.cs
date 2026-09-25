using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// GraphQL technology metadata on the GraphQL tab: server and client with confidence and provenance, evidence behind a disclosure,
/// "Not detected" when there is no evidence, and client-specific remediation only for a Confirmed client.
/// </summary>
public sealed partial class ApiQualityReviewLandingUITests
{
    private static readonly GraphQlTechnologyDetection M2lbStack = new()
    {
        Server = new GraphQlTechnologyFinding
        {
            Technology = GraphQlTechnologies.HotChocolate, Confidence = GraphQlTechnologyConfidence.Likely, Source = GraphQlTechnologyEvidenceSource.RuntimeResponse,
            Evidence = ["GraphQL error code HC0046 (Hot Chocolate's HC#### error-code format)", "Introspection refusal uses Hot Chocolate's message \"Introspection is not allowed for the current request.\""],
        },
        Client = new GraphQlTechnologyFinding
        {
            Technology = GraphQlTechnologies.StrawberryShake, Confidence = GraphQlTechnologyConfidence.Confirmed, Source = GraphQlTechnologyEvidenceSource.DeployedFrontendArtifact,
            Evidence = ["Deployed frontend build manifest (_framework/blazor.boot.json) lists StrawberryShake.Core, StrawberryShake.Transport.Http"],
        },
    };

    private static Func<ApiReviewRunRequest, ApiReviewReport> WithTechnology(GraphQlTechnologyDetection? technology) => request =>
    {
        var report = CompatibilityEvidence(request);
        return report with { Targets = report.Targets.Select(t => t.Target.ApiType == ApiReviewTargetType.GraphQl ? t with { GraphQlTechnology = technology } : t).ToList() };
    };

    [Fact]
    public async Task GraphQlTab_ShowsTechnologyWithConfidenceAndProvenance_EvidenceCollapsed()
    {
        Register(Context(), true, AutorisasjonEndpoints(), WithTechnology(M2lbStack));
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-graphql]").Click();

        page.Find("[data-testid=aqr-gql-tech-server]").TextContent.Should().Be("Hot Chocolate · Likely · from runtime responses to the review's own requests");
        page.Find("[data-testid=aqr-gql-tech-client]").TextContent.Should().Be("Strawberry Shake · Confirmed · from the deployed frontend build");
        var disclosure = page.Find("[data-testid^=aqr-gql-tech-evidence-]");
        disclosure.QuerySelector("button")!.GetAttribute("aria-expanded").Should().Be("false");
        disclosure.QuerySelector(".disclosure-body")!.HasAttribute("hidden").Should().BeTrue();
        disclosure.TextContent.Should().Contain("HC0046").And.Contain("blazor.boot.json").And.Contain("never changes schema source, compatibility or severity");
        page.Find("[data-testid=aqr-gql-technology]").TextContent.Should().NotContainAny("Passed", "Healthy", "Validated");
    }

    [Fact]
    public async Task ConfirmedStrawberryShake_GivesTheClientSpecificAction()
    {
        Register(Context(), true, AutorisasjonEndpoints(), WithTechnology(M2lbStack));
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-graphql]").Click();
        page.Find("[data-testid=aqr-gql-issues-incompat-toggle]").Click();
        page.Find("[data-testid=aqr-gql-operations]").TextContent.Should().Contain("regenerate the Strawberry Shake client after the schema change");
    }

    [Fact]
    public async Task NoEvidence_IsNotDetected_AndTheActionStaysGeneric()
    {
        var unknown = new GraphQlTechnologyDetection
        {
            Server = new GraphQlTechnologyFinding { Confidence = GraphQlTechnologyConfidence.NotDetected, Note = "No server-specific fingerprint in the review's own requests." },
            Client = new GraphQlTechnologyFinding { Confidence = GraphQlTechnologyConfidence.NotDetected, Note = "The deployed frontend's build manifest was not readable (index HTTP 302, blazor.boot.json HTTP 302)." },
        };
        Register(Context(), true, AutorisasjonEndpoints(), WithTechnology(unknown));
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-graphql]").Click();

        page.Find("[data-testid=aqr-gql-tech-server]").TextContent.Should().Be("Not detected");
        page.Find("[data-testid=aqr-gql-tech-client]").TextContent.Should().Be("Not detected");
        page.Markup.Should().NotContain("Hot Chocolate").And.NotContain("Strawberry Shake");
        page.Find("[data-testid=aqr-gql-issues-incompat-toggle]").Click();
        page.Find("[data-testid=aqr-gql-operations]").TextContent.Should().Contain("Regenerate generated GraphQL client code if applicable");
    }

    [Fact]
    public async Task AnOlderResultWithoutTechnology_RendersNoTechnologyBlock()
    {
        Register(Context(), true, AutorisasjonEndpoints(), WithTechnology(null));
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-graphql]").Click();
        page.FindAll("[data-testid=aqr-gql-technology]").Should().BeEmpty();
    }

    [Fact]
    public void Export_CarriesTechnologyWithConfidenceAndProvenance()
    {
        var snapshot = new BirkNext.Web.Models.EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, AutorisasjonEndpoints(), DateTimeOffset.UtcNow);
        var request = new ApiReviewRunRequest { Targets = ApiReviewTargetResolver.Resolve(Context(), snapshot) };
        var html = new ReportExportService().ExportApiReview(WithTechnology(M2lbStack)(request), "Test");
        html.Should().Contain("GraphQL server technology</dt><dd>Hot Chocolate · Likely from runtime responses to the review's own requests")
            .And.Contain("GraphQL client technology</dt><dd>Strawberry Shake · Confirmed from the deployed frontend build");
    }
}
