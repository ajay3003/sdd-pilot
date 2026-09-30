using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.DatabaseArchitecture;
using BirkNext.SourceArchitecture;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.SourceArchitecture;

/// <summary>
/// Source-derived architecture over synthetic, non-M2LB fixtures (Storefront/Catalog/Orders/Billing…) read through the real archive reader.
/// Targets resolve only from explicit wiring; names never resolve anything; configuration values never leave except safe entity names.
/// </summary>
public sealed class SourceArchitectureTests
{
    private const string Web = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string Worker = """<Project Sdk="Microsoft.NET.Sdk.Worker"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string Lib = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string AppHost = """<Project Sdk="Aspire.AppHost.Sdk/9.0.0"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";

    private static string Pkg(params string[] packages) => "<ItemGroup>" + string.Concat(packages.Select(p => $"""<PackageReference Include="{p}" Version="1.0.0" />""")) + "</ItemGroup>";
    private static string Ref(params string[] paths) => "<ItemGroup>" + string.Concat(paths.Select(p => $"""<ProjectReference Include="{p}" />""")) + "</ItemGroup>";

    private static ArchitectureSnapshot Analyze(DatabaseArchitectureSnapshot? database = null, params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        var (workspace, error) = IqrSourceArchiveReader.Read("fixture.zip", buffer.ToArray());
        error.Should().BeNull();
        return SourceArchitectureAnalyzer.Analyze(Guid.NewGuid(), workspace!, DateTimeOffset.UtcNow, database);
    }

    private static ArchitectureSnapshot Analyze(params (string Path, string Content)[] files) => Analyze(null, files);

    private static ArchitectureComponent Component(ArchitectureSnapshot a, string name) => a.Components.Single(c => c.Name == name || c.LogicalName == name);
    private static ArchitectureDependency Dep(ArchitectureSnapshot a, string from, ArchitectureDependencyType type) =>
        a.Dependencies.Single(d => d.FromComponentId == Component(a, from).Id && d.DependencyType == type);

    private static readonly (string, string)[] StorefrontAndCatalog =
    [
        ("Shop/Storefront/Storefront.csproj", string.Format(Web, "")),
        ("Shop/Storefront/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddHttpClient("Catalog", c => c.BaseAddress = new Uri(builder.Configuration["CatalogApi:BaseUrl"]!));
            var app = builder.Build();
            app.MapGet("/", () => "shop");
            app.Run();
            """),
        ("Shop/Storefront/appsettings.json", """{ "CatalogApi": { "BaseUrl": "https://catalog.example.test/" } }"""),
        ("Shop/Catalog.Api/Catalog.Api.csproj", string.Format(Web, Ref("../Shared.Contracts/Shared.Contracts.csproj"))),
        ("Shop/Catalog.Api/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/products", () => Results.Ok());
            app.MapPost("/products", () => Results.Ok());
            app.Run();
            """),
        ("Shop/Shared.Contracts/Shared.Contracts.csproj", string.Format(Lib, "")),
        ("Shop/Shared.Contracts/Product.cs", "public sealed record Product(string Id);"),
        ("Shop/AppHost/AppHost.csproj", string.Format(AppHost, "")),
        ("Shop/AppHost/AppHost.cs", """
            var builder = DistributedApplication.CreateBuilder(args);
            var catalog = builder.AddProject<Projects.Catalog_Api>("catalog");
            var storefront = builder.AddProject<Projects.Storefront>("storefront")
                .WithEnvironment("CatalogApi__BaseUrl", catalog.GetEndpoint("http"))
                .WaitFor(catalog);
            builder.Build().Run();
            """),
    ];

    [Fact]
    public void FixtureA_RestCallResolvesOnlyThroughExplicitWiring_AndProjectReferencesStayBuildTime()
    {
        var a = Analyze(StorefrontAndCatalog);
        a.Components.Select(c => (c.Name, c.ComponentType)).Should().BeEquivalentTo([("Storefront", ArchitectureComponentType.Api), ("Catalog.Api", ArchitectureComponentType.Api)],
            "the AppHost is development orchestration, not a runtime component; the contracts library is not a component");
        var http = Dep(a, "Storefront", ArchitectureDependencyType.Http);
        (http.ToId, http.EvidenceState, http.ConfigurationReference).Should().Be((Component(a, "Catalog.Api").Id, ArchitectureEvidenceState.StronglySupported, "CatalogApi:BaseUrl"));
        http.Evidence.Select(e => e.Kind).Should().Contain([ArchitectureEvidenceKind.ApplicationSource, ArchitectureEvidenceKind.DevelopmentOrchestration]);
        http.Evidence.First().Should().Match<ArchitectureEvidence>(e => e.File == "Shop/Storefront/Program.cs" && e.Line == 2 && e.Extractor == "HttpClient");
        var reference = Dep(a, "Catalog.Api", ArchitectureDependencyType.ProjectReference);
        reference.ToId.Should().Be("library:Shared.Contracts");
        reference.Confidence.Should().Contain("not a runtime call");
        a.SharedLibraries.Should().Contain(l => l.Name == "Shared.Contracts" && l.Internal && l.ReferencedByComponents.Single() == Component(a, "Catalog.Api").Id);
        a.Interfaces.Single(i => i.ComponentId == Component(a, "Catalog.Api").Id && i.Type == "REST endpoints").RouteOrTopic.Should().Contain("GET /products").And.Contain("POST /products");
        a.Dependencies.Should().Contain(d => d.DependencyType == ArchitectureDependencyType.OrchestrationDependency && d.EvidenceState == ArchitectureEvidenceState.Inferred);
        a.Diagnostics.Should().Contain(d => d.Kind == "Local orchestration only");
        a.Status.Should().Be(ArchitectureStatus.Complete);
    }

    [Fact]
    public void FixtureE_BillingApiBaseUrl_IsNeverResolvedByNameSimilarity()
    {
        var a = Analyze(
            ("Billing/Billing/Billing.csproj", string.Format(Web, "")),
            ("Billing/Billing/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddHttpClient<BillingClient>(c => c.BaseAddress = new Uri(builder.Configuration["BillingApi:BaseUrl"]!));
                var app = builder.Build(); app.MapGet("/", () => 1); app.Run();
                """),
            ("Billing/BillingWorker/BillingWorker.csproj", string.Format(Worker, "")),
            ("Billing/BillingWorker/Program.cs", "var b = Host.CreateApplicationBuilder(args); b.Services.AddHostedService<Poller>(); b.Build().Run();"),
            ("Billing/BillingApi/BillingApi.csproj", string.Format(Web, "")),
            ("Billing/BillingApi/Program.cs", "var builder = WebApplication.CreateBuilder(args); var app = builder.Build(); app.MapGet(\"/invoices\", () => 1); app.Run();"));
        var http = Dep(a, "Billing", ArchitectureDependencyType.Http);
        (http.ToId, http.EvidenceState, http.TargetReference).Should().Be((null, ArchitectureEvidenceState.Unresolved, "BillingApi:BaseUrl"));
        http.Diagnostics.Single().Should().Contain("Nothing was guessed from names");
        a.Diagnostics.Should().Contain(d => d.Kind == "Unresolved target" && d.Message.Contains("BillingApi:BaseUrl"));
    }

    [Fact]
    public void FixtureB_EventHubProducerAndConsumerPairOnlyOnTheSameHubName_AndFixtureH_CheckpointIsNotBusinessStorage()
    {
        var a = Analyze(
            ("Orders/OrderPublisher/OrderPublisher.csproj", string.Format(Worker, Pkg("Azure.Messaging.EventHubs"))),
            ("Orders/OrderPublisher/Program.cs", """
                var b = Host.CreateApplicationBuilder(args);
                await using var producer = new EventHubProducerClient(ns, "orders-events", credential);
                await using var other = new EventHubProducerClient(ns, "audit-events", credential);
                b.Build().Run();
                """),
            ("Orders/OrderProjector/OrderProjector.csproj", string.Format(Worker, Pkg("Azure.Messaging.EventHubs.Processor", "Azure.Storage.Blobs"))),
            ("Orders/OrderProjector/Program.cs", """
                var b = Host.CreateApplicationBuilder(args);
                var checkpoints = new BlobContainerClient(new Uri(b.Configuration["Checkpoints:ContainerUri"]!), credential);
                var processor = new EventProcessorClient(checkpoints, "$Default", ns, "orders-events", credential);
                b.Build().Run();
                """));
        var orders = a.MessagingChannels.Single(c => c.Name == "orders-events");
        orders.Producers.Single().ComponentId.Should().Be(Component(a, "OrderPublisher").Id);
        orders.Consumers.Single().ComponentId.Should().Be(Component(a, "OrderProjector").Id);
        (orders.Type, orders.ConsumerGroup, orders.Confidence).Should().Be((MessagingChannelType.EventHub, "$Default", ArchitectureEvidenceState.Confirmed));
        a.MessagingChannels.Single(c => c.Name == "audit-events").Consumers.Should().BeEmpty("a consumer of another hub is never paired by proximity");
        a.Diagnostics.Should().Contain(d => d.Kind == "Consumer not found" && d.Message.StartsWith("audit-events"));
        var checkpoint = Dep(a, "OrderProjector", ArchitectureDependencyType.CheckpointStore);
        checkpoint.Confidence.Should().Contain("not business storage");
        a.DataStores.Single(s => s.Id == checkpoint.ToId).Usage.Should().Be("Event Hub checkpoint storage");
        a.Dependencies.Should().NotContain(d => d.DependencyType == ArchitectureDependencyType.BlobReadWrite);
    }

    [Fact]
    public void FixtureC_ServiceBusTopicSubscriptionAndQueueStayDistinct_AndSharedLibraryEvidenceIsInferred()
    {
        var a = Analyze(
            ("Invoicing/Invoicing.Messaging/Invoicing.Messaging.csproj", string.Format(Lib, Pkg("Azure.Messaging.ServiceBus"))),
            ("Invoicing/Invoicing.Messaging/Publisher.cs", """
                public static class Topics { public const string Invoices = "invoices"; }
                public sealed class Publisher(ServiceBusClient client) { public ServiceBusSender Sender => client.CreateSender(Topics.Invoices); }
                """),
            ("Invoicing/Invoicing.Api/Invoicing.Api.csproj", string.Format(Web, Ref("../Invoicing.Messaging/Invoicing.Messaging.csproj"))),
            ("Invoicing/Invoicing.Api/Program.cs", "var builder = WebApplication.CreateBuilder(args); var app = builder.Build(); app.MapPost(\"/invoices\", () => 1); app.Run();"),
            ("Invoicing/Invoicing.Batch/Invoicing.Batch.csproj", string.Format(Worker, Ref("../Invoicing.Messaging/Invoicing.Messaging.csproj"))),
            ("Invoicing/Invoicing.Batch/Program.cs", "var b = Host.CreateApplicationBuilder(args); b.Build().Run();"),
            ("Ledger/Ledger.Worker/Ledger.Worker.csproj", string.Format(Worker, Pkg("Azure.Messaging.ServiceBus"))),
            ("Ledger/Ledger.Worker/Consumer.cs", """
                public sealed class Consumer(ServiceBusClient client)
                {
                    private readonly ServiceBusProcessor _topic = client.CreateProcessor("invoices", "ledger-sub", new ServiceBusProcessorOptions());
                    private readonly ServiceBusProcessor _queue = client.CreateProcessor("audit-queue", new ServiceBusProcessorOptions());
                }
                """));
        var invoices = a.MessagingChannels.Single(c => c.Name == "invoices");
        (invoices.Type, invoices.Subscription).Should().Be((MessagingChannelType.ServiceBusTopic, "ledger-sub"));
        invoices.Consumers.Single().ComponentId.Should().Be(Component(a, "Ledger.Worker").Id);
        invoices.Producers.Select(p => p.ComponentId).Should().BeEquivalentTo([Component(a, "Invoicing.Api").Id, Component(a, "Invoicing.Batch").Id]);
        invoices.Producers.Should().OnlyContain(p => p.State == ArchitectureEvidenceState.Inferred, "the sender lives in a library both components include: which one runs it is not known");
        a.MessagingChannels.Single(c => c.Name == "audit-queue").Type.Should().Be(MessagingChannelType.ServiceBusQueue);
    }

    [Fact]
    public void Wolverine_IsFrameworkEvidenceOnTheEdge_NeverAComponent()
    {
        var a = Analyze(
            ("Sales/Sales.Api/Sales.Api.csproj", string.Format(Web, Pkg("WolverineFx.AzureServiceBus"))),
            ("Sales/Sales.Api/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Host.UseWolverine(opts => { opts.UseAzureServiceBus(cs); opts.PublishMessage<OrderPlaced>().ToAzureServiceBusTopic("sales.orders"); });
                var app = builder.Build(); app.MapPost("/orders", () => 1); app.Run();
                """),
            ("Shipping/Shipping.Worker/Shipping.Worker.csproj", string.Format(Worker, Pkg("WolverineFx.AzureServiceBus"))),
            ("Shipping/Shipping.Worker/Program.cs", """
                var b = Host.CreateApplicationBuilder(args);
                b.UseWolverine(opts => opts.ListenToAzureServiceBusSubscription("shipping").FromTopic("sales.orders"));
                b.Build().Run();
                """));
        a.Components.Should().NotContain(c => c.Name.Contains("Wolverine"));
        var channel = a.MessagingChannels.Single(c => c.Name == "sales.orders");
        (channel.Type, channel.Subscription).Should().Be((MessagingChannelType.ServiceBusTopic, "shipping"));
        channel.Producers.Single().Framework.Should().Be("Wolverine");
        channel.Consumers.Single().Framework.Should().Be("Wolverine");
        Dep(a, "Sales.Api", ArchitectureDependencyType.ServiceBusPublish).Framework.Should().Be("Wolverine");
        a.Technologies.Should().Contain(t => t.Name == "Wolverine");
    }

    [Fact]
    public void FixtureD_DbContextIsADatastoreReference_WithoutTables_AndLinksOnlyToTheSameSnapshotsDbContext()
    {
        (string, string)[] files =
        [
            ("Orders/Orders.Api/Orders.Api.csproj", string.Format(Web, Pkg("Microsoft.EntityFrameworkCore.SqlServer"))),
            ("Orders/Orders.Api/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddDbContext<OrdersDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("OrdersDb")));
                var app = builder.Build(); app.MapGet("/", () => 1); app.Run();
                """),
        ];
        var linked = Analyze(new DatabaseArchitectureSnapshot { Databases = [new DatabaseModel { Id = "db-orders", DbContext = "OrdersDbContext", LogicalName = "Orders" }, new DatabaseModel { Id = "db-other", DbContext = "OrdersDbContextV2" }] }, files);
        var store = linked.DataStores.Single();
        (store.StoreType, store.DbContext, store.ConnectionReference, store.DatabaseModelId).Should().Be((DataStoreType.SqlServer, "OrdersDbContext", "ConnectionStrings:OrdersDb", "db-orders"));
        JsonSerializer.Serialize(linked).Should().NotContain("\"Tables\"").And.NotContain("\"Columns\"", "architecture never embeds the database model");
        Analyze(new DatabaseArchitectureSnapshot { Databases = [new DatabaseModel { Id = "db-x", DbContext = "Orders" }] }, files).DataStores.Single().DatabaseModelId
            .Should().BeNull("a similar name is not the same DbContext");
        Analyze(files).DataStores.Single().DatabaseModelId.Should().BeNull("no database analysis, no link");
    }

    [Fact]
    public void FixtureF_OrchestrationAndComposeDisagree_IsAConflict_AndTheSnapshotNeedsReview()
    {
        var a = Analyze(
            ("Pay/Gateway/Gateway.csproj", string.Format(Web, "")),
            ("Pay/Gateway/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddHttpClient("Payments", c => c.BaseAddress = new Uri(builder.Configuration["Payments:BaseUrl"]!));
                var app = builder.Build(); app.MapGet("/", () => 1); app.Run();
                """),
            ("Pay/Payments.V1/Payments.V1.csproj", string.Format(Web, "")),
            ("Pay/Payments.V1/Program.cs", "var builder = WebApplication.CreateBuilder(args); var app = builder.Build(); app.MapGet(\"/pay\", () => 1); app.Run();"),
            ("Pay/Payments.V2/Payments.V2.csproj", string.Format(Web, "")),
            ("Pay/Payments.V2/Program.cs", "var builder = WebApplication.CreateBuilder(args); var app = builder.Build(); app.MapGet(\"/pay\", () => 2); app.Run();"),
            ("Pay/AppHost/AppHost.csproj", string.Format(AppHost, "")),
            ("Pay/AppHost/AppHost.cs", """
                var builder = DistributedApplication.CreateBuilder(args);
                var v1 = builder.AddProject<Projects.Payments_V1>("payments-v1");
                var gateway = builder.AddProject<Projects.Gateway>("gateway").WithEnvironment("Payments__BaseUrl", v1.GetEndpoint("http"));
                """),
            ("Pay/docker-compose.yml", """
                services:
                  gateway:
                    build:
                      context: ./Gateway
                    environment:
                      - Payments__BaseUrl=http://payments:8080
                  payments:
                    build:
                      context: ./Payments.V2
                """));
        var http = Dep(a, "Gateway", ArchitectureDependencyType.Http);
        http.EvidenceState.Should().Be(ArchitectureEvidenceState.Conflict);
        http.ToId.Should().BeNull();
        http.ConflictingTargets.Should().BeEquivalentTo([Component(a, "Payments.V1").Id, Component(a, "Payments.V2").Id]);
        http.Evidence.Select(e => e.Kind).Should().Contain([ArchitectureEvidenceKind.DevelopmentOrchestration, ArchitectureEvidenceKind.ContainerCompose]);
        a.Status.Should().Be(ArchitectureStatus.NeedsReview);
        a.Diagnostics.Should().Contain(d => d.Kind == "Conflict");
    }

    [Fact]
    public void FixtureG_GraphQlServerAndStrawberryShakeClient_ResolveThroughWiring()
    {
        var a = Analyze(
            ("Cat/Catalog.Graph/Catalog.Graph.csproj", string.Format(Web, Pkg("HotChocolate.AspNetCore"))),
            ("Cat/Catalog.Graph/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddGraphQLServer().AddQueryType<Query>();
                var app = builder.Build(); app.MapGraphQL("/graphql"); app.Run();
                """),
            ("Cat/Shop.Web/Shop.Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><ItemGroup><PackageReference Include="StrawberryShake.Blazor" Version="1" /><PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="1" /></ItemGroup></Project>"""),
            ("Cat/Shop.Web/Program.cs", """
                var builder = WebAssemblyHostBuilder.CreateDefault(args);
                var graphUrl = builder.Configuration["CatalogGraph:Url"];
                builder.Services.AddCatalogClient().ConfigureHttpClient(c => c.BaseAddress = new Uri(graphUrl!));
                builder.Services.AddMsalAuthentication(o => builder.Configuration.Bind("AzureAd", o.ProviderOptions.Authentication));
                """),
            ("Cat/AppHost/AppHost.csproj", string.Format(AppHost, "")),
            ("Cat/AppHost/AppHost.cs", """
                var builder = DistributedApplication.CreateBuilder(args);
                var graph = builder.AddProject<Projects.Catalog_Graph>("catalog-graph");
                var web = builder.AddProject<Projects.Shop_Web>("web").WithEnvironment("CatalogGraph__Url", graph.GetEndpoint("https"));
                """));
        Component(a, "Shop.Web").ComponentType.Should().Be(ArchitectureComponentType.Frontend);
        a.Interfaces.Should().Contain(i => i.ComponentId == Component(a, "Catalog.Graph").Id && i.Type == "GraphQL endpoint" && i.RouteOrTopic == "/graphql");
        var gql = Dep(a, "Shop.Web", ArchitectureDependencyType.GraphQl);
        (gql.ToId, gql.EvidenceState, gql.Framework).Should().Be((Component(a, "Catalog.Graph").Id, ArchitectureEvidenceState.StronglySupported, "Strawberry Shake"));
        var auth = Dep(a, "Shop.Web", ArchitectureDependencyType.Auth);
        (auth.ToId, auth.Protocol).Should().Be(("external:microsoft-entra-id", "sign-in"));
        a.ExternalSystems.Should().Contain(e => e.Name == "Microsoft Entra ID" && e.Type == "Identity provider");
    }

    [Fact]
    public void Secrets_NeverLeaveTheAnalyzer_OnlySafeEntityNamesAndHostsDo()
    {
        const string sentinel = "SECRET_ARCH_123";
        var a = Analyze(
            ("Sec/Sec.Api/Sec.Api.csproj", string.Format(Web, Pkg("Azure.Messaging.ServiceBus", "Azure.Security.KeyVault.Secrets"))),
            ("Sec/Sec.Api/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                var secrets = new SecretClient(new Uri(builder.Configuration["KeyVault:Uri"]!), new DefaultAzureCredential());
                builder.Services.AddHttpClient("Partner", c => c.BaseAddress = new Uri(builder.Configuration["Partner:BaseUrl"]!));
                var app = builder.Build(); app.MapGet("/", () => 1); app.Run();
                """),
            ("Sec/Sec.Api/appsettings.json", $$"""
                {
                  "AzureAd": { "ClientId": "11111111-1111-1111-1111-111111111111", "ClientSecret": "{{sentinel}}" },
                  "ConnectionStrings": { "Main": "Server=db;Database=x;User Id=a;Password={{sentinel}}" },
                  "Storage": { "Sas": "sv=2020&sig={{sentinel}}", "ContainerName": "uploads" },
                  "Partner": { "BaseUrl": "https://user:{{sentinel}}@partner.example.test/api?code={{sentinel}}" },
                  "Messaging": { "Topics": { "Orders": "orders.topic" }, "ConnectionString": "Endpoint=sb://x/;SharedAccessKey={{sentinel}}" },
                  "KeyVault": { "Uri": "https://vault.example.test/" }
                }
                """));
        var json = JsonSerializer.Serialize(a);
        json.Should().NotContain(sentinel).And.NotContain("SharedAccessKey").And.NotContain("Password=");
        a.ConfigurationReferences.Should().Contain(c => c.Key == "Messaging:Topics:Orders" && c.SafeValues.Single() == "orders.topic");
        a.ConfigurationReferences.Should().Contain(c => c.Key == "Storage:ContainerName" && c.SafeValues.Single() == "uploads");
        a.ConfigurationReferences.Single(c => c.Key == "ConnectionStrings:Main").SafeValues.Should().BeEmpty();
        a.ConfigurationReferences.Single(c => c.Key == "Partner:BaseUrl").SafeValues.Should().BeEmpty("a URL with user info is dropped entirely");
        a.ExternalSystems.Should().Contain(e => e.Name == "Azure Key Vault");
        Dep(a, "Sec.Api", ArchitectureDependencyType.KeyVault).Confidence.Should().Contain("no secret names or values");
        ArchitectureText.SafeValue("Api:BaseUrl", "https://host.example.test/v1?key=abc").Should().Be("https://host.example.test/v1", "query strings never survive");
    }

    [Fact]
    public void Snapshots_AreOwnedByTheirSourceSnapshot_AndDiffIsASourceArchitectureChange()
    {
        var before = Analyze(StorefrontAndCatalog);
        var after = Analyze([.. StorefrontAndCatalog.Where(f => !f.Item1.StartsWith("Shop/AppHost/", StringComparison.Ordinal)),
            ("Shop/Reports.Worker/Reports.Worker.csproj", string.Format(Worker, "")), ("Shop/Reports.Worker/Program.cs", "var b = Host.CreateApplicationBuilder(args); b.Build().Run();")]);
        before.SnapshotId.Should().NotBe(after.SnapshotId);
        var changes = ArchitectureDiff.Compare(before, after);
        changes.Should().Contain(c => c.Kind == ArchitectureChangeKind.Added && c.Area == "Component" && c.Name == "Reports.Worker");
        changes.Should().Contain(c => c.Kind == ArchitectureChangeKind.Removed && c.Area == "Dependency" && c.Key.Contains("OrchestrationDependency"));
        changes.Should().Contain(c => c.Area == "Dependency" && c.Name.Contains("CatalogApi:BaseUrl"), "the HTTP call lost its orchestration wiring, so it became unresolved");
        ArchitectureDiff.Label.Should().Contain("not deployment drift");
        ArchitectureDiff.Compare(after, after).Should().BeEmpty();
    }

    [Fact]
    public async Task SourceStore_AttachesAnImmutableArchitecturePerSnapshot_TiedToItsFingerprint()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in StorefrontAndCatalog)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        var (first, error) = await store.AnalyzeAsync("env", "source-analysis", "shop.zip", buffer.ToArray());
        error.Should().BeNull();
        first!.Architecture!.SourceSnapshotId.Should().Be(first.Id);
        first.Architecture.SourceFingerprint.Should().Be(first.Archive.Sha256);
        first.Architecture.ExtractorVersions.Should().ContainKeys("DotNetProject", "HttpClient", "EventHubs", "ServiceBus", "Wolverine", "DataStores");
        var (second, _) = await store.AnalyzeAsync("env", "source-analysis", "shop.zip", buffer.ToArray());
        second!.Architecture!.SnapshotId.Should().NotBe(first.Architecture.SnapshotId, "a new analysis is a new immutable architecture snapshot");
        (await store.GetAsync("env", "source-analysis", first.Id))!.Architecture!.SnapshotId.Should().Be(first.Architecture.SnapshotId);
    }

    [Fact]
    public void NoProjects_IsUnsupported()
    {
        var a = Analyze(("docs/readme.cs", "// nothing"));
        (a.Status, a.Components.Count).Should().Be((ArchitectureStatus.Unsupported, 0));
    }

    [Fact]
    public void CoreExtraction_HasNoProjectSpecificNames()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var files = Directory.GetFiles(Path.Combine(root, "BirkNext.Api", "Services", "SourceArchitecture"), "*.cs").ToList();
        files.Should().HaveCountGreaterThanOrEqualTo(3);
        var forbidden = new Regex(@"(?i)\b(m2lb|birk|personadapter|person\.api|hendelse|tjeneste|autorisasjon|revisjon|bufdir|m2lb-cdc)\b|""person\.|PersonModule");
        foreach (var file in files) forbidden.Matches(File.ReadAllText(file)).Select(m => m.Value).Should().BeEmpty($"{Path.GetFileName(file)} must stay project-agnostic");
        var shared = File.ReadAllText(Path.Combine(root, "..", "shared", "SourceArchitectureContracts.cs"));
        Regex.IsMatch(shared, @"using\s+BirkNext\.DatabaseArchitecture|(DatabaseModel|TableModel|ColumnModel|RelationshipModel|KeyModel|IndexModel)").Should().BeFalse("the architecture model does not reuse the database model");
    }
}
