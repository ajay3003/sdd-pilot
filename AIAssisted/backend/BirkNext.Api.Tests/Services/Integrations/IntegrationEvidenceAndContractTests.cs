using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>Azure evidence adapters (disabled by default, typed reasons, read-only by construction) and trusted JSON Schema contracts.</summary>
public sealed class IntegrationEvidenceAndContractTests
{
    private static IntegrationPlatform Platform(IntegrationRuntimeEvidenceSettings? runtime = null) => new()
    {
        Id = "dev:eventhub:m2lb", Name = "M2LB DEV Event Hubs", Namespace = "evhns-m2lb-dev-nwe-001", NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net",
        ResourceGroup = "rg-m2lb-dev-integration-nwe", RuntimeEvidence = runtime,
    };

    private static IIntegrationAzureCredential Credential(bool enabled) =>
        new IntegrationAzureCredential(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationReview:Azure:Enabled"] = enabled ? "true" : "false" }).Build());

    // ── Adapters ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AzureEvidence_IsDisabledByDefault_WithTheReason_AndNothingIsContacted()
    {
        var credential = Credential(enabled: false);
        credential.Credential.Should().BeNull();
        var metadata = new AzureEventHubMetadataSource(credential, NullLogger<AzureEventHubMetadataSource>.Instance);
        var result = await metadata.GetHubAsync(Platform(new() { EventHubMetadata = true }), "m2lb-cdc-dev.BirkM2LB.dbo.Person", CancellationToken.None);
        result.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        result.Reason.Should().Be(IntegrationAzureCredential.DisabledMessage);
        result.Value.Should().BeNull();

        var checkpoints = new BlobCheckpointEvidenceSource(credential, NullLogger<BlobCheckpointEvidenceSource>.Instance);
        (await checkpoints.GetAsync(Platform(), "hub", "group", CancellationToken.None)).State.Should().Be(IntegrationEvidenceState.NotConfigured);
        var telemetry = new LogAnalyticsTelemetrySource(credential, NullLogger<LogAnalyticsTelemetrySource>.Instance);
        (await telemetry.GetConsumerAsync(Platform(), "role", 24, CancellationToken.None)).State.Should().Be(IntegrationEvidenceState.NotConfigured);
    }

    [Fact]
    public void EnabledInstance_StillNeedsPerPlatformConfiguration_BeforeAnAdapterIsReady()
    {
        var credential = Credential(enabled: true);
        new AzureEventHubMetadataSource(credential, NullLogger<AzureEventHubMetadataSource>.Instance).Describe(Platform()).State.Should().Be(IntegrationEvidenceState.NotConfigured);
        new AzureEventHubMetadataSource(credential, NullLogger<AzureEventHubMetadataSource>.Instance).Describe(Platform(new() { EventHubMetadata = true })).State.Should().Be(IntegrationEvidenceState.Available);
        new BlobCheckpointEvidenceSource(credential, NullLogger<BlobCheckpointEvidenceSource>.Instance).Describe(Platform(new() { CheckpointContainerUrl = "http://insecure/c" })).State
            .Should().Be(IntegrationEvidenceState.NotConfigured, "only an https checkpoint container is accepted");
        new BlobCheckpointEvidenceSource(credential, NullLogger<BlobCheckpointEvidenceSource>.Instance).Describe(Platform()).Reason.Should().Contain("No checkpoint evidence source configured");
        new LogAnalyticsTelemetrySource(credential, NullLogger<LogAnalyticsTelemetrySource>.Instance).Describe(Platform(new() { TelemetryWorkspaceId = "not-a-guid" })).State.Should().Be(IntegrationEvidenceState.NotConfigured);
        new ArmConsumerGroupSource(credential, new HttpClient(), NullLogger<ArmConsumerGroupSource>.Instance).Describe(Platform()).State.Should().Be(IntegrationEvidenceState.NotConfigured);
    }

    [Fact]
    public void CheckpointBlobLayout_MatchesTheEventProcessorClientStore()
    {
        BlobCheckpointEvidenceSource.Prefix("EVHNS-M2LB-DEV-NWE-001.servicebus.windows.net", "m2lb-cdc-dev.BirkM2LB.dbo.Person", "Person-Adapter", "checkpoint")
            .Should().Be("evhns-m2lb-dev-nwe-001.servicebus.windows.net/m2lb-cdc-dev.birkm2lb.dbo.person/person-adapter/checkpoint/");
    }

    [Fact]
    public void TelemetryQueries_AreBoundedAggregates_WithAnEscapedRoleLiteral()
    {
        var role = "ca-\"x\"\\y";
        LogAnalyticsTelemetrySource.Literal(role).Should().Be("\"ca-\\\"x\\\"\\\\y\"");
        foreach (var query in new[] { LogAnalyticsTelemetrySource.ExceptionsQuery(role), LogAnalyticsTelemetrySource.TracesQuery(role), LogAnalyticsTelemetrySource.DependenciesQuery(role) })
        {
            query.Should().Contain("| summarize", "only aggregates come back");
            query.Should().NotContainAny("project ", "take ", "Message,", "| extend");
        }
    }

    /// <summary>The adapters' source may not contain any API that sends, receives, checkpoints, creates or changes anything.</summary>
    [Fact]
    public void EvidenceAdapters_HaveNoWriteReceiveOrManagementMutationPath()
    {
        var source = File.ReadAllText(FindRepoFile("backend/BirkNext.Api/Services/Integrations/AzureIntegrationEvidence.cs"));
        foreach (var forbidden in new[] { "SendAsync(new EventData", "EventHubProducerClient", "CreateBatch", "ReadEventsAsync", "ReadEventsFromPartitionAsync", "EventProcessorClient(",
                     "UpdateCheckpoint", "SetMetadata", "Upload", "DeleteBlob", "DeleteIfExists", "HttpMethod.Put", "HttpMethod.Post", "HttpMethod.Delete", "HttpMethod.Patch", "CreateConsumerGroup" })
            source.Should().NotContain(forbidden);
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    // ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidSchema_IsParsed_WithNestedPathsNullabilityEnumsAndLocalRefs()
    {
        const string schema = """
            { "$schema": "https://json-schema.org/draft/2020-12/schema", "$id": "urn:person:v2", "type": "object",
              "$defs": { "row": { "type": "object", "properties": { "Id": { "type": "integer" }, "Navn": { "type": ["string","null"] } }, "required": ["Id"] } },
              "properties": { "after": { "$ref": "#/$defs/row" }, "op": { "enum": ["c","u","d"] }, "tags": { "type": "array", "items": { "type": "string" } } }, "required": ["after"] }
            """;
        var (contract, error) = JsonSchemaContract.Parse("person.schema.json", schema);
        error.Should().BeNull();
        contract!.Version.Should().Be("urn:person:v2");
        contract.Fields.Keys.Should().Contain(["after", "after.Id", "after.Navn", "op", "tags", "tags[]"]);
        contract.Fields["after.Navn"].Nullable.Should().BeTrue();
        contract.Fields["after.Id"].Required.Should().BeTrue();
        contract.Fields["op"].Enum.Should().Equal("c", "u", "d");
    }

    [Theory]
    [InlineData("s.json", "{ \"PersonId\": 1, \"Fornavn\": \"Ola\" }", "sample event")]
    [InlineData("s.json", "{ not json", "Not valid JSON")]
    [InlineData("s.json", "[1,2]", "JSON object")]
    [InlineData("s.avsc", "{}", "Only JSON Schema")]
    [InlineData("s.json", "   ", "empty")]
    [InlineData("s.json", "{ \"type\": \"object\", \"properties\": {} }", "no fields")]
    public void InvalidContracts_AreRejectedWithAReason(string fileName, string content, string reason)
    {
        var (contract, error) = JsonSchemaContract.Parse(fileName, content);
        contract.Should().BeNull();
        error.Should().Contain(reason);
    }

    [Fact]
    public void Comparer_AddedProducerFieldsAreCompatible_OptionalMissingIsInformational()
    {
        var producer = JsonSchemaContract.Parse("p.json", """{ "type":"object", "properties": { "a": { "type":"integer" }, "extra": { "type":"string" } }, "required": ["a"] }""").Contract!;
        var consumer = JsonSchemaContract.Parse("c.json", """{ "type":"object", "properties": { "a": { "type":"number" }, "opt": { "type":"string" } }, "required": ["a"] }""").Contract!;
        var differences = EventContractComparer.Compare(producer, consumer);
        differences.Should().ContainSingle(d => d.Code == "OPTIONAL_FIELD_MISSING" && !d.Breaking);
        differences.Should().NotContain(d => d.Breaking, "integer is a number, and an extra producer field is compatible");
    }

    [Fact]
    public void Comparer_NullableProducer_BreaksARequiredNonNullConsumerField()
    {
        var producer = JsonSchemaContract.Parse("p.json", """{ "type":"object", "properties": { "a": { "type":["string","null"] } }, "required": ["a"] }""").Contract!;
        var consumer = JsonSchemaContract.Parse("c.json", """{ "type":"object", "properties": { "a": { "type":"string" } }, "required": ["a"] }""").Contract!;
        EventContractComparer.Compare(producer, consumer).Should().ContainSingle(d => d.Code == "NULLABILITY_MISMATCH" && d.Breaking);
    }

    [Fact]
    public void DebeziumEnvelope_IsRecognisedWithOrWithoutThePayloadWrapper()
    {
        var wrapped = JsonSchemaContract.Parse("p.json", """
            { "type":"object", "properties": { "payload": { "type":"object", "properties": { "before": { "type":["object","null"], "properties": { "Id": { "type":"integer" } } },
              "after": { "type":["object","null"] }, "source": { "type":"object", "properties": { "table": { "type":"string" } } }, "op": { "enum": ["c","d"] } } } } }
            """).Contract!;
        var envelope = DebeziumEnvelope.From(wrapped);
        envelope.IsEnvelope.Should().BeTrue();
        envelope.Operations.Should().Equal("c", "d");
        envelope.BeforeIsObject.Should().BeTrue();
        envelope.SourceFields.Should().Contain("table");
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task ContractStore_ValidatesStoresAndSyncsTheRelationship_WithoutKeepingInvalidFiles()
    {
        await using var db = Db();
        var catalog = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        await catalog.GetAsync("dev", "Development", "https://m2lbdev.bufetat.no/");
        var store = new IntegrationContractStore(db, catalog, NullLogger<IntegrationContractStore>.Instance);
        const string person = "dev:eventhub:birk-cdc:dbo.Person";
        const string schema = """{ "type":"object", "properties": { "op": { "type":"string" } } }""";

        var (rejected, error) = await store.SaveAsync(new IntegrationContractUpload { EnvironmentId = "dev", IntegrationId = person, Role = IntegrationContractRole.Producer, FileName = "sample.json", Content = "{ \"op\": \"c\" }" });
        rejected.Should().BeNull();
        error.Should().Contain("sample event");
        (await store.ListAsync("dev")).Should().BeEmpty();

        var (producer, _) = await store.SaveAsync(new IntegrationContractUpload { EnvironmentId = "dev", IntegrationId = person, Role = IntegrationContractRole.Producer, FileName = @"C:\repo\person.producer.schema.json", Content = schema });
        producer!.FileName.Should().Be("person.producer.schema.json");
        producer.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
        (await catalog.GetAsync("dev", null, null)).Integrations.Single(i => i.Id == person).ContractRelationship.Should().Be(ContractRelationshipState.ProducerContractAvailable);

        await store.SaveAsync(new IntegrationContractUpload { EnvironmentId = "dev", IntegrationId = person, Role = IntegrationContractRole.Consumer, FileName = "person.consumer.schema.json", Content = schema });
        (await catalog.GetAsync("dev", null, null)).Integrations.Single(i => i.Id == person).ContractRelationship.Should().Be(ContractRelationshipState.BothContractsAvailable, "never Verified automatically");
        (await store.LoadAsync("dev")).Should().HaveCount(2).And.OnlyContain(i => i.Contract != null);

        (await store.DeleteAsync("dev", person, IntegrationContractRole.Consumer)).Should().BeTrue();
        (await catalog.GetAsync("dev", null, null)).Integrations.Single(i => i.Id == person).ContractRelationship.Should().Be(ContractRelationshipState.ProducerContractAvailable);
    }

    [Fact]
    public void Freshness_FollowsTheReviewWindow_AndUnknownWithoutATimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        IntegrationReviewLabels.FreshnessOf(now.AddMinutes(-10), now, 24).Should().Be(IntegrationEvidenceItemFreshness.Current);
        IntegrationReviewLabels.FreshnessOf(now.AddHours(-5), now, 24).Should().Be(IntegrationEvidenceItemFreshness.Recent);
        IntegrationReviewLabels.FreshnessOf(now.AddDays(-3), now, 24).Should().Be(IntegrationEvidenceItemFreshness.Historical);
        IntegrationReviewLabels.FreshnessOf(null, now, 24).Should().Be(IntegrationEvidenceItemFreshness.Unknown);
    }
}

/// <summary>Runtime evidence settings are identifiers only — the shared rule the UI and the backend both enforce.</summary>
public sealed class IntegrationRuntimeEvidenceSettingsTests
{
    [Theory]
    [InlineData("https://acct.blob.core.windows.net/checkpoints?sv=2024-01-01&sig=abc", "never a SAS URL")]
    [InlineData("http://acct.blob.core.windows.net/checkpoints", "https")]
    [InlineData("https://user:pw@acct.blob.core.windows.net/checkpoints", "credentials")]
    [InlineData("https://acct.blob.core.windows.net/", "one container")]
    [InlineData("https://acct.blob.core.windows.net/checkpoints/ns/hub", "one container")]
    public void CheckpointContainer_MustBeAPlainHttpsContainerUrl(string url, string reason) =>
        new IntegrationRuntimeEvidenceSettings { CheckpointContainerUrl = url }.Validate().Should().Contain(reason);

    [Fact]
    public void Ids_AreGuids_AndTheWindowAndThresholdsAreBounded()
    {
        new IntegrationRuntimeEvidenceSettings { SubscriptionId = "Endpoint=sb://x/;SharedAccessKey=y" }.Validate().Should().Contain("GUID");
        new IntegrationRuntimeEvidenceSettings { TelemetryWorkspaceId = "InstrumentationKey=abc" }.Validate().Should().Contain("GUID");
        new IntegrationRuntimeEvidenceSettings { ReviewWindowHours = 0 }.Validate().Should().Contain("1–168");
        new IntegrationRuntimeEvidenceSettings { ReviewWindowHours = 169 }.Validate().Should().Contain("1–168");
        new IntegrationRuntimeEvidenceSettings { MaxConsumerLagEvents = -1 }.Validate().Should().NotBeNull();
        new IntegrationRuntimeEvidenceSettings { MaxCheckpointAgeMinutes = 0 }.Validate().Should().NotBeNull();
    }

    [Fact]
    public void ValidIdentifiersAndAnEmptyRecordPass()
    {
        new IntegrationRuntimeEvidenceSettings().Validate().Should().BeNull("every source is optional");
        new IntegrationRuntimeEvidenceSettings
        {
            EventHubMetadata = true, SubscriptionId = Guid.NewGuid().ToString(), TelemetryWorkspaceId = Guid.NewGuid().ToString(),
            CheckpointContainerUrl = "https://acct.blob.core.windows.net/checkpoints", ReviewWindowHours = 24, MaxConsumerLagEvents = 0, MaxCheckpointAgeMinutes = 15,
        }.Validate().Should().BeNull();
    }

    [Fact]
    public async Task TheControllerRejectsASasUrlBeforeAnythingIsStored()
    {
        var catalog = new Moq.Mock<IIntegrationCatalogService>(Moq.MockBehavior.Strict);
        var controller = new BirkNext.Api.Controllers.IntegrationsController(catalog.Object);
        var platform = new IntegrationPlatform { Id = "p", RuntimeEvidence = new() { CheckpointContainerUrl = "https://acct.blob.core.windows.net/c?sig=secret" } };
        var response = await controller.UpdatePlatform("dev", "p", platform, CancellationToken.None);
        response.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        catalog.VerifyNoOtherCalls();
    }
}
