using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment → Performance Thresholds states which review owns each latency setting, so the two latency groups do not read
/// as competing policies: Single Request Latency (API Quality Review, per request), API Response Warning/Poor (BirkNext Performance
/// Quality, proxy/browser-observed), Average API Latency (no owner yet).
/// </summary>
public sealed class PerformanceThresholdOwnershipTests : BunitContext
{
    public PerformanceThresholdOwnershipTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(new FrontendAnalysisSettingsService());
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IIntegrationCatalogApiService>(new FakeIntegrationCatalogApi());
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"https://m2lbdev.example.test/"}]}
            """);
    }

    private IRenderedComponent<Component> OpenThresholds()
    {
        var cut = Render<Component>();
        cut.WaitForAssertion(() => cut.FindAll("[role=tab]").Should().Contain(t => t.TextContent.Trim() == "Performance Thresholds"));
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Performance Thresholds").Click();
        return cut;
    }

    [Fact]
    public void ReadOnlyView_NamesTheOwnerOfEachLatencySetting()
    {
        var text = OpenThresholds().Markup;
        text.Should().Contain("Average API Latency <small class=\"fa-threshold-hint\"")
            .And.Contain("not used by any review yet")
            .And.Contain("API Quality Review, per request")
            .And.Contain("proxy/browser-observed");
    }

    [Fact]
    public void EditView_HintsAreProgrammaticallyLinkedToTheirInputs()
    {
        var cut = OpenThresholds();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit Environment").Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Performance Thresholds").Click();

        var single = cut.Find("#fa-thr-single-latency");
        single.GetAttribute("aria-describedby").Should().Be("fa-thr-single-latency-hint");
        cut.Find("#fa-thr-single-latency-hint").TextContent.Should().Be("Each API request · API Quality Review response time");
        cut.Find("label[for=fa-thr-single-latency]").TextContent.Should().Be("Maximum Single Request Latency");

        cut.Find("#fa-thr-avg-latency").GetAttribute("aria-describedby").Should().Be("fa-thr-avg-latency-hint");
        cut.Find("[data-testid=fa-threshold-owner-avg-latency]").TextContent.Should().Contain("not used by any review yet");
        cut.Find("[data-testid=fa-threshold-owner-performance-quality]").TextContent.Should().Be("Proxy/browser-observed traffic only; not used by API Quality Review.");
    }
}
