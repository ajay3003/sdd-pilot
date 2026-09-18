using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Known M2LB templates are suggestions from an audited reading of the target's source — never detections and never
/// runtime verification. Selecting one previews it; only the explicit Add integration action writes to the draft, and
/// a value a person entered is never overwritten.
/// </summary>
public sealed class KnownIntegrationTemplateFlowTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<IIntegrationTemplateService> _templates = new();
    private string? _requestedEnvironment;

    private static readonly KnownIntegrationTemplate PersonCdc = new()
    {
        Id = "qa-eh-person", EnvironmentName = "QA", DisplayName = "Person CDC",
        IntegrationType = IntegrationType.EventHub, ResourceKind = IntegrationResourceKind.EventHub,
        Resource = "m2lb-cdc-qa.birk.dbo.person",
        SuggestedProducer = "BiRK / Debezium", SuggestedConsumer = "PersonBiRKAdapter",
        SuggestedConsumerGroup = "$Default",
    };

    private static readonly KnownIntegrationTemplate Leselogg = new()
    {
        Id = "qa-sb-leselogg", EnvironmentName = "QA", DisplayName = "Leselogg",
        IntegrationType = IntegrationType.ServiceBus, ResourceKind = IntegrationResourceKind.ServiceBusQueue,
        Resource = "leselogg", SuggestedConsumer = "Revisjon",
        RelationshipNote = "Written to by several services; the audited source did not identify a single producing service.",
    };

    public KnownIntegrationTemplateFlowTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton(_templates.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _templates.Setup(t => t.GetForEnvironmentAsync(It.IsAny<string?>()))
            .ReturnsAsync((string? env) =>
            {
                _requestedEnvironment = env;
                return string.Equals(env, "QA", StringComparison.OrdinalIgnoreCase)
                    ? [PersonCdc, Leselogg]
                    : [];
            });
    }

    private IRenderedComponent<Component> OpenAddFlow(string environmentType, string integrationsJson = "[]")
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"p","profiles":[
          {"id":"p","name":"M2LB {{environmentType}}","environmentType":"{{environmentType}}",
           "targetUrl":"https://app.example.test","integrations":{{integrationsJson}}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Edit Environment")).Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Integrations").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "+ Add Integration").Click();
        return cut;
    }

    private static void SelectTemplate(IRenderedComponent<Component> cut, string id) =>
        cut.Find("[data-testid=fa-template-select]").Change(id);

    private IntegrationConfig? Saved(string resource) =>
        _settings.Settings.Profiles.Single(p => p.Id == "p").Integrations.FirstOrDefault(i => i.Resource == resource);

    // ── 18. Environment matching is deterministic, not a display-name guess ──

    [Fact]
    public void TemplatesAreLookedUpByEnvironmentTypeNotByTheProfileDisplayName()
    {
        OpenAddFlow("QA");

        _requestedEnvironment.Should().Be("QA",
            "the profile is named \"M2LB QA\"; looking up by that name would find no templates at all");
    }

    // ── 22–23. QA renders the catalogue, grouped by transport ───────────────

    [Fact]
    public void QaRendersTheAvailableTemplatesGroupedByTransport()
    {
        var cut = OpenAddFlow("QA");

        cut.FindAll("[data-testid=fa-templates-empty]").Should().BeEmpty("QA has templates");
        var select = cut.Find("[data-testid=fa-template-select]");
        select.QuerySelectorAll("[data-testid=fa-template-group]").Select(g => g.GetAttribute("label"))
            .Should().BeEquivalentTo(new[] { "Event Hub", "Service Bus" });
        select.TextContent.Should().Contain("Person CDC").And.Contain("Leselogg");
    }

    // ── 30. DEV keeps the truthful empty state ──────────────────────────────

    [Fact]
    public void DevStillRendersTheNeutralNoTemplateEmptyState()
    {
        var cut = OpenAddFlow("Development");

        _requestedEnvironment.Should().Be("Development");
        cut.FindAll("[data-testid=fa-template-select]").Should().BeEmpty();
        cut.Find("[data-testid=fa-templates-empty]").TextContent
            .Should().Contain("No known M2LB templates are available for this environment.")
            .And.Contain("You can configure a custom integration instead.");
        cut.FindAll("[data-testid=fa-templates-empty-custom]").Should().ContainSingle();
    }

    // ── 24, 26–28. Preview shows only what the audit established ────────────

    [Fact]
    public void SelectingAnEventHubTemplatePreviewsItWithItsConsumerGroupAndNoFabricatedNamespace()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, PersonCdc.Id);

        var preview = cut.Find("[data-testid=fa-template-preview]");
        cut.Find("[data-testid=fa-template-resource]").TextContent.Trim().Should().Be("m2lb-cdc-qa.birk.dbo.person");
        cut.Find("[data-testid=fa-template-consumer-group]").TextContent.Trim().Should().Be("$Default");
        cut.Find("[data-testid=fa-template-producer]").TextContent.Trim().Should().Be("BiRK / Debezium");
        cut.Find("[data-testid=fa-template-consumer]").TextContent.Trim().Should().Be("PersonBiRKAdapter");

        // Namespace was never established, so the row is omitted rather than shown blank.
        preview.TextContent.Should().NotContain("Namespace");
        preview.QuerySelectorAll("dd").Should().OnlyContain(d => !string.IsNullOrWhiteSpace(d.TextContent));
    }

    [Fact]
    public void ServiceBusTemplatePreviewShowsNoConsumerGroupAndCarriesItsRelationshipNote()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, Leselogg.Id);

        cut.FindAll("[data-testid=fa-template-consumer-group]").Should().BeEmpty("Service Bus has subscriptions, not consumer groups");
        cut.FindAll("[data-testid=fa-template-producer]").Should().BeEmpty("no single producing service was established");
        cut.Find("[data-testid=fa-template-consumer]").TextContent.Trim().Should().Be("Revisjon");
        cut.Find("[data-testid=fa-template-note]").TextContent.Should().Contain("several services");
    }

    // ── 14, 25, 29. Selection previews; only Add integration accepts ────────

    [Fact]
    public void SelectingATemplateDoesNotAddOrPersistItByItself()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, PersonCdc.Id);

        cut.FindAll(".fa-integration-row").Should().BeEmpty("selection only previews");
        Saved(PersonCdc.Resource).Should().BeNull();
        cut.FindAll("[data-testid=fa-template-add]").Should().ContainSingle();
    }

    [Fact]
    public void AddIntegrationAcceptsTheTemplateAsASuggestionFromAuditedSource()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, PersonCdc.Id);
        cut.Find("[data-testid=fa-template-add]").Click();

        cut.FindAll(".fa-integration-row").Should().ContainSingle();
        cut.Find("[data-testid=fa-integration-source]").TextContent.Trim()
            .Should().Be("Suggested from audited M2LB source");
        // The accepted row claims a source reading, never verification or detection.
        cut.Find(".fa-integration-row").TextContent
            .Should().NotContainAny("Verified", "Runtime verified", "Detected", "Discovered");

        // Draft only until Save changes — acceptance is not persistence.
        Saved(PersonCdc.Resource).Should().BeNull();
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "birkNextStorage.setItem");
    }

    [Fact]
    public void SavingAnAcceptedTemplatePersistsItWithCodeSuggestedProvenance()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, PersonCdc.Id);
        cut.Find("[data-testid=fa-template-add]").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Saved(PersonCdc.Resource).Should().NotBeNull());
        var saved = Saved(PersonCdc.Resource)!;
        saved.ConfigurationSource.Should().Be(IntegrationConfigurationSource.CodeSuggested);
        saved.Type.Should().Be(IntegrationType.EventHub);
        saved.Consumer.Should().Be("$Default", "Consumer carries the Event Hub consumer group");
        saved.LogicalConsumerService.Should().Be("PersonBiRKAdapter");
        saved.Endpoint.Should().BeNullOrEmpty("namespace was never established and must not be invented");
    }

    // ── 17–18, 20–21. Identity, duplicates and manual precedence ────────────

    [Fact]
    public void AddingAnAlreadyConfiguredTemplateEnrichesItInsteadOfDuplicating()
    {
        // A person already configured this hub and typed their own producer.
        var existing = $$"""
        [{"id":"i1","name":"My own name","type":"EventHub","resource":"m2lb-cdc-qa.birk.dbo.person",
          "logicalProducerService":"Typed by a person","configurationSource":"Manual","enabled":true}]
        """;
        var cut = OpenAddFlow("QA", existing);
        SelectTemplate(cut, PersonCdc.Id);

        cut.Find("[data-testid=fa-template-existing]").TextContent.Should().Contain("already configured");
        cut.Find("[data-testid=fa-template-add]").Click();

        cut.FindAll(".fa-integration-row").Should().ContainSingle("structural identity matched, so nothing was duplicated");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Saved(PersonCdc.Resource)!.Consumer.Should().Be("$Default"));
        var saved = Saved(PersonCdc.Resource)!;
        saved.LogicalProducerService.Should().Be("Typed by a person", "a suggestion never overwrites a manual value");
        saved.Name.Should().Be("My own name", "the display name a person chose survives");
        saved.ConfigurationSource.Should().Be(IntegrationConfigurationSource.Manual, "provenance is not downgraded");
        // Values the person had not supplied are filled in from the audited suggestion.
        saved.LogicalConsumerService.Should().Be("PersonBiRKAdapter");
    }

    [Fact]
    public void IdentityIgnoresDisplayNameProducerAndConsumer()
    {
        // Same type + resource, everything descriptive different — still the same integration.
        var existing = $$"""
        [{"id":"i1","name":"Totally different label","type":"EventHub","resource":"m2lb-cdc-qa.birk.dbo.person",
          "logicalProducerService":"Someone else","logicalConsumerService":"Another service",
          "configurationSource":"Manual","enabled":true}]
        """;
        var cut = OpenAddFlow("QA", existing);
        SelectTemplate(cut, PersonCdc.Id);
        cut.Find("[data-testid=fa-template-add]").Click();

        cut.FindAll(".fa-integration-row").Should().ContainSingle();
    }

    // ── 31. The custom flow is unaffected ───────────────────────────────────

    [Fact]
    public void CustomIntegrationFlowStillWorksAlongsideTheCatalogue()
    {
        var cut = OpenAddFlow("QA");

        cut.Find("[data-testid=fa-add-mode-custom]").Change(true);
        cut.Find("[data-testid=fa-add-custom-confirm]").Click();

        cut.FindAll(".fa-integration-row").Should().ContainSingle();
        cut.FindAll("[data-testid=fa-template-preview]").Should().BeEmpty();
    }
}
