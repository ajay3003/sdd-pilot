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
/// Target Environment → Integrations: the M2LB DEV helper v5 (three platforms and ONE integration — the confirmed Person mapping; every
/// discoverable hub is a source suggestion instead), no $Default on any integration (only a labelled platform assumption), no secrets, no
/// internal source host, idempotent add-missing seeding that never overwrites a user edit or resurrects a deleted platform, a server-side
/// guard that refuses QA, Production and other hosts, logical-identity duplicate prevention, and the one-time browser-profile import.
/// </summary>
public sealed class IntegrationCatalogTests
{
    private const string DevId = "0b13cd886b4b441896f931ba2ef13907";
    private const string DevUrl = "https://m2lbdev.bufetat.no/";

    private static AppDbContext Db(string? name = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private static IntegrationCatalogService Service(AppDbContext db) => new(db, NullLogger<IntegrationCatalogService>.Instance);

    private static Task<IntegrationCatalog> DevCatalog(IntegrationCatalogService service) => service.GetWithM2lbHelperAsync(DevId, "Development", DevUrl);

    [Fact]
    public async Task DevPlatform_HasTheKnownValues()
    {
        var catalog = await DevCatalog(Service(Db()));
        var platform = catalog.Platforms.Should().ContainSingle(p => p.Kind == IntegrationKind.EventHub).Subject;
        platform.Name.Should().Be("M2LB DEV Event Hubs");
        platform.Id.Should().Be("dev:eventhub:m2lb");
        platform.Namespace.Should().Be("evhns-m2lb-dev-nwe-001");
        platform.NamespaceFqdn.Should().Be("evhns-m2lb-dev-nwe-001.servicebus.windows.net");
        platform.ResourceGroup.Should().Be("rg-m2lb-dev-integration-nwe");
        platform.SourceDatabase.Should().Be("BirkM2LB");
        platform.TopicPrefix.Should().Be("m2lb-cdc-dev");
        platform.SourceHost.Should().BeNull("the internal source host is not needed to configure or review the integration");
        platform.SourcePort.Should().BeNull();
        platform.Region.Should().Be("Norway East");
        platform.ProducerTechnology.Should().Be("Debezium SQL Server CDC");
        platform.ProducerAuthentication.Should().Be(IntegrationAuthMechanism.Sas);
        platform.DefaultConsumerAuthentication.Should().Be(IntegrationAuthMechanism.ManagedIdentity);
        platform.TechnicalOwner.Should().Be("platform-team");
        platform.MonitoringProvider.Should().Be("Application Insights");
        platform.MonitoringUrl.Should().BeNull("no concrete dashboard URL is known");
        platform.RunbookUrl.Should().BeNull();
    }

    [Fact]
    public async Task TheHelperSeedsOnlyTheConfirmedPersonIntegration_TechnicalTopicsKeptApart()
    {
        var catalog = await DevCatalog(Service(Db()));
        catalog.Integrations.Should().ContainSingle().Which.Should().Match<IntegrationDefinition>(i => i.Id == "dev:eventhub:birk-cdc:dbo.Person" && i.Kind == IntegrationKind.EventHub
            && i.Enabled && i.SystemName == "BIRK CDC / Debezium" && i.PartitionCount == 1 && i.RetentionDays == 7);
        var technical = new[] { "m2lb-cdc-dev", "schemahistory", "connect-configs", "connect-offsets", "connect-status" };
        catalog.Platforms.Single(p => p.Kind == IntegrationKind.EventHub).TechnicalTopics.Select(t => t.Name).Should().BeEquivalentTo(technical);
        catalog.Platforms.Select(p => p.Region).Should().OnlyContain(region => region == "Norway East", "one canonical region label");
    }

    [Fact]
    public async Task DiscoverableHubsAndConsumers_AreNoLongerSeeded()
    {
        var db = Db();
        var catalog = await DevCatalog(Service(db));
        var stored = string.Join("\n", db.IntegrationPlatforms.Select(p => p.DocumentJson).ToList().Concat(db.IntegrationDefinitions.Select(d => d.DocumentJson).ToList()));
        foreach (var discoverable in new[] { "dbo.Barn", "dbo.Tiltak", "dbo.Kommune", "KjønnType", "Kj_nnType", "Tjeneste API", "Hendelse BiRK Adapter", "QA-context audit", "10.31.19.31" })
            stored.Should().NotContain(discoverable, "Source Analysis owns discoverable hubs and consumers, and the internal source host is not seeded");
        catalog.Integrations.Should().NotContain(i => i.Consumer.MappingState == ConsumerMappingState.Suggested, "the helper seeds no suggestion; suggestions come from source");
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
    public async Task SeedIsIdempotent_AndUserEditsSurviveRestart()
    {
        var name = Guid.NewGuid().ToString();
        await DevCatalog(Service(Db(name)));
        var service = Service(Db(name));
        var person = (await DevCatalog(service)).Integrations.Single(i => i.Id.EndsWith("dbo.Person"));
        await service.UpdateAsync(DevId, person.Id, person with { ConsumerGroup = "person-adapter-cg", TechnicalOwner = "m2lb-team" });

        // "Restart": a fresh context reading the same store attaches nothing new and keeps the edit.
        var again = await DevCatalog(Service(Db(name)));
        again.Integrations.Should().ContainSingle();
        again.Platforms.Should().HaveCount(3, "one Event Hub, one Service Bus and one SCIM identity provisioning platform, never duplicated");
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

    // ── Project independence: the M2LB template is explicit, never inferred from a hostname ──────────────────────────────

    [Fact]
    public async Task M2lbHost_NeverInjectsRecords_OnlySuggestsTheTemplate()
    {
        var name = Guid.NewGuid().ToString();
        var catalog = await Service(Db(name)).GetAsync(DevId, "Development", DevUrl);
        catalog.Platforms.Should().BeEmpty("a hostname that resembles M2LB must never mutate generic project state");
        catalog.Integrations.Should().BeEmpty();
        catalog.AppliedTemplateId.Should().BeNull();
        catalog.DomainExtensions.Should().BeEmpty();
        catalog.Templates.Should().ContainSingle(t => t.Id == M2lbDevIntegrationSeed.Name && t.Suggested && t.SuggestionReason!.Contains(M2lbDevIntegrationSeed.FrontendHost));
        (await Db(name).IntegrationEnvironmentStates.CountAsync()).Should().Be(0, "a read writes no template state");
    }

    [Theory]
    [InlineData("Development", "https://paymenthub.example.test/")]
    [InlineData("QA", "https://m2lbdev.bufetat.no/")]
    [InlineData("Production", "https://m2lbdev.bufetat.no/")]
    public async Task OtherEnvironments_AreNotOfferedTheDevHelper(string type, string url)
    {
        var catalog = await Service(Db()).GetAsync("other", type, url);
        catalog.Integrations.Should().BeEmpty();
        catalog.Templates.Should().BeEmpty("the M2LB DEV helper is offered only to the M2LB DEV environment");
        catalog.DomainExtensions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Production", "https://m2lbdev.bufetat.no/", "never applied to a Production")]
    [InlineData("QA", "https://m2lbqa.bufetat.no/", "only to a Development")]
    [InlineData("Development", "https://paymenthub.example.test/", "only to the M2LB DEV environment")]
    [InlineData(null, null, "only to a Development")]
    public async Task ApplyingTheDevHelperElsewhere_IsRefusedByTheServer_AndWritesNothing(string? type, string? url, string reason)
    {
        var name = Guid.NewGuid().ToString();
        var result = await Service(Db(name)).ApplyTemplateAsync("other", M2lbDevIntegrationSeed.Name, type, url);
        result!.Catalog.Should().BeNull();
        result.Blocked.Should().Contain(reason);
        await using var db = Db(name);
        (await db.IntegrationPlatforms.CountAsync()).Should().Be(0);
        (await db.IntegrationDefinitions.CountAsync()).Should().Be(0);
        (await db.IntegrationEnvironmentStates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheOffer_ListsExactlyWhatApplyingAdds_WithProvenance()
    {
        var offer = (await Service(Db()).GetAsync(DevId, "Development", DevUrl)).Templates.Should().ContainSingle().Subject;
        offer.Description.Should().Contain("cannot be reliably discovered from source").And.NotContain("complete");
        offer.Items.Select(i => i.Name).Should().Contain(["M2LB DEV Event Hubs", "BIRK Person CDC → Person Adapter", "M2LB DEV Service Bus", "M2LB Entra SCIM Provisioning", "Expected consumer group $Default"]);
        offer.Items.Single(i => i.Name == "BIRK Person CDC → Person Adapter").Provenance.Should().Be(IntegrationTemplateProvenance.ConfirmedMapping);
        offer.Items.Single(i => i.Name == "Expected consumer group $Default").Provenance.Should().Be(IntegrationTemplateProvenance.ConfiguredAssumption);
        offer.Items.Single(i => i.Name == "M2LB DEV Event Hubs").Provenance.Should().Be(IntegrationTemplateProvenance.HistoricalAudit);
        JsonSerializer.Serialize(offer).Should().NotContain("10.31.19.31").And.NotContain("KjønnType");
    }

    [Fact]
    public async Task ApplyTemplate_IsTheOnlyWriter_AndEnablesTheDomainExtension()
    {
        var service = Service(Db());
        var applied = (await service.ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name, "Development", DevUrl))!.Catalog!;
        applied.Integrations.Should().ContainSingle();
        applied.Platforms.Should().HaveCount(3);
        applied.AppliedTemplateId.Should().Be(M2lbDevIntegrationSeed.Name);
        applied.DomainExtensions.Should().Equal(BirkNext.Technology.DomainExtensionIds.M2lbChildSecurityClassification);
        applied.Templates.Should().BeEmpty("an applied template is not offered again");
        applied.Notices.Should().ContainSingle(n => n.StartsWith("Applied the M2LB DEV helper: 4 records added"));
        (await service.ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name, "Development", DevUrl))!.Catalog!.Notices.Should().ContainSingle(n => n.Contains("already applied"));
        (await service.ApplyTemplateAsync(DevId, "unknown-template", "Development", DevUrl)).Should().BeNull();
    }

    [Fact]
    public async Task PersistedM2lbWorkspace_StillLoads_AndReceivesAddMissingUpgrades()
    {
        // A workspace seeded by an earlier version (hostname auto-seed, v3): its state record carries the seed name. It keeps loading and is
        // upgraded add-missing, whatever URL (or none) the read passes, because the template was already part of it.
        var name = Guid.NewGuid().ToString();
        await Service(Db(name)).ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name, "Development", DevUrl);
        await using (var db = Db(name)) { (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion = 3; await db.SaveChangesAsync(); }
        var loaded = await Service(Db(name)).GetAsync(DevId, "Development", "https://renamed-host.example.test/");
        loaded.Integrations.Should().ContainSingle();
        loaded.DomainExtensions.Should().Contain(BirkNext.Technology.DomainExtensionIds.M2lbChildSecurityClassification);
        (await Db(name).IntegrationEnvironmentStates.SingleAsync()).SeedVersion.Should().Be(M2lbDevIntegrationSeed.Version);
    }

    [Theory]
    [InlineData("QA")]
    [InlineData("Production")]
    public async Task AnEnvironmentThatIsNoLongerDevelopment_ReceivesNoDevUpgrade(string type)
    {
        var name = Guid.NewGuid().ToString();
        await Service(Db(name)).ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name, "Development", DevUrl);
        await using (var db = Db(name)) { (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion = 3; await db.SaveChangesAsync(); }
        await Service(Db(name)).GetAsync(DevId, type, DevUrl);
        (await Db(name).IntegrationEnvironmentStates.SingleAsync()).SeedVersion.Should().Be(3, "DEV values never reach QA or Production");
    }

    [Fact]
    public async Task ADeletedScimPlatform_IsNotResurrectedByAVersionUpgrade()
    {
        var name = Guid.NewGuid().ToString();
        await Service(Db(name)).ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name, "Development", DevUrl);
        await using (var db = Db(name))
        {
            db.IntegrationPlatforms.Remove(await db.IntegrationPlatforms.SingleAsync(p => p.Id == M2lbDevIntegrationSeed.ScimPlatformId));
            (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion = 4;
            await db.SaveChangesAsync();
        }
        var upgraded = await Service(Db(name)).GetAsync(DevId, "Development", DevUrl);
        upgraded.Platforms.Should().NotContain(p => p.Id == M2lbDevIntegrationSeed.ScimPlatformId, "a person deleted it");
        upgraded.Notices.Should().NotContain(n => n.Contains("missing record"));
        (await Db(name).IntegrationEnvironmentStates.SingleAsync()).SeedVersion.Should().Be(M2lbDevIntegrationSeed.Version);
    }

    [Fact]
    public async Task TheV5Upgrade_NormalizesAnUneditedSeededRegion_AndLeavesAnEditedOneAlone()
    {
        var name = Guid.NewGuid().ToString();
        var service = Service(Db(name));
        await service.ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name, "Development", DevUrl);
        var catalog = await service.GetAsync(DevId, "Development", DevUrl);
        var scim = catalog.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.ScimPlatformId);
        await service.UpdatePlatformAsync(DevId, scim.Id, scim with { Region = "nwe" });
        await using (var db = Db(name))
        {
            var serviceBus = await db.IntegrationPlatforms.SingleAsync(p => p.Id == M2lbDevIntegrationSeed.ServiceBusPlatformId);
            serviceBus.DocumentJson = serviceBus.DocumentJson.Replace("\"Norway East\"", "\"nwe\"");
            (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion = 4;
            await db.SaveChangesAsync();
        }
        var upgraded = await Service(Db(name)).GetAsync(DevId, "Development", DevUrl);
        upgraded.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.ServiceBusPlatformId).Region.Should().Be("Norway East");
        upgraded.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.ScimPlatformId).Region.Should().Be("nwe", "a person's edit is never overwritten");
    }

    [Fact]
    public async Task UpgradingAV1Workspace_NeverAddsDiscoverableIntegrations()
    {
        var name = Guid.NewGuid().ToString();
        await Service(Db(name)).ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name, "Development", DevUrl);
        await using (var db = Db(name))
        {
            db.IntegrationDefinitions.RemoveRange(db.IntegrationDefinitions);
            (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion = 1;
            await db.SaveChangesAsync();
        }
        (await Service(Db(name)).GetAsync(DevId, "Development", DevUrl)).Integrations.Should().BeEmpty("an upgrade adds platforms only; deleted integrations stay deleted");
    }

    // ── Duplicate prevention and origin ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSameLogicalIntegrationUnderAnotherId_IsRefused_ButDistinctChannelsAreNot()
    {
        var service = Service(Db());
        await service.CreateAsync("env", new IntegrationDefinition { Id = "a", DisplayName = "Person", Kind = IntegrationKind.EventHub, PlatformId = "p1", EndpointOrTopic = "m2lb-cdc-dev.BirkM2LB.dbo.Person" });
        var duplicate = () => service.CreateAsync("env", new IntegrationDefinition { Id = "b", DisplayName = "Person again", Kind = IntegrationKind.EventHub, PlatformId = "p1", EndpointOrTopic = "M2LB-CDC-DEV.birkm2lb.dbo.person" });
        await duplicate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already configures the channel*");
        await service.CreateAsync("env", new IntegrationDefinition { Id = "c", DisplayName = "Barn", Kind = IntegrationKind.EventHub, PlatformId = "p1", EndpointOrTopic = "m2lb-cdc-dev.BirkM2LB.dbo.Barn" });
        await service.CreateAsync("env", new IntegrationDefinition { Id = "d", DisplayName = "Person on another platform", Kind = IntegrationKind.EventHub, PlatformId = "p2", EndpointOrTopic = "m2lb-cdc-dev.BirkM2LB.dbo.Person" });
        (await service.GetAsync("env", null, null)).Integrations.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnIntegrationCreatedFromATemplate_KeepsTemplateOriginAndKey_ButAKeylessTemplateOriginIsManual()
    {
        var service = Service(Db());
        var fromTemplate = await service.CreateAsync("env", new IntegrationDefinition
        {
            DisplayName = "Report intake", Kind = IntegrationKind.HttpApi, EndpointOrTopic = "https://intake.example.test/hook", Origin = IntegrationRecordOrigin.Template, TemplateKey = "demo-intake",
        });
        (fromTemplate.Origin, fromTemplate.TemplateKey).Should().Be((IntegrationRecordOrigin.Template, "demo-intake"));
        var reread = (await service.GetAsync("env", null, null)).Integrations.Single(i => i.Id == fromTemplate.Id);
        (reread.Origin, reread.TemplateKey).Should().Be((IntegrationRecordOrigin.Template, "demo-intake"), "the template origin survives persistence");

        var keyless = await service.CreateAsync("env", new IntegrationDefinition { DisplayName = "Other", Kind = IntegrationKind.HttpApi, EndpointOrTopic = "https://other.example.test/", Origin = IntegrationRecordOrigin.Template });
        (keyless.Origin, keyless.TemplateKey).Should().Be((IntegrationRecordOrigin.Manual, (string?)null));
        var manual = await service.CreateAsync("env", new IntegrationDefinition { DisplayName = "Manual", Kind = IntegrationKind.HttpApi, EndpointOrTopic = "https://manual.example.test/", TemplateKey = "ignored" });
        manual.TemplateKey.Should().BeNull("only a template origin records a template key");

        var edited = await service.UpdateAsync("env", fromTemplate.Id, fromTemplate with { DisplayName = "Report intake (edited)", TemplateKey = "other", Origin = IntegrationRecordOrigin.Manual });
        (edited!.Origin, edited.TemplateKey).Should().Be((IntegrationRecordOrigin.Template, "demo-intake"), "an edit never rewrites where the record came from");
    }

    [Fact]
    public async Task AnIntegrationAddedFromASourceSuggestion_KeepsThatOrigin_AndIsNeverConfirmedByBeingAdded()
    {
        var created = await Service(Db()).CreateAsync("env", new IntegrationDefinition
        {
            DisplayName = "BIRK Barn CDC", Kind = IntegrationKind.EventHub, EndpointOrTopic = "m2lb-cdc-dev.birkm2lb.dbo.barn", Origin = IntegrationRecordOrigin.SourceSuggestion,
            Consumer = new IntegrationConsumer { DisplayName = "PersonBiRKAdapter.Worker", MappingState = ConsumerMappingState.Confirmed, MappingConfirmedAt = DateTimeOffset.UtcNow },
        });
        created.Origin.Should().Be(IntegrationRecordOrigin.SourceSuggestion);
        created.Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested, "a source suggestion is never a confirmed mapping");
        created.Consumer.MappingConfirmedAt.Should().BeNull();
        (await Service(Db()).CreateAsync("env", new IntegrationDefinition { DisplayName = "x", Kind = IntegrationKind.Other, Origin = IntegrationRecordOrigin.Seed })).Origin
            .Should().Be(IntegrationRecordOrigin.Manual, "a client cannot claim the seed origin");
    }
}
