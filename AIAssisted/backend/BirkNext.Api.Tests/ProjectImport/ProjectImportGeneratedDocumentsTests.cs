using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.GeneratedDocumentation;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>
/// Generated documentation arrives with the same Project Import (no separate autodoc import): the commit result lists the generated documents
/// the import's source snapshot identified — path, module, kind, fingerprint, generator — as references. The snapshot stores no document text.
/// </summary>
public sealed class ProjectImportGeneratedDocumentsTests : IDisposable
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

    private const string Skill = "---\nname: maintain-docs\ndescription: Keeps module docs current.\n---\n# Maintain docs\n\nAll output goes to `autodoc/`.\n\n### Write autodoc/architecture.md\n";

    [Fact]
    public async Task Commit_lists_the_generated_documents_of_the_import_snapshot_as_references()
    {
        var architecture = "Last updated: 2026-03-01\n\n# Architecture\n\nThe API serves HTTP.\n";
        var bytes = ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source,
            ("shop/.claude/skills/maintain-docs/SKILL.md", Skill), ("shop/src/Shop.Api/autodoc/architecture.md", architecture)]);
        var service = Service();
        var preview = service.Preview("shop.zip", bytes).Preview!;
        preview.Documents.Should().Contain(d => d.RelativePath == "shop/src/Shop.Api/autodoc/architecture.md", "the import carries the document text once");

        var commit = (await service.CommitAsync(preview.StagingId))!;

        commit.Source.SnapshotId.Should().NotBeNull();
        var generated = commit.Source.GeneratedDocuments.Should().ContainSingle().Subject;
        generated.RelativePath.Should().Be("shop/src/Shop.Api/autodoc/architecture.md");
        generated.Kind.Should().Be(GeneratedDocumentKind.Architecture);
        generated.Generator.Should().Be("maintain-docs");
        generated.ModuleRoot.Should().NotBeNullOrEmpty();
        generated.Fingerprint.Should().HaveLength(64);
        var record = await _db.IqrSourceSnapshots.SingleAsync();
        record.EvidenceJson.Should().NotContain("The API serves HTTP", "the source snapshot keeps fingerprints, never generated-document text");
    }

    [Fact]
    public async Task Commit_without_generated_documentation_reports_an_empty_list_not_an_earlier_set()
    {
        var service = Service();
        var preview = service.Preview("shop.zip", ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source])).Preview!;
        var commit = (await service.CommitAsync(preview.StagingId))!;
        commit.Source.GeneratedDocuments.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task Documents_only_import_has_no_snapshot_and_no_generated_references()
    {
        var service = Service();
        var preview = service.Preview("docs.zip", ProjectImportServiceTests.Zip(ProjectImportServiceTests.Documents)).Preview!;
        var commit = (await service.CommitAsync(preview.StagingId))!;
        commit.Source.SnapshotId.Should().BeNull();
        commit.Source.GeneratedDocuments.Should().BeNull();
    }
}
