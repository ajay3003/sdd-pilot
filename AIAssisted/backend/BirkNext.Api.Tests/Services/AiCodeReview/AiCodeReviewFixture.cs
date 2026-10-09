using System.IO.Compression;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.AiCodeReview;

/// <summary>
/// A generic, non-M2LB project ("Contoso Shop") in two versions: a baseline and an AI-assisted change that introduces the risk patterns the
/// profile looks for. Snapshots are produced by the real Source Analysis pipeline (archive reader → analyzers → stored snapshot).
/// </summary>
internal static class AiCodeReviewFixture
{
    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files) { using var w = new StreamWriter(archive.CreateEntry(path).Open()); w.Write(content); }
        return stream.ToArray();
    }

    private static string Csproj(params string[] packages) =>
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n  <ItemGroup>\n"
        + string.Join("\n", packages.Select(p => $"    <PackageReference Include=\"{p.Split('@')[0]}\" Version=\"{p.Split('@')[1]}\" />")) + "\n  </ItemGroup>\n</Project>";

    private const string TestProject = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n  <ItemGroup>\n"
        + "    <PackageReference Include=\"xunit\" Version=\"2.9.0\" />\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.8.0\" />\n  </ItemGroup>\n"
        + "  <ItemGroup><ProjectReference Include=\"..\\..\\src\\Shop.Api\\Shop.Api.csproj\" /></ItemGroup>\n</Project>";

    private const string OpenApiBaseline = """
        openapi: 3.0.1
        info:
          title: Shop API
          version: 1.0.0
        paths:
          /orders:
            get:
              operationId: listOrders
              responses:
                '200':
                  description: OK
            post:
              operationId: createOrder
              responses:
                '201':
                  description: Created
        """;

    private const string OpenApiCurrent = """
        openapi: 3.0.1
        info:
          title: Shop API
          version: 1.1.0
        paths:
          /orders:
            get:
              operationId: listOrders
              responses:
                '200':
                  description: OK
          /orders/export:
            get:
              operationId: exportOrders
              responses:
                '200':
                  description: OK
        """;

    private const string Models = """
        using System.ComponentModel.DataAnnotations;
        namespace Shop.Api.Models;
        public sealed class CreateOrder
        {
            [Required] public string Customer { get; set; } = "";
            [Range(1, 100)] public int Quantity { get; set; }
            public string? Note { get; set; }
        }
        public sealed class OrderDto { public int Id { get; set; } public string Customer { get; set; } = ""; public decimal Total { get; set; } }
        """;

    private const string OrdersBaseline = """
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        using Shop.Api.Models;
        namespace Shop.Api.Controllers;
        [ApiController, Authorize, Route("orders")]
        public sealed class OrdersController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
            [HttpPost] public IActionResult Create([FromBody] CreateOrder order) => Ok();
        }
        """;

    private const string ProductsBaseline = """
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        namespace Shop.Api.Controllers;
        [ApiController, Route("products")]
        public sealed class ProductsController : ControllerBase
        {
            [HttpGet, Authorize] public IActionResult Get() => Ok();
            [HttpDelete, Authorize] public IActionResult Delete(int id) => Ok();
            [HttpGet("public"), AllowAnonymous] public IActionResult Catalogue() => Ok();
        }
        """;

    private const string ProgramBaseline = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("https://shop.example").AllowAnyHeader()));
        var app = builder.Build();
        if (app.Environment.IsDevelopment()) { app.UseDeveloperExceptionPage(); }
        app.MapGet("/health", () => "ok").AllowAnonymous();
        app.Run();
        """;

    private const string TestsBaseline = """
        using Xunit;
        namespace Shop.Api.Tests;
        public sealed class OrdersTests
        {
            [Fact] public void Lists_orders() { var x = 1; Assert.Equal(1, x); }
            [Fact] public void Uses_helper() { Check(2); }
            private static void Check(int value) => Assert.True(value > 0);
        }
        """;

    private const string Appsettings = """{ "Authentication": { "Authority": "https://login.example/tenant", "Audience": "api://shop" }, "Cors": { "Origins": "https://shop.example" } }""";

    public static byte[] Baseline() => Zip(
        ("ContosoShop/Shop.sln", "Microsoft Visual Studio Solution File"),
        ("ContosoShop/src/Shop.Api/Shop.Api.csproj", Csproj("Serilog@3.1.1")),
        ("ContosoShop/src/Shop.Api/Program.cs", ProgramBaseline),
        ("ContosoShop/src/Shop.Api/appsettings.json", Appsettings),
        ("ContosoShop/src/Shop.Api/openapi.yaml", OpenApiBaseline),
        ("ContosoShop/src/Shop.Api/Models/Models.cs", Models),
        ("ContosoShop/src/Shop.Api/Controllers/OrdersController.cs", OrdersBaseline),
        ("ContosoShop/src/Shop.Api/Controllers/ProductsController.cs", ProductsBaseline),
        ("ContosoShop/tests/Shop.Api.Tests/Shop.Api.Tests.csproj", TestProject),
        ("ContosoShop/tests/Shop.Api.Tests/OrdersTests.cs", TestsBaseline));

    /// <summary>The AI-assisted change: removes authorization, adds stubs and leaks, a permissive CORS policy, an unused dependency, a contract
    /// operation removal and an authentication setting removal. Tests are not touched.</summary>
    public static byte[] Current() => Zip(
        ("ContosoShop/Shop.sln", "Microsoft Visual Studio Solution File"),
        ("ContosoShop/src/Shop.Api/Shop.Api.csproj", Csproj("Serilog@3.1.1", "Polly@8.4.0")),
        ("ContosoShop/src/Shop.Api/Program.cs", """
            using Serilog;
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowCredentials()));
            var app = builder.Build();
            app.UseDeveloperExceptionPage();
            app.MapGet("/health", () => "ok").AllowAnonymous();
            app.MapPost("/orders/import", () => "imported");
            app.Run();
            """),
        ("ContosoShop/src/Shop.Api/appsettings.json", """{ "Authentication": { "Audience": "api://shop" }, "Cors": { "Origins": "*" } }"""),
        ("ContosoShop/src/Shop.Api/openapi.yaml", OpenApiCurrent),
        ("ContosoShop/src/Shop.Api/Models/Models.cs", Models),
        ("ContosoShop/src/Shop.Api/Models/Summaries.cs", """
            namespace Shop.Api.Models.Summaries;
            public sealed class OrderSummary { public int Id { get; set; } public string Customer { get; set; } = ""; public decimal Total { get; set; } }
            public sealed class UpdateProduct { public string Name { get; set; } = ""; public decimal Price { get; set; } }
            """),
        ("ContosoShop/src/Shop.Api/Controllers/OrdersController.cs", """
            using Microsoft.AspNetCore.Mvc;
            using Shop.Api.Models;
            using Shop.Api.Payments;
            namespace Shop.Api.Controllers;
            [ApiController, Route("orders")]
            public sealed class OrdersController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
                [HttpPost] public IActionResult Create([FromBody] CreateOrder order)
                {
                    try { return Ok(); }
                    catch (Exception ex) { return BadRequest(ex.Message); }
                }
            }
            """),
        ("ContosoShop/src/Shop.Api/Controllers/ProductsController.cs", """
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Mvc;
            using Shop.Api.Models.Summaries;
            namespace Shop.Api.Controllers;
            [ApiController, Route("products")]
            public sealed class ProductsController : ControllerBase
            {
                [HttpGet, Authorize] public IActionResult Get() => Ok();
                [HttpDelete] public IActionResult Delete(int id) => Ok();
                [HttpGet("public"), AllowAnonymous] public IActionResult Catalogue() => Ok();
                [HttpPut] public IActionResult Update([FromBody] UpdateProduct product) => Ok();
            }
            """),
        ("ContosoShop/src/Shop.Api/Services/OrderService.cs", """
            using Microsoft.Extensions.Configuration;
            namespace Shop.Api.Services;
            public sealed class OrderService(IConfiguration configuration)
            {
                public decimal Discount(int customer) => throw new NotImplementedException();
                public string Status() => "TODO";
                public bool Validate(string input)
                {
                    // TODO: implement validation rules
                    return true;
                }
                public bool Submit()
                {
                    try { Send(); }
                    catch (Exception) { return true; }
                    return true;
                }
                public void Cleanup()
                {
                    try { Send(); }
                    catch { }
                    try { Send(); }
                    catch (IOException) { /* best effort: the file may already be gone */ }
                }
                public string Key() => configuration["Payments:ApiKey"] ?? "";
                public string Legacy() => throw new NotSupportedException("Legacy export is not supported.");
                private void Send() { }
                private int Unused(int value) => value * 2;
                public OperationResult Safe() { try { Send(); return new(true, ""); } catch (Exception ex) { return new(false, ex.Message); } }
            }
            public sealed record OperationResult(bool Ok, string Error);
            public interface IOrderPort { void Publish(int id); Task<int> CountAsync(); }
            """),
        ("ContosoShop/tests/Shop.Api.Tests/Shop.Api.Tests.csproj", TestProject),
        ("ContosoShop/tests/Shop.Api.Tests/OrdersTests.cs", TestsBaseline + """

            public sealed class MoreTests
            {
                [Fact] public void Nothing_checked() { var service = new object(); service.ToString(); }
                [Fact] public void Empty() { }
                [Fact(Skip = "flaky")] public void Skipped() { Assert.True(true); }
            }
            """));

    public static async Task<IqrSourceSnapshot> AnalyzeAsync(AppDbContext db, string name, byte[] bytes)
    {
        var (snapshot, error) = await new IqrSourceStore(db).AnalyzeAsync("env", IqrSourceStore.SourceAnalysisOwner, name, bytes);
        return snapshot ?? throw new InvalidOperationException(error);
    }

    public static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
