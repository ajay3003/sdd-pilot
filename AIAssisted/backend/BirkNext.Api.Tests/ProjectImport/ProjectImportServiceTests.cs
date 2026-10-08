using System.IO.Compression;
using System.Security.Cryptography;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>
/// Project Import: one archive validated and staged once; documents reported for the artifact repository, source turned into a Source Analysis
/// snapshot with shared provenance. No project-specific names: the fixtures are generic Spec-Kit-like and .NET-like trees.
/// </summary>
public sealed class ProjectImportServiceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ProjectImportStagingStore _staging = new();

    public void Dispose() => _db.Dispose();

    private ProjectImportService Service(bool sourceAnalysisEnabled = true, AppDbContext? db = null)
    {
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(sourceAnalysisEnabled);
        return new ProjectImportService(new IqrSourceStore(db ?? _db), _staging, sources.Object, NullLogger<ProjectImportService>.Instance);
    }

    internal static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        return stream.ToArray();
    }

    internal static readonly (string, string)[] Documents =
    [
        ("shop/.specify/memory/constitution.md", "# Shop Constitution\n\n## Core Principles\n\n### I. Test first\nEvery change MUST have tests.\n"),
        ("shop/specs/001-cart/spec.md", "# Feature Specification: Cart\n\n## User Scenarios & Testing\n\n### User Story 1\n\n## Requirements\n\n- **FR-001**: System MUST store carts.\n"),
        ("shop/specs/001-cart/plan.md", "# Implementation Plan: Cart\n\n## Technical Context\n\n**Language/Version**: C# 12\n"),
        ("shop/specs/001-cart/tasks.md", "# Tasks: Cart\n\n- [ ] T001 Create project\n- [ ] T002 [US1] Add cart entity\n"),
        ("shop/specs/001-cart/data-model.md", "# Data Model: Cart\n\n## Entities\n\n### Cart\n\n| Field | Type |\n|---|---|\n| Id | Guid |\n"),
    ];

    internal static readonly (string, string)[] Source =
    [
        ("shop/src/Shop.Api/Shop.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ("shop/src/Shop.Api/Program.cs", "var builder = WebApplication.CreateBuilder(args); var app = builder.Build(); app.MapGet(\"/carts\", () => 1); app.Run();"),
        ("shop/infra/main.tf", "resource \"azurerm_resource_group\" \"rg\" { name = \"rg-shop\" location = \"westeurope\" }"),
    ];

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task MixedArchive_PreviewsBoth_AndCommitCreatesASnapshotWithTheSameImportProvenance()
    {
        var bytes = Zip([.. Documents, .. Source]);
        var service = Service();

        var preview = service.Preview("shop.zip", bytes).Preview!;

        preview.Documents.Select(d => d.RelativePath).Should().BeEquivalentTo(Documents.Select(d => d.Item1));
        preview.Documents.Should().OnlyContain(d => d.Content.Length > 0);
        preview.Source.Detected.Should().BeTrue();
        preview.Archive.Sha256.Should().Be(Sha(bytes), "one archive identity: the SHA-256 of the exact uploaded bytes");
        preview.ImportId.Should().Be(ProjectImportService.ImportIdFor(Sha(bytes)));
        preview.ProjectName.Should().Be("shop");
        preview.ProjectNameBasis.Should().Be(ProjectNameBasis.ArchiveRoot);
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(0, "a preview never persists anything");

        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.State.Should().Be(ProjectImportSourceState.Created);
        commit.Provenance.ImportId.Should().Be(preview.ImportId);
        commit.Provenance.ArchiveSha256.Should().Be(Sha(bytes));
        var stored = await new IqrSourceStore(_db).FindSourceAnalysisAsync(commit.Source.SnapshotId!.Value);
        stored!.ProjectImport!.ImportId.Should().Be(preview.ImportId, "Source Analysis shows which import created the snapshot");
        stored.Archive.Sha256.Should().Be(Sha(bytes), "artifacts and source come from the same accepted bytes");
        stored.IntegrationId.Should().Be(IqrSourceStore.SourceAnalysisOwner, "it is an ordinary Source Analysis snapshot every review can read");
        _staging.Find(preview.StagingId).Should().BeNull("a settled import releases its staged archive");
    }

    [Fact]
    public async Task DocumentsOnlyArchive_IsImportable_WithNoSourceAndNoSnapshot()
    {
        var service = Service();
        var preview = service.Preview("docs.zip", Zip(Documents)).Preview!;

        preview.Source.Detected.Should().BeFalse();
        preview.Documents.Should().HaveCount(5);
        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.State.Should().Be(ProjectImportSourceState.NotDetected, "no source is a neutral outcome, not a failure");
        commit.Source.CanRetry.Should().BeFalse();
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SourceOnlyArchive_CreatesTheSnapshot_WithNoDocuments()
    {
        var service = Service();
        var preview = service.Preview("api.zip", Zip(Source)).Preview!;

        preview.Documents.Should().BeEmpty();
        preview.Source.Detected.Should().BeTrue();
        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.State.Should().Be(ProjectImportSourceState.Created);
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UnsupportedSourceTechnology_IsReportedAsNotAnalyzed_NeverAsComplete()
    {
        var service = Service();
        var preview = service.Preview("legacy.zip", Zip([.. Documents, ("shop/app/src/Main.java", "class Main {}"), ("shop/app/pom.xml", "<project/>")])).Preview!;

        preview.Source.Detected.Should().BeTrue("Java is source even though Source Analysis does not analyze it");
        preview.Source.UnsupportedSourceFiles.Should().BeGreaterThan(0);
        preview.Source.Technologies.Should().Contain(t => t.TechnologyId == "lang.java" && !t.SourceAnalysisSupported);
        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.State.Should().Be(ProjectImportSourceState.Created);
        commit.Source.SnapshotStatus.Should().NotBe(BirkNext.Integrations.SourceAnalysisStatus.Ready, "unsupported technology is never a fake complete");
    }

    [Theory]
    [InlineData("../escape.md", "ARCHIVE_PATH_TRAVERSAL")]
    [InlineData("/etc/spec.md", "ARCHIVE_ABSOLUTE_PATH")]
    [InlineData("C:/Windows/spec.md", "ARCHIVE_ABSOLUTE_PATH")]
    [InlineData("//server/share/spec.md", "ARCHIVE_ABSOLUTE_PATH")]
    public void UnsafeArchive_IsRejectedBeforeStaging(string entry, string code)
    {
        var result = Service().Preview("unsafe.zip", Zip([.. Documents, (entry, "# Feature Specification: X")]));

        result.Preview.Should().BeNull();
        result.Failure!.Code.Should().Be(code);
        _staging.Count.Should().Be(0, "nothing is staged or activated for a rejected archive");
    }

    [Fact]
    public void ArchiveSizeLimit_IsTheSourceAnalysisLimit()
    {
        var result = Service().Preview("big.zip", new byte[IqrSourceArchiveReader.MaxArchiveBytes + 1]);

        result.Failure!.Code.Should().Be("ARCHIVE_TOO_LARGE");
        _staging.Count.Should().Be(0);
    }

    [Fact]
    public void DocumentCapture_UsesTheSameValidationPass_AndLeavesSourceAnalysisOutputUnchanged()
    {
        var bytes = Zip([.. Documents, .. Source, ("shop/node_modules/pkg/README.md", "# dependency readme"), ("shop/docs/huge.md", new string('x', 1_100_000))]);

        var plain = IqrSourceArchiveReader.ReadDetailed("shop.zip", bytes).Workspace!;
        var withDocuments = IqrSourceArchiveReader.ReadDetailed("shop.zip", bytes, captureDocuments: true).Workspace!;

        plain.DocumentFiles.Should().BeNull("Source Analysis alone never captures documents");
        withDocuments.Archive.Should().Be(plain.Archive);
        withDocuments.Files.Select(f => f.Path).Should().Equal(plain.Files.Select(f => f.Path));
        withDocuments.AllPaths.Should().Equal(plain.AllPaths);
        withDocuments.Limitations.Should().BeEquivalentTo(plain.Limitations);
        withDocuments.DocumentFiles!.Select(d => d.Path).Should().BeEquivalentTo(Documents.Select(d => d.Item1), "dependency folders are skipped like Sample Project discovery");
        withDocuments.SkippedDocuments.Should().ContainSingle(s => s.Path == "shop/docs/huge.md" && s.Reason == "TooLarge");
    }

    [Fact]
    public async Task WithoutTargetEnvironment_TheSourceSnapshotIsCreated_AndNothingStaysStaged()
    {
        var service = Service();
        var preview = service.Preview("shop.zip", Zip([.. Documents, .. Source])).Preview!;

        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.State.Should().Be(ProjectImportSourceState.Created, "a source snapshot needs no Target Environment");
        commit.Source.Code.Should().BeNull();
        commit.Source.CanRetry.Should().BeFalse("no retry is needed merely because no target is selected");
        commit.StagedUntil.Should().BeNull();
        _staging.Find(preview.StagingId).Should().BeNull("a settled import releases its staged archive");
        var record = await _db.IqrSourceSnapshots.SingleAsync();
        record.Id.Should().Be(commit.Source.SnapshotId!.Value);
        record.EnvironmentId.Should().Be(IqrSourceStore.NoEnvironment, "the snapshot is not bound to any target");
        (await new IqrSourceStore(_db).FindSourceAnalysisAsync(record.Id))!.ProjectImport!.ImportId.Should().Be(commit.Provenance.ImportId);
    }

    [Fact]
    public async Task SnapshotSaveFailure_IsAnExplicitRecoverableSourceFailure_NotASuccess()
    {
        await using var failing = new FailingDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = Service(db: failing);
        var preview = service.Preview("shop.zip", Zip([.. Documents, .. Source])).Preview!;

        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.State.Should().Be(ProjectImportSourceState.Failed);
        commit.Source.Code.Should().Be("SOURCE_SNAPSHOT_SAVE_FAILED");
        commit.Source.SnapshotId.Should().BeNull();
        commit.Source.CanRetry.Should().BeTrue();
        _staging.Find(preview.StagingId).Should().NotBeNull();
    }

    [Fact]
    public async Task SameArchiveTwice_ReusesTheCurrentSnapshot_InsteadOfDuplicatingIt()
    {
        var bytes = Zip([.. Documents, .. Source]);
        var service = Service();
        var first = (await service.CommitAsync(service.Preview("shop.zip", bytes).Preview!.StagingId))!;

        var again = (await service.CommitAsync(service.Preview("shop (2).zip", bytes).Preview!.StagingId))!;

        again.Source.State.Should().Be(ProjectImportSourceState.Reused);
        again.Source.SnapshotId.Should().Be(first.Source.SnapshotId);
        again.Provenance.ImportId.Should().Be(first.Provenance.ImportId, "the same bytes are the same import identity, whatever the file name");
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task NewVersion_CreatesANewCurrentSnapshot_AndKeepsTheOldOneAsHistory()
    {
        var service = Service();
        // Built once: ZipArchive stamps entry times, so zipping the same files again a few seconds later gives different bytes.
        var v1Bytes = Zip([.. Documents, .. Source]);
        var v1 = (await service.CommitAsync(service.Preview("shop-v1.zip", v1Bytes).Preview!.StagingId))!;
        var v2Source = Source.Select(s => s.Item1.EndsWith("Program.cs") ? (s.Item1, s.Item2 + " // v2") : s).ToArray();
        var v2 = (await service.CommitAsync(service.Preview("shop-v2.zip", Zip([.. Documents, .. v2Source])).Preview!.StagingId))!;

        v2.Provenance.ImportId.Should().NotBe(v1.Provenance.ImportId);
        v2.Source.State.Should().Be(ProjectImportSourceState.Created);
        var snapshots = await new IqrSourceStore(_db).ListSourceAnalysisAsync();
        snapshots.Should().HaveCount(2);
        snapshots[0].ProjectImport!.ImportId.Should().Be(v2.Provenance.ImportId, "the newest snapshot is current");
        snapshots[1].ProjectImport!.ImportId.Should().Be(v1.Provenance.ImportId, "earlier snapshots stay immutable history");

        // Importing v1 again after v2: v1 is not current, so a new snapshot is created rather than silently reviving the old one.
        var v1Again = (await service.CommitAsync(service.Preview("shop-v1.zip", v1Bytes).Preview!.StagingId))!;
        v1Again.Source.State.Should().Be(ProjectImportSourceState.Created);
        (await new IqrSourceStore(_db).ListSourceAnalysisAsync())[0].ProjectImport!.ImportId.Should().Be(v1.Provenance.ImportId);
    }

    [Fact]
    public void ConcurrentUploadsOfTheSameArchive_GetSeparateStagings()
    {
        var bytes = Zip([.. Documents, .. Source]);
        var service = Service();

        var a = service.Preview("shop.zip", bytes).Preview!;
        var b = service.Preview("shop.zip", bytes).Preview!;

        a.StagingId.Should().NotBe(b.StagingId);
        a.ImportId.Should().Be(b.ImportId);
        service.Discard(a.StagingId);
        _staging.Find(b.StagingId).Should().NotBeNull("discarding one upload never touches another");
    }

    [Fact]
    public void Staging_IsBoundedAndExpires()
    {
        var clock = new ManualClock();
        var store = new ProjectImportStagingStore(clock);
        var workspace = IqrSourceArchiveReader.ReadDetailed("a.zip", Zip(Source)).Workspace!;
        var ids = Enumerable.Range(0, ProjectImportStagingStore.MaxStaged + 1)
            .Select(_ => { clock.Advance(TimeSpan.FromSeconds(1)); return store.Add("import-x", "a.zip", [], workspace, new()).StagingId; }).ToList();

        store.Count.Should().Be(ProjectImportStagingStore.MaxStaged);
        store.Find(ids[0]).Should().BeNull("the oldest staging is evicted first");
        clock.Advance(ProjectImportStagingStore.TimeToLive);
        store.Find(ids[^1]).Should().BeNull("staged archives expire");
        store.Count.Should().Be(0);
    }

    [Fact]
    public async Task SourceAnalysisTurnedOff_ImportsDocumentsAndSaysSo()
    {
        var service = Service(sourceAnalysisEnabled: false);
        var preview = service.Preview("shop.zip", Zip([.. Documents, .. Source])).Preview!;

        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.State.Should().Be(ProjectImportSourceState.NotCreated);
        commit.Source.Code.Should().Be("SOURCE_ANALYSIS_DISABLED");
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("M2LB (2).zip", "M2LB")]
    [InlineData("payments-service.zip", "payments-service")]
    public void ProjectName_ComesFromTheArchiveFileName_WhenThereAreSeveralRoots(string fileName, string expected)
    {
        var workspace = IqrSourceArchiveReader.ReadDetailed(fileName, Zip(("a/spec.md", "# x"), ("b/Program.cs", "class P {}")), captureDocuments: true).Workspace!;

        var (name, basis) = ProjectImportService.ProjectName(fileName, workspace);

        name.Should().Be(expected);
        basis.Should().Be(ProjectNameBasis.ArchiveFileName, "several top-level roots are valid; no single root is required");
    }

    [Fact]
    public async Task UnknownOrExpiredStaging_ReturnsNull()
    {
        (await Service().CommitAsync(Guid.NewGuid())).Should().BeNull();
    }

    private sealed class FailingDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new DbUpdateException("simulated save failure");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
