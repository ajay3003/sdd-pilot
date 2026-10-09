using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ActiveEventTesting.Debezium;
using BirkNext.Api.Services.ActiveEventTesting.Observation;
using BirkNext.Api.Services.ActiveEventTesting.Providers.M2lbPerson;
using BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;
using BirkNext.Integrations;
using FluentAssertions;
using H = BirkNext.Api.Tests.Services.ActiveEventTesting.ActiveEventTestHarness;

namespace BirkNext.Api.Tests.Services.ActiveEventTesting;

/// <summary>Generic Debezium construction, the Person and Skolenærvær fixtures, and the Skolenærvær provider through the shared runner (FAKE producer).</summary>
public sealed class ActiveEventFixtureTests
{
    private static readonly DebeziumSource Source = new("BirkM2LB", "dbo", "Utdanning");
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, long> Codes = new Dictionary<string, long> { ["UtdanningTypeFk"] = 1, ["UtdanningSkoleTypeFk"] = 2, ["ManglendeSkoletilbudArsakTypeFk"] = 3 };

    private static JsonElement Payload(DebeziumEnvelope envelope) => JsonDocument.Parse(envelope.Body).RootElement.GetProperty("payload");

    [Fact]
    public void Debezium_CreateUpdateDeleteAndSnapshot_FollowTheOperationRules()
    {
        var row = new DebeziumRow { { "Id", DebeziumValue.Number(7) }, { "Code", DebeziumValue.Text("x") } };
        var create = Payload(DebeziumEventBuilder.Build(new(ActiveEventOperation.Create, Source, null, row, Now), 4096));
        create.GetProperty("op").GetString().Should().Be("c");
        create.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        create.GetProperty("after").GetProperty("Id").GetInt64().Should().Be(7);
        create.GetProperty("source").GetProperty("table").GetString().Should().Be("Utdanning");
        create.GetProperty("source").GetProperty("snapshot").GetString().Should().Be("false");

        var update = Payload(DebeziumEventBuilder.Build(new(ActiveEventOperation.Update, Source, row, row.With("Code", DebeziumValue.Text("y")), Now), 4096));
        update.GetProperty("op").GetString().Should().Be("u");
        update.GetProperty("before").GetProperty("Code").GetString().Should().Be("x");
        update.GetProperty("after").GetProperty("Code").GetString().Should().Be("y");

        var delete = Payload(DebeziumEventBuilder.Build(new(ActiveEventOperation.Delete, Source, row, null, Now), 4096));
        delete.GetProperty("op").GetString().Should().Be("d");
        delete.GetProperty("after").ValueKind.Should().Be(JsonValueKind.Null);

        var snapshot = Payload(DebeziumEventBuilder.Build(new(ActiveEventOperation.ReadSnapshot, Source, null, row, Now), 4096));
        snapshot.GetProperty("op").GetString().Should().Be("r");
        snapshot.GetProperty("source").GetProperty("snapshot").GetString().Should().Be("true");

        var invalid = () => DebeziumEventBuilder.Build(new(ActiveEventOperation.Delete, Source, null, row, Now), 4096);
        invalid.Should().Throw<InvalidOperationException>().WithMessage("*requires a before row*");
        var tooLarge = () => DebeziumEventBuilder.Build(new(ActiveEventOperation.Create, Source, null, row, Now), 50);
        tooLarge.Should().Throw<InvalidOperationException>().WithMessage("*byte bound*");
    }

    [Fact]
    public void Debezium_Tombstone_IsAKeyWithoutABody_AndOnlyATombstoneMayBeEmpty()
    {
        var tombstone = DebeziumEventBuilder.Tombstone(new DebeziumRow { { "UtdanningPK", DebeziumValue.Number(5) } });
        tombstone.Body.Should().BeEmpty();
        tombstone.Key.Should().Be("{\"payload\":{\"UtdanningPK\":5}}");

        var runId = Guid.NewGuid();
        var descriptor = new ActiveEventScenarioDescriptor { ExtensionId = "x", ScenarioId = "s", SupportedOperations = [ActiveEventOperation.Tombstone, ActiveEventOperation.Create] };
        GeneratedActiveEvent Event(ActiveEventOperation operation, string? key) => new()
        {
            EventId = "e", ExtensionId = "x", ScenarioId = "s", Operation = operation, Body = [], BodyBytes = 0, BodySha256 = tombstone.Sha256, EventKey = key,
            Correlation = new() { RunId = runId, EventId = "e", EventFingerprint = tombstone.Sha256 },
        };
        var valid = () => ActiveEventExecutionRunner.ValidateGeneratedEvent(Event(ActiveEventOperation.Tombstone, tombstone.Key), descriptor, runId);
        valid.Should().NotThrow();
        var keyless = () => ActiveEventExecutionRunner.ValidateGeneratedEvent(Event(ActiveEventOperation.Tombstone, null), descriptor, runId);
        keyless.Should().Throw<InvalidOperationException>().WithMessage("*must carry the record key*");
        var emptyCreate = () => ActiveEventExecutionRunner.ValidateGeneratedEvent(Event(ActiveEventOperation.Create, "k"), descriptor, runId);
        emptyCreate.Should().Throw<InvalidOperationException>().WithMessage("*body size*", "non-tombstone validation is not weakened");
    }

    [Fact]
    public void PersonFixture_IsTheEnvelopeThePersonAdapterReads_WithSyntheticValuesOnly()
    {
        var runId = Guid.Parse("0123456789abcdef0123456789abcdef");
        var envelope = PersonCdcFixtureBuilder.Create(900_000_001, PersonCdcFixtureBuilder.Marker(runId), new("BirkM2LB", "dbo", "Person"), Now, 8192);
        var json = Encoding.UTF8.GetString(envelope.Body);
        json.Should().StartWith("{\"schema\":{\"type\":\"struct\"},\"payload\":{\"before\":null,\"after\":{\"PersonPK\":900000001,\"Fornavn\":\"BIRKNEXT-TEST-0123456789AB\",\"Etternavn\":\"Synthetic\"");
        json.Should().Contain("\"source\":{\"connector\":\"sqlserver\",\"name\":\"birknext-active-test\",\"db\":\"BirkM2LB\",\"schema\":\"dbo\",\"table\":\"Person\",\"snapshot\":\"false\"},\"op\":\"c\"");
        foreach (var forbidden in new[] { "\"Fødselsnummer\"", "Personnummer", "Dufnummer", "\"Navn\"" }) json.Should().NotContain(forbidden);
        var invalid = Encoding.UTF8.GetString(PersonCdcFixtureBuilder.Create(null, "M", new("BirkM2LB", "dbo", "Person"), Now, 8192).Body);
        invalid.Should().NotContain("PersonPK").And.Contain("\"Fornavn\":\"M\"");
        PersonCdcFixtureBuilder.ExpectedPersonId(900_000_001).Should().Be(PersonCdcFixtureBuilder.ExpectedPersonId(900_000_001));
    }

    [Theory]
    [InlineData("Utdanning")]
    [InlineData("ManglendeSkoletilbud")]
    public void SkoleFixtures_SendOnlySpecifiedFields_ForEveryOperation_AndNoPii(string tableName)
    {
        var table = SkoleNaervaerFixtures.ForTable(tableName)!;
        var source = new DebeziumSource("BirkM2LB", "dbo", table.Table);
        var values = new SkoleNaervaerFixtures.RecordValues(910_000_001, 920_000_001, new DateOnly(2026, 8, 15), null, Codes);
        var create = Payload(SkoleNaervaerFixtures.Insert(table, source, values, Now, 8192));
        create.GetProperty("after").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(table.Fields);
        create.GetProperty("after").GetProperty(table.ChildReferenceField).GetInt64().Should().Be(920_000_001);
        create.GetProperty("after").GetProperty(table.FromField).GetInt64().Should().Be(SkoleNaervaerFixtures.DayNumber(new DateOnly(2026, 8, 15)));
        create.GetProperty("after").GetProperty(table.ToField).ValueKind.Should().Be(JsonValueKind.Null);

        var update = Payload(SkoleNaervaerFixtures.Update(table, source, values, values with { To = new DateOnly(2027, 6, 20) }, Now, 8192));
        update.GetProperty("op").GetString().Should().Be("u");
        update.GetProperty("before").GetProperty(table.KeyField).GetInt64().Should().Be(update.GetProperty("after").GetProperty(table.KeyField).GetInt64());
        var otherKey = () => SkoleNaervaerFixtures.Update(table, source, values, values with { Key = 1 }, Now, 8192);
        otherKey.Should().Throw<InvalidOperationException>();
        Payload(SkoleNaervaerFixtures.Delete(table, source, values, Now, 8192)).GetProperty("op").GetString().Should().Be("d");
        Payload(SkoleNaervaerFixtures.Insert(table, source, values, Now, 8192, snapshot: true)).GetProperty("op").GetString().Should().Be("r");
        SkoleNaervaerFixtures.Tombstone(table, values.Key).Key.Should().Contain(table.KeyField);
        Payload(SkoleNaervaerFixtures.Insert(table, source, values with { ChildReference = null }, Now, 8192)).GetProperty("after").TryGetProperty(table.ChildReferenceField, out _).Should().BeFalse();

        var all = Encoding.UTF8.GetString(SkoleNaervaerFixtures.Update(table, source, values, values, Now, 8192).Body);
        foreach (var forbidden in new[] { "Navn", "Merknad", "Kontakt", "Fødselsnummer", "Personnummer", "Dufnummer", "Skolenaervaer\"" })
            all.Should().NotContain(forbidden, "names, remarks, contacts and national ids are never part of a Skolenærvær fixture");
    }

    [Fact]
    public async Task Skolenaervaer_GeneratesThroughTheSharedRunnerAndTransport_WhenPrerequisitesAreSatisfied()
    {
        await using var h = new H();
        h.Settings["ActiveEventTesting:SyntheticIdentityRanges:skolenaervaer.Utdanning.UtdanningPK:Min"] = "910000000";
        h.Settings["ActiveEventTesting:SyntheticIdentityRanges:skolenaervaer.Utdanning.UtdanningPK:Max"] = "910000099";
        h.Settings["ActiveEventTesting:SyntheticIdentityRanges:skolenaervaer.BarnFK:Min"] = "920000000";
        h.Settings["ActiveEventTesting:SyntheticIdentityRanges:skolenaervaer.BarnFK:Max"] = "920000099";
        h.Settings["ActiveEventTesting:Providers:skolenaervaer.cdc:ReferenceCodes:UtdanningTypeFk"] = "1";
        h.Settings["ActiveEventTesting:Providers:skolenaervaer.cdc:ReferenceCodes:UtdanningSkoleTypeFk"] = "2";
        ActiveEventLifecycleTests.AddSkoleIntegration(h);
        var skole = h.Skole();
        h.Lifecycle([h.Person(), skole], out var runner);
        var context = new ActiveEventProjectEvidenceContext(H.Env, h.Catalog.Extra.Single(), h.Catalog.Platform);
        var descriptor = skole.GetScenarios(context).Single(s => s.ScenarioId == "skolenaervaer.utdanning.exact-replay");
        var target = new ActiveEventTrustedTarget
        {
            TargetEnvironmentId = H.Env, EnvironmentType = "Development", TargetUrl = "https://m2lb-dev.example.test", IntegrationId = H.SkoleIntegrationId,
            IntegrationType = "EventHub", TransportType = "EventHub", Endpoint = H.Fqdn, Resource = H.SkoleHub, Consumer = "$Default",
        };
        // The source-contract, CDC-capture and review gates are bypassed here on purpose: this proves only that a Skolenærvær scenario is
        // structurally executable by the shared runner and transport. The real lifecycle keeps it blocked (see the lifecycle tests).
        var preparation = new ActiveEventScenarioPreparation { Compatible = true };
        var contract = new ActiveEventSourceContractReference { ContractFingerprint = "reviewed", ExtensionVersion = skole.ExtensionVersion, SourceResource = "BirkM2LB.dbo.Utdanning" };

        var result = await runner.ExecuteAsync(skole, descriptor, preparation, new ActiveEventObservationContext(target, context.Integration, context.Platform), contract,
            Guid.NewGuid(), TimeSpan.FromSeconds(5), TimeSpan.Zero, default);

        result.Events.Should().HaveCount(2);
        result.Events[1].Body.Should().Equal(result.Events[0].Body, "the replay is byte-identical");
        result.Events[0].PartitionKey.Should().Be("910000000");
        h.Producers.Sent.Should().HaveCount(2);
        h.Producers.PartitionKeys.Should().AllBe("910000000");
        var history = result.ToHistoryResult(contract);
        history.Status.Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence, "Event Hub acceptance is never a Skolenærvær pass: replay sent ≠ idempotency verified");
        history.Scenario.ResultDoesNotMean.Should().Contain("Replay sent ≠ idempotency verified");

        var notAssessed = skole.GetScenarios(context).Single(s => s.ScenarioId == "skolenaervaer.utdanning.unknown-child-late-linkage");
        var blocked = await runner.ExecuteAsync(skole, notAssessed, preparation, new ActiveEventObservationContext(target, context.Integration, context.Platform), contract, Guid.NewGuid(),
            TimeSpan.FromSeconds(5), TimeSpan.Zero, default);
        blocked.Evidence.Should().ContainSingle(e => e.Status == ActiveEventEvidenceStatus.SafetyBlocked && e.Detail.Contains("needs a downstream verifier"));
        h.Producers.Sent.Should().HaveCount(2, "a NotAssessed scenario never sends");
    }

    [Fact]
    public async Task Transport_NeverSendsATombstone_AndMapsPartitionKey()
    {
        await using var h = new H();
        var sender = new AzureEventHubTestSender(h.Azure, h.Producers, Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureEventHubTestSender>.Instance);
        var approved = h.Policy().Approve("Development", H.Fqdn, H.Hub, null).Approved!;
        var tombstone = await sender.SendAsync(approved, new GeneratedActiveEvent { EventId = "t", Operation = ActiveEventOperation.Tombstone, EventKey = "{}" }, TimeSpan.FromSeconds(5), default);
        tombstone.Status.Should().Be(ActiveEventEvidenceStatus.Unavailable);
        tombstone.Detail.Should().Contain("not verified");
        h.Producers.Sent.Should().BeEmpty();
        h.Azure.Enabled = false;
        (await sender.SendAsync(approved, new GeneratedActiveEvent { EventId = "x", Body = [1] }, TimeSpan.FromSeconds(5), default)).Status.Should().Be(ActiveEventEvidenceStatus.Unavailable);
    }
}
