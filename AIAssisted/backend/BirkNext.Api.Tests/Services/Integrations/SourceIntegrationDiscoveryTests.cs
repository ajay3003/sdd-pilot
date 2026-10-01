using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Api.Services.Integrations.SourceDiscovery;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Generic source integration discovery over synthetic, non-M2LB fixtures read through the real archive reader: Change Data Capture with
/// Debezium or a custom publisher, Event Hub / Service Bus messaging, HTTP and GraphQL calls. Detected values are at most suggestions; the
/// reconciler never confirms, never overwrites and makes conflicts, stale and changed evidence visible.
/// </summary>
public sealed class SourceIntegrationDiscoveryTests
{
    private const string Web = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string Worker = """<Project Sdk="Microsoft.NET.Sdk.Worker"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string AppHost = """<Project Sdk="Aspire.AppHost.Sdk/9.0.0"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private static string Pkg(params string[] packages) => "<ItemGroup>" + string.Concat(packages.Select(p => $"""<PackageReference Include="{p}" Version="1.0.0" />""")) + "</ItemGroup>";

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    /// <summary>Analyzes a fixture exactly as Source Analysis does (architecture + signals), without the database store.</summary>
    private static IqrSourceSnapshot Snapshot(params (string Path, string Content)[] files)
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("fixture.zip", Zip(files));
        error.Should().BeNull();
        var id = Guid.NewGuid();
        var architecture = SourceArchitectureAnalyzer.Analyze(id, workspace!, DateTimeOffset.UtcNow, null);
        return new IqrSourceSnapshot
        {
            Id = id, IntegrationId = "source", Archive = workspace!.Archive, AnalyzedAt = DateTimeOffset.UtcNow, Architecture = architecture,
            IntegrationSignals = SourceIntegrationSignalExtractor.Extract(workspace, architecture),
        };
    }

    // ── Fixture A: Debezium-style change capture, consumed from Event Hub by a worker; the capture runs outside the source ──
    private const string CdcHubs = """{ "EventHub": { "EventHubNames": [ "shop-cdc.ShopDb.dbo.Orders", "shop-cdc.ShopDb.dbo.Customers" ] } }""";

    private static (string, string)[] DebeziumConsumer(bool debeziumMarkers = true) =>
    [
        ("Shop/Shop.CdcConsumer/Shop.CdcConsumer.csproj", string.Format(Worker, Pkg("Azure.Messaging.EventHubs.Processor"))),
        ("Shop/Shop.CdcConsumer/Program.cs", """
            var b = Host.CreateApplicationBuilder(args);
            var options = b.Configuration.GetSection("EventHub").Get<EventHubOptions>()!;
            foreach (var hubName in options.EventHubNames)
            {
                var processor = new EventProcessorClient(checkpoints, "$Default", ns, hubName, credential);
            }
            b.Build().Run();
            """),
        ("Shop/Shop.CdcConsumer/EventHubOptions.cs", "public sealed class EventHubOptions { public string[] EventHubNames { get; set; } = []; }"),
        ("Shop/Shop.CdcConsumer/appsettings.json", CdcHubs),
        ("Shop/Shop.CdcConsumer/ChangeEvents.cs", debeziumMarkers
            ? """
              public sealed record CdcEvent(string Table, string Operation);
              // A Debezium mention in a comment is never evidence.
              public sealed record DebeziumEnvelope(DebeziumPayload? Payload);
              public sealed record DebeziumPayload(string Op);
              """
            : "public sealed record CdcEvent(string Table, string Operation);"),
        ("Shop/AppHost/ConfigFiles/Eventhub-config.json", """
            { "UserConfig": { "NamespaceConfig": [ { "Type": "EventHub", "Name": "shopNs", "Entities": [
              { "Name": "shop-cdc.ShopDb.dbo.Orders" }, { "Name": "shop-cdc.ShopDb.dbo.Customers" }, { "Name": "shop-cdc.ShopDb.dbo.Audit" }, { "Name": "connect-offsets" } ] } ] } }
            """),
    ];

    // ── Fixture B: a custom change-capture publisher in source → Service Bus topic → worker (no Debezium anywhere) ──
    private static readonly (string, string)[] CustomPublisher =
    [
        ("Legacy/Legacy.ChangePublisher/Legacy.ChangePublisher.csproj", string.Format(Worker, Pkg("Azure.Messaging.ServiceBus"))),
        ("Legacy/Legacy.ChangePublisher/Publisher.cs", """
            public sealed class OrderChangeCapturePublisher(ServiceBusClient client)
            {
                public ServiceBusSender Sender => client.CreateSender("orders.changes");
            }
            """),
        ("Legacy/Legacy.ChangePublisher/Program.cs", "var b = Host.CreateApplicationBuilder(args); b.Build().Run();"),
        ("Orders/Orders.Worker/Orders.Worker.csproj", string.Format(Worker, Pkg("Azure.Messaging.ServiceBus"))),
        ("Orders/Orders.Worker/Consumer.cs", """
            public sealed class Consumer(ServiceBusClient client)
            {
                private readonly ServiceBusProcessor _changes = client.CreateProcessor("orders.changes", "orders-worker", new ServiceBusProcessorOptions());
            }
            """),
        ("Orders/Orders.Worker/Program.cs", "var b = Host.CreateApplicationBuilder(args); b.Build().Run();"),
    ];

    // ── Fixture C: HTTP call from a gateway to an internal API, wired explicitly by the orchestration ──
    private static readonly (string, string)[] HttpCall =
    [
        ("Pay/Partner.Gateway/Partner.Gateway.csproj", string.Format(Web, "")),
        ("Pay/Partner.Gateway/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddHttpClient("Orders", c => c.BaseAddress = new Uri(builder.Configuration["Orders:BaseUrl"]!));
            var app = builder.Build(); app.MapPost("/partner", () => 1); app.Run();
            """),
        ("Pay/Orders.Api/Orders.Api.csproj", string.Format(Web, "")),
        ("Pay/Orders.Api/Program.cs", "var builder = WebApplication.CreateBuilder(args); var app = builder.Build(); app.MapGet(\"/orders\", () => 1); app.Run();"),
        ("Pay/AppHost/AppHost.csproj", string.Format(AppHost, "")),
        ("Pay/AppHost/AppHost.cs", """
            var builder = DistributedApplication.CreateBuilder(args);
            var orders = builder.AddProject<Projects.Orders_Api>("orders");
            var gateway = builder.AddProject<Projects.Partner_Gateway>("gateway").WithEnvironment("Orders__BaseUrl", orders.GetEndpoint("http"));
            """),
    ];

    // ── Fixture D: GraphQL — Strawberry Shake client in a frontend, Hot Chocolate server behind it ──
    private static readonly (string, string)[] GraphQl =
    [
        ("Cat/Catalog.Graph/Catalog.Graph.csproj", string.Format(Web, Pkg("HotChocolate.AspNetCore"))),
        ("Cat/Catalog.Graph/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddGraphQLServer().AddQueryType<Query>();
            var app = builder.Build(); app.MapGraphQL("/graphql"); app.Run();
            """),
        ("Cat/Shop.Web/Shop.Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><ItemGroup><PackageReference Include="StrawberryShake.Blazor" Version="1" /></ItemGroup></Project>"""),
        ("Cat/Shop.Web/Program.cs", """
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            var graphUrl = builder.Configuration["CatalogGraph:Url"];
            builder.Services.AddCatalogClient().ConfigureHttpClient(c => c.BaseAddress = new Uri(graphUrl!));
            """),
        ("Cat/AppHost/AppHost.csproj", string.Format(AppHost, "")),
        ("Cat/AppHost/AppHost.cs", """
            var builder = DistributedApplication.CreateBuilder(args);
            var graph = builder.AddProject<Projects.Catalog_Graph>("catalog-graph");
            var web = builder.AddProject<Projects.Shop_Web>("web").WithEnvironment("CatalogGraph__Url", graph.GetEndpoint("https"));
            """),
    ];

    private static SourceIntegrationCandidate Single(SourceIntegrationDiscoveryResult r, string channel) => r.Candidates.Single(c => c.ChannelName == channel);

    [Fact]
    public void CdcWithDebeziumOverEventHub_IsChangeDataCapture_WithDebeziumAsCaptureTechnology()
    {
        var r = SourceIntegrationDiscoveryEngine.Discover(Snapshot(DebeziumConsumer()));
        var orders = Single(r, "shop-cdc.ShopDb.dbo.Orders");
        (orders.Pattern, orders.CaptureTechnology, orders.Transport).Should().Be((IntegrationPattern.ChangeDataCapture, "Debezium", IntegrationTransport.EventHub));
        (orders.SourceDatabase, orders.SourceSchema, orders.SourceEntity, orders.SourceEntityType).Should().Be(("ShopDb", "dbo", "Orders", "Table"));
        orders.ConsumerCandidate.Should().Be("Shop.CdcConsumer");
        orders.ProducerComponent.Should().BeNull("the change capture runs outside the analyzed source");
        orders.Diagnostics.Should().Contain(d => d.Contains("No producer"));
        orders.Evidence.Should().Contain(e => e.Field == "Capture technology" && e.Value == "Debezium" && e.File.EndsWith("ChangeEvents.cs") && e.Line > 1);
        orders.Evidence.Should().Contain(e => e.Field == "Source entity" && e.Value == "dbo.Orders");
        r.DetectedTechnologies.Should().Contain("Debezium");
        r.SourceSnapshotId.Should().NotBeEmpty();
        r.ExtractorVersions.Keys.Should().Contain(["change-data-capture", "messaging", "http", "graphql", "architecture-bridge", "signals"]);
    }

    [Fact]
    public void CdcWithACustomPublisherOverServiceBus_HasNoDebeziumTerminology()
    {
        var r = SourceIntegrationDiscoveryEngine.Discover(Snapshot(CustomPublisher));
        var changes = Single(r, "orders.changes");
        (changes.Pattern, changes.CaptureTechnology, changes.Transport).Should().Be((IntegrationPattern.ChangeDataCapture, "Custom publisher", IntegrationTransport.ServiceBusTopic));
        (changes.ProducerComponent, changes.ConsumerCandidate).Should().Be(("Legacy.ChangePublisher", "Orders.Worker"));
        JsonSerializer.Serialize(r).Should().NotContain("Debezium");
        SourceIntegrationLabels.Pattern(changes.Pattern).Should().Be("Change Data Capture");
    }

    [Fact]
    public void HttpCallBetweenComponents_IsAnApiCallFromCallerToCallee()
    {
        var r = SourceIntegrationDiscoveryEngine.Discover(Snapshot(HttpCall));
        var call = r.Candidates.Single(c => c.Transport == IntegrationTransport.Http);
        (call.Pattern, call.ProducerComponent, call.ConsumerCandidate, call.CaptureTechnology).Should().Be((IntegrationPattern.ApiCall, "Partner.Gateway", "Orders.Api", null));
        call.ConsumerEvidence.Should().BeOneOf(ArchitectureEvidenceState.Confirmed, ArchitectureEvidenceState.StronglySupported);
        r.Candidates.Should().NotContain(c => c.Pattern == IntegrationPattern.ChangeDataCapture, "no CDC, no Debezium and no Event Hub in this source");
    }

    [Fact]
    public void GraphQl_KeepsStrawberryShakeAndHotChocolateAsTechnologies_NotNodes()
    {
        var r = SourceIntegrationDiscoveryEngine.Discover(Snapshot(GraphQl));
        var gql = r.Candidates.Single(c => c.Transport == IntegrationTransport.GraphQl);
        (gql.SourceSystem, gql.ConsumerCandidate, gql.ChannelName).Should().Be(("Shop.Web", "Catalog.Graph", "/graphql"));
        gql.CallerTechnology.Should().Be("Strawberry Shake");
        gql.ServerTechnology.Should().Contain("Hot Chocolate");
        r.Candidates.Should().NotContain(c => c.ConsumerCandidate == "Strawberry Shake" || c.ConsumerCandidate!.Contains("Hot Chocolate") || c.SourceSystem == "Strawberry Shake");
    }

    [Fact]
    public void EventHubAndServiceBusProducersAndConsumersPairOnlyOnTheSameChannel()
    {
        var r = SourceIntegrationDiscoveryEngine.Discover(Snapshot([.. DebeziumConsumer(), .. CustomPublisher]));
        Single(r, "shop-cdc.ShopDb.dbo.Customers").ConsumerCandidate.Should().Be("Shop.CdcConsumer");
        Single(r, "orders.changes").ConsumerCandidate.Should().Be("Orders.Worker");
        r.Candidates.Should().NotContain(c => c.ChannelName == "orders.changes" && c.ConsumerCandidate == "Shop.CdcConsumer");
    }

    [Fact]
    public void ADeclaredChangeTopicWithoutConsumer_IsUnassigned_AndPlatformChannelsAreExcluded()
    {
        var r = SourceIntegrationDiscoveryEngine.Discover(Snapshot(DebeziumConsumer()));
        var audit = Single(r, "shop-cdc.ShopDb.dbo.Audit");
        (audit.Pattern, audit.ConsumerCandidate, audit.ConsumerEvidence).Should().Be((IntegrationPattern.ChangeDataCapture, null, ArchitectureEvidenceState.Unresolved));
        audit.CaptureTechnology.Should().Be("Debezium", "the same change-topic family carries the capture evidence");
        audit.Diagnostics.Should().Contain(d => d.Contains("Not a failure"));
        r.TechnicalChannels.Should().ContainSingle(t => t.Name == "connect-offsets").Which.Purpose.Should().Contain("Kafka Connect");
        r.Candidates.Should().NotContain(c => c.ChannelName == "connect-offsets");
    }

    [Fact]
    public void TheGenericModelHasNoDebeziumDependency()
    {
        Enum.GetNames<IntegrationPattern>().Concat(Enum.GetNames<IntegrationTransport>()).Should().NotContain(n => n.Contains("Debezium", StringComparison.OrdinalIgnoreCase));
        typeof(SourceIntegrationCandidate).GetProperty(nameof(SourceIntegrationCandidate.CaptureTechnology))!.PropertyType.Should().Be(typeof(string), "capture technology is open, never an enum");
        SourceIntegrationLabels.PatternShort(IntegrationPattern.ChangeDataCapture).Should().Be("CDC");
    }

    [Fact]
    public void ReplacingDebeziumWithACustomPublisher_KeepsTheModel_AndShowsTheChange()
    {
        var before = SourceIntegrationDiscoveryEngine.Discover(Snapshot(DebeziumConsumer()));
        var after = SourceIntegrationDiscoveryEngine.Discover(Snapshot(
        [
            .. DebeziumConsumer(debeziumMarkers: false),
            ("Shop/Shop.ChangePublisher/Shop.ChangePublisher.csproj", string.Format(Worker, Pkg("Azure.Messaging.EventHubs"))),
            ("Shop/Shop.ChangePublisher/Program.cs", """
                var b = Host.CreateApplicationBuilder(args);
                await using var producer = new EventHubProducerClient(ns, "shop-cdc.ShopDb.dbo.Orders", credential);
                b.Build().Run();
                """),
            ("Shop/Shop.ChangePublisher/OrdersChangeCapture.cs", "public sealed class OrdersChangeCapture { }"),
        ]));
        var a = Single(before, "shop-cdc.ShopDb.dbo.Orders");
        var b = Single(after, "shop-cdc.ShopDb.dbo.Orders");
        b.Id.Should().Be(a.Id, "the same integration across snapshots");
        (b.Pattern, b.CaptureTechnology, b.ConsumerComponentId).Should().Be((IntegrationPattern.ChangeDataCapture, "Custom publisher", a.ConsumerComponentId));
        b.ProducerComponent.Should().EndWith("ChangePublisher");
        SourceIntegrationReconciler.Changes(a, b).Should().Contain("Capture technology: Debezium → Custom publisher");
    }

    // ── Reconciliation with the configured catalog ──

    private static IntegrationCatalog Catalog(params IntegrationDefinition[] integrations) => new() { EnvironmentId = "env", Integrations = [.. integrations] };

    private static IntegrationDefinition Configured(string channel, string? consumer = null, ConsumerMappingState state = ConsumerMappingState.NeedsConfirmation, Guid? basis = null) => new()
    {
        Id = $"env:{channel}", EnvironmentId = "env", DisplayName = channel, Kind = IntegrationKind.EventHub, EndpointOrTopic = channel,
        Consumer = new IntegrationConsumer { DisplayName = consumer, MappingState = state, MappingSourceSnapshotId = basis },
    };

    [Fact]
    public void StrongSourceSupportStaysASuggestion_UntilAPersonConfirms()
    {
        var discovery = SourceIntegrationDiscoveryEngine.Discover(Snapshot(HttpCall));
        var call = discovery.Candidates.Single(c => c.Transport == IntegrationTransport.Http);
        call.ConsumerEvidence.Should().BeOneOf(ArchitectureEvidenceState.Confirmed, ArchitectureEvidenceState.StronglySupported);
        var definition = Configured(call.ChannelName!) with { Kind = IntegrationKind.HttpApi };
        var catalog = Catalog(definition);
        var report = SourceIntegrationReconciler.Reconcile(catalog, discovery, null, DateTimeOffset.UtcNow);
        var match = report.Matches.Single();
        (match.State, match.SourceConsumer).Should().Be((SourceMatchState.SourceSuggestion, "Orders.Api"));
        match.Detail.Should().Contain("has not been confirmed");
        catalog.Integrations.Single().Consumer.Should().BeEquivalentTo(definition.Consumer, "reconciliation never writes the catalog");
        JsonSerializer.Serialize(report).Should().NotContain("\"MappingState\":\"Confirmed\"").And.NotContain("Confirmed mapping");
    }

    [Fact]
    public void AMatchingConfiguredConsumerIsSupported_AndADifferentOneIsAConflict_NothingIsOverwritten()
    {
        var discovery = SourceIntegrationDiscoveryEngine.Discover(Snapshot(DebeziumConsumer()));
        var supported = Configured("shop-cdc.ShopDb.dbo.Orders", "Shop CDC Consumer", ConsumerMappingState.Confirmed);
        var conflicting = Configured("shop-cdc.ShopDb.dbo.Customers", "Billing API", ConsumerMappingState.Confirmed);
        var report = SourceIntegrationReconciler.Reconcile(Catalog(supported, conflicting), discovery, null, DateTimeOffset.UtcNow);
        report.Matches.Single(m => m.IntegrationId == supported.Id).State.Should().Be(SourceMatchState.Supported);
        var conflict = report.Matches.Single(m => m.IntegrationId == conflicting.Id);
        (conflict.State, conflict.SourceConsumer).Should().Be((SourceMatchState.Conflict, "Shop.CdcConsumer"));
        conflict.Detail.Should().Contain("Billing API").And.Contain("Shop.CdcConsumer").And.Contain("configured mapping is kept");
    }

    [Fact]
    public void AConfirmedMappingSurvivesReanalysis_ChangedEvidenceIsVisible_AndOutdatedBasisIsStale()
    {
        var first = SourceIntegrationDiscoveryEngine.Discover(Snapshot(DebeziumConsumer()));
        var second = SourceIntegrationDiscoveryEngine.Discover(Snapshot([.. DebeziumConsumer(debeziumMarkers: false),
            ("Shop/Shop.ChangePublisher/Shop.ChangePublisher.csproj", string.Format(Worker, Pkg("Azure.Messaging.EventHubs"))),
            ("Shop/Shop.ChangePublisher/Program.cs", "var b = Host.CreateApplicationBuilder(args); await using var producer = new EventHubProducerClient(ns, \"shop-cdc.ShopDb.dbo.Orders\", credential); b.Build().Run();"),
            ("Shop/Shop.ChangePublisher/OrdersChangeCapture.cs", "public sealed class OrdersChangeCapture { }")]));
        var confirmed = Configured("shop-cdc.ShopDb.dbo.Orders", "Shop CDC Consumer", ConsumerMappingState.Confirmed, basis: first.SourceSnapshotId);
        var report = SourceIntegrationReconciler.Reconcile(Catalog(confirmed), second, first, DateTimeOffset.UtcNow);
        var match = report.Matches.Single();
        match.State.Should().Be(SourceMatchState.Supported, "the confirmation is kept");
        match.StaleEvidence.Should().BeTrue("it was confirmed against the previous snapshot");
        match.SourceChanges.Should().Contain("Capture technology: Debezium → Custom publisher");
        report.PreviousSnapshotId.Should().Be(first.SourceSnapshotId);
    }

    [Fact]
    public void AnUnresolvedConsumerIsNeverAFailure_AndAMissingChannelIsNotAbsence()
    {
        var discovery = SourceIntegrationDiscoveryEngine.Discover(Snapshot(DebeziumConsumer()));
        var unassigned = Configured("shop-cdc.ShopDb.dbo.Audit");
        var elsewhere = Configured("shop-cdc.ShopDb.dbo.Invoices");
        var report = SourceIntegrationReconciler.Reconcile(Catalog(unassigned, elsewhere), discovery, null, DateTimeOffset.UtcNow);
        report.Matches.Single(m => m.IntegrationId == unassigned.Id).State.Should().Be(SourceMatchState.Supported);
        var missing = report.Matches.Single(m => m.IntegrationId == elsewhere.Id);
        missing.State.Should().Be(SourceMatchState.NotFoundInSource);
        missing.Detail.Should().Contain("not evidence that the integration is absent");
        report.UnconfiguredCandidateIds.Should().Contain(c => c.Contains("customers"));
        JsonSerializer.Serialize(report).Should().NotContain("Failed");
    }

    [Fact]
    public void WithoutASnapshotEverythingIsNotAnalyzed()
    {
        var report = SourceIntegrationReconciler.Reconcile(Catalog(Configured("x")), null, null, DateTimeOffset.UtcNow);
        (report.SourceAnalysis, report.Discovery).Should().Be((SourceDiscoveryStatus.NotAnalyzed, null));
        report.Matches.Single().State.Should().Be(SourceMatchState.NotAnalyzed);
    }

    [Fact]
    public void ConsumerNamesMatchByWords_NeverByFuzzyGuess()
    {
        var candidate = new SourceIntegrationCandidate { ConsumerCandidate = "Shop.CdcConsumer.Worker" };
        SourceIntegrationReconciler.SameConsumer(new IntegrationConsumer { DisplayName = "Cdc Consumer" }, candidate).Should().BeTrue();
        SourceIntegrationReconciler.SameConsumer(new IntegrationConsumer { LogicalName = "shop-cdc-consumer" }, candidate).Should().BeTrue();
        SourceIntegrationReconciler.SameConsumer(new IntegrationConsumer { DisplayName = "Consumer API" }, candidate).Should().BeFalse();
    }

    [Fact]
    public void SecretsInSourceConfigurationNeverReachSignalsOrDiscovery()
    {
        const string sentinel = "SECRET_SI_4711";
        var snapshot = Snapshot([.. DebeziumConsumer(),
            ("Shop/Shop.CdcConsumer/appsettings.Development.json", $$"""{ "EventHub": { "ConnectionString": "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey={{sentinel}}" }, "ConnectionStrings": { "Db": "Server=x;Password={{sentinel}}" } }""")]);
        var discovery = SourceIntegrationDiscoveryEngine.Discover(snapshot);
        var json = JsonSerializer.Serialize(new { snapshot.IntegrationSignals, discovery });
        json.Should().NotContain(sentinel).And.NotContain("SharedAccessKey=").And.NotContain("Password=");
    }

    [Fact]
    public async Task SourceAnalysisStoresTheSignals_AndTheReportIsBoundToTheExactSnapshot()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        var (snapshot, error) = await store.AnalyzeAsync("env", "source", "fixture.zip", Zip(DebeziumConsumer()));
        error.Should().BeNull();
        snapshot!.IntegrationSignals.Should().Contain(s => s.Kind == SourceIntegrationSignalExtractor.CaptureTechnology && s.Value == "Debezium");
        snapshot.IntegrationSignals.Should().Contain(s => s.Kind == SourceIntegrationSignalExtractor.DeclaredChannel && s.Value == "connect-offsets");

        var service = new SourceIntegrationService(store, new Catalog_(Catalog(Configured("shop-cdc.ShopDb.dbo.Orders"))));
        var report = await service.ReportAsync("env");
        report.Discovery!.SourceSnapshotId.Should().Be(snapshot.Id);
        report.Discovery.SourceFingerprint.Should().Be(snapshot.Architecture!.SourceFingerprint).And.NotBeEmpty();
        report.Matches.Single().State.Should().Be(SourceMatchState.SourceSuggestion);
        report.Boundary.Should().Contain("No Event Hub, Service Bus, database or external endpoint was contacted");
        db.IqrSourceSnapshots.Count().Should().Be(1, "reading the report never writes");
    }

    private sealed class Catalog_(IntegrationCatalog catalog) : IIntegrationCatalogService
    {
        public Task<IntegrationCatalog> GetAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default) => Task.FromResult(catalog);
        public Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) => throw new NotSupportedException("discovery never writes");
        public Task<IntegrationDefinition?> UpdateAsync(string environmentId, string id, IntegrationDefinition definition, CancellationToken ct = default) => throw new NotSupportedException("discovery never writes");
        public Task<IntegrationDefinition?> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationPlatform?> UpdatePlatformAsync(string environmentId, string id, IntegrationPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ImportLegacyAsync(string environmentId, IReadOnlyList<IntegrationConfigDto> legacy, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
