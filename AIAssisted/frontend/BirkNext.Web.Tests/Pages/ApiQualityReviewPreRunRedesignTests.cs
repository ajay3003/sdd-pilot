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
        page.FindAll("[data-testid=aqr-target-review-endpoint]").Select(e => e.TextContent).Should().Contain(e => e.Contains(rest));
        page.FindAll("[data-testid=aqr-target-review-endpoint]").Select(e => e.TextContent).Should().Contain(e => e.Contains(graphQl));
        page.FindAll("[data-testid=aqr-target-path]").Select(e => e.TextContent).Should().Contain(e => e.Contains(rest)).And.Contain(e => e.Contains(graphQl));
        page.FindAll("[data-testid=aqr-copy-url]").Select(e => e.GetAttribute("aria-label"))
            .Should().Contain(["Copy REST endpoint URL", "Copy GraphQL endpoint URL"]);
        page.FindAll("[data-testid=aqr-target-access]").Should().OnlyContain(e => e.TextContent.Contains("Authenticated · Ready"));
        page.Find("[data-testid=aqr-target-authentication]").TextContent.Should().Be("Required (Microsoft Entra ID)");
        page.Find("[data-testid=aqr-readiness]").TextContent.Should().Contain("Ready for read-only review · partial contract coverage").And.Contain("Contract coverage: partial (2 coverage notes)").And.NotContain("Needs attention");
        page.Find("[data-testid=aqr-known-limitations]").TextContent.Should().Contain("Autorisasjon API REST contract").And.Contain("Not available");
        page.Find("[data-testid=aqr-domain][data-domain=contracts] [data-testid=aqr-domain-state]").TextContent.Should().Be("Limited");
        page.Find("[data-testid=aqr-domain-detail][data-domain=contracts] [data-testid=aqr-domain-limitation]").TextContent.Should().Contain("OpenAPI");
        page.Find("[data-testid=aqr-add-openapi]").TextContent.Should().Be("Configure contract");
        page.Find("[data-testid=aqr-target][data-type=GraphQl] [data-testid=aqr-target-contract]").TextContent.Should().Contain("Retrieval will be attempted during review");
        page.Find("[data-testid=aqr-add-schema]").TextContent.Should().Be("Configure schema fallback");
        page.Find("[data-testid=aqr-target-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    /// <summary>The fuzzing card's action opens the ONE configuration (the panel), never a second setting; Off keeps the read-only run enabled.</summary>
    [Fact]
    public void ConfigureSafeFuzzing_OpensTheCanonicalPanel_AndOptionalChecksNeverBlockTheRun()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]"));

        page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse("fuzzing Off and unready authorization checks never block the read-only review");
        page.Find("[data-testid=aqr-fuzzing-summary]").ClassList.Should().Contain("aqr-active-check-neutral");
        page.FindAll("[data-testid=aqr-fuzzing-summary] select, [data-testid=aqr-fuzzing-summary] input").Should().BeEmpty("the card holds no second setting");

        page.Find("[data-testid=aqr-configure-fuzzing]").Click();

        page.Find("[data-testid=aqr-active-checks-details-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=aqr-fuzzing-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=aqr-fuzzing-levels]").Should().NotBeNull();
        page.Find("[data-testid=aqr-fuzzing-disclosure]").TextContent.Should().Contain("It is not a penetration test").And.Contain("no GraphQL mutations")
            .And.Contain("no attack dictionaries").And.Contain("never Production");
    }

    [Fact]
    public void ReviewOrderAndOptionalChecksStartCompact()
    {
        Register(Context(), true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]"));

        page.FindAll("[data-testid=aqr-overview], [data-testid=aqr-run-row], [data-testid=aqr-domains], [data-testid=aqr-target-section], [data-testid=aqr-additional-checks], [data-testid=aqr-details]")
            .Select(e => e.GetAttribute("data-testid"))
            .Should().Equal("aqr-run-row", "aqr-overview", "aqr-target-section", "aqr-domains", "aqr-additional-checks", "aqr-details");
        page.FindAll("[data-testid=aqr-domains], [data-testid=aqr-known-limitations], [data-testid=aqr-additional-checks]")
            .Select(e => e.GetAttribute("data-testid")).Should().Equal("aqr-domains", "aqr-known-limitations", "aqr-additional-checks");
        // Optional active checks: state only until expanded (Task 9 §12, §55).
        page.Find("[data-testid=aqr-active-checks-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-fuzzing-state]").TextContent.Should().Be("Off");
        page.Find("[data-testid=aqr-fuzzing-off]").TextContent.Should().Be("No active fuzz requests will be sent. Optional — the read-only API review can still run.");
        page.Find("[data-testid=aqr-configure-fuzzing]").TextContent.Trim().Should().Be("Configure safe fuzzing");
        page.FindAll("[data-testid=aqr-fuzzing-summary] select, [data-testid=aqr-fuzzing-summary] input").Should().BeEmpty("no fuzzing configuration is shown by default");
        page.Find("[data-testid=aqr-configure-authorization]").GetAttribute("href")
            .Should().Contain("section=target-environments").And.Contain("tab=endpoint").And.Contain("profile=dev").And.Contain("focus=authorization-checks").And.Contain("return=");
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
