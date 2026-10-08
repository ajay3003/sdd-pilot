using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>
/// Document roles are classified on the backend during the preview by the browser's own classifier (compiled into both projects): the same
/// roles, the same candidates, no selection among several, and phase timings that are measured. Generic fixtures only.
/// </summary>
public sealed class ProjectImportServerDiscoveryTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private ProjectImportService Service() => new(new IqrSourceStore(_db), new ProjectImportStagingStore(), Mock.Of<IReviewSourceEvidenceProvider>(), NullLogger<ProjectImportService>.Instance);

    [Fact]
    public void Preview_ClassifiesEveryDocument_WithTheSharedClassifier()
    {
        var bytes = ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source]);

        var preview = Service().Preview("shop.zip", bytes).Preview!;

        preview.Discovery.Should().NotBeNull();
        preview.Discovery!.Select(d => d.RelativePath).Should().BeEquivalentTo(preview.Documents.Select(d => d.RelativePath));
        var direct = ArtifactDocumentDiscovery.Classify(preview.Documents.Select(d => new ArtifactDocumentDiscovery.Candidate(d.RelativePath, d.FileName, d.Content)));
        preview.Discovery.Should().BeEquivalentTo(direct, o => o.WithStrictOrdering(), "the server result is exactly what the classifier produces");
        ArtifactDocumentDiscovery.Roles(preview.Discovery, _ => null).Where(r => r.State == SampleRoleState.Detected).Select(r => r.Role)
            .Should().Contain([WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification]);
        preview.Timings.Should().NotBeNull();
        preview.Timings!.ClassificationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void SeveralCandidatesOfARole_AreAllKept_NoneIsSelected()
    {
        var spec = ProjectImportServiceTests.Documents.Single(d => d.Item1.EndsWith("spec.md")).Item2;
        var bytes = ProjectImportServiceTests.Zip(("shop/specs/001/spec.md", spec), ("shop/specs/002/spec.md", spec.Replace("Cart", "Checkout")));

        var preview = Service().Preview("shop.zip", bytes).Preview!;

        var role = ArtifactDocumentDiscovery.Roles(preview.Discovery!, _ => null).Single(r => r.Role == WorkspaceArtifactType.Specification);
        role.State.Should().Be(SampleRoleState.Multiple);
        role.Primary.Should().BeNull("nothing is picked on the user's behalf");
    }

    [Fact]
    public void TheDiscoverySurvivesTheJsonContract()
    {
        var preview = Service().Preview("shop.zip", ProjectImportServiceTests.Zip(ProjectImportServiceTests.Documents)).Preview!;
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var roundTripped = JsonSerializer.Deserialize<BirkNext.ProjectImport.ProjectImportPreview>(JsonSerializer.Serialize(preview, web), web)!;

        roundTripped.Discovery.Should().BeEquivalentTo(preview.Discovery, o => o.WithStrictOrdering());
    }
}
