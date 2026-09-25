using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// The catalogue of integrations known from an external audit of the M2LB source.
///
/// The catalogue is now two things, and these tests keep them apart. A TEMPLATE is reusable
/// knowledge — "Person CDC" is one logical integration, the same in every environment. A BINDING is
/// what one environment calls it. Most of these tests exist to police what must NOT happen across
/// that line: a template must not carry a QA hub name, a DEV binding must not be derived from a QA
/// one, and an environment without bindings must not lose the catalogue.
/// </summary>
public class KnownIntegrationTemplatesTests
{
    private static IReadOnlyList<KnownIntegrationTemplateView> For(string? environmentType) =>
        KnownIntegrationTemplates.ForEnvironment(environmentType);

    private static KnownIntegrationTemplateView View(string environmentType, string displayName) =>
        For(environmentType).Single(v => v.Template.DisplayName == displayName);

    private static KnownIntegrationTemplate Template(string displayName) =>
        KnownIntegrationTemplates.All.Single(t => t.DisplayName == displayName);

    // ── §62. Templates are reusable and environment-independent ──────────────────────────────

    // 1, 2, 3.
    [Theory]
    [InlineData("Person CDC", IntegrationType.EventHub)]
    [InlineData("Barn CDC", IntegrationType.EventHub)]
    [InlineData("Tiltak CDC", IntegrationType.EventHub)]
    [InlineData("Leselogg", IntegrationType.ServiceBus)]
    [InlineData("Hendelser Barn", IntegrationType.ServiceBus)]
    [InlineData("Autorisasjon Roller", IntegrationType.ServiceBus)]
    public void ALogicalTemplateExistsIndependentlyOfAnyEnvironment(string displayName, IntegrationType type)
    {
        var template = Template(displayName);

        Assert.Equal(type, template.IntegrationType);
        // It is reachable in every environment, including ones with no binding at all.
        foreach (var environment in new[] { "Development", "QA", "Production", "", null })
            Assert.Contains(For(environment), v => v.Template.Id == template.Id);
    }

    // 4. The logical fields are identical whatever environment resolved them.
    [Fact]
    public void TemplateFieldsAreStableAcrossEnvironments()
    {
        var dev = For("Development").Select(v => v.Template).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        var qa = For("QA").Select(v => v.Template).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        var prod = For("Production").Select(v => v.Template).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();

        Assert.Equal(dev, qa);
        Assert.Equal(dev, prod);
    }

    // 5, 6. The catalogue never empties because of the environment.
    [Theory]
    [InlineData("Development")]
    [InlineData("DEV")]
    [InlineData("Production")]
    [InlineData("PROD")]
    [InlineData("QA")]
    [InlineData("")]
    public void TheCatalogueIsOfferedInEveryEnvironment(string environmentType)
    {
        var views = For(environmentType);

        Assert.Equal(20, views.Count);
        Assert.Equal(9, views.Count(v => v.Template.IntegrationType == IntegrationType.EventHub));
        Assert.Equal(11, views.Count(v => v.Template.IntegrationType == IntegrationType.ServiceBus));
    }

    // A template carries no environment-specific value at all: that is the whole point of the split.
    [Fact]
    public void NoTemplateCarriesAnEnvironmentSpecificValue()
    {
        foreach (var template in KnownIntegrationTemplates.All)
        {
            var text = $"{template.Id} {template.DisplayName} {template.SuggestedProducer} {template.SuggestedConsumer}";
            Assert.DoesNotContain("-qa.", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-dev.", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-prod.", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── §63. Environment bindings ────────────────────────────────────────────────────────────

    // 8. QA resolves the audited values exactly, casing included.
    [Theory]
    [InlineData("Person CDC", "m2lb-cdc-qa.birk.dbo.person")]
    [InlineData("Barn CDC", "m2lb-cdc-qa.birk.dbo.barn")]
    [InlineData("Tiltak CDC", "m2lb-cdc-qa.birk.dbo.tiltak")]
    [InlineData("Bestilling CDC", "m2lb-cdc-qa.birk.dbo.bestilling")]
    [InlineData("TjenesteType CDC", "m2lb-cdc-qa.birk.dbo.tjenesteType")]
    [InlineData("TiltaksStatusType CDC", "m2lb-cdc-qa.birk.dbo.tiltaksStatusType")]
    [InlineData("AvslutningsGrunnType CDC", "m2lb-cdc-qa.birk.dbo.avslutningsGrunnType")]
    [InlineData("Tvangsprotokoll CDC", "m2lb-cdc-qa.birk.dbo.tvangsprotokoll")]
    [InlineData("Rømning CDC", "m2lb-cdc-qa.birk.dbo.romning")]
    [InlineData("Leselogg", "leselogg")]
    [InlineData("Operasjonsregistrering", "operasjonsregistrering")]
    [InlineData("BiRK Adapter Errors", "birk-adapter-errors")]
    [InlineData("Operatørkontroll Varsler", "operatorkontroll.varsler")]
    [InlineData("Hendelser Barn", "hendelser.barn")]
    [InlineData("Tjeneste Tjenester", "tjeneste.tjenester")]
    [InlineData("Autorisasjon Operasjoner", "autorisasjon.operasjoner")]
    [InlineData("Autorisasjon Roller", "autorisasjon.roller")]
    [InlineData("Autorisasjon Tilganger", "autorisasjon.tilganger")]
    [InlineData("Autorisasjon Nødtilganger", "autorisasjon.nodtilganger")]
    [InlineData("Autorisasjon Organisasjon", "autorisasjon.organisasjon")]
    public void QaBindingsResolveTheAuditedResourceExactly(string displayName, string resource)
    {
        var view = View("QA", displayName);

        Assert.Equal(resource, view.Binding?.Resource);
        Assert.Empty(view.MissingRequiredFields);
    }

    // 9, 10. Nothing is fabricated for an environment the audit never covered.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void NoBindingIsFabricatedForAnUnevidencedEnvironment(string environmentType)
    {
        foreach (var view in For(environmentType))
        {
            Assert.Null(view.Binding);
            Assert.Contains(KnownIntegrationTemplates.ResourceField, view.MissingRequiredFields);
        }
    }

    // 9, 10 again, at the point it would actually be tempting: a QA value must never reach DEV/PROD.
    [Fact]
    public void NoQaResourceLeaksIntoAnotherEnvironment()
    {
        foreach (var environment in new[] { "Development", "DEV", "Production", "PROD" })
            Assert.All(For(environment), v => Assert.Null(v.Binding?.Resource));
    }

    [Fact]
    public void NoResourceNameWasProducedBySubstitutingTheEnvironment()
    {
        // A "dev"/"prod" twin of an evidenced QA name would be an invention.
        var bindings = new[] { "Development", "DEV", "Production", "PROD", "QA" }
            .SelectMany(For)
            .Select(v => v.Binding?.Resource)
            .Where(r => r is not null)
            .ToList();

        Assert.DoesNotContain(bindings, r => r!.Contains("-dev.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bindings, r => r!.Contains("-prod.", StringComparison.OrdinalIgnoreCase));
        // Every binding that exists is a QA one, so nothing here can be another environment's twin.
        Assert.All(bindings, r => Assert.Contains(r!, For("QA").Select(v => v.Binding?.Resource)));
    }

    // 11. The audit never established a namespace, in any environment.
    [Fact]
    public void NoBindingInventsANamespace() =>
        Assert.All(new[] { "Development", "QA", "Production" }.SelectMany(For),
            v => Assert.Null(v.Binding?.EndpointOrNamespace));

    // 12. Service Bus has subscriptions, not consumer groups.
    [Fact]
    public void ServiceBusBindingsCarryNoConsumerGroup() =>
        Assert.All(For("QA").Where(v => v.Template.IntegrationType == IntegrationType.ServiceBus),
            v => Assert.Null(v.Binding?.ConsumerGroup));

    // 13. The Event Hub consumer group resolves only where it was evidenced.
    [Fact]
    public void EventHubConsumerGroupResolvesOnlyWhereEvidenced()
    {
        Assert.All(For("QA").Where(v => v.Template.IntegrationType == IntegrationType.EventHub),
            v => Assert.Equal("$Default", v.Binding?.ConsumerGroup));

        // Not evidenced for DEV or PROD, so it is absent rather than assumed to be a global default.
        foreach (var environment in new[] { "Development", "Production" })
            Assert.All(For(environment), v => Assert.Null(v.Binding?.ConsumerGroup));
    }

    [Fact]
    public void NoBindingUsesTheLowerCaseConsumerGroupSpelling() =>
        Assert.All(For("QA"), v => Assert.NotEqual("$default", v.Binding?.ConsumerGroup));

    // 14. Lookup is by environment TYPE; a profile display name resolves nothing.
    [Fact]
    public void BindingLookupUsesEnvironmentTypeNotProfileDisplayName()
    {
        Assert.NotNull(KnownIntegrationTemplates.BindingFor("eh-person", "QA"));
        Assert.NotNull(KnownIntegrationTemplates.BindingFor("eh-person", "qa"));

        Assert.Null(KnownIntegrationTemplates.BindingFor("eh-person", "M2LB QA"));
        Assert.Null(KnownIntegrationTemplates.BindingFor("eh-person", "M2LB DEV"));
        Assert.Null(KnownIntegrationTemplates.BindingFor("eh-person", null));
    }

    // ── Resource kinds and relationships (unchanged invariants) ──────────────────────────────

    [Theory]
    [InlineData("Leselogg")]
    [InlineData("Operasjonsregistrering")]
    [InlineData("BiRK Adapter Errors")]
    [InlineData("Operatørkontroll Varsler")]
    public void QueueEntities_AreQueues(string displayName) =>
        Assert.Equal(IntegrationResourceKind.ServiceBusQueue, Template(displayName).ResourceKind);

    [Theory]
    [InlineData("Hendelser Barn")]
    [InlineData("Tjeneste Tjenester")]
    [InlineData("Autorisasjon Operasjoner")]
    [InlineData("Autorisasjon Roller")]
    [InlineData("Autorisasjon Tilganger")]
    [InlineData("Autorisasjon Nødtilganger")]
    [InlineData("Autorisasjon Organisasjon")]
    public void TopicEntities_AreTopics(string displayName) =>
        Assert.Equal(IntegrationResourceKind.ServiceBusTopic, Template(displayName).ResourceKind);

    [Fact]
    public void NoSubscriptionsAreGuessed() =>
        Assert.DoesNotContain(KnownIntegrationTemplates.All,
            t => t.ResourceKind == IntegrationResourceKind.ServiceBusSubscription);

    // §60. Relationship evidence is reusable, and absent where the audit established nothing.
    [Fact]
    public void PersonCdc_CarriesItsProvenRelationship()
    {
        var person = Template("Person CDC");

        Assert.Equal("BiRK / Debezium", person.SuggestedProducer);
        Assert.Equal("PersonBiRKAdapter", person.SuggestedConsumer);
    }

    [Theory]
    [InlineData("Barn CDC", "PersonBiRKAdapter")]
    [InlineData("Tiltak CDC", "Tjeneste API")]
    [InlineData("Bestilling CDC", "Tjeneste API")]
    [InlineData("TjenesteType CDC", "Tjeneste API")]
    [InlineData("TiltaksStatusType CDC", "Tjeneste API")]
    [InlineData("AvslutningsGrunnType CDC", "Tjeneste API")]
    [InlineData("Tvangsprotokoll CDC", "Hendelse BiRK Adapter")]
    [InlineData("Rømning CDC", "Hendelse BiRK Adapter")]
    public void EachCdcHubNamesItsAuditedConsumingService(string displayName, string consumer)
    {
        var template = Template(displayName);

        Assert.Equal(consumer, template.SuggestedConsumer);
        // Only the person hub had a producing service established; the rest stay unknown rather
        // than inferring "BiRK / Debezium" from the fact that the data comes from BiRK CDC.
        Assert.Null(template.SuggestedProducer);
    }

    [Fact]
    public void Leselogg_HasProvenConsumerButNoSingleProducer()
    {
        var leselogg = Template("Leselogg");

        Assert.Equal("Revisjon", leselogg.SuggestedConsumer);
        Assert.Null(leselogg.SuggestedProducer);
        Assert.Contains("several services", leselogg.RelationshipNote);
    }

    // §59. The audited queue name stands; sample data is not deployment evidence.
    [Fact]
    public void Leselogg_KeepsTheAuditedQueueName() =>
        Assert.Equal("leselogg", View("QA", "Leselogg").Binding?.Resource);

    // ── Provenance ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProvenanceNamesAnAuditedSourceReadingNotVerification() =>
        Assert.All(KnownIntegrationTemplates.All, t =>
        {
            Assert.Equal("Suggested from audited M2LB source", t.SuggestionOrigin);
            Assert.DoesNotContain("Verified", t.SuggestionOrigin, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("this repository", t.SuggestionOrigin, StringComparison.OrdinalIgnoreCase);
        });

    [Fact]
    public void ProvenanceExposesNoFilePaths() =>
        Assert.All(KnownIntegrationTemplates.All, t =>
        {
            Assert.DoesNotContain(":\\", t.SuggestionOrigin);
            Assert.DoesNotContain("/Users/", t.SuggestionOrigin);
            Assert.DoesNotContain(".cs", t.SuggestionOrigin);
        });

    // ── §64. Acceptance ──────────────────────────────────────────────────────────────────────

    // 19. Accepting a template records it as a source reading, not as verification.
    [Fact]
    public void MaterialisedIntegration_IsMarkedCodeSuggested()
    {
        var integration = View("QA", "Person CDC").ToIntegration("new-id")!;

        Assert.Equal(IntegrationConfigurationSource.CodeSuggested, integration.ConfigurationSource);
        Assert.Equal(IntegrationType.EventHub, integration.Type);
        Assert.Equal(IntegrationResourceKind.EventHub, integration.ResourceKind);
        Assert.Equal("m2lb-cdc-qa.birk.dbo.person", integration.Resource);
        Assert.Equal("PersonBiRKAdapter", integration.LogicalConsumerService);
        Assert.Equal("$Default", integration.Consumer);
    }

    // 22. An integration whose structural identity is unknown is never produced.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void NoIntegrationIsMaterialisedWhileItsStructuralIdentityIsUnknown(string environmentType) =>
        Assert.All(For(environmentType), v => Assert.Null(v.ToIntegration("id")));

    // §41. Required fields are the ones structural identity actually needs.
    [Fact]
    public void RequiredFieldsAreTheStructuralOnesOnly()
    {
        Assert.Equal([KnownIntegrationTemplates.ResourceField], KnownIntegrationTemplates.RequiredFieldsFor(IntegrationType.EventHub));
        Assert.Equal([KnownIntegrationTemplates.ResourceField], KnownIntegrationTemplates.RequiredFieldsFor(IntegrationType.ServiceBus));
        Assert.Equal([KnownIntegrationTemplates.EndpointField], KnownIntegrationTemplates.RequiredFieldsFor(IntegrationType.REST));

        // The namespace is deliberately not required: the audit never established one, and every
        // QA template would be unusable if it were.
        Assert.DoesNotContain(KnownIntegrationTemplates.EndpointField, KnownIntegrationTemplates.RequiredFieldsFor(IntegrationType.EventHub));
    }

    // ── §49. Baseline identity is untouched by any of this ───────────────────────────────────

}
