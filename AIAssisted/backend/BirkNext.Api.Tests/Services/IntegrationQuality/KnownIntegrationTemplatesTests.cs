using System.Text.Json;
using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// The catalogue of integrations known from an external audit of the M2LB source.
///
/// Most of these tests exist to police what the catalogue must NOT contain: environment names
/// derived by substitution, namespaces and consumer groups that were never evidenced, and
/// service identities inferred from resource names.
/// </summary>
public class KnownIntegrationTemplatesTests
{
    private static IReadOnlyList<KnownIntegrationTemplate> Qa() =>
        KnownIntegrationTemplates.ForEnvironment("QA");

    // ── Event Hub coverage ───────────────────────────────────────────────────

    [Theory]
    [InlineData("m2lb-cdc-qa.birk.dbo.person")]
    [InlineData("m2lb-cdc-qa.birk.dbo.barn")]
    [InlineData("m2lb-cdc-qa.birk.dbo.tiltak")]
    [InlineData("m2lb-cdc-qa.birk.dbo.bestilling")]
    [InlineData("m2lb-cdc-qa.birk.dbo.tjenesteType")]
    [InlineData("m2lb-cdc-qa.birk.dbo.tiltaksStatusType")]
    [InlineData("m2lb-cdc-qa.birk.dbo.avslutningsGrunnType")]
    [InlineData("m2lb-cdc-qa.birk.dbo.tvangsprotokoll")]
    [InlineData("m2lb-cdc-qa.birk.dbo.romning")]
    public void QaCatalogue_ContainsEventHub(string resource) =>
        Assert.Contains(Qa(), t => t.Resource == resource
                                   && t.IntegrationType == IntegrationType.EventHub
                                   && t.ResourceKind == IntegrationResourceKind.EventHub);

    [Fact]
    public void QaCatalogue_ContainsExactlyTheNineEvidencedEventHubs() =>
        Assert.Equal(9, Qa().Count(t => t.IntegrationType == IntegrationType.EventHub));

    [Fact]
    public void ResourceCasing_IsPreservedExactly()
    {
        // Broker entity names can be case-sensitive, and the audited spelling is authoritative.
        Assert.Contains(Qa(), t => t.Resource == "m2lb-cdc-qa.birk.dbo.tjenesteType");
        Assert.DoesNotContain(Qa(), t => t.Resource == "m2lb-cdc-qa.birk.dbo.tjenestetype");
    }

    // ── Service Bus entity kinds ─────────────────────────────────────────────

    [Theory]
    [InlineData("leselogg")]
    [InlineData("operasjonsregistrering")]
    [InlineData("birk-adapter-errors")]
    [InlineData("operatorkontroll.varsler")]
    public void QueueEntities_AreQueues(string resource) =>
        Assert.Contains(Qa(), t => t.Resource == resource
                                   && t.IntegrationType == IntegrationType.ServiceBus
                                   && t.ResourceKind == IntegrationResourceKind.ServiceBusQueue);

    [Theory]
    [InlineData("hendelser.barn")]
    [InlineData("tjeneste.tjenester")]
    [InlineData("autorisasjon.operasjoner")]
    [InlineData("autorisasjon.roller")]
    [InlineData("autorisasjon.tilganger")]
    [InlineData("autorisasjon.nodtilganger")]
    [InlineData("autorisasjon.organisasjon")]
    public void TopicEntities_AreTopics(string resource) =>
        Assert.Contains(Qa(), t => t.Resource == resource
                                   && t.ResourceKind == IntegrationResourceKind.ServiceBusTopic);

    [Fact]
    public void NoSubscriptionsAreGuessed() =>
        Assert.DoesNotContain(Qa(), t => t.ResourceKind == IntegrationResourceKind.ServiceBusSubscription);

    // ── Relationships: only where proven ─────────────────────────────────────

    [Fact]
    public void PersonCdc_CarriesItsProvenRelationship()
    {
        var person = Qa().Single(t => t.Resource == "m2lb-cdc-qa.birk.dbo.person");

        Assert.Equal("BiRK / Debezium", person.SuggestedProducer);
        Assert.Equal("PersonBiRKAdapter", person.SuggestedConsumer);
    }

    [Fact]
    public void OtherCdcHubs_HaveNoInferredRelationship()
    {
        // "person -> PersonBiRKAdapter" must not be generalised into "barn -> BarnBiRKAdapter":
        // that is inference from a resource name, not evidence.
        var others = Qa().Where(t => t.IntegrationType == IntegrationType.EventHub
                                     && t.Resource != "m2lb-cdc-qa.birk.dbo.person");

        Assert.All(others, t =>
        {
            Assert.Null(t.SuggestedProducer);
            Assert.Null(t.SuggestedConsumer);
        });

        Assert.DoesNotContain(Qa(), t => t.SuggestedConsumer == "BarnBiRKAdapter");
    }

    [Fact]
    public void Leselogg_HasProvenConsumerButNoSingleProducer()
    {
        var leselogg = Qa().Single(t => t.Resource == "leselogg");

        Assert.Equal("Revisjon", leselogg.SuggestedConsumer);

        // Several services write to it, and the model holds one producing service, so a composite
        // string would misrepresent a single service identity.
        Assert.Null(leselogg.SuggestedProducer);
        Assert.NotNull(leselogg.RelationshipNote);
        Assert.DoesNotContain("/", leselogg.SuggestedProducer ?? "");
    }

    // ── Values the audit did not establish ───────────────────────────────────

    [Fact]
    public void NoTemplateInventsANamespace() =>
        Assert.All(Qa(), t => Assert.Null(t.EndpointOrNamespace));

    [Fact]
    public void NoTemplateInventsAConsumerGroup()
    {
        Assert.All(Qa(), t => Assert.Null(t.SuggestedConsumerGroup));

        // "$Default" is a common convention, not evidence.
        Assert.DoesNotContain(Qa(), t => t.SuggestedConsumerGroup == "$Default");
    }

    // ── Environment discipline ───────────────────────────────────────────────

    [Fact]
    public void OnlyQaHasTemplates()
    {
        Assert.NotEmpty(Qa());
        Assert.Empty(KnownIntegrationTemplates.ForEnvironment("Development"));
        Assert.Empty(KnownIntegrationTemplates.ForEnvironment("DEV"));
        Assert.Empty(KnownIntegrationTemplates.ForEnvironment("Production"));
        Assert.Empty(KnownIntegrationTemplates.ForEnvironment("PROD"));
        Assert.Empty(KnownIntegrationTemplates.ForEnvironment(null));
    }

    [Fact]
    public void NoResourceNameWasProducedBySubstitutingTheEnvironment()
    {
        // A "dev"/"prod" twin of an evidenced QA name would be an invention.
        var all = new[] { "Development", "DEV", "Production", "PROD", "QA" }
            .SelectMany(KnownIntegrationTemplates.ForEnvironment)
            .ToList();

        Assert.DoesNotContain(all, t => t.Resource.Contains("-dev.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, t => t.Resource.Contains("-prod.", StringComparison.OrdinalIgnoreCase));
        Assert.All(all, t => Assert.Equal("QA", t.EnvironmentName));
    }

    // ── Provenance ───────────────────────────────────────────────────────────

    [Fact]
    public void ProvenanceNamesAnAuditedSourceReadingNotVerification()
    {
        Assert.All(Qa(), t =>
        {
            Assert.Equal("Suggested from audited M2LB source", t.SuggestionOrigin);
            Assert.DoesNotContain("Verified", t.SuggestionOrigin, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("this repository", t.SuggestionOrigin, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ProvenanceExposesNoFilePaths() =>
        Assert.All(Qa(), t =>
        {
            Assert.DoesNotContain(":\\", t.SuggestionOrigin);
            Assert.DoesNotContain("/Users/", t.SuggestionOrigin);
            Assert.DoesNotContain(".cs", t.SuggestionOrigin);
        });

    // ── Materialisation ──────────────────────────────────────────────────────

    [Fact]
    public void MaterialisedIntegration_IsMarkedCodeSuggested()
    {
        var integration = Qa().Single(t => t.Resource == "m2lb-cdc-qa.birk.dbo.person")
            .ToIntegration("new-id");

        Assert.Equal(IntegrationConfigurationSource.CodeSuggested, integration.ConfigurationSource);
        Assert.Equal(IntegrationType.EventHub, integration.Type);
        Assert.Equal(IntegrationResourceKind.EventHub, integration.ResourceKind);
        Assert.Equal("m2lb-cdc-qa.birk.dbo.person", integration.Resource);
        Assert.Equal("PersonBiRKAdapter", integration.LogicalConsumerService);
    }

    [Fact]
    public void MaterialisedIntegration_LeavesUnknownFieldsEmpty()
    {
        var integration = Qa().Single(t => t.Resource == "hendelser.barn").ToIntegration("new-id");

        // Empty rather than filled, so the form shows them as still needing input.
        Assert.Null(integration.Endpoint);
        Assert.Null(integration.Consumer);
        Assert.Null(integration.LogicalProducerService);
        Assert.Null(integration.LogicalConsumerService);
    }

    [Fact]
    public void MaterialisedIntegration_IsEditableAndEditsSurvive()
    {
        var integration = Qa().Single(t => t.Resource == "leselogg").ToIntegration("i1");

        integration.Endpoint = "my-namespace.servicebus.windows.net";
        integration.LogicalConsumerService = "RevisjonV2";
        integration.ConfigurationSource = IntegrationConfigurationSource.Manual;

        var round = JsonSerializer.Deserialize<IntegrationConfigDto>(
            JsonSerializer.Serialize(integration));

        Assert.Equal("my-namespace.servicebus.windows.net", round!.Endpoint);
        Assert.Equal("RevisjonV2", round.LogicalConsumerService);
        Assert.Equal(IntegrationConfigurationSource.Manual, round.ConfigurationSource);
    }

    [Fact]
    public void AcceptingThenEditingATemplate_DoesNotMoveBaselineKey()
    {
        var accepted = Qa().Single(t => t.Resource == "m2lb-cdc-qa.birk.dbo.person")
            .ToIntegration("i1");
        accepted.Endpoint = "ns.servicebus.windows.net";

        var keyAsSuggested = IntegrationBaselineIdentity.Compute("QA", accepted);

        // The person confirms it and renames it; structural identity is untouched.
        accepted.ConfigurationSource = IntegrationConfigurationSource.Manual;
        accepted.Name = "BiRK Person CDC (reviewed)";
        accepted.LogicalProducerService = "BiRK";

        Assert.Equal(keyAsSuggested, IntegrationBaselineIdentity.Compute("QA", accepted));
    }

    // ── Secrets ──────────────────────────────────────────────────────────────

    [Fact]
    public void CatalogueContainsNoSecrets()
    {
        var serialised = JsonSerializer.Serialize(Qa());

        foreach (var forbidden in new[]
                 {
                     // "SAS" alone is not usable as a marker: it occurs inside "autorisasjon".
                     "SharedAccessKey", "SharedAccessSignature", "AccountKey", "connectionstring",
                     "Endpoint=sb://", "password", "secret", "Bearer ", "eyJ", "client_secret",
                     "sig=", "BEGIN PRIVATE KEY", "Authorization"
                 })
        {
            Assert.DoesNotContain(forbidden, serialised, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TemplateIdsAreUniqueAndStable()
    {
        var ids = Qa().Select(t => t.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(KnownIntegrationTemplates.ById("qa-eh-person"));
        Assert.Null(KnownIntegrationTemplates.ById("does-not-exist"));
    }
}
