using Bunit;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>The Skolenærvær page is a passive shell: without a Target Environment it reports Not ready and never calls the integration API.</summary>
public sealed class SkoleNaervaerTestingPageTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _integrations = new();
    private FrontendAnalysisContext _context = new();

    public SkoleNaervaerTestingPageTests()
    {
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(() => _context);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(_integrations.Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void WithoutTargetEnvironment_ShowsNotReady_AndCallsNoIntegrationApi()
    {
        var page = Render<SkoleNaervaerTesting>();

        page.Markup.Should().Contain("Not selected · execution not ready");
        page.Find("[data-testid=snt-journeys-not-ready]").TextContent.Should().Contain("Nothing has been run or assessed");
        page.FindAll("#snt-education [role=status]").Should().ContainSingle();
        _integrations.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void ShowsThreeJourneys_AndPersonLookupResponsibilities()
    {
        var page = Render<SkoleNaervaerTesting>();

        page.FindAll(".snt-journeys h3").Select(h => h.TextContent).Should().Equal("Utdanningsdata", "Rapportmottak", "Periodeavslutning");
        var lookup = page.Find("[data-testid=snt-person-lookup]").TextContent;
        lookup.Should().Contain("MM performs transport and intake only; it does not perform the Person lookup.");
        lookup.Should().Contain("Skolenærværsrapport performs the Person lookup for submitted reports.");
        lookup.Should().Contain("Utdanning performs the Person lookup for BiRK education data.");
        page.Find("#snt-education").TextContent.Should().NotContain("Altinn");
        // Section links carry the page path: with <base href="/"> a bare "#id" would resolve to the root route and leave the page.
        page.FindAll(".snt-tabs a").Select(a => a.GetAttribute("href")).Should().OnlyContain(href => href!.StartsWith("extensions/skolenaervaer#snt-"));
        page.FindAll(".snt-links a").Select(a => a.GetAttribute("href")).Should().Equal(
            "/integration-quality-review", "/admin/system-settings?section=target-environments", "/source-analysis", "/test-coverage-review");
    }

    [Fact]
    public void SectionLink_MovesFocusToItsHeading_InsteadOfLeavingThePage()
    {
        var page = Render<SkoleNaervaerTesting>();

        page.FindAll(".snt-tabs a").Single(a => a.TextContent == "Rapportmottak").Click();

        JSInterop.VerifyInvoke("birknextFocusElement").Arguments.Should().Equal("snt-rapport-heading");
        page.Find("#snt-rapport-heading").GetAttribute("tabindex").Should().Be("-1");
    }

    [Fact]
    public void WithTargetButNoSourceSnapshot_ArchitectureRulesAreNotAssessed()
    {
        _context = new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV" } };
        _integrations.Setup(a => a.GetCatalogAsync(It.IsAny<FrontendAnalysisProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationCatalog());
        _integrations.Setup(a => a.ListSourceSnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var page = Render<SkoleNaervaerTesting>();

        page.WaitForAssertion(() => page.Markup.Should().Contain("No source snapshot · architecture rules Not assessed"));
        page.FindAll("[data-testid=snt-journeys-not-ready]").Should().BeEmpty();
    }
}
