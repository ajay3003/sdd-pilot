using BirkNext.ApiReview;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

public sealed partial class ApiQualityReviewLandingUITests
{
    [Fact]
    public void SelectedRestAndGraphQlUrlsAreVisibleBeforeRunAndInTargetRows()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]"));

        var rest = "https://api-dev.bufetat.no/api/autorisasjon";
        var graphQl = "https://api-dev.bufetat.no/api/autorisasjon/graphql";
        page.FindAll("[data-testid=aqr-scope-endpoint]").Select(e => e.TextContent).Should().Contain(e => e.Contains(rest));
        page.FindAll("[data-testid=aqr-scope-endpoint]").Select(e => e.TextContent).Should().Contain(e => e.Contains(graphQl));
        page.FindAll("[data-testid=aqr-target-path]").Select(e => e.TextContent).Should().Contain(e => e.Contains(rest)).And.Contain(e => e.Contains(graphQl));
        page.FindAll("[data-testid=aqr-copy-url]").Select(e => e.GetAttribute("aria-label"))
            .Should().Contain(["Copy REST endpoint URL", "Copy GraphQL endpoint URL"]);
        page.FindAll("[data-testid=aqr-target-access]").Should().OnlyContain(e => e.TextContent.Contains("Authenticated · Ready"));
        page.Find("[data-testid=aqr-target-authentication]").TextContent.Should().Be("Required (Microsoft Entra ID)");
        page.Find("[data-testid=aqr-readiness]").TextContent.Should().Contain("Review can run with limitations").And.Contain("REST contract validation unavailable");
        page.Find("[data-testid=aqr-domain][data-domain=contracts] [data-testid=aqr-domain-state]").TextContent.Should().Be("Limited");
        page.Find("[data-testid=aqr-domain][data-domain=contracts] [data-testid=aqr-domain-limitation]").TextContent.Should().Contain("OpenAPI");
        page.Find("[data-testid=aqr-add-openapi]").TextContent.Should().Be("Configure OpenAPI URL");
        page.Find("[data-testid=aqr-target][data-type=GraphQl] [data-testid=aqr-target-contract]").TextContent.Should().Contain("Retrieval will be attempted during review");
        page.Find("[data-testid=aqr-add-schema]").TextContent.Should().Be("Add schema artifact");
        page.Find("[data-testid=aqr-target-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public void ReviewOrderAndOptionalChecksStartCompact()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]"));

        page.FindAll("[data-testid=aqr-overview], [data-testid=aqr-run-row], [data-testid=aqr-domains], [data-testid=aqr-target-section], [data-testid=aqr-additional-checks], [data-testid=aqr-details]")
            .Select(e => e.GetAttribute("data-testid"))
            .Should().Equal("aqr-overview", "aqr-run-row", "aqr-domains", "aqr-target-section", "aqr-additional-checks", "aqr-details");
        page.Find("[data-testid=aqr-fuzzing-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-authorization-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-configure-authorization]").GetAttribute("href")
            .Should().Contain("section=target-environments").And.Contain("tab=endpoint").And.Contain("profile=dev");
        page.Find("[data-testid=aqr-access-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public async Task CopyEndpointCopiesOnlyTheSafeExactUrl()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]"));

        await page.Find("[data-testid=aqr-target][data-type=Rest] [data-testid=aqr-copy-url]").ClickAsync();

        JSInterop.Invocations.Should().ContainSingle(i => i.Identifier == "navigator.clipboard.writeText")
            .Which.Arguments.Should().ContainSingle().Which.Should().Be("https://api-dev.bufetat.no/api/autorisasjon");
        page.Find("[data-testid=aqr-target][data-type=Rest] [data-testid=aqr-copy-url]").TextContent.Should().Be("Copied");
        JSInterop.Invocations.SelectMany(i => i.Arguments).OfType<string>()
            .Should().NotContain(s => s.Contains("Authorization", StringComparison.OrdinalIgnoreCase) || s.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("api-dev.bufetat.no", "/api/autorisasjon", "https://api-dev.bufetat.no/api/autorisasjon")]
    [InlineData("api-dev.bufetat.no", "/api/autorisasjon?access_token=secret", null)]
    [InlineData("user:secret@api-dev.bufetat.no", "/api/autorisasjon", null)]
    public void EndpointPresentationNeverIncludesCredentialsOrQuerySecrets(string host, string path, string? expected)
    {
        var target = new ApiReviewTarget { Scheme = "https", Host = host, Port = 443, BasePath = path };

        ApiReviewPresentation.SafeEndpointUrl(target).Should().Be(expected);
        ApiReviewPresentation.SafeEndpointPath(target).Should().Be("/api/autorisasjon");
    }
}
