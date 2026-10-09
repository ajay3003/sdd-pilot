using BirkNext.Web.Components.Explorers;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class ArtifactExplorerDocumentIdentityTests : BunitContext
{
    [Fact]
    public void Loaded_artifact_exposes_the_same_canonical_fingerprint_used_by_role_parsers()
    {
        const string markdown = "# Requirements\r\n- REQ-1: Sign in\r\n";
        var role = WorkspaceArtifactType.Specification;
        var artifact = new ExplorerArtifact("workspace:spec.md", role, "Specification", "spec.md", null,
            ExplorerArtifactSource.Workspace, null, 1, 1, "Unknown", ExplorerArtifactCurrentness.Current, null);
        var state = new ArtifactExplorerState(role, ExplorerArtifactStatus.Loaded, null, null, [artifact], artifact,
            ExplorerSelectionReason.OnlyArtifact, markdown);
        Services.AddSingleton<IArtifactExplorerContext>(new StubExplorerContext(state));

        var cut = Render<ArtifactExplorerHost>(parameters => parameters.Add(component => component.Role, role));

        cut.Find("[data-testid='artifact-explorer']").GetAttribute("data-birknext-document-id")
            .Should().Be(MarkdownTokenizer.DocumentFingerprint(markdown));
        cut.Find("[data-testid='artifact-explorer']").GetAttribute("data-role").Should().Be("Specification");
    }

    private sealed class StubExplorerContext(ArtifactExplorerState state) : IArtifactExplorerContext
    {
        public event EventHandler? Changed;
        public string? CurrentScope => null;
        public Task<ArtifactExplorerState> GetStateAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default) => Task.FromResult(state);
        public void Select(WorkspaceArtifactType role, string artifactId) => Changed?.Invoke(this, EventArgs.Empty);
        public ArtifactImportResult Import(ArtifactImportRequest request) => new("workspace:imported.md", null, null);
    }
}
