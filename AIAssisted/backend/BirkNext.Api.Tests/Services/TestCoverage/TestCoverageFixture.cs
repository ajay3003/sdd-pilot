using System.IO.Compression;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using BirkNext.TestCoverage;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.TestCoverage;

/// <summary>
/// A generic, non-M2LB project ("Northwind Orders"): an Event Hub adapter calling an Orders API over explicitly wired HTTP, the API storing
/// orders in SQL Server and publishing to a Service Bus topic consumed by a notifier. Developer tests cover the duplicate rule as a unit test
/// (mocked repository), an in-process API test (422), an in-memory repository test, a mocked Service Bus publish and an adapter mapping test.
/// Snapshots are produced by the real Source Analysis pipeline.
/// </summary>
internal static class TestCoverageFixture
{
    private const string Web = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string Worker = """<Project Sdk="Microsoft.NET.Sdk.Worker"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string AppHost = """<Project Sdk="Aspire.AppHost.Sdk/9.0.0"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private static string Pkg(params string[] packages) => "<ItemGroup>" + string.Concat(packages.Select(p => $"""<PackageReference Include="{p}" Version="1.0.0" />""")) + "</ItemGroup>";
    private static string TestProject(string reference, params string[] packages) =>
        $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{Pkg(["xunit", "Microsoft.NET.Test.Sdk", "Moq", .. packages])}<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup></Project>""";

    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files) { using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); w.Write(content); }
        return buffer.ToArray();
    }

    private static (string, string)[] Production(bool changed) =>
    [
        ("Orders/AppHost/AppHost.csproj", string.Format(AppHost, "")),
        ("Orders/AppHost/AppHost.cs", """
            var builder = DistributedApplication.CreateBuilder(args);
            var api = builder.AddProject<Projects.Orders_Api>("orders-api");
            var adapter = builder.AddProject<Projects.Orders_Adapter>("orders-adapter")
                .WithEnvironment("OrdersApi__BaseUrl", api.GetEndpoint("http"))
                .WaitFor(api);
            builder.Build().Run();
            """),
        ("Orders/Orders.Adapter/Orders.Adapter.csproj", string.Format(Worker, Pkg("Azure.Messaging.EventHubs.Processor", "Azure.Storage.Blobs"))),
        ("Orders/Orders.Adapter/Program.cs", """
            var b = Host.CreateApplicationBuilder(args);
            b.Services.AddHttpClient<OrdersApiClient>(c => c.BaseAddress = new Uri(b.Configuration["OrdersApi:BaseUrl"]!));
            var checkpoints = new BlobContainerClient(new Uri(b.Configuration["Checkpoints:ContainerUri"]!), credential);
            var processor = new EventProcessorClient(checkpoints, "$Default", ns, "orders-inbound", credential);
            b.Build().Run();
            """),
        ("Orders/Orders.Adapter/OrdersApiClient.cs", """
            public sealed class OrdersApiClient(HttpClient http) { public Task<HttpResponseMessage> CreateAsync(OrderRequest r) => http.PostAsJsonAsync("/orders", r); }
            public sealed record OrderRequest(string OrderId, decimal Amount);
            """),
        ("Orders/Orders.Adapter/OrderMessageMapper.cs", """
            public sealed class OrderMessageMapper { public OrderRequest Map(InboundOrder m) => new(m.Id, m.Total); }
            public sealed record InboundOrder(string Id, decimal Total);
            """),
        ("Orders/Orders.Api/Orders.Api.csproj", string.Format(Web, Pkg("Microsoft.EntityFrameworkCore.SqlServer", "Azure.Messaging.ServiceBus"))),
        ("Orders/Orders.Api/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddDbContext<OrdersDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Orders")));
            var app = builder.Build();
            app.MapPost("/orders", (OrderService s) => s.CreateAsync());
            app.Run();
            """),
        ("Orders/Orders.Api/OrderEvents.cs", """
            public sealed class OrderEvents(ServiceBusClient client) { public ServiceBusSender Sender => client.CreateSender("order-events"); }
            """),
        ("Orders/Orders.Api/OrdersDbContext.cs", """
            public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> o) : DbContext(o) { public DbSet<Order> Orders => Set<Order>(); }
            public sealed class Order { public string Id { get; set; } = ""; }
            """),
        ("Orders/Orders.Api/OrderService.cs", changed
            ? """
              public interface IOrderRepository { Task<bool> ExistsAsync(string id); Task AddAsync(Order o); }
              public sealed class DuplicateOrderException : Exception { }
              public sealed class OrderService(IOrderRepository repository)
              {
                  public async Task CreateAsync(string id)
                  {
                      if (await repository.ExistsAsync(id.Trim().ToUpperInvariant())) throw new DuplicateOrderException();
                      await repository.AddAsync(new Order { Id = id });
                  }
              }
              """
            : """
              public interface IOrderRepository { Task<bool> ExistsAsync(string id); Task AddAsync(Order o); }
              public sealed class DuplicateOrderException : Exception { }
              public sealed class OrderService(IOrderRepository repository)
              {
                  public async Task CreateAsync(string id)
                  {
                      if (await repository.ExistsAsync(id)) throw new DuplicateOrderException();
                      await repository.AddAsync(new Order { Id = id });
                  }
              }
              """),
        ("Orders/Orders.Notifier/Orders.Notifier.csproj", string.Format(Worker, Pkg("Azure.Messaging.ServiceBus"))),
        ("Orders/Orders.Notifier/Program.cs", """
            var b = Host.CreateApplicationBuilder(args);
            var client = new ServiceBusClient(b.Configuration["ServiceBus:Namespace"], credential);
            var processor = client.CreateProcessor("order-events", "notifier");
            b.Build().Run();
            """),
    ];

    private static readonly (string, string)[] Tests =
    [
        ("Orders/tests/Orders.Api.Tests/Orders.Api.Tests.csproj", TestProject("../../Orders.Api/Orders.Api.csproj", "Microsoft.AspNetCore.Mvc.Testing", "Microsoft.EntityFrameworkCore.InMemory")),
        ("Orders/tests/Orders.Api.Tests/OrderServiceTests.cs", """
            namespace Orders.Api.Tests;
            public sealed class OrderServiceTests
            {
                [Fact]
                [Trait("Requirement", "FR-002")]
                public async Task Create_DuplicateOrder_ThrowsDuplicateOrderException()
                {
                    var repository = new Mock<IOrderRepository>();
                    repository.Setup(r => r.ExistsAsync("A1")).ReturnsAsync(true);
                    var service = new OrderService(repository.Object);
                    await Assert.ThrowsAsync<DuplicateOrderException>(() => service.CreateAsync("A1"));
                }

                [Fact(Skip = "flaky on CI")]
                public void Create_LargeOrder_IsAccepted() { Assert.True(true); }

                [Fact]
                public void Create_Order_DoesSomething() { var service = new OrderService(new Mock<IOrderRepository>().Object); }
            }
            """),
        ("Orders/tests/Orders.Api.Tests/OrdersApiTests.cs", """
            namespace Orders.Api.Tests;
            public sealed class OrdersApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
            {
                [Fact]
                [Trait("Requirement", "FR-002")]
                public async Task Post_DuplicateOrder_Returns422()
                {
                    var client = factory.CreateClient();
                    await client.PostAsJsonAsync("/orders", new { id = "A1" });
                    var response = await client.PostAsJsonAsync("/orders", new { id = "A1" });
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                }
            }
            """),
        ("Orders/tests/Orders.Api.Tests/OrderRepositoryTests.cs", """
            namespace Orders.Api.Tests;
            public sealed class OrderRepositoryTests
            {
                [Fact]
                public async Task Save_DuplicateOrder_StoresOneRecord()
                {
                    var db = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>().UseInMemoryDatabase("orders").Options);
                    db.Orders.Add(new Order { Id = "A1" });
                    await db.SaveChangesAsync();
                    Assert.Single(db.Orders);
                }
            }
            """),
        ("Orders/tests/Orders.Api.Tests/OrderEventsTests.cs", """
            namespace Orders.Api.Tests;
            public sealed class OrderEventsTests
            {
                [Fact]
                public async Task Publish_OrderCreated_SendsMessage()
                {
                    var sender = new Mock<ServiceBusSender>();
                    await new OrderPublisher(sender.Object).PublishAsync("A1");
                    sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default), Times.Once);
                }
            }
            """),
        ("Orders/tests/Orders.Adapter.Tests/Orders.Adapter.Tests.csproj", TestProject("../../Orders.Adapter/Orders.Adapter.csproj")),
        ("Orders/tests/Orders.Adapter.Tests/OrderMessageMapperTests.cs", """
            namespace Orders.Adapter.Tests;
            public sealed class OrderMessageMapperTests
            {
                [Fact]
                public void Map_InboundOrder_MapsIdAndAmount()
                {
                    var result = new OrderMessageMapper().Map(new InboundOrder("A1", 10m));
                    Assert.Equal("A1", result.OrderId);
                }
            }
            """),
        ("Orders/tests/Orders.Adapter.Tests/OrdersApiClientTests.cs", """
            namespace Orders.Adapter.Tests;
            public sealed class OrdersApiClientTests
            {
                [Fact]
                public async Task Create_PostsOrder()
                {
                    var handler = new Mock<HttpMessageHandler>();
                    var client = new OrdersApiClient(new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost") });
                    await client.CreateAsync(new OrderRequest("A1", 1m));
                    handler.Verify();
                }
            }
            """),
        ("Orders/tests/Orders.Legacy.IntegrationTests/Orders.Legacy.IntegrationTests.csproj", TestProject("../../Orders.Api/Orders.Api.csproj")),
        ("Orders/tests/Orders.Legacy.IntegrationTests/LegacyTests.cs", """
            namespace Orders.Legacy.IntegrationTests;
            public sealed class LegacyTests { [Fact] public void Legacy_Orders_Work() { Assert.True(Environment.Is64BitProcess); } }
            """),
        ("web/src/app.spec.ts", "test('x', () => {});"),
        ("tools/test_scripts.py", "def test_x(): pass"),
    ];

    public static byte[] Baseline() => Zip([.. Production(changed: false), .. Tests]);
    public static byte[] Current() => Zip([.. Production(changed: true), .. Tests]);
    public static byte[] CurrentWith(params (string Path, string Content)[] extra) => Zip([.. Production(changed: true), .. Tests, .. extra]);

    public static async Task<IqrSourceSnapshot> AnalyzeAsync(AppDbContext db, byte[] bytes, string name = "northwind-orders.zip")
    {
        var (snapshot, error) = await new IqrSourceStore(db).AnalyzeAsync("env", IqrSourceStore.SourceAnalysisOwner, name, bytes);
        return snapshot ?? throw new InvalidOperationException(error);
    }

    public static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public static CoverageWorkspaceEvidence Workspace(params CoverageExecution[] executions) => new()
    {
        ProjectName = "Northwind Orders", SpecificationAvailable = true,
        Requirements = [new("FR-002", "Duplicate orders must be rejected", null), new("FR-009", "Customers receive an order confirmation", null)],
        AcceptanceScenarios =
        [
            new() { Id = "AS-1", Title = "Duplicate order is rejected by the API", Source = "Specification acceptance scenario", RequirementIds = ["FR-002"],
                When = "POST /orders is sent twice with the same order id", Then = "the API returns 422 for the duplicate order" },
            new() { Id = "AS-2", Title = "Duplicate order event through the inbound flow", Source = "Specification acceptance scenario", RequirementIds = ["FR-002"],
                When = "the same order event arrives twice on the event hub", Then = "only one order is stored and the duplicate is rejected" },
            new() { Id = "AS-3", Title = "Order history page lists orders", Source = "Specification acceptance scenario", RequirementIds = ["FR-002"],
                When = "the customer opens the history page", Then = "previous purchases are shown" },
            new() { Id = "AS-4", Title = "Customer confirmation email", Source = "Specification acceptance scenario", RequirementIds = ["FR-009"],
                When = "an order is accepted", Then = "a confirmation email is sent" },
        ],
        Executions = [.. executions],
    };
}
