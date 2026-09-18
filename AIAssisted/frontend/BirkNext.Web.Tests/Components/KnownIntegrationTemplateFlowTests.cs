using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Known M2LB templates are reusable logical integrations, and they are offered in every environment.
/// What varies is the binding: QA has audited values, Development and Production have none yet. A
/// template with no binding is not a missing template — it is a template whose environment values the
/// user has still to supply, and until they do, nothing is persisted.
///
/// Templates remain suggestions from an audited reading of the target's source, never detections and
/// never runtime verification. Selecting one previews it; only the explicit Add integration action
/// writes to the draft, and a value a person entered is never overwritten.
/// </summary>
public sealed class KnownIntegrationTemplateFlowTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<IIntegrationTemplateService> _templates = new();
    private string? _requestedEnvironment;

    /// <summary>The reusable definitions. Identical whatever environment asks for them.</summary>
    private static readonly KnownIntegrationTemplate PersonCdc = new()
    {
        Id = "eh-person", DisplayName = "Person CDC",
        IntegrationType = IntegrationType.EventHub, ResourceKind = IntegrationResourceKind.EventHub,
        SuggestedProducer = "BiRK / Debezium", SuggestedConsumer = "PersonBiRKAdapter",
        SuggestionOrigin = "Suggested from audited M2LB source",
    };

    private static readonly KnownIntegrationTemplate Leselogg = new()
    {
        Id = "sb-leselogg", DisplayName = "Leselogg",
        IntegrationType = IntegrationType.ServiceBus, ResourceKind = IntegrationResourceKind.ServiceBusQueue,
        SuggestedConsumer = "Revisjon",
        RelationshipNote = "Written to by several services; the audited source did not identify a single producing service.",
        SuggestionOrigin = "Suggested from audited M2LB source",
    };

    /// <summary>The backend's view: the same templates every time, with the binding the environment has.</summary>
    private static List<KnownIntegrationTemplateView> Catalogue(string environmentType)
    {
        var qa = string.Equals(environmentType, "QA", StringComparison.OrdinalIgnoreCase);
        return
        [
            new KnownIntegrationTemplateView
            {
                Template = PersonCdc, EnvironmentType = environmentType,
                Binding = qa ? new KnownIntegrationEnvironmentBinding
                {
                    TemplateId = PersonCdc.Id, EnvironmentType = "QA",
                    Resource = "m2lb-cdc-qa.birk.dbo.person", ConsumerGroup = "$Default",
                } : null,
                RequiredFields = ["Resource"],
                MissingRequiredFields = qa ? [] : ["Resource"],
            },
            new KnownIntegrationTemplateView
            {
                Template = Leselogg, EnvironmentType = environmentType,
                Binding = qa ? new KnownIntegrationEnvironmentBinding
                {
                    TemplateId = Leselogg.Id, EnvironmentType = "QA", Resource = "leselogg",
                } : null,
                RequiredFields = ["Resource"],
                MissingRequiredFields = qa ? [] : ["Resource"],
            },
        ];
    }

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
                return Catalogue(env ?? "");
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

    private static void Supply(IRenderedComponent<Component> cut, string field, string value) =>
        cut.Find($"[data-testid=fa-template-field-{field}]").Change(value);

    private static void SaveChanges(IRenderedComponent<Component> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();

    private static bool AddDisabled(IRenderedComponent<Component> cut) =>
        cut.Find("[data-testid=fa-template-add]").HasAttribute("disabled");

    private List<IntegrationConfig> SavedIntegrations() =>
        _settings.Settings.Profiles.Single(p => p.Id == "p").Integrations;

    private IntegrationConfig? Saved(string resource) =>
        SavedIntegrations().FirstOrDefault(i => i.Resource == resource);

    // ── §63.14. Environment matching is deterministic, not a display-name guess ───────────────

    [Fact]
    public void TemplatesAreLookedUpByEnvironmentTypeNotByTheProfileDisplayName()
    {
        OpenAddFlow("QA");

        _requestedEnvironment.Should().Be("QA",
            "the profile is named \"M2LB QA\"; looking up by that name would find no binding at all");
    }

    // ── §65. Development ─────────────────────────────────────────────────────────────────────

    // 23, 26. The catalogue is reusable knowledge, so Development sees all of it.
    [Fact]
    public void DevelopmentShowsEveryLogicalTemplate()
    {
        var cut = OpenAddFlow("Development");

        cut.FindAll("[data-testid=fa-template-select] option").Select(o => o.TextContent.Trim())
            .Should().Contain(["Person CDC", "Leselogg"]);
        cut.FindAll("[data-testid=fa-template-group]").Should().HaveCount(2);
        cut.FindAll("[data-testid=fa-templates-empty]").Should().BeEmpty();
        cut.Markup.Should().NotContain("No known M2LB templates are available for this environment");
    }

    // 24. The reusable half of the preview is the same knowledge QA sees.
    [Fact]
    public void DevelopmentPreviewShowsTheReusableTemplateKnowledge()
    {
        var cut = OpenAddFlow("Development");
        SelectTemplate(cut, PersonCdc.Id);

        var template = cut.Find("[data-testid=fa-template-preview]");
        template.TextContent.Should().Contain("Event Hub");
        cut.Find("[data-testid=fa-template-producer]").TextContent.Should().Be("BiRK / Debezium");
        cut.Find("[data-testid=fa-template-consumer]").TextContent.Should().Be("PersonBiRKAdapter");
        cut.Find("[data-testid=fa-template-source]").TextContent.Should().Be("Suggested from audited M2LB source");
    }

    // 25. An unknown environment value is stated as unknown — never filled in from QA.
    [Fact]
    public void DevelopmentPreviewStatesItsMissingValuesWithoutBorrowingQaValues()
    {
        var cut = OpenAddFlow("Development");
        SelectTemplate(cut, PersonCdc.Id);

        cut.Find("[data-testid=fa-template-binding-heading]").TextContent.Should().Be("Development values");
        cut.Find("[data-testid=fa-template-resource]").TextContent.Trim().Should().Be("Not defined for Development");
        cut.Find("[data-testid=fa-template-namespace]").TextContent.Trim().Should().Be("Not defined for Development");
        cut.Find("[data-testid=fa-template-binding]").TextContent.Should().NotContain("m2lb-cdc-qa");
    }

    // 27, 28. Add is disabled until the structural value exists, then enabled.
    [Fact]
    public void DevelopmentAddIsDisabledUntilTheRequiredValueIsSupplied()
    {
        var cut = OpenAddFlow("Development");
        SelectTemplate(cut, PersonCdc.Id);

        AddDisabled(cut).Should().BeTrue();
        cut.Find("[data-testid=fa-template-required-reason]").TextContent
            .Should().Contain("Resource").And.Contain("Development");

        Supply(cut, "Resource", "m2lb-cdc-dev.birk.dbo.person");

        AddDisabled(cut).Should().BeFalse();
    }

    // 22 (§64). Nothing is persisted while the structural identity is unknown.
    [Fact]
    public void DevelopmentAddDoesNothingWhileTheStructuralIdentityIsUnknown()
    {
        var cut = OpenAddFlow("Development");
        SelectTemplate(cut, PersonCdc.Id);

        cut.Find("[data-testid=fa-template-add]").Click();

        SavedIntegrations().Should().BeEmpty("an integration without its resource could never be matched again");
    }

    // 17, 18. The supplied value becomes the configured integration's own structural value.
    [Fact]
    public void ASuppliedDevelopmentValueBecomesTheConfiguredIntegration()
    {
        var cut = OpenAddFlow("Development");
        SelectTemplate(cut, PersonCdc.Id);
        Supply(cut, "Resource", "m2lb-cdc-dev.birk.dbo.person");

        cut.Find("[data-testid=fa-template-add]").Click();
        SaveChanges(cut);

        cut.WaitForAssertion(() => Saved("m2lb-cdc-dev.birk.dbo.person").Should().NotBeNull());
        var saved = Saved("m2lb-cdc-dev.birk.dbo.person");
        saved.Should().NotBeNull();
        saved!.Type.Should().Be(IntegrationType.EventHub);
        saved.LogicalConsumerService.Should().Be("PersonBiRKAdapter");
        // 19. Supplying an environment value does not turn a suggestion into a verified fact.
        saved.ConfigurationSource.Should().Be(IntegrationConfigurationSource.CodeSuggested);
        IntegrationConfigPresenter.SourceLabel(saved.ConfigurationSource).Should().Be("Suggested from audited M2LB source");
    }

    // ── §66. QA ──────────────────────────────────────────────────────────────────────────────

    // 29, 30, 31, 32.
    [Fact]
    public void QaResolvesItsAuditedBindingWithoutFabricatingANamespace()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, PersonCdc.Id);

        cut.FindAll("[data-testid=fa-template-select] option").Select(o => o.TextContent.Trim())
            .Should().Contain(["Person CDC", "Leselogg"]);
        cut.Find("[data-testid=fa-template-binding-heading]").TextContent.Should().Be("QA values");
        cut.Find("[data-testid=fa-template-resource]").TextContent.Trim().Should().Be("m2lb-cdc-qa.birk.dbo.person");
        cut.Find("[data-testid=fa-template-consumer-group]").TextContent.Trim().Should().Be("$Default");
        cut.Find("[data-testid=fa-template-namespace]").TextContent.Trim().Should().Be("Not defined for QA");
    }

    // 33. A complete binding needs nothing from the user.
    [Fact]
    public void QaAcceptsTheTemplateWithoutAskingForAnything()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, PersonCdc.Id);

        AddDisabled(cut).Should().BeFalse();
        cut.FindAll("[data-testid=fa-template-required]").Should().BeEmpty();

        cut.Find("[data-testid=fa-template-add]").Click();
        SaveChanges(cut);

        cut.WaitForAssertion(() => Saved("m2lb-cdc-qa.birk.dbo.person").Should().NotBeNull());
    }

    // §58. Service Bus has subscriptions, so no consumer group field is offered at all.
    [Fact]
    public void ServiceBusTemplateOffersNoConsumerGroup()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, Leselogg.Id);

        cut.FindAll("[data-testid=fa-template-consumer-group]").Should().BeEmpty();
        cut.Find("[data-testid=fa-template-note]").TextContent.Should().Contain("several services");
        cut.Find("[data-testid=fa-template-resource]").TextContent.Trim().Should().Be("leselogg");
    }

    // ── §67. Production ──────────────────────────────────────────────────────────────────────

    // 34, 35, 36, 37.
    [Fact]
    public void ProductionShowsTheTemplatesAndCopiesNothingFromQa()
    {
        var cut = OpenAddFlow("Production");

        cut.FindAll("[data-testid=fa-template-select] option").Select(o => o.TextContent.Trim())
            .Should().Contain(["Person CDC", "Leselogg"]);

        SelectTemplate(cut, PersonCdc.Id);

        cut.Find("[data-testid=fa-template-binding-heading]").TextContent.Should().Be("Production values");
        cut.Find("[data-testid=fa-template-resource]").TextContent.Trim().Should().Be("Not defined for Production");
        cut.Markup.Should().NotContain("m2lb-cdc-qa");
        AddDisabled(cut).Should().BeTrue();
    }

    // ── §64. Acceptance, deduplication and manual precedence ─────────────────────────────────

    // 15. Selecting only previews.
    [Fact]
    public void SelectingATemplateDoesNotPersistAnything()
    {
        var cut = OpenAddFlow("QA");
        SelectTemplate(cut, PersonCdc.Id);

        cut.Find("[data-testid=fa-template-preview]").Should().NotBeNull();
        SavedIntegrations().Should().BeEmpty();
    }

    // 20, 21. An equivalent integration is enriched, never duplicated, and never overwritten.
    [Fact]
    public void AnExistingIntegrationIsFilledInRatherThanDuplicated()
    {
        var existing = """
        [{"id":"i1","name":"My own name","type":2,"resource":"m2lb-cdc-qa.birk.dbo.person",
          "configurationSource":3,"enabled":true}]
        """;
        var cut = OpenAddFlow("QA", existing);
        SelectTemplate(cut, PersonCdc.Id);

        cut.Find("[data-testid=fa-template-existing]").Should().NotBeNull();
        cut.Find("[data-testid=fa-template-add]").Click();
        SaveChanges(cut);

        cut.WaitForAssertion(() => SavedIntegrations().Should().ContainSingle("the structural identity already existed"));
        var saved = SavedIntegrations()[0];
        saved.Name.Should().Be("My own name", "a value a person entered is never overwritten");
        saved.ConfigurationSource.Should().Be(IntegrationConfigurationSource.Manual, "Manual outranks CodeSuggested");
        saved.LogicalConsumerService.Should().Be("PersonBiRKAdapter", "an empty field is still filled in");
    }

    // 21. No structural match is claimed while the structural values are unknown, so no false merge.
    [Fact]
    public void NoDeduplicationIsAttemptedWhileTheStructuralValuesAreUnknown()
    {
        var existing = """
        [{"id":"i1","name":"Person CDC","type":2,"resource":"m2lb-cdc-qa.birk.dbo.person",
          "configurationSource":3,"enabled":true}]
        """;
        var cut = OpenAddFlow("Development", existing);
        SelectTemplate(cut, PersonCdc.Id);

        // The names match, but Development has no resource yet — matching on the name would merge
        // two different integrations.
        cut.FindAll("[data-testid=fa-template-existing]").Should().BeEmpty();

        Supply(cut, "Resource", "m2lb-cdc-dev.birk.dbo.person");
        cut.Find("[data-testid=fa-template-add]").Click();
        SaveChanges(cut);

        cut.WaitForAssertion(() => SavedIntegrations().Should().HaveCount(2, "a different resource is a different integration, correctly"));
    }
}
