using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Services.SampleProjects;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>The browser uses the backend's document-role classification as is, and classifies itself only when a preview has none for its documents.</summary>
public sealed class ProjectImportServerDiscoveryTests
{
    private static BirkNext.ProjectImport.ProjectImportPreview Preview() => ProjectImportActivationTests.Preview(new string('a', 64), false,
        ("shop/spec.md", ProjectImportActivationTests.Spec), ("shop/plan.md", ProjectImportActivationTests.Plan));

    [Fact]
    public async Task ServerDiscovery_IsUsedAsIs_WithoutClassifyingInTheBrowser()
    {
        var preview = Preview();
        // A deliberately different server answer proves the browser did not classify again: both documents as Research.
        var server = preview.Documents.Select(d => new DiscoveredDocument(d.RelativePath, d.FileName, ArtifactDiscoveryStatus.Detected, WorkspaceArtifactType.Research,
            ArtifactConfidence.Strong, ["server"], [], null, null)).ToList();
        var progress = new List<(int, int)>();

        var discovery = await ProjectImportArtifactDiscovery.FromAsync(preview with { Discovery = server }, new Progress<(int Done, int Total)>(progress.Add));

        discovery.Roles.Single(r => r.Role == WorkspaceArtifactType.Research).Documents.Should().HaveCount(2);
        discovery.Roles.Single(r => r.Role == WorkspaceArtifactType.Specification).Documents.Should().BeEmpty();
        progress.Should().BeEmpty("no browser classification ran");
    }

    [Fact]
    public async Task WithoutServerDiscovery_TheBrowserClassifies_AndTheRolesAreTheSame()
    {
        var preview = Preview();
        var direct = ArtifactDocumentDiscovery.Classify(preview.Documents.Select(d => new ArtifactDocumentDiscovery.Candidate(d.RelativePath, d.FileName, d.Content)));

        var local = await ProjectImportArtifactDiscovery.FromAsync(preview, progress: null, yieldEvery: 0);
        var fromServer = await ProjectImportArtifactDiscovery.FromAsync(preview with { Discovery = direct }, progress: null);

        fromServer.Roles.Select(r => (r.Role, r.State, string.Join(",", r.Documents.Select(d => d.RelativePath))))
            .Should().Equal(local.Roles.Select(r => (r.Role, r.State, string.Join(",", r.Documents.Select(d => d.RelativePath)))));
        local.Roles.Single(r => r.Role == WorkspaceArtifactType.Specification).State.Should().Be(SampleRoleState.Detected);
    }

    [Fact]
    public void ServerDiscovery_ForOtherDocuments_IsIgnored()
    {
        var preview = Preview();
        var mismatched = new List<DiscoveredDocument> { new("other/spec.md", "spec.md", ArtifactDiscoveryStatus.Detected, WorkspaceArtifactType.Specification, ArtifactConfidence.Strong, [], [], null, null) };

        ProjectImportArtifactDiscovery.ServerDiscovery(preview with { Discovery = mismatched }).Should().BeNull();
        ProjectImportArtifactDiscovery.ServerDiscovery(preview).Should().BeNull();
    }
}
