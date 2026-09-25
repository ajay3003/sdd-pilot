using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Target Environment → Integrations: the M2LB DEV seed (one platform, 16 business CDC integrations, technical topics apart),
/// consumer mapping certainty, no $Default, producer/consumer authentication apart, no secrets, idempotent add-missing seeding
/// that never overwrites a user edit, QA/Prod never seeded, and the one-time browser-profile import.
/// </summary>
public sealed class IntegrationCatalogTests
{
    private const string DevId = "0b13cd886b4b441896f931ba2ef13907";
    private const string DevUrl = "https://m2lbdev.bufetat.no/";

    private static AppDbContext Db(string? name = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private static IntegrationCatalogService Service(AppDbContext db) => new(db, NullLogger<IntegrationCatalogService>.Instance);

    private static Task<IntegrationCatalog> DevCatalog(IntegrationCatalogService service) => service.GetAsync(DevId, "Development", DevUrl);

    [Fact]
    public async Task DevPlatform_HasTheKnownValues()
    {
        var catalog = await DevCatalog(Service(Db()));
        var platform = catalog.Platforms.Should().ContainSingle().Subject;
        platform.Name.Should().Be("M2LB DEV Event Hubs");
        platform.Id.Should().Be("dev:eventhub:m2lb");
        platform.Namespace.Should().Be("evhns-m2lb-dev-nwe-001");
        platform.NamespaceFqdn.Should().Be("evhns-m2lb-dev-nwe-001.servicebus.windows.net");
        platform.ResourceGroup.Should().Be("rg-m2lb-dev-integration-nwe");
        platform.SourceDatabase.Should().Be("BirkM2LB");
        platform.TopicPrefix.Should().Be("m2lb-cdc-dev");
        platform.SourceHost.Should().Be("10.31.19.31");
        platform.SourcePort.Should().Be(50806);
        platform.ProducerTechnology.Should().Be("Debezium SQL Server CDC");
        platform.ProducerAuthentication.Should().Be(IntegrationAuthMechanism.Sas);
        platform.DefaultConsumerAuthentication.Should().Be(IntegrationAuthMechanism.ManagedIdentity);
        platform.TechnicalOwner.Should().Be("platform-team");
        platform.MonitoringProvider.Should().Be("Application Insights");
        platform.MonitoringUrl.Should().BeNull("no concrete dashboard URL is known");
        platform.RunbookUrl.Should().BeNull();
    }

    [Fact]
    public async Task ExactlySixteenBusinessIntegrations_TechnicalTopicsKeptApart()
    {
        var catalog = await DevCatalog(Service(Db()));
        catalog.Integrations.Should().HaveCount(16).And.OnlyContain(i => i.Kind == IntegrationKind.EventHub && i.Enabled && i.SystemName == "BIRK CDC / Debezium");
        catalog.Integrations.Select(i => i.EndpointOrTopic).Should().Contain("m2lb-cdc-dev.BirkM2LB.dbo.KjønnType").And.Contain("m2lb-cdc-dev.BirkM2LB.dbo.RomningKategoriType");
        var technical = new[] { "m2lb-cdc-dev", "schemahistory", "connect-configs", "connect-offsets", "connect-status" };
        catalog.Platforms.Single().TechnicalTopics.Select(t => t.Name).Should().BeEquivalentTo(technical);
        catalog.Integrations.Should().NotContain(i => technical.Contains(i.EndpointOrTopic));
        catalog.Integrations.Should().OnlyContain(i => i.PartitionCount == 1 && i.RetentionDays == 7);
    }

    [Fact]
    public async Task PersonIsConfirmed_WithItsAdapterIdentityAndSeparateAuth()
    {
        var person = (await DevCatalog(Service(Db()))).Integrations.Single(i => i.Id == "dev:eventhub:birk-cdc:dbo.Person");
        person.DisplayName.Should().Be("BIRK Person CDC");
        person.EndpointOrTopic.Should().Be("m2lb-cdc-dev.BirkM2LB.dbo.Person");
        person.SourceResource.Should().Be("BirkM2LB.dbo.Person");
        person.Producer.Should().Be("Debezium SQL Server CDC");
        person.ProducerAuthentication.Should().Be(IntegrationAuthMechanism.Sas);
        person.ConsumerAuthentication.Should().Be(IntegrationAuthMechanism.ManagedIdentity);
        person.Consumer.Should().BeEquivalentTo(new IntegrationConsumer
        {
            DisplayName = "Person Adapter", LogicalName = "person-adapter", ContainerApp = "ca-m2lb-person-adp-dev-nwe-001",
            ManagedIdentity = "id-m2lb-person-adp-dev-nwe", MappingState = ConsumerMappingState.Confirmed, MappingSource = "Confirmed M2LB DEV mapping",
        });
        person.ConsumerGroup.Should().BeNull("unknown, never $Default");
        person.ContractRelationship.Should().Be(ContractRelationshipState.NotConfigured);
        person.HealthUrl.Should().BeNull();
        person.WorkerUrl.Should().BeNull();
        IntegrationConfigurationRules.Evaluate(person, null).State.Should().NotBe(IntegrationConfigurationState.Disabled);
    }

    [Fact]
    public async Task OtherConsumers_AreSuggestedFromTheAuditedSourceOrNeedConfirmation_NeverGuessed()
    {
        var catalog = await DevCatalog(Service(Db()));
        var byTable = catalog.Integrations.ToDictionary(i => i.SourceResource!.Split(".dbo.")[1]);
        byTable["Barn"].Consumer.Should().Match<IntegrationConsumer>(c => c.MappingState == ConsumerMappingState.Suggested && c.DisplayName == "PersonBiRKAdapter" && c.ContainerApp == null && c.ManagedIdentity == null);
        new[] { "Tiltak", "Bestilling", "TjenesteType", "TiltaksStatusType", "AvslutningsGrunnType" }.Should().OnlyContain(t => byTable[t].Consumer.DisplayName == "Tjeneste API" && byTable[t].Consumer.MappingState == ConsumerMappingState.Suggested);
        new[] { "TvangsProtokoll", "Romning" }.Should().OnlyContain(t => byTable[t].Consumer.DisplayName == "Hendelse BiRK Adapter" && byTable[t].Consumer.MappingState == ConsumerMappingState.Suggested);
        var unknown = new[] { "BarnStatusType", "BarnType", "Kommune", "KjønnType", "HjemmelType", "TvangsProtokollStatusType", "RomningKategoriType" };
        unknown.Should().OnlyContain(t => byTable[t].Consumer.MappingState == ConsumerMappingState.NeedsConfirmation && byTable[t].Consumer.DisplayName == null);
        catalog.Integrations.Should().OnlyContain(i => i.ConsumerGroup == null, "$Default is never assumed");
        catalog.Integrations.Count(i => IntegrationConfigurationRules.Evaluate(i, catalog.Platforms[0]).State == IntegrationConfigurationState.Ready).Should().Be(1);
        catalog.Integrations.Count(i => IntegrationConfigurationRules.Evaluate(i, catalog.Platforms[0]).State == IntegrationConfigurationState.NeedsConfirmation).Should().Be(15);
    }

    [Fact]
    public async Task SeedIsIdempotent_AndUserEditsSurviveRestart()
    {
        var name = Guid.NewGuid().ToString();
        await DevCatalog(Service(Db(name)));
        var service = Service(Db(name));
        var person = (await DevCatalog(service)).Integrations.Single(i => i.Id.EndsWith("dbo.Person"));
        await service.UpdateAsync(DevId, person.Id, person with { ConsumerGroup = "person-adapter-cg", TechnicalOwner = "m2lb-team" });

        // "Restart": a fresh context reading the same store attaches nothing new and keeps the edit.
        var again = await DevCatalog(Service(Db(name)));
        again.Integrations.Should().HaveCount(16);
        again.Platforms.Should().ContainSingle();
        again.Notices.Should().BeEmpty();
        var edited = again.Integrations.Single(i => i.Id == person.Id);
        edited.ConsumerGroup.Should().Be("person-adapter-cg");
        edited.TechnicalOwner.Should().Be("m2lb-team");
        edited.UserModified.Should().BeTrue();
    }

    [Theory]
    [InlineData("QA", "https://m2lbqa.bufetat.no/")]
    [InlineData("Production", "https://m2lb.bufetat.no/")]
    [InlineData("Development", "https://some-other-dev.example.test/")]
    public async Task NoOtherEnvironmentIsSeeded(string type, string url)
    {
        var catalog = await Service(Db()).GetAsync("other", type, url);
        catalog.Platforms.Should().BeEmpty();
        catalog.Integrations.Should().BeEmpty();
    }

    [Fact]
    public async Task NothingSecretIsStoredOrReturned()
    {
        var db = Db();
        var catalog = await DevCatalog(Service(db));
        var stored = string.Join("\n", db.IntegrationPlatforms.Select(p => p.DocumentJson).ToList().Concat(db.IntegrationDefinitions.Select(d => d.DocumentJson).ToList()));
        var returned = JsonSerializer.Serialize(catalog);
        foreach (var text in new[] { stored, returned })
            text.Should().NotContainAny("SharedAccessKey", "Endpoint=sb://", "AccountKey", "Password=", "password", "client_secret", "Bearer ");
    }

    [Fact]
    public async Task BrowserProfileIntegrations_AreImportedOnce_WithoutTheSuggestedDefaultGroup()
    {
        var db = Db();
        var service = Service(db);
        var legacy = new List<IntegrationConfigDto>
        {
            new() { Id = "a1", Name = "Leselogg", Type = IntegrationType.ServiceBus, Resource = "leselogg", Consumer = null, ConfigurationSource = IntegrationConfigurationSource.Manual },
            new() { Id = "a2", Name = "Old person", Type = IntegrationType.EventHub, Resource = "x", Consumer = "$Default", ConfigurationSource = IntegrationConfigurationSource.CodeSuggested },
        };
        (await service.ImportLegacyAsync("env", legacy)).Should().Be(2);
        (await service.ImportLegacyAsync("env", legacy)).Should().Be(0, "the import runs once per environment");
        var catalog = await service.GetAsync("env", "QA", "https://m2lbqa.bufetat.no/");
        catalog.Integrations.Should().HaveCount(2).And.OnlyContain(i => i.Origin == IntegrationRecordOrigin.ImportedFromBrowserProfile);
        catalog.Integrations.Single(i => i.DisplayName == "Old person").ConsumerGroup.Should().BeNull("a code-suggested $Default is not a configured value");
    }

    [Fact]
    public async Task CreateEnableDelete_WorkOnTheCatalog()
    {
        var service = Service(Db());
        var created = await service.CreateAsync("env", new IntegrationDefinition { DisplayName = "Leselogg", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "leselogg" });
        created.Id.Should().Be("env:servicebus:leselogg");
        created.Origin.Should().Be(IntegrationRecordOrigin.Manual);
        (await service.SetEnabledAsync("env", created.Id, false))!.Enabled.Should().BeFalse();
        IntegrationConfigurationRules.Evaluate((await service.GetAsync("env", null, null)).Integrations.Single(), null).State.Should().Be(IntegrationConfigurationState.Disabled);
        (await service.DeleteAsync("env", created.Id)).Should().BeTrue();
        (await service.GetAsync("env", null, null)).Integrations.Should().BeEmpty();
    }
}
