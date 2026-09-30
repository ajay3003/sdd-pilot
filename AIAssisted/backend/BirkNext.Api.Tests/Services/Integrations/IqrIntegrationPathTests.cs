using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Multi-stage source path (adapter → ingestion → domain → event → outbox → Service Bus) over a synthetic "Customer" system with no
/// M2LB names, so every stage, field, event and test is discovered — never hardcoded. Covers transformation classes, intentional vs
/// unresolved drops, data minimization, developer-test reuse without duplication, cross-layer/runtime/E2E gaps and snapshot evolution.
/// </summary>
public sealed class IqrIntegrationPathTests
{
    private const string Lib = "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Azure.Messaging.EventHubs.Processor\" /></ItemGroup></Project>";
    private const string Web = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
    private const string TestProject = "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>";
    private const string PersonalValue = "01019912345";

    private const string AdapterModel = """
        using System.Text.Json.Serialization;
        public sealed record CustomerRecord(Guid CustomerId, string Name, string? NationalId, string? Email, string Region, string? LegacyCode, [property: JsonIgnore] string? DebugNote);
        """;
    private const string AdapterMapper = """
        using System.Text.Json;
        public sealed class CustomerMapper
        {
            public CustomerRecord? Map(JsonElement payload)
            {
                var key = GetString(payload, "CustomerKey");
                if (key is null) return null;
                return new CustomerRecord(CustomerId: Guid.NewGuid(), Name: GetString(payload, "FullName") ?? "", NationalId: GetString(payload, "NationalId"),
                    Email: GetString(payload, "Email"), Region: GetString(payload, "Region") ?? "Unknown", LegacyCode: GetString(payload, "Legacy"), DebugNote: GetString(payload, "Note"));
            }
            private static string? GetString(JsonElement p, string name) => p.TryGetProperty(name, out var v) ? v.GetString() : null;
        }
        """;
    private const string AdapterClient = """
        using System.Net.Http.Json;
        public sealed class IngestClient
        {
            private const string Path = "api/customers/ingest";
            private readonly HttpClient _http;
            public IngestClient(HttpClient http) { _http = http; }
            public Task Send(CustomerRecord record) => _http.PutAsJsonAsync(Path, record);
        }
        public sealed class Worker { private EventProcessorClient? _processor; }
        """;
    private const string Endpoint = """
        public static class CustomerEndpoint
        {
            public static void Map(WebApplication app) =>
                app.MapPut("/api/customers/ingest", async (CustomerIngestRequest request, CustomerService service) =>
                {
                    await service.Ingest(new CustomerDto(CustomerId: request.CustomerId, Name: request.Name, NationalId: request.NationalId, Email: request.Email, Region: request.Region));
                    return Results.NoContent();
                });
        }
        public record CustomerIngestRequest(Guid CustomerId, string Name, string? NationalId, string? Email, string Region);
        public record CustomerDto(Guid CustomerId, string Name, string? NationalId, string? Email, string Region);
        """;
    private const string Domain = """
        public class Customer
        {
            public Guid CustomerId { get; set; }
            public string Name { get; set; } = "";
            public string? NationalId { get; set; }
            public string? Email { get; set; }
            public string Region { get; set; } = "";
            public string ChangedBy { get; set; } = "";
        }
        public class Conflict { public Guid Id { get; set; } public string Payload { get; set; } = ""; }
        public class OutboxRow
        {
            public Guid MessageId { get; set; }
            public string Topic { get; set; } = "";
            public string SessionId { get; set; } = "";
            public string Subject { get; set; } = "";
            public string Payload { get; set; } = "";
            public string Priority { get; set; } = "Normal";
            public int Attempts { get; set; }
            public string Status { get; set; } = "Pending";
        }
        public class Db : DbContext
        {
            public DbSet<Customer> Customers { get; set; } = null!;
            public DbSet<Conflict> Conflicts { get; set; } = null!;
            public DbSet<OutboxRow> Outbox { get; set; } = null!;
        }
        public record CustomerCreatedEvent
        {
            public Guid CustomerId { get; init; }
            public bool HasNationalId { get; init; }
            public string Region { get; init; } = "";
            public string? Email { get; init; }
        }
        public record CustomerChangedEvent
        {
            public Guid CustomerId { get; init; }
            public IReadOnlyList<string> ChangedFields { get; init; } = [];
            public bool NationalIdChanged { get; init; }
        }
        public record Envelope<TData>
        {
            public Guid EventId { get; init; } = Guid.NewGuid();
            public string Type { get; init; } = "";
            public Guid CorrelationId { get; init; }
            public TData Data { get; init; } = default!;
        }
        """;
    private const string Service = """
        using System.Text.Json;
        public class OutboxPublisher
        {
            private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            private readonly Db _db;
            public OutboxPublisher(Db db) { _db = db; }
            public Task PublishAsync<TData>(string topicName, string subject, string sessionId, TData data, string priority = "Normal")
            {
                var envelope = new Envelope<TData> { Type = subject, Data = data };
                var payload = JsonSerializer.Serialize(envelope, Options);
                var row = new OutboxRow { MessageId = Guid.NewGuid(), Topic = topicName, SessionId = sessionId, Subject = subject, Payload = payload, Priority = priority, Status = "Pending" };
                _db.Outbox.Add(row);
                return Task.CompletedTask;
            }
        }
        public static class EventMapper
        {
            public static CustomerCreatedEvent ToCreated(CustomerDto dto) => new() { CustomerId = dto.CustomerId, HasNationalId = dto.NationalId is not null, Region = dto.Region, Email = dto.Email };
            public static CustomerChangedEvent ToChanged(CustomerDto dto, List<string> changedFields, bool nationalIdChanged) => new() { CustomerId = dto.CustomerId, ChangedFields = changedFields, NationalIdChanged = nationalIdChanged };
        }
        public class CustomerService
        {
            private readonly Db _db;
            private readonly OutboxPublisher _publisher;
            public CustomerService(Db db, OutboxPublisher publisher) { _db = db; _publisher = publisher; }
            public async Task Ingest(CustomerDto dto)
            {
                var existing = await _db.Customers.FindAsync(dto.CustomerId);
                if (existing is null)
                {
                    if (dto.Name == "") { _db.Conflicts.Add(new Conflict { Id = Guid.NewGuid(), Payload = JsonSerializer.Serialize(dto) }); return; }
                    _db.Customers.Add(new Customer { CustomerId = dto.CustomerId, Name = dto.Name, NationalId = dto.NationalId, Email = dto.Email, Region = dto.Region });
                    await _publisher.PublishAsync(topicName: "customer.events", subject: "CustomerCreated", sessionId: dto.CustomerId.ToString(), data: EventMapper.ToCreated(dto));
                }
                else
                {
                    var changed = Detect(existing, dto, out bool nationalIdChanged);
                    if (changed.Count == 0) return;
                    Apply(existing, dto);
                    await _publisher.PublishAsync(topicName: "customer.events", subject: "CustomerChanged", sessionId: dto.CustomerId.ToString(),
                        data: EventMapper.ToChanged(dto, changed, nationalIdChanged), priority: "High");
                }
                await _db.SaveChangesAsync();
            }
            private static List<string> Detect(Customer existing, CustomerDto dto, out bool nationalIdChanged)
            {
                var changed = new List<string>();
                if (existing.Name != dto.Name) changed.Add("Name");
                nationalIdChanged = existing.NationalId != dto.NationalId;
                if (nationalIdChanged) changed.Add("NationalId");
                return changed;
            }
            private static void Apply(Customer existing, CustomerDto dto)
            {
                existing.Name = dto.Name;
                existing.NationalId = dto.NationalId;
                existing.Region = dto.Region;
                existing.ChangedBy = dto.Name;
            }
        }
        public class BusOptions
        {
            public string FQDN { get; set; } = "";
            public string ConnectionString { get; set; } = "";
            public ServiceBusClient Create() => string.IsNullOrEmpty(ConnectionString) ? new ServiceBusClient(FQDN, new DefaultAzureCredential()) : new ServiceBusClient(ConnectionString);
        }
        public class OutboxDispatcher
        {
            private readonly Db _db;
            private readonly ServiceBusClient _client;
            public OutboxDispatcher(Db db, ServiceBusClient client) { _db = db; _client = client; }
            public async Task RunOnce()
            {
                var pending = _db.Outbox.Where(m => m.Status == "Pending").OrderBy(m => m.MessageId).Take(20).ToList();
                foreach (var message in pending)
                {
                    await using var sender = _client.CreateSender(message.Topic);
                    try
                    {
                        var sb = new ServiceBusMessage(message.Payload) { MessageId = message.MessageId.ToString(), SessionId = message.SessionId, Subject = message.Subject };
                        sb.ApplicationProperties["Priority"] = message.Priority;
                        await sender.SendMessageAsync(sb);
                        message.Status = "Published";
                    }
                    catch (Exception) { message.Attempts++; if (message.Attempts >= 3) message.Status = "Failed"; }
                }
            }
        }
        """;
    private static string ServiceTests(string regionSetup = "") => $$"""
        [Trait("Category", "Integration")]
        public class OutboxTests
        {
            private readonly Db _db = new();
            private readonly CustomerService _sut = new(new Db(), new OutboxPublisher(new Db()));
            [Fact] public async Task SessionIdIsCustomerId()
            {
                var dto = new CustomerDto(Guid.NewGuid(), "x", "{{PersonalValue}}", null, "r");
                await _sut.Ingest(dto);
                Assert.All(_db.Outbox.ToList(), r => Assert.Equal(dto.CustomerId.ToString(), r.SessionId));
            }
            [Fact] public async Task ChangeIsHighPriority()
            {
                var id = Guid.NewGuid();
                await _sut.Ingest(new CustomerDto(id, "a", null, null, "r"));
                await _sut.Ingest(new CustomerDto(id, "b", null, null, "r"));
                Assert.Equal("High", _db.Outbox.ToList().Last().Priority);
            }
            [Fact] public async Task SameRecordTwiceWritesOneRow()
            {
                var dto = new CustomerDto(Guid.NewGuid(), "x", null, null, "r");
                await _sut.Ingest(dto);
                await _sut.Ingest(dto);
                Assert.Single(_db.Outbox.ToList());
            }
            {{regionSetup}}
        }
        [Trait("Category", "Contract")]
        public class EventShapeTests
        {
            [Fact] public void CreatedCarriesNoRawNationalId()
            {
                var e = new CustomerCreatedEvent { CustomerId = Guid.NewGuid(), HasNationalId = true };
                Assert.DoesNotContain("nationalId\"", JsonSerializer.Serialize(e));
            }
        }
        """;
    private const string AdapterTests = """
        public class CustomerMapperTests
        {
            [Fact] public void MapsName()
            {
                var mapper = new CustomerMapper();
                var record = mapper.Map(Doc());
                Assert.Equal("n", record!.Name);
            }
        }
        """;

    private static (string, string)[] Fixture(string? regionTest = null, string? extraEventField = null) =>
    [
        ("Adapter/src/Adapter.csproj", Lib), ("Adapter/src/CustomerRecord.cs", AdapterModel), ("Adapter/src/CustomerMapper.cs", AdapterMapper), ("Adapter/src/IngestClient.cs", AdapterClient),
        ("Adapter/tests/Adapter.Tests.csproj", TestProject), ("Adapter/tests/CustomerMapperTests.cs", AdapterTests),
        ("Service/src/Service.csproj", Web), ("Service/src/CustomerEndpoint.cs", Endpoint),
        ("Service/src/Domain.cs", extraEventField is null ? Domain : Domain.Replace("public bool HasNationalId { get; init; }", "public bool HasNationalId { get; init; }\n    public bool " + extraEventField + " { get; init; }")),
        ("Service/src/Service.cs", extraEventField is null ? Service : Service.Replace("Region = dto.Region, Email = dto.Email };", "Region = dto.Region, Email = dto.Email, " + extraEventField + " = dto.Email is not null };")),
        ("Service/tests/Service.Tests.csproj", TestProject), ("Service/tests/OutboxTests.cs", ServiceTests(regionTest ?? "")),
    ];

    private static IqrSourceSnapshot Analyze(params (string Path, string Content)[] files)
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("customer-fixture.zip", IqrSourceEvidenceTests.Zip(files));
        error.Should().BeNull();
        return IqrSourceAnalyzer.Analyze("customer", workspace!, DateTimeOffset.UtcNow);
    }

    private static IntegrationPathEvidence Path(params (string, string)[] files) => Analyze(files).IntegrationPath ?? throw new InvalidOperationException("no path");
    private static FieldTrace Trace(IntegrationPathEvidence p, string field) => p.Fields.Single(f => f.OriginField == field);

    // ── Stages, hops and the contract boundary ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DiscoversEveryStage_FromSource_WithoutApplicationSpecificNames()
    {
        var snapshot = Analyze(Fixture());
        snapshot.AnalyzerVersion.Should().Be(2);
        var p = snapshot.IntegrationPath!;
        p.Stages.Select(s => (s.Kind, s.TypeName)).Should().Contain([
            (SourceStageKind.CdcField, "CDC payload"), (SourceStageKind.AdapterModel, "CustomerRecord"), (SourceStageKind.IngestionRequest, "CustomerIngestRequest"),
            (SourceStageKind.IngestionDto, "CustomerDto"), (SourceStageKind.DomainEntity, "Customer"), (SourceStageKind.DomainEvent, "CustomerCreatedEvent"),
            (SourceStageKind.DomainEvent, "CustomerChangedEvent"), (SourceStageKind.EventEnvelope, "Envelope"), (SourceStageKind.Outbox, "OutboxRow"), (SourceStageKind.ServiceBus, "customer.events")]);
        p.Hops.Select(h => h.Id).Should().ContainInOrder("eventhub-adapter", "http-CustomerRecord-CustomerIngestRequest-0", "ingestion-domain", "domain-event", "event-outbox", "outbox-servicebus", "servicebus-subscribers");
        p.Hops.Single(h => h.Id.StartsWith("http-")).Mechanism.Should().Be("HTTP PUT /api/customers/ingest (JSON by property name)");
        var boundary = p.Boundaries.Single();
        (boundary.From, boundary.To, boundary.FormalSchema).Should().Be(("CustomerRecord", "CustomerIngestRequest", "Not available"));
        boundary.Mismatches.Should().ContainSingle(m => m.Contains("LegacyCode is sent but CustomerIngestRequest has no such property"));
        boundary.Mismatches.Should().NotContain(m => m.Contains("DebugNote"), "a [JsonIgnore] property is an intentional filter, not a contract mismatch");
        p.Gaps.Should().Contain(g => g.Kind == SourceCoverageStatus.CrossLayerGap && g.Detail.Contains("LegacyCode"));
    }

    // ── §56–59 transformation classes; intentional vs unresolved drops ──────────────────────────────────────────

    [Fact]
    public void PassThroughField_StaysPassThroughAcrossStages()
    {
        var region = Trace(Path(Fixture()), "Region");
        region.Steps.Should().Contain(s => s.Stage == SourceStageKind.AdapterModel && s.Transformation == FieldTransformation.Defaulted, "missing Region falls back to a constant");
        region.Steps.Where(s => s.Stage is SourceStageKind.IngestionRequest or SourceStageKind.IngestionDto or SourceStageKind.DomainEntity or SourceStageKind.DomainEvent && s.TypeName != "Conflict")
            .Should().NotBeEmpty().And.OnlyContain(s => s.Transformation == FieldTransformation.PassThrough);
        region.Steps.Single(s => s.TypeName == "Conflict").Transformation.Should().Be(FieldTransformation.Derived, "a serialized raw copy is not a pass-through");
        region.Sensitive.Should().BeFalse();
        region.Minimization.Should().Be("Not sensitive");
    }

    [Fact]
    public void SensitiveValueReducedToAFlag_IsReducedMetadata_NotAnAccidentalDrop()
    {
        var p = Path(Fixture());
        var national = Trace(p, "NationalId");
        national.Sensitive.Should().BeTrue();
        national.Minimization.Should().Be("Intentional reduction / metadata projection");
        national.Steps.Should().Contain(s => s.TypeName == "CustomerCreatedEvent" && s.Field == "HasNationalId" && s.Transformation == FieldTransformation.Booleanized);
        national.Steps.Should().Contain(s => s.TypeName == "CustomerChangedEvent" && s.Field == "NationalIdChanged" && s.Transformation == FieldTransformation.Booleanized);
        national.Steps.Should().Contain(s => s.TypeName == "CustomerChangedEvent" && s.Field == "ChangedFields" && s.Transformation == FieldTransformation.MetadataOnly);
        national.Steps.Should().NotContain(s => s.Transformation == FieldTransformation.Dropped);
        national.Steps.Should().Contain(s => s.Stage == SourceStageKind.DomainEntity && s.TypeName == "Customer" && s.Field == "NationalId", "retained internally");
        p.Minimization.ReducedMetadata.Should().Contain("CustomerCreatedEvent.HasNationalId (Booleanized)");
        p.Minimization.EmittedRaw.Should().NotContain("NationalId");
    }

    [Fact]
    public void JsonIgnoredField_IsFilteredIntentionally_NoDefect()
    {
        var note = Trace(Path(Fixture()), "Note");
        note.Steps.Should().Contain(s => s.Transformation == FieldTransformation.FilteredIntentionally && s.Detail.Contains("[JsonIgnore]"));
        note.Gap.Should().BeEmpty();
    }

    [Fact]
    public void FieldTheReceiverDoesNotAccept_IsDroppedNotResolved_NotAHighSeverityIssue()
    {
        var p = Path(Fixture());
        var legacy = Trace(p, "Legacy");
        legacy.Steps.Should().Contain(s => s.Transformation == FieldTransformation.Dropped && s.Detail.Contains("reason not resolved"));
        legacy.Gap.Should().StartWith("Not resolved:");
        legacy.Outcome.Should().Contain("Dropped at the adapter → ingestion boundary");
        p.Gaps.Where(g => g.Detail.Contains("LegacyCode")).Should().OnlyContain(g => g.Kind == SourceCoverageStatus.CrossLayerGap, "a potential cross-layer gap, never a severity verdict");
    }

    [Fact]
    public void RawSensitiveFieldInAnEvent_IsAPotentialDataMinimizationIssue_WithItsPath()
    {
        var p = Path(Fixture());
        var email = Trace(p, "Email");
        email.Minimization.Should().Be("Potential data-minimization issue");
        email.Outcome.Should().Contain("CustomerCreatedEvent.Email");
        p.Minimization.EmittedRaw.Should().Contain("Email");
        p.Gaps.Should().ContainSingle(g => g.Title == "Potential data-minimization issue: Email").Which.Kind.Should().Be(SourceCoverageStatus.CrossLayerGap);
        p.Minimization.InternalRawCopies.Should().Contain("Conflict.Payload ← serialized CustomerDto");
        p.Gaps.Should().Contain(g => g.Kind == SourceCoverageStatus.ManualVerification && g.Title.StartsWith("Raw copy retained internally"), "internal retention is flagged for confirmation, not as an exposure");
    }

    // ── Events, change detection, outbox, dispatcher ─────────────────────────────────────────────────────────────

    [Fact]
    public void EventContracts_ChangeDetection_Outbox_AndServiceBusComeFromSource()
    {
        var p = Path(Fixture());
        var created = p.Events.Single(e => e.EventType == "CustomerCreatedEvent");
        (created.Topics.Single(), created.Subjects.Single(), created.SessionId, created.Priority).Should().Be(("customer.events", "CustomerCreated", "CustomerDto.CustomerId", "Normal (publisher default)"));
        created.FormalSchema.Should().Be("Not available");
        p.Events.Single(e => e.EventType == "CustomerChangedEvent").Priority.Should().Be("High");

        var change = p.ChangeDetection.Single();
        (change.Entity, change.Input).Should().Be(("Customer", "CustomerDto"));
        change.TrackedFields.Should().BeEquivalentTo(["Name", "NationalId"]);
        change.AssignedNotTracked.Should().Equal("Region");
        change.AuditMetadataNotTracked.Should().Equal("ChangedBy");
        change.Gate.Should().StartWith("An empty change set returns before the update is applied");
        change.Emits.Should().Be("Changed field names and boolean change flags; no field values");

        var outbox = p.Outbox!;
        outbox.EntityType.Should().Be("OutboxRow");
        outbox.Envelope.Should().Be("Envelope");
        outbox.Serialization.Should().Be("JSON, camelCase property names");
        outbox.Transaction.Should().StartWith("Outbox row added to the caller's unit of work");
        outbox.MessageId.Should().Contain("reused on every send attempt");
        outbox.Retry.Should().Contain("after 3 attempts");
        outbox.Dispatcher.Should().StartWith("OutboxDispatcher polls OutboxRow where status is Pending");
        var bus = p.ServiceBus!;
        bus.Publications.Single().Entity.Should().Be("customer.events");
        bus.Authentication.Should().Contain("DefaultAzureCredential").And.Contain("connection string");
        bus.MessageMapping.Should().Contain("Body ← Payload").And.Contain("SessionId ← SessionId").And.Contain("ApplicationProperties[Priority] ← Priority");
        bus.ConfigurationKeys.Should().Contain("BusOptions:FQDN");
    }

    // ── §60–64 developer tests reused, never duplicated; existence ≠ pass ──────────────────────────────────────────

    [Fact]
    public void ExistingDeveloperTests_AreReusedAsEvidence_AndNoDuplicateIsRecommended()
    {
        var snapshot = Analyze(Fixture());
        var p = snapshot.IntegrationPath!;
        string Test(string id) => snapshot.Tests.Single(t => t.Id == id).Method;
        var privacy = p.Rules.Single(r => r.Kind == "EventPrivacy" && r.Title.StartsWith("CustomerCreatedEvent"));
        privacy.Coverage.Should().Contain(SourceCoverageStatus.DeveloperContractCovered);
        privacy.DeveloperTestIds.Select(Test).Should().Equal("CreatedCarriesNoRawNationalId");
        privacy.BirkNextAction.Should().Contain("no duplicate BirkNext test");
        var session = p.Rules.Single(r => r.Kind == "SessionId");
        session.DeveloperTestIds.Select(Test).Should().Equal("SessionIdIsCustomerId");
        session.Coverage.Should().Contain(SourceCoverageStatus.DeveloperIntegrationCovered).And.Contain(SourceCoverageStatus.RuntimeGap);
        p.Rules.Single(r => r.Kind == "Priority").DeveloperTestIds.Select(Test).Should().Equal("ChangeIsHighPriority");
        p.Rules.Single(r => r.Kind == "Idempotency").DeveloperTestIds.Select(Test).Should().Contain("SameRecordTwiceWritesOneRow");
        p.Rules.Single(r => r.Kind == "OutboxCreation").Coverage.Should().Contain(SourceCoverageStatus.DeveloperIntegrationCovered);
        snapshot.Tests.Where(t => p.Rules.SelectMany(r => r.DeveloperTestIds).Contains(t.Id))
            .Should().OnlyContain(t => t.ExecutionResult == "Developer test exists; execution result unavailable", "a discovered test is not a passed test");
        snapshot.Tests.Single(t => t.Method == "SessionIdIsCustomerId").Layer.Should().Be(DeveloperTestLayer.Integration, "declared by the Category trait");
        snapshot.Tests.Single(t => t.Method == "CreatedCarriesNoRawNationalId").Layer.Should().Be(DeveloperTestLayer.Contract);
        snapshot.Limitations.Should().Contain(l => l.Contains("No executable tests recommended or created"));
    }

    [Fact]
    public void OutboxCovered_DeliveryUnknown_AndSubscribersNotAssessed()
    {
        var p = Path(Fixture());
        p.Hops.Single(h => h.Id == "event-outbox").Coverage.Should().Contain(SourceCoverageStatus.DeveloperIntegrationCovered);
        var delivery = p.Hops.Single(h => h.Id == "outbox-servicebus");
        (delivery.SourceState, delivery.RuntimeState).Should().Be(("Source-defined", "Not assessed"));
        delivery.Coverage.Should().Contain(SourceCoverageStatus.SourceEvidenceOnly).And.Contain(SourceCoverageStatus.RuntimeGap);
        delivery.Note.Should().Be("A source-defined publisher is not observed delivery.");
        p.Hops.Single(h => h.Id == "servicebus-subscribers").Coverage.Should().Equal(SourceCoverageStatus.E2EGap);
        p.Gaps.Should().Contain(g => g.Kind == SourceCoverageStatus.RuntimeGap && g.Title == "Outbox → Service Bus delivery");
        p.Gaps.Should().Contain(g => g.Kind == SourceCoverageStatus.E2EGap && g.Title == "Subscriber consumption");
    }

    [Fact]
    public void AdapterUnitTest_CoversTheAdapterStepOnly_TheBoundaryStaysSourceTraceOnly()
    {
        var snapshot = Analyze(Fixture());
        var name = snapshot.IntegrationPath!.Fields.Single(f => f.OriginField == "FullName");
        var adapterStep = name.Steps.Single(s => s.Stage == SourceStageKind.AdapterModel);
        adapterStep.DeveloperTestIds!.Select(id => snapshot.Tests.Single(t => t.Id == id).Method).Should().Equal("MapsName");
        snapshot.IntegrationPath.Boundaries.Single().DeveloperContractTests.Should().StartWith("One-sided only");
        snapshot.IntegrationPath.Gaps.Should().Contain(g => g.Title == "No developer test spans the adapter → ingestion contract");
    }

    // ── §65 cross-layer gap stays visible despite developer tests; §31/§66 later snapshots reflect new tests/fields ─

    [Fact]
    public void UntrackedUpdateField_IsACrossLayerGap_AndAnUpdateTestIsOnlyACandidateNeverClosure()
    {
        var before = Path(Fixture());
        var gap = before.Gaps.Single(g => g.Title == "Customer.Region is assigned on update but not change-tracked");
        gap.Confidence.Should().Be(SourceConfidence.StrongSourceEvidence, "the empty-change gate is resolved");
        gap.Detail.Should().Contain("No developer test sets this field in an update scenario");

        var withTest = Path(Fixture("""
            [Fact] public async Task RegionOnlyChange()
            {
                var id = Guid.NewGuid();
                await _sut.Ingest(new CustomerDto(CustomerId: id, Name: "a", NationalId: null, Email: null, Region: "north"));
                await _sut.Ingest(new CustomerDto(CustomerId: id, Name: "a", NationalId: null, Email: null, Region: "south"));
                Assert.Single(_db.Outbox.ToList());
            }
            """));
        withTest.Gaps.Single(g => g.Title == "Customer.Region is assigned on update but not change-tracked").Detail
            .Should().Contain("RegionOnlyChange").And.Contain("whether they change only this field is not resolved");
    }

    [Fact]
    public void ANewEventFieldInALaterArchive_AppearsInTheNewSnapshot_AndTheOldSnapshotIsUnchanged()
    {
        var a = Analyze(Fixture());
        var aJson = JsonSerializer.Serialize(a);
        var b = Analyze(Fixture(extraEventField: "HasEmail"));
        a.IntegrationPath!.Events.Single(e => e.EventType == "CustomerCreatedEvent").Fields.Select(f => f.Name).Should().NotContain("HasEmail");
        var hasEmail = b.IntegrationPath!.Events.Single(e => e.EventType == "CustomerCreatedEvent").Fields.Single(f => f.Name == "HasEmail");
        hasEmail.Transformation.Should().Be(FieldTransformation.Booleanized);
        Trace(b.IntegrationPath, "Email").Steps.Should().Contain(s => s.Field == "HasEmail");
        JsonSerializer.Serialize(a).Should().Be(aJson, "snapshot A is immutable evidence");
        b.Id.Should().NotBe(a.Id);
        b.Archive.Sha256.Should().NotBe(a.Archive.Sha256);
    }

    [Fact]
    public void UnsupportedPattern_IsNotResolved_NeverAssumed()
    {
        // Adapter only: the CDC → adapter part is traced; no boundary, ingestion, event or outbox stage is invented.
        var snapshot = Analyze(("Adapter/src/Adapter.csproj", Lib), ("Adapter/src/CustomerRecord.cs", AdapterModel), ("Adapter/src/CustomerMapper.cs", AdapterMapper));
        snapshot.IntegrationPath!.Stages.Select(st => st.Kind).Distinct().Should().BeEquivalentTo([SourceStageKind.CdcField, SourceStageKind.AdapterModel]);
        snapshot.IntegrationPath.Boundaries.Should().BeEmpty();
        snapshot.IntegrationPath.Outbox.Should().BeNull();
        Analyze(("Adapter/src/Adapter.csproj", Lib), ("Adapter/src/CustomerRecord.cs", AdapterModel)).IntegrationPath.Should().BeNull("a model alone is not a path");
        var mapperOnly = Analyze(("Adapter/src/Adapter.csproj", Lib), ("Adapter/src/CustomerRecord.cs", AdapterModel),
            ("Adapter/src/CustomerMapper.cs", AdapterMapper.Replace("Region: GetString(payload, \"Region\") ?? \"Unknown\"", "Region: Dynamic(payload)")),
            ("Adapter/src/IngestClient.cs", AdapterClient), ("Service/src/Service.csproj", Web), ("Service/src/CustomerEndpoint.cs", Endpoint), ("Service/src/Domain.cs", Domain), ("Service/src/Service.cs", Service));
        mapperOnly.IntegrationPath!.Fields.Should().NotContain(f => f.OriginField == "Region", "a value from an unresolved helper has no CDC origin");
    }

    // ── §72 no source text or personal values; §70 no data-plane operation in the analyzer ───────────────────────────

    [Fact]
    public void Snapshot_HoldsFieldNamesOnly_NoPersonalValueOrSourceText()
    {
        var json = JsonSerializer.Serialize(Analyze(Fixture()));
        json.Should().NotContain(PersonalValue);
        json.Should().NotContain("JsonSerializer.Serialize(dto)").And.NotContain("GetString(payload");
    }

    [Fact]
    public void PathAnalysis_NeverCreatesAClientOrSendsAnything()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(System.IO.Path.Combine(root, "BirkNext.Api"))) root = System.IO.Path.GetDirectoryName(root)!;
        foreach (var file in new[] { "IqrPathAnalyzer.cs", "IqrPathReview.cs" })
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(System.IO.Path.Combine(root, "BirkNext.Api", "Services", "Integrations", "SourceEvidence", file)));
            var nodes = tree.GetCompilationUnitRoot().DescendantNodes().ToList();
            nodes.OfType<ObjectCreationExpressionSyntax>().Select(o => o.Type.ToString()).Should().NotContain(t => t.EndsWith("Client") || t.EndsWith("Sender") || t.EndsWith("Receiver") || t.EndsWith("Processor"),
                $"{file} only reads syntax");
            nodes.OfType<InvocationExpressionSyntax>().Select(i => i.Expression.ToString()).Should().NotContain(e => e.EndsWith(".SendMessageAsync") || e.EndsWith(".SendAsync") || e.EndsWith(".PutAsJsonAsync") || e.EndsWith(".PostAsJsonAsync"),
                $"{file} never sends; method names appear only as strings it looks for");
        }
    }

    // ── §53 source ≠ configured ≠ observed ≠ processed; broker settings vs publisher behavior ───────────────────────

    [Fact]
    public void ServiceBusStates_StaySeparate_AndBrokerSettingsAreCorrelatedWithThePublisher()
    {
        var p = Path(Fixture());
        var topology = new ServiceBusTopology
        {
            Entities =
            [
                new() { EntityType = ServiceBusEntityType.Topic, Name = "customer.events", RequiresDuplicateDetection = false },
                new() { EntityType = ServiceBusEntityType.Subscription, Topic = "customer.events", Name = "billing", RequiresSession = false },
            ],
        };
        IntegrationReviewResult Result(ServiceBusRuntimeEvidence? runtime) => new()
        {
            ConfigurationSnapshot = new IntegrationCatalog { Platforms = [new IntegrationPlatform { Id = "sb", Kind = IntegrationKind.ServiceBus, ServiceBusTopology = topology }] },
            ServiceBusSnapshot = runtime is null ? [] : [new ServiceBusEvidenceCheck { PlatformId = "sb", Runtime = runtime }],
        };
        IqrPathReview.ServiceBusStates(p, Result(null)).Single().Should()
            .Contain("source-defined publisher").And.Contain("configured: yes, 1 subscription(s) (billing)").And.Contain("Not assessed (no Service Bus runtime read in this run)").And.Contain("runtime processing: not assessed");
        var observed = Result(new ServiceBusRuntimeEvidence { State = IntegrationEvidenceState.Available, Entities = [new() { EntityType = ServiceBusEntityType.Topic, Name = "customer.events" }] });
        IqrPathReview.ServiceBusStates(p, observed).Single().Should().Contain("Observed in Azure").And.Contain("runtime processing: not assessed", "an entity that exists is not a processed message");
        var consistency = IqrPathReview.TopologyConsistency(p, observed);
        consistency.Should().Contain(l => l.Contains("configured subscription billing does not require sessions") && l.Contains("needs confirmation"));
        consistency.Should().Contain(l => l.Contains("duplicate detection is off for customer.events") && l.Contains("subscribers must be idempotent"));
    }

    [Fact]
    public void ReviewDomains_UsePathEvidence_AndNeverBecomeFullyAssessedFromSource()
    {
        var snapshot = Analyze(Fixture());
        var result = new IntegrationReviewResult
        {
            Domains = Enum.GetValues<IntegrationReviewDomain>().Select(d => new IntegrationDomainResult { Domain = d, StateLabel = d == IntegrationReviewDomain.Configuration ? "Assessed" : "Not assessed" }).ToList(),
        };
        var reviewed = IqrSourceReview.Augment(result, [snapshot]);
        IntegrationDomainResult D(IntegrationReviewDomain d) => reviewed.Domains.Single(x => x.Domain == d);
        D(IntegrationReviewDomain.Contract).Observed.Should().Contain(o => o.StartsWith("Contract boundary CustomerRecord → CustomerIngestRequest") && o.Contains("formal schema Not available"));
        D(IntegrationReviewDomain.MessageFlow).Observed.Should().Contain(o => o.StartsWith("OutboxRow → customer.events: Source-defined; source trace only; runtime Not assessed"));
        D(IntegrationReviewDomain.Reliability).Observed.Should().Contain(o => o.StartsWith("Session id on customer.events: Covered by developer integration test"));
        D(IntegrationReviewDomain.Reliability).Missing.Should().Contain(m => m.Contains("Region is assigned on update but not change-tracked"));
        D(IntegrationReviewDomain.Security).Observed.Should().Contain(o => o.StartsWith("Data minimization (field names only)"));
        D(IntegrationReviewDomain.Security).Missing.Should().Contain(m => m.StartsWith("Potential data-minimization issue")).And.Contain(m => m.Contains("separate control from authorization"));
        reviewed.Domains.Where(d => d.Domain != IntegrationReviewDomain.Configuration).Should().OnlyContain(d => d.StateLabel != "Assessed", "source evidence alone never completes a domain");
        reviewed.WhatWasTested.Should().Contain(t => t.StartsWith("Source inspected: Outbound HTTP PUT /api/customers/ingest"));
        reviewed.WhatWasNotAssessed.Should().Contain("No Event Hub event sent and no Service Bus message sent or received").And.Contain("No real personal data inspected (field names and contracts only)");
    }

    [Fact]
    public void AnalyzerV1SnapshotsWithoutAPath_StillReadAndReview()
    {
        var legacy = JsonSerializer.Deserialize<IqrSourceSnapshot>("""{"integrationId":"x","analyzerVersion":1,"status":"Ready","rules":[],"tests":[]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        legacy.IntegrationPath.Should().BeNull();
        var reviewed = IqrSourceReview.Augment(new IntegrationReviewResult { Domains = [new IntegrationDomainResult { Domain = IntegrationReviewDomain.MessageFlow, StateLabel = "Not assessed" }] }, [legacy]);
        reviewed.WhatWasNotAssessed.Should().NotContain("No Event Hub event sent and no Service Bus message sent or received");
    }
}
