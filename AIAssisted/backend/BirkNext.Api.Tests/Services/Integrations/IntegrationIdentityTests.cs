using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.ContractSchemaEvidence;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// One canonical integration identity used by journeys, providers and IQR source evidence: typed source resources (database.schema.table,
/// schema.table and CDC hub names are one resource), logical ↔ technical aliases with provenance, channel ≠ contract, "found · mapping incomplete"
/// ≠ missing; integration-specific source evidence from a multi-domain snapshot; producer ↔ consumer contract review on exact artifacts only.
/// </summary>
public sealed class IntegrationIdentityTests
{
    private const string Env = ActiveEventTestHarness.Env;

    [Theory]
    [InlineData("BirkM2LB.dbo.Utdanning", "BirkM2LB", "dbo", "Utdanning")]
    [InlineData("dbo.Utdanning", null, "dbo", "Utdanning")]
    [InlineData("[dbo].[Utdanning]", null, "dbo", "Utdanning")]
    public void SourceResource_RepresentationsParseToOneTypedIdentity(string value, string? database, string schema, string table)
    {
        var resource = SourceResourceIdentity.Parse(value)!;
        (resource.Database, resource.Schema, resource.Table).Should().Be((database, schema, table));
        resource.SameResource(new SourceResourceIdentity("BirkM2LB", "dbo", "Utdanning")).Should().BeTrue();
        SourceResourceIdentity.FromCdcTopic("m2lb-cdc-dev.BirkM2LB.dbo.Utdanning")!.Full.Should().Be("BirkM2LB.dbo.Utdanning");
        SourceResourceIdentity.FromCdcTopic("skole.reports").Should().BeNull("an ordinary channel name is never read as a table");
        resource.SameResource(new SourceResourceIdentity("Other", "dbo", "Utdanning")).Should().Be(database is null, "a stated database must agree");
    }

    [Fact]
    public void PeriodClosed_ResolvesTheExistingSkoleReportsIntegration_AsFoundWithMappingIncomplete()
    {
        var skoleReports = new IntegrationDefinition { Id = "sb", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "skole.reports", Producer = "M2LB.SkoleService.Api" };
        var match = IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.PeriodClosedIdentity, skoleReports);
        match.State.Should().Be(IntegrationMatchState.MappingIncomplete, "the integration exists; only its consumer mapping is missing");
        match.Matched.Should().Contain(["Channel skole.reports", "Producer M2LB.SkoleService.Api"]);
        match.Unresolved.Should().ContainSingle().Which.Should().Be("Consumer Bufdata not configured");

        IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.PeriodClosedIdentity, skoleReports with { Consumer = new IntegrationConsumer { DisplayName = "Bufdata" } })
            .State.Should().Be(IntegrationMatchState.Matched);
        IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.PeriodClosedIdentity, skoleReports with { Kind = IntegrationKind.EventHub }).Found.Should().BeFalse();
        IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.PeriodClosedIdentity, new IntegrationDefinition { Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "other.topic", Producer = "Something" })
            .Found.Should().BeFalse("no anchor matched — a display name alone never resolves an integration");
    }

    [Fact]
    public void EducationCdc_AndReportIntake_ResolveByTypedIdentity_NotByDisplayName()
    {
        var utdanning = new IntegrationDefinition { Kind = IntegrationKind.EventHub, DisplayName = "anything", EndpointOrTopic = "m2lb-cdc-dev.BirkM2LB.dbo.Utdanning" };
        IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.EducationCdcIdentity, utdanning).Found.Should().BeTrue("the CDC hub name states the resource");
        IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.EducationCdcIdentity, utdanning with { EndpointOrTopic = null, SourceResource = "dbo.Utdanning" }).Found.Should().BeTrue();
        IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.EducationCdcIdentity, utdanning with { EndpointOrTopic = null, SourceResource = "BirkM2LB.dbo.Person" }).Found.Should().BeFalse();

        var intake = new IntegrationDefinition { Kind = IntegrationKind.HttpApi, Producer = "Altinn", Consumer = new IntegrationConsumer { DisplayName = "Meldingsutveksleren (MU)" } };
        IntegrationIdentityResolver.Match(SkoleNaervaerJourneyPack.ReportIntakeIdentity, intake).State.Should().Be(IntegrationMatchState.Matched);
        IntegrationIdentityResolver.AnyName("MU", "Kommune").Should().BeFalse("a short name never matches inside another word");
    }

    // ── Active Event provider: typed resource, resource-level readiness ───────────────────────────────────────────────

    [Fact]
    public async Task Provider_RecognizesUtdanningByItsResourceIdentity_AndReportsEachResourceSeparately()
    {
        await using var h = new ActiveEventTestHarness();
        h.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = ActiveEventTestHarness.SkoleIntegrationId, EnvironmentId = Env, PlatformId = "dev:eventhub:m2lb", DisplayName = "Utdanning Event Hub", Kind = IntegrationKind.EventHub, Enabled = true,
            SourceResource = "dbo.Utdanning", EndpointOrTopic = ActiveEventTestHarness.SkoleHub,
        });
        var providers = await h.Lifecycle().ProvidersAsync(Env, default);
        var skole = providers.Single(p => p.ExtensionId == "skolenaervaer.cdc");
        skole.ApplicableIntegrationIds.Should().Equal([ActiveEventTestHarness.SkoleIntegrationId], "dbo.Utdanning and BirkM2LB.dbo.Utdanning are the same resource");
        skole.Coverage.Should().Be(ActiveEventProviderCoverage.Partial);
        skole.ResourceStatuses.Single(r => r.Resource == "dbo.Utdanning").State.Should().Be(ActiveEventResourceState.IntegrationFound);
        skole.ResourceStatuses.Single(r => r.Resource == "dbo.ManglendeSkoletilbud").State.Should().Be(ActiveEventResourceState.IntegrationMissing);
        providers.Single(p => p.ExtensionId != "skolenaervaer.cdc").ApplicableIntegrationIds.Should().NotContain(ActiveEventTestHarness.SkoleIntegrationId,
            "the Person provider never claims the Utdanning integration");
    }

    [Fact]
    public async Task Provider_MissingNamespace_IsAnExecutionBlocker_NotAMissingIntegration()
    {
        await using var h = new ActiveEventTestHarness();
        h.Catalog.Platform = h.Catalog.Platform with { NamespaceFqdn = null };
        h.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = ActiveEventTestHarness.SkoleIntegrationId, EnvironmentId = Env, PlatformId = "dev:eventhub:m2lb", DisplayName = "Utdanning", Kind = IntegrationKind.EventHub, Enabled = true,
            SourceResource = "BirkM2LB.dbo.Utdanning", EndpointOrTopic = ActiveEventTestHarness.SkoleHub,
        });
        var lifecycle = h.Lifecycle();
        var skole = (await lifecycle.ProvidersAsync(Env, default)).Single(p => p.ExtensionId == "skolenaervaer.cdc");
        skole.ApplicableIntegrationIds.Should().Contain(ActiveEventTestHarness.SkoleIntegrationId);
        skole.ResourceStatuses.Single(r => r.Resource == "dbo.Utdanning").Detail.Should().Contain("execution destination incomplete");
        var scenario = (await lifecycle.ScenariosAsync(Env, ActiveEventTestHarness.SkoleIntegrationId, default)).First(s => s.Support == ActiveEventScenarioSupport.Supported);
        var readiness = await lifecycle.ReadinessAsync(Env, ActiveEventTestHarness.SkoleIntegrationId, "skolenaervaer.cdc", scenario.ScenarioId, null, default);
        readiness.CanRun.Should().BeFalse("nothing is sent without a destination");
        readiness.Checks.Single(c => c.Key == "integration").Detail.Should().Contain("Event Hub namespace (FQDN) is not configured");
    }

    // ── Integration-specific source evidence ────────────────────────────────────────────────────────────────────────

    private static IqrSourceSnapshot RepositorySnapshot() => new()
    {
        Archive = new("M2LB.zip", "sha", 10),
        Architecture = new ArchitectureSnapshot
        {
            Components =
            [
                new() { Id = "c-adapter", Name = "M2LB.SkoleAdapter.Worker", SourceProject = "SkoleAdapter/src/M2LB.SkoleAdapter.Worker/M2LB.SkoleAdapter.Worker.csproj" },
                new() { Id = "c-service", Name = "M2LB.SkoleService.Api", SourceProject = "SkoleService/src/M2LB.SkoleService.Api/M2LB.SkoleService.Api.csproj" },
                new() { Id = "c-person", Name = "M2LB.PersonBiRKAdapter.Worker", SourceProject = "PersonAdapter/src/M2LB.PersonBiRKAdapter.Worker/M2LB.PersonBiRKAdapter.Worker.csproj" },
                new() { Id = "c-auth", Name = "M2LB.Autorisasjon.Api", SourceProject = "Autorisasjon/src/M2LB.Autorisasjon.Api/M2LB.Autorisasjon.Api.csproj" },
            ],
            MessagingChannels = [new() { Id = "ch", Name = "skole.reports", EntityName = "skole.reports", Type = MessagingChannelType.ServiceBusTopic,
                Producers = [new("c-service", ChannelRole.Producer, ArchitectureEvidenceState.Confirmed, "Wolverine", [])] }],
        },
        Rules =
        [
            new() { Id = "r1", Project = "M2LB.SkoleAdapter.Worker", Kind = "Route", Table = "Utdanning", Location = new("SkoleAdapter/src/M2LB.SkoleAdapter.Worker/Router.cs", 1) },
            new() { Id = "r2", Project = "M2LB.SkoleService.Api", Kind = "Operation", Location = new("SkoleService/src/M2LB.SkoleService.Api/Publish.cs", 1) },
            new() { Id = "r3", Project = "M2LB.PersonBiRKAdapter.Worker", Kind = "Route", Table = "Person", Location = new("PersonAdapter/src/M2LB.PersonBiRKAdapter.Worker/Router.cs", 1) },
            new() { Id = "r4", Project = "M2LB.Autorisasjon.Api", Kind = "Security", Location = new("Autorisasjon/src/M2LB.Autorisasjon.Api/Auth.cs", 1) },
        ],
        Tests =
        [
            new() { Id = "t1", Project = "M2LB.SkoleAdapter.Worker.Tests" },
            new() { Id = "t2", Project = "M2LB.PersonBiRKAdapter.Worker.Tests" },
            new() { Id = "t3", Project = "M2LB.Autorisasjon.Api.Tests" },
        ],
    };

    [Fact]
    public void BoundSnapshot_GivesEachIntegrationOnlyItsOwnEvidence_RepositoryStaysContext()
    {
        var snapshot = RepositorySnapshot();
        var utdanning = new IntegrationDefinition { Id = "eh", Kind = IntegrationKind.EventHub, SourceResource = "BirkM2LB.dbo.Utdanning", Producer = "Debezium", Consumer = new IntegrationConsumer { DisplayName = "SkoleAdapter" } };
        var (scopedU, scopeU) = IntegrationSourceCorrelation.Correlate(utdanning, snapshot, SkoleNaervaerJourneyPack.ExpectedAliases);
        scopeU.EvidenceResolved.Should().BeTrue();
        scopedU.Rules.Select(r => r.Id).Should().Equal(["r1"]);
        scopedU.Tests.Select(t => t.Id).Should().Equal(["t1"]);
        (scopeU.RepositoryRules, scopeU.IntegrationRules).Should().Be((4, 1), "repository-wide counts are context, never the integration's evidence");

        var reports = new IntegrationDefinition { Id = "sb", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "skole.reports", Producer = "Skolenærværsrapport" };
        var (scopedR, scopeR) = IntegrationSourceCorrelation.Correlate(reports, snapshot, SkoleNaervaerJourneyPack.ExpectedAliases);
        scopedR.Rules.Select(r => r.Id).Should().Equal(["r2"], "the channel and the Skolenærværsrapport ↔ SkoleService alias correlate; Person and Autorisasjon do not");
        scopeR.CorrelatedOn.Should().Contain(item => item.StartsWith("Channel skole.reports"));

        var unrelated = new IntegrationDefinition { Id = "x", Kind = IntegrationKind.HttpApi, Producer = "Altinn", Consumer = new IntegrationConsumer { DisplayName = "MU" } };
        var (scopedX, scopeX) = IntegrationSourceCorrelation.Correlate(unrelated, snapshot, SkoleNaervaerJourneyPack.ExpectedAliases);
        scopeX.EvidenceResolved.Should().BeFalse();
        scopeX.Summary.Should().StartWith("Source bound · integration-specific evidence unresolved");
        (scopedX.Rules.Count, scopedX.Tests.Count).Should().Be((0, 0), "unrelated repository evidence is never presented as support");
    }

    // ── Contract relationship ───────────────────────────────────────────────────────────────────────────────────────

    private static ContractFact Xsd(string name, params XmlSchemaElementEvidence[] elements)
    {
        var contract = new SourceContract { Id = name, Name = name, Type = SourceContractType.XmlSchema, File = $"xsd/{name}.xsd", XmlSchema = new XmlSchemaEvidence(null, null, "", [.. elements], [], [], []) };
        return ContractEvidenceQueries.Fact(new ContractEvidenceSource(Guid.NewGuid(), "a.zip", DateTimeOffset.UtcNow, new ContractEvidence { Contracts = [contract] }), contract);
    }

    private static XmlSchemaElementEvidence Element(string path, string type, decimal min = 1, params string[] allowed) =>
        new(path, path.Split('/').Last(), type, min, "1", false, null, null, null, [.. allowed.Select(value => new XmlSchemaRestrictionEvidence("enumeration", value))]);

    [Fact]
    public void ContractRelationship_ComparesFields_AndNamesEveryDifference()
    {
        var consumer = Xsd("consumer", Element("Skolenaervaer/Elev", "xs:string"), Element("Skolenaervaer/Fravaer", "xs:int"));
        ContractRelationshipComparer.Compare(Xsd("producer", Element("Skolenaervaer/Elev", "xs:string"), Element("Skolenaervaer/Fravaer", "xs:int")), consumer)
            .State.Should().Be(ContractRelationshipResultState.ExactStructuralMatch);
        var (missing, differences) = ContractRelationshipComparer.Compare(Xsd("producer", Element("Skolenaervaer/Elev", "xs:string")), consumer);
        missing.Should().Be(ContractRelationshipResultState.Incompatible);
        differences.Should().ContainSingle(d => d.Path == "Skolenaervaer/Fravaer" && d.Kind == "Required field not produced");
        ContractRelationshipComparer.Compare(Xsd("producer", Element("Skolenaervaer/Elev", "xs:string"), Element("Skolenaervaer/Fravaer", "xs:int"), Element("Skolenaervaer/Ekstra", "xs:string", 0)), consumer)
            .State.Should().Be(ContractRelationshipResultState.CompatibleWithDifferences);
        ContractRelationshipComparer.Compare(Xsd("producer"), consumer).State.Should().Be(ContractRelationshipResultState.NotAssessable);
    }

    [Theory]
    [InlineData(@"Buf.Meldingsutveksler.TestData\TestData\xsds\v3\xsd\bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd")]
    [InlineData("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd")]
    [InlineData("./Buf.Meldingsutveksler.TestData/TestData/xsds/v3/xsd/bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd")]
    public void TheExactSkolenaervaerXsd_IsFound_ByPathSeparatorsOrFileName(string reference)
    {
        var contract = new SourceContract { Name = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn", Type = SourceContractType.XmlSchema,
            File = "Buf.Meldingsutveksler.TestData/TestData/xsds/v3/xsd/bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd" };
        ContractArtifactPaths.PathMatches(contract, reference).Should().BeTrue();
        ContractArtifactPaths.NamedExactly(contract, "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn").Should().BeTrue();
        ContractArtifactPaths.NamedExactly(contract, "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0").Should().BeFalse("a prefix never matches");
        ContractArtifactPaths.PathMatches(contract with { File = "henvisning/bufdir_henvisning.xsd" }, reference).Should().BeFalse("no other schema is ever substituted");
    }
}
