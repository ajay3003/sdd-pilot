using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.Specifications.OpenSpec;
using BirkNext.Api.Tests.Services.Specifications;
using BirkNext.Specifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static BirkNext.Api.Tests.ProjectImport.ProjectImportServiceTests;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>C08 (2026-11-27): the Project Import entry point (Preview → Commit of a real ZIP) asks the router, which follows the application's
/// configured format; the same archive is processed by Spec Kit when the application was not switched.</summary>
public sealed class ProjectImportSpecificationRoutingTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ProjectImportStagingStore _staging = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pisr-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SpecificationFormatSettingsStore Settings => new(Path.Combine(_dir, "format"));
    private OpenSpecSourceModelStore Models => new(Path.Combine(_dir, "models"));

    private ProjectImportService Service()
    {
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        var store = new IqrSourceStore(_db);
        var catalogs = new SpecificationCatalogService(_db);
        var router = new SpecificationProcessingRouter(Settings, catalogs, new OpenSpecProcessingRoute(catalogs, Models));
        return new ProjectImportService(store, _staging, sources.Object, NullLogger<ProjectImportService>.Instance, catalogs: catalogs, baselines: new SourceBaselineService(_db, store),
            specificationRouter: router);
    }

    private static byte[] Archive(string root) => Zip([.. OpenSpecFixture.Documents($"{root}/").Select(d => (d.Path, d.Content)),
        ($"{root}/src/Demo.Api/Demo.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ($"{root}/src/Demo.Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build(); app.Run();")]);

    [Fact]
    public async Task An_application_configured_as_OpenSpec_is_captured_by_the_OpenSpec_route_at_import()
    {
        var service = Service();
        var preview = service.Preview("demo.zip", Archive("demo")).Preview!;
        var options = (await service.ApplicationOptionsAsync(preview.StagingId, default))!;
        Settings.Save(options.ArchiveKey, SpecificationFormat.OpenSpec, "admin", DateTimeOffset.UtcNow);
        var commit = (await service.CommitAsync(preview.StagingId))!;
        commit.Source.SnapshotId.Should().NotBeNull();
        var catalog = await new SpecificationCatalogService(_db).CurrentAsync(SpecificationCatalogService.ProjectKeyOf(preview.ImportId), default);
        catalog.Should().NotBeNull();
        SpecificationFormatMarker.IsOpenSpec(catalog!.Fingerprint).Should().BeTrue();
        catalog.Requirements.Should().HaveCount(5).And.OnlyContain(r => SpecificationFormatMarker.IsGeneratedOpenSpecKey(r.RequirementId));
        var model = Models.Find(catalog.ProjectKey, catalog.Fingerprint)!;
        model.ApplicationKey.Should().Be(options.ArchiveKey);
        model.SchemaName.Should().Be("spec-driven", "openspec/config.yaml is read from the import's configuration files, as text");
        model.ActiveChanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_same_archive_of_an_application_left_on_SpecKit_is_processed_by_Spec_Kit_unchanged()
    {
        var service = Service();
        var preview = service.Preview("demo.zip", Archive("demo")).Preview!;
        await service.CommitAsync(preview.StagingId);
        var catalog = await new SpecificationCatalogService(_db).CurrentAsync(SpecificationCatalogService.ProjectKeyOf(preview.ImportId), default);
        // Spec Kit's classifier decides what (if anything) is a specification here, exactly as before; the catalog is never OpenSpec-marked.
        if (catalog is not null) SpecificationFormatMarker.IsOpenSpec(catalog.Fingerprint).Should().BeFalse();
        var direct = SpecificationCatalogService.Read(OpenSpecFixture.Documents("demo/"));
        (catalog?.Requirements.Count ?? 0).Should().Be(direct.Entries.Count, "the Spec Kit route reads it exactly as SpecificationCatalogService.Read does");
        Directory.Exists(Path.Combine(_dir, "models")).Should().BeFalse("the OpenSpec route was not invoked");
    }
}
