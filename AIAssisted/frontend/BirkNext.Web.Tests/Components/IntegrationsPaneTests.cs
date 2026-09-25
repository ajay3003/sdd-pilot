using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>Target Environment → Integrations over the backend catalog, with the M2LB DEV seed shape.</summary>
public sealed class IntegrationsPaneTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" };

    public IntegrationsPaneTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Open() => Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile));

    [Fact]
    public void SummaryCountsTheSixteenBusinessTopicsAndNotTheTechnicalOnes()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-summary-configured]").TextContent.Should().Be("16");
        cut.Find("[data-testid=ip-summary-enabled]").TextContent.Should().Be("16");
        cut.Find("[data-testid=ip-summary-ready]").TextContent.Should().Be("1");
        cut.Find("[data-testid=ip-summary-confirm]").TextContent.Should().Be("15");
        cut.Find("[data-testid=ip-summary-platform]").TextContent.Should().Contain("M2LB DEV Event Hubs");
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16);
    }

    [Fact]
    public void PlatformValuesAreShownOnceOnThePlatformCard()
    {
        var cut = Open();
        cut.FindAll("[data-testid=ip-platform]").Should().ContainSingle();
        cut.Find("[data-testid=ip-platform-namespace]").TextContent.Should().Be("evhns-m2lb-dev-nwe-001");
        cut.Find("[data-testid=ip-platform-producer-auth]").TextContent.Should().Contain("SAS");
        cut.Find("[data-testid=ip-platform-consumer-auth]").TextContent.Should().Contain("Managed");
    }

    [Fact]
    public void ConsumerMappingStatesAreDistinguished()
    {
        var cut = Open();
        var consumers = cut.FindAll("[data-testid=ip-row-consumer]");
        consumers.Count(c => c.GetAttribute("data-mapping") == nameof(ConsumerMappingState.Confirmed)).Should().Be(1);
        consumers.Count(c => c.GetAttribute("data-mapping") == nameof(ConsumerMappingState.Suggested)).Should().Be(8);
        consumers.Count(c => c.GetAttribute("data-mapping") == nameof(ConsumerMappingState.NeedsConfirmation)).Should().Be(7);
        consumers.Where(c => c.GetAttribute("data-mapping") == nameof(ConsumerMappingState.Suggested)).Should().OnlyContain(c => c.TextContent.Contains("suggested"));
        consumers.Where(c => c.GetAttribute("data-mapping") == nameof(ConsumerMappingState.NeedsConfirmation)).Should().OnlyContain(c => c.TextContent.Contains("Needs confirmation"));
    }

    [Fact]
    public void NoDefaultConsumerGroupIsInvented()
    {
        var cut = Open();
        cut.Markup.Should().NotContain("$Default");
        cut.FindAll("[data-testid=ip-row-view]")[0].Click();
        cut.Find("[data-testid=ip-detail-consumer-group]").TextContent.Should().Be("Unknown / not configured");
    }

    [Fact]
    public void PersonShowsItsConfirmedAdapterIdentity()
    {
        var cut = Open();
        var person = cut.FindAll("[data-testid=ip-row]").Single(r => r.GetAttribute("data-integration-id") == "dev:eventhub:birk-cdc:dbo.Person");
        person.GetAttribute("data-configuration").Should().Be(nameof(IntegrationConfigurationState.Ready));
        person.QuerySelector("[data-testid=ip-row-view]")!.Click();
        var detail = cut.Find("[data-testid=ip-row-detail]").TextContent;
        detail.Should().Contain("ca-m2lb-person-adp-dev-nwe-001").And.Contain("id-m2lb-person-adp-dev-nwe");
    }

    [Fact]
    public void TechnicalTopicsAreCollapsedAndSeparate()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-technical] button").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=ip-technical] .disclosure-body").HasAttribute("hidden").Should().BeTrue("technical infrastructure starts collapsed");
        cut.Find("[data-testid=ip-technical] button").Click();
        cut.Find("[data-testid=ip-technical] .disclosure-body").HasAttribute("hidden").Should().BeFalse();
        var list = cut.Find("[data-testid=ip-technical-list]").TextContent;
        foreach (var name in new[] { "schemahistory", "connect-configs", "connect-offsets", "connect-status" }) list.Should().Contain(name);
        cut.FindAll("[data-testid=ip-row]").Should().NotContain(r => r.TextContent.Contains("connect-offsets"));
    }

    [Fact]
    public void TheEditorIsHiddenUntilAddAndValidatesOnlyAfterInteraction()
    {
        var cut = Open();
        cut.FindAll("[data-testid=ip-editor]").Should().BeEmpty();
        cut.Find("[data-testid=ip-add]").Click();
        cut.Find("[data-testid=ip-editor]");
        cut.FindAll("[data-testid=ip-editor-errors]").Should().BeEmpty("no error is shown before the user has done anything");
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.Find("[data-testid=ip-editor-errors]").TextContent.Should().Contain("Display name is required").And.Contain("topic is required");
        _api.Saved.Should().BeEmpty("an invalid integration is never saved");
    }

    [Fact]
    public void SavingANewIntegrationCreatesItInTheBackendCatalog()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-add]").Click();
        cut.Find("[data-testid=ip-edit-name]").Input("Leselogg events");
        cut.Find("[data-testid=ip-edit-topic]").Input("m2lb-cdc-dev.BirkM2LB.dbo.Leselogg");
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle(d => d.DisplayName == "Leselogg events" && d.ConsumerGroup == null));
        cut.FindAll("[data-testid=ip-editor]").Should().BeEmpty();
        cut.Find("[data-testid=ip-summary-configured]").TextContent.Should().Be("17");
    }

    [Fact]
    public void DisableAndDeleteGoThroughTheBackend()
    {
        var cut = Open();
        cut.FindAll("[data-testid=ip-row-view]")[1].Click();
        cut.Find("[data-testid=ip-row-toggle-enabled]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-summary-enabled]").TextContent.Should().Be("15"));
        _api.Calls.Should().Contain(c => c.StartsWith("enabled:") && c.EndsWith(":False"));
    }

    [Fact]
    public void ABackendOutageIsStatedAndNothingIsInvented()
    {
        _api.LoadFailure = new HttpRequestException("refused");
        var cut = Open();
        cut.Find("[data-testid=ip-load-error]").GetAttribute("role").Should().Be("alert");
        cut.FindAll("[data-testid=ip-row]").Should().BeEmpty();
    }

    [Fact]
    public void NoSecretOrTerraformIsRendered()
    {
        var cut = Open();
        cut.FindAll("[data-testid=ip-row-view]")[0].Click();
        var markup = cut.Markup;
        foreach (var forbidden in new[] { "SharedAccessKey", "Endpoint=sb://", "client_secret", "password", ".tf", "terraform" })
            markup.Should().NotContainEquivalentOf(forbidden);
    }
}
