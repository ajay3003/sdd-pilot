using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.GeneratedDocumentation;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Generic generated-documentation fixtures: a small .NET "shop" with an orders module, authored Spec-Kit artifacts, a generated-docs folder
/// and a generic agent workflow that declares it. No real project names. Entry times are set explicitly: uniform by default (like a
/// downloaded archive), varied when a test needs trustworthy archive times.
/// </summary>
internal static class GeneratedDocumentationFixture
{
    public static readonly DateTimeOffset Uniform = new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    public const string Workflow = """
        ---
        name: doc-writer
        description: Keeps module documentation current.
        ---
        # Doc writer

        All output goes to `generated-docs/`.

        ### Write generated-docs/README.md
        ### Write generated-docs/architecture.md
        ### Write generated-docs/openapi.yaml
        ### Write generated-docs/data-model.md
        ### Write generated-docs/message-bus.md
        ### Write generated-docs/schema.graphql
        """;

    public const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";

    public static string Program(params string[] routes) =>
        "var builder = WebApplication.CreateBuilder(args); var app = builder.Build();\n" + string.Concat(routes.Select(r => $"app.MapGet(\"{r}\", () => 1);\n")) + "app.Run();\n";

    public static string OpenApi(params string[] paths) =>
        "openapi: 3.0.3\ninfo:\n  title: Orders\n  version: 1.0.0\n  x-last-updated: \"2026-03-01\"\npaths:\n" + string.Concat(paths.Select(p => $"  {p}:\n    get:\n      responses:\n        '200':\n          description: ok\n"));

    public const string Readme = "Last updated: 2026-03-01\n\n# Shop.Orders\n\nOrders service. Technology: ASP.NET Core, SQL Server, Entity Framework Core.\n\n| Key | Description |\n|---|---|\n| `Orders:PageSize` | Page size |\n";
    public const string Architecture = "Last updated: 2026-03-01\n\n# Architecture\n\nThe `Shop.Orders.Api` project serves HTTP. Storage: SQL Server.\n";

    public static string DataModel(string date, params string[] entities) =>
        $"Last updated: {date}\n\n# Data Model\n\n## Entities\n\n" + string.Concat(entities.Select(e => $"### Entity: `{e}`\n\n| Column | Type |\n|---|---|\n| Id | uniqueidentifier |\n\n"));

    public static string DbContext(params string[] entities) =>
        "using Microsoft.EntityFrameworkCore;\nnamespace Shop.Orders.Api;\npublic sealed class OrdersDbContext : DbContext\n{\n" +
        string.Concat(entities.Select(e => $"    public DbSet<{e}> {e}s => Set<{e}>();\n")) + "}\n" + string.Concat(entities.Select(e => $"public sealed class {e} {{ public Guid Id {{ get; set; }} }}\n"));

    public const string Migration = "using Microsoft.EntityFrameworkCore.Migrations;\nnamespace Shop.Orders.Api.Migrations;\npublic partial class AddOrders : Migration { protected override void Up(MigrationBuilder m) { } }\n";

    public static List<(string Path, string Content)> Orders(string generatedDate = "2026-03-01", string migrationId = "20260201090000", bool workflow = true,
        string[]? documentedRoutes = null, string[]? sourceRoutes = null, string[]? documentedEntities = null)
    {
        var files = new List<(string, string)>
        {
            ("shop/orders/src/Shop.Orders.Api/Shop.Orders.Api.csproj", Csproj),
            ("shop/orders/src/Shop.Orders.Api/Program.cs", Program(sourceRoutes ?? ["/orders"])),
            ("shop/orders/src/Shop.Orders.Api/OrdersDbContext.cs", DbContext("Order")),
            ($"shop/orders/src/Shop.Orders.Api/Migrations/{migrationId}_AddOrders.cs", Migration),
            ("shop/orders/src/Shop.Orders.Api/appsettings.json", "{ \"Orders\": { \"PageSize\": 20 } }"),
            ("shop/orders/generated-docs/README.md", Readme.Replace("2026-03-01", generatedDate)),
            ("shop/orders/generated-docs/architecture.md", Architecture.Replace("2026-03-01", generatedDate)),
            ("shop/orders/generated-docs/openapi.yaml", OpenApi(documentedRoutes ?? ["/orders"]).Replace("2026-03-01", generatedDate)),
            ("shop/orders/generated-docs/data-model.md", DataModel(generatedDate, documentedEntities ?? ["Order"])),
            ("shop/orders/.specify/memory/constitution.md", "# Shop Constitution\n\n## Principles\n\nServices MUST NOT use MongoDB.\n"),
            ("shop/orders/specs/001-orders/spec.md", "# Feature Specification: Orders\n\n## Requirements\n\n- **FR-001**: System MUST expose GET /orders.\n\n### Key Entities\n\n- **Order**: a placed order.\n"),
            ("shop/orders/specs/001-orders/plan.md", "# Implementation Plan: Orders\n\n## Technical Context\n\n**Language/Version**: C# 12\n**Storage**: SQL Server\n"),
            ("shop/orders/specs/001-orders/tasks.md", "# Tasks: Orders\n\n- [X] T001 Create project\n- [ ] T002 Add GET /orders endpoint\n"),
        };
        if (workflow) files.Add((".claude/skills/doc-writer/SKILL.md", Workflow));
        return files;
    }

    public static byte[] Zip(IEnumerable<(string Path, string Content)> files, Func<string, DateTimeOffset>? time = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path);
                entry.LastWriteTime = time?.Invoke(path) ?? Uniform;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return stream.ToArray();
    }

    public static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public static async Task<IqrSourceSnapshot> AnalyzeAsync(AppDbContext db, IEnumerable<(string Path, string Content)> files, Func<string, DateTimeOffset>? time = null, string? importId = null)
    {
        var bytes = Zip(files, time);
        var store = new IqrSourceStore(db);
        var read = IqrSourceArchiveReader.ReadDetailed("shop.zip", bytes);
        if (!read.IsValid) throw new InvalidOperationException(read.Failure!.Message);
        var provenance = importId is null ? null : new ProjectImportProvenance { ImportId = importId, ArchiveFileName = "shop.zip", ArchiveSha256 = read.Workspace!.Archive.Sha256, ImportedAt = DateTimeOffset.UtcNow };
        return await store.AnalyzeValidatedAsync(null, IqrSourceStore.SourceAnalysisOwner, "shop.zip", bytes, read.Workspace!, default, provenance);
    }

    public static AuthoredArtifactInput Authored(string role, string path, string content) =>
        new(role, "artifact:" + path, Path.GetFileName(path), path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(), content);

    public static GeneratedDocumentationModule Module(IqrSourceSnapshot snapshot, string root) => snapshot.GeneratedDocumentation!.Modules.Single(m => m.RootPath == root);

    public static GeneratedDocumentationEvidence Doc(IqrSourceSnapshot snapshot, string path) => snapshot.GeneratedDocumentation!.Documents.Single(d => d.SafeRelativePath == path);
}
