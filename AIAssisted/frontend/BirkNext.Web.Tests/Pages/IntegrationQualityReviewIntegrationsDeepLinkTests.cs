using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class IntegrationQualityReviewIntegrationsDeepLinkTests : BunitContext
{
    [Fact]
    public void TheLinkOpensIntegrationsForTheEnvironmentWithoutActivatingOrSavingIt()
    {
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IIntegrationCatalogApiService>(new FakeIntegrationCatalogApi());
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[
              {"id":"dev","name":"Dev target","environmentType":"Development","targetUrl":"https://dev.example.test"},
              {"id":"qa","name":"QA target","environmentType":"QA","targetUrl":"https://qa.example.test"}]}
            """);

        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p => p.Add(c => c.InitialTab, "integrations").Add(c => c.InitialProfileId, "qa"));

        cut.Find("#target-tab-integrations").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("#target-tab-general").GetAttribute("aria-selected").Should().Be("false");
        cut.Markup.Should().Contain("QA target");
        settings.Settings.ActiveProfileId.Should().Be("dev", "navigation selects the environment for viewing; it does not activate it");
        JSInterop.Invocations.Where(i => i.Identifier.Contains("setItem", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
    }
}
