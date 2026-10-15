using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static BirkNext.Api.Tests.ProjectImport.ProjectImportServiceTests;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>
/// "Source detected" means Source Analysis can consume the archive, and a successful import leaves a snapshot that every consumer
/// (Source Analysis, Schema Studio) resolves through the same snapshot id and import identity.
/// </summary>
public sealed class ProjectImportSourceHandoffTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ProjectImportStagingStore _staging = new();

    public void Dispose() => _db.Dispose();

    private ProjectImportService Service()
    {
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        return new ProjectImportService(new IqrSourceStore(_db), _staging, sources.Object, NullLogger<ProjectImportService>.Instance);
    }

    private const string Xsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:shop"><xs:element name="Order" type="xs:string"/></xs:schema>""";

    [Fact]
    public async Task NestedSource_AndTwoCandidateSpecifications_AreBothDetected_AndTheSnapshotIsCreated()
    {
        var bytes = Zip(
            ("shop/specs/001-cart/spec.md", Documents[1].Item2),
            ("shop/specs/002-pay/spec.md", Documents[1].Item2.Replace("Cart", "Pay")),
            ("shop/backend/services/Shop.Api/Shop.Api.csproj", Source[0].Item2),
            ("shop/backend/services/Shop.Api/Program.cs", Source[1].Item2));
        var service = Service();

        var preview = service.Preview("shop.zip", bytes).Preview!;
        preview.Source.Detected.Should().BeTrue("source in a nested folder is still source");
        preview.Documents.Select(d => d.RelativePath).Should().Contain(["shop/specs/001-cart/spec.md", "shop/specs/002-pay/spec.md"]);

        var commit = (await service.CommitAsync(preview.StagingId))!;
        commit.Source.State.Should().Be(ProjectImportSourceState.Created);
        (await new IqrSourceStore(_db).FindSourceAnalysisAsync(commit.Source.SnapshotId!.Value)).Should().NotBeNull();
    }

    [Fact]
    public async Task NoSupportedContent_IsNoSource_AndCreatesNoSnapshot()
    {
        var service = Service();
        var preview = service.Preview("pictures.zip", Zip(("pictures/logo.png", "not really a png"))).Preview!;

        preview.Source.Detected.Should().BeFalse();
        preview.Documents.Should().BeEmpty();
        var commit = (await service.CommitAsync(preview.StagingId))!;
        commit.Source.State.Should().Be(ProjectImportSourceState.NotDetected);
        commit.Source.SnapshotId.Should().BeNull();
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SnapshotCreatedByImport_IsResolvableBySchemaStudio_ThroughTheSameSnapshotIdentity()
    {
        var bytes = Zip([.. Documents, .. Source, ("shop/contracts/order.xsd", Xsd)]);
        var service = Service();
        var preview = service.Preview("shop.zip", bytes).Preview!;

        var commit = (await service.CommitAsync(preview.StagingId))!;

        var snapshotId = commit.Source.SnapshotId!.Value;
        var store = new IqrSourceStore(_db);
        var artifact = await store.FindSchemaArtifactAsync(snapshotId);
        artifact.Should().NotBeNull("the XSD is retained with the snapshot the import created");
        artifact!.ProjectImportId.Should().Be(preview.ImportId);
        artifact.SnapshotFingerprint.Should().Be(preview.Archive.Sha256);
        artifact.PackageJson.Should().Contain("shop/contracts/order.xsd");
        var (index, _) = await store.ListSchemaSnapshotIndexAsync();
        index.Should().ContainSingle(i => i.Id == snapshotId && i.HasSchemaArtifact && i.ProjectImportId == preview.ImportId);
    }
}
