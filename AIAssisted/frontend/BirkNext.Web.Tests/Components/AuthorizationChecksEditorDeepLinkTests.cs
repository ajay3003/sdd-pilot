using BirkNext.RuntimeSecurity;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TargetSettingsComponent = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Task 9 §28–§33, §62: "Review authorization checks" lands on the editor itself — the right Target Environment, Endpoint Discovery, the API
/// review safety settings opened, edit mode and focus on Authorization checks — with a Back link to API Quality Review. The editor keeps one
/// model: suggestions are added as AuthorizationScenario entries with their provenance; identities are role → alias mappings.
/// </summary>
public sealed class AuthorizationChecksEditorDeepLinkTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();

    public AuthorizationChecksEditorDeepLinkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(SettingsJson);
    }

    [Fact]
    public void TheDeepLinkOpensTheSafetySettings_EntersEditMode_FocusesAuthorizationChecks_AndOffersBack()
    {
        var cut = Render<TargetSettingsComponent>(p => p
            .Add(x => x.InitialTab, "endpoint")
            .Add(x => x.InitialProfileId, "qa")
            .Add(x => x.InitialFocus, TargetSettingsComponent.AuthorizationChecksFocus)
            .Add(x => x.ReturnHref, "/api-quality-review?targets=rest-1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=rse-authz]"));
        cut.Find("[data-testid=endpoint-api-review-settings]").HasAttribute("open").Should().BeTrue();
        cut.Find("[data-testid=rse-authz] legend").TextContent.Should().Be("Authorization checks");
        cut.Find("[data-testid=rse-authz]").GetAttribute("tabindex").Should().Be("-1", "the deep-link target can receive focus");
        cut.FindAll(".fa-profile-chip").Single(c => c.TextContent.Contains("QA")).ClassList.Should().Contain("fa-profile-chip-selected");
        cut.WaitForAssertion(() => JSInterop.VerifyFocusAsyncInvoke());

        var back = cut.Find("[data-testid=te-return-api-review] a");
        back.TextContent.Should().Be("Back to API Quality Review");
        back.GetAttribute("href").Should().Be("/api-quality-review?targets=rest-1");
    }

    [Fact]
    public void WithoutTheDeepLink_TheSafetySettingsStayCollapsed_AndThereIsNoBackLink()
    {
        var cut = Render<TargetSettingsComponent>(p => p.Add(x => x.InitialTab, "endpoint").Add(x => x.InitialProfileId, "qa"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=endpoint-api-review-settings]"));
        cut.Find("[data-testid=endpoint-api-review-settings]").HasAttribute("open").Should().BeFalse();
        cut.FindAll("[data-testid=te-return-api-review]").Should().BeEmpty();
        cut.FindAll("[data-testid=rse-authz]").Should().BeEmpty("not in edit mode, the summary is shown");
    }

    [Fact]
    public void TheEditorAddsSuggestionsAsScenarios_KeepsProvenance_AndMapsRolesToIdentities()
    {
        var settings = new RuntimeSecurityExpectations();
        var suggestion = new AuthorizationScenario
        {
            ScenarioId = "source-1", DisplayName = "GET /api/orders/admin — policy AdminOnly", Url = "https://api.example.test/api/orders/admin",
            Origin = AuthorizationCheckOrigin.SourceDerived, Confirmation = AuthorizationCheckConfirmation.NeedsConfirmation, Evidence = "policy \"AdminOnly\" · Orders.cs:12",
            Identities = [new() { Alias = "", Role = "AdminOnly", Expected = AuthorizationExpectation.Allow }],
        };
        var cut = Render<RuntimeSecurityExpectationsEditor>(p => p.Add(x => x.Settings, settings).Add(x => x.Editable, true).Add(x => x.Suggestions, new[] { suggestion }));

        cut.Find("[data-testid=rse-authz-suggestion]").TextContent.Should().Contain("Source-derived").And.Contain("AdminOnly: Allowed").And.Contain("Orders.cs:12");
        cut.Find("[data-testid=rse-authz-suggestion-add]").Click();

        settings.AuthorizationScenarios.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Origin = AuthorizationCheckOrigin.SourceDerived, Confirmation = AuthorizationCheckConfirmation.NeedsConfirmation,
        }, o => o.ExcludingMissingMembers());
        cut.FindAll("[data-testid=rse-authz-suggestion]").Should().BeEmpty("an added suggestion is no longer offered");
        cut.Find("[data-testid=rse-authz-unmapped]").TextContent.Should().Contain("AdminOnly");
        cut.Find("[data-testid=rse-authz-confirmed]").HasAttribute("checked").Should().BeFalse("a source-derived expectation is not confirmed by adding it");

        cut.Find("[data-testid=rse-authz-mapping-add]").Click();
        settings.AuthorizationIdentities.Should().ContainSingle().Which.Role.Should().Be("AdminOnly", "the first unmapped role is prefilled");
        cut.Find("[data-testid=rse-authz-confirmed]").Change(true);
        settings.AuthorizationScenarios[0].Confirmation.Should().Be(AuthorizationCheckConfirmation.Confirmed);
    }

    private const string SettingsJson = """
    {
      "activeProfileId": "local",
      "profiles": [
        { "id": "local", "name": "Local", "environmentType": "Local", "targetUrl": "https://application-dev.example.test" },
        { "id": "qa", "name": "QA", "environmentType": "QA", "targetUrl": "https://application-qa.example.test" }
      ]
    }
    """;
}
