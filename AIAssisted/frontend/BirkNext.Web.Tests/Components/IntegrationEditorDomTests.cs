using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TargetSettingsComponent = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Rendered markup of the Target Environment integration editor.
///
/// These assert what a person actually sees, rather than the rules behind it: that a transport's
/// irrelevant fields are absent from the DOM rather than merely hidden, that provenance and
/// incompleteness read correctly, and that the editor offers nowhere to type a credential.
/// </summary>
public sealed class IntegrationEditorDomTests : BunitContext
{
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detection = new();
    private readonly FrontendAnalysisSettingsService _settings = new();

    public IntegrationEditorDomTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detection.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(SettingsJson);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
    }

    /// <summary>Renders the component, enters edit mode and opens the Integrations tab.</summary>
    private IRenderedComponent<TargetSettingsComponent> RenderIntegrationsTab()
    {
        var cut = Render<TargetSettingsComponent>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Edit Environment")).Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Integrations").Click();

        return cut;
    }

    /// <summary>Expands the first integration card so its form fields render.</summary>
    private static void ExpandFirstIntegration(IRenderedComponent<TargetSettingsComponent> cut) =>
        cut.FindAll(".fa-integration-toggle").First().Click();

    // ── The component still renders without the optional template service ────

    [Fact]
    public void RendersWithoutTheTemplateServiceRegistered()
    {
        // The template lookup is resolved optionally; hard-requiring it would break every render
        // path that does not register it.
        var cut = RenderIntegrationsTab();

        cut.Markup.Should().NotBeNullOrEmpty();
    }

    // ── Grouping ─────────────────────────────────────────────────────────────

    [Fact]
    public void DetectedAndConfiguredGroupsRenderSeparately()
    {
        var cut = RenderIntegrationsTab();

        cut.FindAll("[data-testid=fa-detected-heading]").Should().NotBeEmpty(
            "a discovered REST integration is configured in the fixture");
        cut.FindAll("[data-testid=fa-configured-heading]").Should().NotBeEmpty(
            "messaging integrations are configured in the fixture");
    }

    [Fact]
    public void DiscoveredHttpIntegrationRendersWithItsProvenance()
    {
        var cut = RenderIntegrationsTab();

        cut.Markup.Should().Contain("Discovered");
        cut.Markup.Should().Contain("Orders API");
    }

    [Fact]
    public void SuggestedMessagingIntegrationRendersItsProvenance()
    {
        var cut = RenderIntegrationsTab();

        cut.Markup.Should().Contain("Suggested from audited M2LB source");
        cut.Markup.Should().NotContain("Verified configuration");
    }

    [Fact]
    public void EachConfiguredIntegrationRendersOnce()
    {
        var cut = RenderIntegrationsTab();

        // The fixture holds one manual REST entry and one discovered one for distinct endpoints;
        // neither should appear twice.
        cut.FindAll("[data-testid=fa-integration-source]").Count
            .Should().Be(cut.FindAll(".fa-integration-row").Count);
    }

    // ── Transport-specific fields are absent, not merely hidden ──────────────

    [Fact]
    public void EventHubRendersConsumerGroupAndNoSubscriptionField()
    {
        var cut = RenderIntegrationsTab();
        ExpandEventHub(cut);

        cut.FindAll("[data-testid=fa-field-consumergroup]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=fa-field-subscription]").Should().BeEmpty();
        cut.FindAll("[data-testid=fa-field-resourcekind]").Should().BeEmpty();
    }

    [Fact]
    public void EventHubConsumerGroupRendersTheAuditedValue()
    {
        var cut = RenderIntegrationsTab();
        ExpandEventHub(cut);

        var consumerGroup = cut.Find("[data-testid=fa-field-consumergroup]");

        consumerGroup.GetAttribute("value").Should().Be("$Default");
        cut.Markup.Should().NotContain("$default\"");
    }

    [Fact]
    public void EventHubRendersNoNamespaceValueWhenTheAuditDidNotEstablishOne()
    {
        var cut = RenderIntegrationsTab();
        ExpandEventHub(cut);

        cut.Find("[data-testid=fa-field-endpoint]").GetAttribute("value")
            .Should().BeNullOrEmpty();
    }

    [Fact]
    public void IncompleteEventHubNamesTheMissingNamespaceInMarkup()
    {
        var cut = RenderIntegrationsTab();
        ExpandEventHub(cut);

        var missing = cut.Find("[data-testid=fa-missing-fields]").TextContent;

        missing.Should().Contain("Namespace");
        missing.Should().NotContain("Failed");
        missing.Should().NotContain("Broken");
        missing.Should().NotContain("Unavailable");
    }

    [Fact]
    public void ServiceBusRendersResourceKindAndNoConsumerGroup()
    {
        var cut = RenderIntegrationsTab();
        ExpandServiceBus(cut);

        cut.FindAll("[data-testid=fa-field-resourcekind]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=fa-field-consumergroup]").Should().BeEmpty(
            "Service Bus has subscriptions, not consumer groups");
    }

    [Fact]
    public void ServiceBusQueueRendersNoSubscriptionField()
    {
        var cut = RenderIntegrationsTab();
        ExpandServiceBus(cut);

        cut.FindAll("[data-testid=fa-field-subscription]").Should().BeEmpty();
    }

    [Fact]
    public void LeseloggRendersItsAuditedResourceName()
    {
        var cut = RenderIntegrationsTab();
        ExpandServiceBus(cut);

        cut.Find("[data-testid=fa-field-resource]").GetAttribute("value").Should().Be("leselogg");
        cut.Markup.Should().NotContain("revisjon.leselogg");
    }

    [Fact]
    public void RestRendersAnEndpointAndNoMessagingFields()
    {
        var cut = RenderIntegrationsTab();
        ExpandRest(cut);

        cut.FindAll("[data-testid=fa-field-endpoint]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=fa-field-resource]").Should().BeEmpty();
        cut.FindAll("[data-testid=fa-field-consumergroup]").Should().BeEmpty();
        cut.FindAll("[data-testid=fa-field-subscription]").Should().BeEmpty();
    }

    [Fact]
    public void RestRelationshipFieldsRenderEmptyRatherThanInferred()
    {
        var cut = RenderIntegrationsTab();
        ExpandRest(cut);

        // Nothing derives a consumer from the URL path.
        cut.Find("[data-testid=fa-field-producer]").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid=fa-field-consumer]").GetAttribute("value").Should().BeNullOrEmpty();
    }

    // ── Accessibility ────────────────────────────────────────────────────────

    [Fact]
    public void EveryRenderedIntegrationInputHasAnAssociatedLabel()
    {
        var cut = RenderIntegrationsTab();
        ExpandEventHub(cut);

        var inputs = cut.FindAll("[data-testid^=fa-field-]");
        inputs.Should().NotBeEmpty();

        foreach (var input in inputs)
        {
            var id = input.GetAttribute("id");
            id.Should().NotBeNullOrEmpty("every field needs an id to be labelled");

            cut.FindAll($"label[for=\"{id}\"]").Should().NotBeEmpty(
                $"input {id} must have an associated label");
        }
    }

    [Fact]
    public void MissingFieldTextIsAnnouncedToAssistiveTechnology()
    {
        var cut = RenderIntegrationsTab();
        ExpandEventHub(cut);

        cut.Find("[data-testid=fa-missing-fields]").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void AddIntegrationModeChoiceIsAGroupedRadioSet()
    {
        var cut = RenderIntegrationsTab();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add Integration")).Click();

        cut.FindAll("[data-testid=fa-add-integration-flow] fieldset").Should().NotBeEmpty();
        cut.FindAll("[data-testid=fa-add-integration-flow] legend").Should().NotBeEmpty();
        cut.FindAll("[data-testid=fa-add-mode-known]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=fa-add-mode-custom]").Should().NotBeEmpty();
    }

    [Fact]
    public void WithoutATemplateServiceTheKnownFlowShowsTheEmptyStateNotAnError()
    {
        var cut = RenderIntegrationsTab();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add Integration")).Click();

        // No template service is registered here, so the catalogue resolves empty. That is a
        // normal state and must not render as a failure.
        var emptyState = cut.Find("[data-testid=fa-templates-empty]").TextContent;

        emptyState.Should().Contain("No known M2LB templates are available for this environment.");
        emptyState.Should().Contain("You can configure a custom integration instead.");
        cut.Markup.Should().NotContain("Failed to load");
    }

    // ── Security ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheRenderedEditorOffersNoCredentialInput()
    {
        var cut = RenderIntegrationsTab();
        ExpandEventHub(cut);

        // Asserting on the controls rather than on words: the surrounding page legitimately warns
        // "never enter passwords, tokens or client secrets", and a substring scan would read that
        // safety guidance as a violation.
        var controls = cut.FindAll(".fa-integration-row input, .fa-integration-row select, .fa-integration-row textarea");
        controls.Should().NotBeEmpty();

        foreach (var control in controls)
        {
            var identity = string.Join(" ",
                control.GetAttribute("id") ?? "",
                control.GetAttribute("name") ?? "",
                control.GetAttribute("placeholder") ?? "",
                control.GetAttribute("type") ?? "");

            foreach (var forbidden in new[]
                     {
                         "connectionstring", "sharedaccesskey", "sharedaccesssignature", "saskey",
                         "secret", "password", "authorization", "bearer", "cookie", "jwt", "token"
                     })
            {
                identity.ToLowerInvariant().Should().NotContain(forbidden,
                    "the editor must not offer a control for {0}", forbidden);
            }

            // A password input would be a credential field whatever it is labelled.
            control.GetAttribute("type").Should().NotBe("password");
        }
    }

    // ── Fixture navigation helpers ───────────────────────────────────────────

    private static void ExpandRest(IRenderedComponent<TargetSettingsComponent> cut) =>
        cut.FindAll(".fa-integration-toggle").First(b => b.TextContent.Contains("Orders API")).Click();

    private static void ExpandEventHub(IRenderedComponent<TargetSettingsComponent> cut) =>
        cut.FindAll(".fa-integration-toggle").First(b => b.TextContent.Contains("BiRK Person CDC")).Click();

    private static void ExpandServiceBus(IRenderedComponent<TargetSettingsComponent> cut) =>
        cut.FindAll(".fa-integration-toggle").First(b => b.TextContent.Contains("Leselogg")).Click();

    /// <summary>
    /// One environment carrying a discovered REST integration, an Event Hub suggested from the
    /// audited source with its namespace still unknown, and a suggested Service Bus queue.
    /// </summary>
    private const string SettingsJson = """
    {
      "activeProfileId": "qa",
      "profiles": [
        {
          "id": "qa", "name": "QA", "environmentType": "QA",
          "targetUrl": "https://application-qa.example.test",
          "integrations": [
            {
              "id": "rest-1", "name": "Orders API", "type": "REST",
              "endpoint": "https://application-qa.example.test/api/orders",
              "enabled": true, "configurationSource": 1, "resourceKind": 1
            },
            {
              "id": "eh-1", "name": "BiRK Person CDC", "type": "EventHub",
              "resource": "m2lb-cdc-qa.birk.dbo.person",
              "consumer": "$Default",
              "logicalProducerService": "BiRK / Debezium",
              "logicalConsumerService": "PersonBiRKAdapter",
              "enabled": true, "configurationSource": 2, "resourceKind": 3
            },
            {
              "id": "sb-1", "name": "Leselogg", "type": "ServiceBus",
              "resource": "leselogg",
              "logicalConsumerService": "Revisjon",
              "enabled": true, "configurationSource": 2, "resourceKind": 4
            }
          ]
        }
      ]
    }
    """;
}
