using AngleSharp.Html.Dom;
using AngleSharp.Dom;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Adding an integration is one editable form, opened directly. There is no catalogue to browse, no mode to
/// pick and no backend call, so it cannot fail because a service is unavailable.
///
/// The form starts from generic M2LB defaults and nothing more. It carries no indication of WHICH logical
/// integration is being created, so a namespace, a resource such as m2lb-cdc-qa.birk.dbo.person, or a
/// producer/consumer relationship would be a guess — right for one integration and wrong for every other, and
/// wrong in every environment but the one it came from. Unknown stays blank, and the user fills it in.
/// </summary>
public sealed class AddIntegrationFormTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();

    public AddIntegrationFormTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<Component> Open(string environmentType = "QA", string integrationsJson = "[]")
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

    private static IElement Field(IRenderedComponent<Component> cut, string name) =>
        cut.Find($"[data-testid=fa-new-{name}]");

    private static string Value(IRenderedComponent<Component> cut, string name) =>
        Field(cut, name).GetAttribute("value") ?? "";

    private static bool Has(IRenderedComponent<Component> cut, string testId) =>
        cut.FindAll($"[data-testid={testId}]").Count > 0;

    private static void Set(IRenderedComponent<Component> cut, string name, string value) =>
        Field(cut, name).Change(value);

    private static void Add(IRenderedComponent<Component> cut) =>
        cut.Find("[data-testid=fa-add-integration-confirm]").Click();

    private static bool AddDisabled(IRenderedComponent<Component> cut) =>
        cut.Find("[data-testid=fa-add-integration-confirm]").HasAttribute("disabled");

    private static void SaveChanges(IRenderedComponent<Component> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();

    private List<IntegrationConfig> Saved() => _settings.Settings.Profiles.Single(p => p.Id == "p").Integrations;

    /// <summary>A complete Event Hub identity, typed by the user as they always must.</summary>
    private static void FillEventHub(IRenderedComponent<Component> cut, string resource = "m2lb-cdc-qa.birk.dbo.person")
    {
        Set(cut, "name", "Person CDC");
        Set(cut, "field-endpoint", "m2lb-qa.servicebus.windows.net");
        Set(cut, "field-resource", resource);
    }

    // ── §24. The form opens directly, and the template UX is gone ────────────────────────────

    [Fact]
    public void AddIntegrationOpensTheEditableFormWithNoIntermediateChoice()
    {
        var cut = Open();

        cut.FindAll("[data-testid=fa-add-integration]").Should().ContainSingle();
        cut.Find("#fa-add-integration-heading").TextContent.Trim().Should().Be("Add integration");
        foreach (var field in new[] { "name", "type", "field-endpoint", "field-resource", "field-consumergroup",
                                      "field-producer", "field-consumer" })
            Has(cut, $"fa-new-{field}").Should().BeTrue(field);
    }

    [Fact]
    public void NoModeTemplateOrCatalogueSurfaceRemains()
    {
        var cut = Open();

        foreach (var gone in new[] { "fa-add-integration-flow", "fa-add-mode-known", "fa-add-mode-custom",
                                     "fa-template-select", "fa-template-preview", "fa-template-binding",
                                     "fa-templates-empty", "fa-templates-empty-custom", "fa-template-add",
                                     "fa-add-custom-confirm" })
            Has(cut, gone).Should().BeFalse(gone);

        cut.Markup.Should().NotContainAny(
            "Known M2LB integration", "Custom integration", "Available templates",
            "The known M2LB templates could not be loaded.", "Configure custom integration");
    }

    // ── §25. Defaults are generic, and every one of them is editable ─────────────────────────

    [Fact]
    public void TheDefaultsAreEventHubAndItsConsumerGroupAndNothingElse()
    {
        var cut = Open();

        Field(cut, "type").As<IHtmlSelectElement>().Value.Should().Be(nameof(IntegrationType.EventHub));
        Value(cut, "field-consumergroup").Should().Be("$Default");
        // Everything that identifies a particular integration, or names a service, is blank.
        foreach (var blank in new[] { "name", "field-endpoint", "field-resource", "field-producer", "field-consumer" })
            Value(cut, blank).Should().BeEmpty(blank);
    }

    [Fact]
    public void EveryPrefilledValueCanBeEdited()
    {
        var cut = Open();

        Set(cut, "field-consumergroup", "birk-consumer");
        Set(cut, "name", "Renamed");
        Field(cut, "type").Change(nameof(IntegrationType.Kafka));

        Value(cut, "field-consumergroup").Should().Be("birk-consumer");
        Value(cut, "name").Should().Be("Renamed");
        Field(cut, "type").As<IHtmlSelectElement>().Value.Should().Be(nameof(IntegrationType.Kafka));
    }

    // ── §26. Nothing environment-specific is prefilled, in any environment ───────────────────

    [Theory]
    [InlineData("Development")]
    [InlineData("QA")]
    [InlineData("Production")]
    public void NoEnvironmentReceivesAnotherEnvironmentsStructuralValues(string environmentType)
    {
        var cut = Open(environmentType);

        // No resource at all — not a QA one in DEV or PROD, and not a QA one in QA either: the form does not
        // know which logical integration this is, so there is nothing it could correctly name.
        Value(cut, "field-resource").Should().BeEmpty();
        Value(cut, "field-endpoint").Should().BeEmpty("a namespace is never guessed; only EventHub__FQDN keys were evidenced, never values");
        // Not even as an example: a QA resource name shown in a Production form is a wrong suggestion.
        cut.Markup.Should().NotContainAny("m2lb-cdc-qa", "m2lb-cdc-dev", "m2lb-cdc-prod");
    }

    [Fact]
    public void RelationshipFieldsAreNeverInferredFromWhatIsTyped()
    {
        var cut = Open();

        Set(cut, "name", "Barn CDC");
        Set(cut, "field-resource", "m2lb-cdc-qa.birk.dbo.barn");

        Value(cut, "field-producer").Should().BeEmpty("a display name is not a supported mapping source");
        Value(cut, "field-consumer").Should().BeEmpty();
        cut.Markup.Should().NotContainAny("PersonBiRKAdapter", "BiRK / Debezium", "Tjeneste API", "Hendelse BiRK Adapter");
    }

    // ── §27. Transport-specific fields follow the chosen type ────────────────────────────────

    [Fact]
    public void EventHubShowsAConsumerGroupAndServiceBusDoesNot()
    {
        var cut = Open();
        Has(cut, "fa-new-field-consumergroup").Should().BeTrue();

        Field(cut, "type").Change(nameof(IntegrationType.ServiceBus));

        Has(cut, "fa-new-field-consumergroup").Should().BeFalse("Service Bus has subscriptions, not consumer groups");
        Has(cut, "fa-new-field-resourcekind").Should().BeTrue();
        // A subscription name appears only once the entity is actually a subscription.
        Has(cut, "fa-new-field-subscription").Should().BeFalse();
        Field(cut, "field-resourcekind").Change(nameof(IntegrationResourceKind.ServiceBusSubscription));
        Has(cut, "fa-new-field-subscription").Should().BeTrue();
    }

    [Theory]
    [InlineData(IntegrationType.REST, "Endpoint", false)]
    [InlineData(IntegrationType.GraphQL, "Endpoint", false)]
    [InlineData(IntegrationType.Kafka, "Broker", true)]
    [InlineData(IntegrationType.RabbitMQ, "Broker", false)]
    public void EachTransportShowsTheFieldsItActuallyHas(IntegrationType type, string endpointLabel, bool consumerGroup)
    {
        var cut = Open();
        Field(cut, "type").Change(type.ToString());

        cut.Find("label[for=fa-endpoint-new]").TextContent.Trim().Should().Be(endpointLabel);
        Has(cut, "fa-new-field-consumergroup").Should().Be(consumerGroup);
        // HTTP transports carry their identity in the endpoint alone.
        Has(cut, "fa-new-field-resource").Should().Be(type is not (IntegrationType.REST or IntegrationType.GraphQL));
    }

    // ── §28. Structural identity gates the action ────────────────────────────────────────────

    [Fact]
    public void AddStaysDisabledUntilTheStructuralIdentityIsComplete()
    {
        var cut = Open();
        AddDisabled(cut).Should().BeTrue();
        cut.Find("[data-testid=fa-new-missing-fields]").TextContent
            .Should().Contain("Missing: Name, Namespace, Event Hub name");

        Set(cut, "name", "Person CDC");
        AddDisabled(cut).Should().BeTrue("a name is not an identity");
        Set(cut, "field-endpoint", "m2lb-qa.servicebus.windows.net");
        AddDisabled(cut).Should().BeTrue();
        Set(cut, "field-resource", "m2lb-cdc-qa.birk.dbo.person");

        AddDisabled(cut).Should().BeFalse();
        Has(cut, "fa-new-missing-fields").Should().BeFalse();
    }

    [Fact]
    public void AnIncompleteIntegrationIsNeverAddedEvenIfTheActionIsInvoked()
    {
        var cut = Open();
        Set(cut, "name", "Half-described");

        Add(cut);

        cut.FindAll(".fa-integration-row").Should().BeEmpty();
        cut.FindAll("[data-testid=fa-add-integration]").Should().ContainSingle("the form stays open with what was typed");
    }

    // ── §29. Draft, then save ────────────────────────────────────────────────────────────────

    [Fact]
    public void EditingTheFormTouchesNothingUntilAddIsPressed()
    {
        var cut = Open();
        FillEventHub(cut);

        cut.FindAll(".fa-integration-row").Should().BeEmpty();
        Saved().Should().BeEmpty();

        Add(cut);

        cut.FindAll(".fa-integration-row").Should().ContainSingle("added to the draft");
        Saved().Should().BeEmpty("the draft is not persisted until Save changes");
    }

    [Fact]
    public void SaveChangesPersistsTheIntegrationWithItsSuggestedProvenance()
    {
        var cut = Open();
        FillEventHub(cut);
        Add(cut);

        SaveChanges(cut);

        var saved = Saved().Should().ContainSingle().Subject;
        saved.Name.Should().Be("Person CDC");
        saved.Type.Should().Be(IntegrationType.EventHub);
        saved.Endpoint.Should().Be("m2lb-qa.servicebus.windows.net");
        saved.Resource.Should().Be("m2lb-cdc-qa.birk.dbo.person");
        saved.Consumer.Should().Be("$Default");
        saved.ConfigurationSource.Should().Be(IntegrationConfigurationSource.CodeSuggested);
        IntegrationConfigPresenter.SourceLabel(saved.ConfigurationSource)
            .Should().Be("Suggested from audited M2LB source");
    }

    [Fact]
    public void TheSourceIsStatedOnTheFormItself()
    {
        var cut = Open();

        cut.Find("[data-testid=fa-new-source]").TextContent.Trim()
            .Should().Be("Suggested from audited M2LB source");
    }

    [Fact]
    public void AStructurallyIdenticalIntegrationIsOpenedRatherThanDuplicated()
    {
        var cut = Open(integrationsJson: """
            [{"id":"existing","name":"Person CDC","type":"EventHub",
              "endpoint":"m2lb-qa.servicebus.windows.net","resource":"m2lb-cdc-qa.birk.dbo.person",
              "consumer":"$Default","configurationSource":"Manual"}]
            """);

        // A different display name and different relationship fields: neither takes part in identity.
        Set(cut, "name", "Person change feed");
        Set(cut, "field-endpoint", "m2lb-qa.servicebus.windows.net");
        Set(cut, "field-resource", "m2lb-cdc-qa.birk.dbo.person");
        Set(cut, "field-producer", "BiRK / Debezium");
        Add(cut);
        SaveChanges(cut);

        var saved = Saved().Should().ContainSingle("type, endpoint and resource already identify this integration").Subject;
        saved.Id.Should().Be("existing");
        saved.Name.Should().Be("Person CDC", "the configured record is not overwritten by the form");
        saved.ConfigurationSource.Should().Be(IntegrationConfigurationSource.Manual, "a stronger provenance is not downgraded");
    }

    [Fact]
    public void ADifferentResourceIsADifferentIntegration()
    {
        var cut = Open(integrationsJson: """
            [{"id":"existing","name":"Person CDC","type":"EventHub",
              "endpoint":"m2lb-qa.servicebus.windows.net","resource":"m2lb-cdc-qa.birk.dbo.person"}]
            """);

        FillEventHub(cut, "m2lb-cdc-qa.birk.dbo.barn");
        Add(cut);
        SaveChanges(cut);

        Saved().Should().HaveCount(2);
    }

    // ── §21. One form, whether adding or editing ─────────────────────────────────────────────

    [Fact]
    public void TheConfiguredIntegrationEditorIsTheSameFormAsTheAddForm()
    {
        var cut = Open();
        FillEventHub(cut);
        Add(cut);

        // The new integration is expanded for editing, and shows the same fields under the row test ids.
        foreach (var field in new[] { "fa-field-endpoint", "fa-field-resource", "fa-field-consumergroup",
                                      "fa-field-producer", "fa-field-consumer" })
            Has(cut, field).Should().BeTrue(field);

        // Transport behaviour is identical, because it is the same component.
        cut.Find("[data-testid=fa-type]").Change(nameof(IntegrationType.ServiceBus));
        Has(cut, "fa-field-consumergroup").Should().BeFalse();
        Has(cut, "fa-field-resourcekind").Should().BeTrue();
    }
}
