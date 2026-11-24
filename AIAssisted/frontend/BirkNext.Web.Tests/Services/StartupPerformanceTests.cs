using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Browser start-up and navigation cost (measured 2026-11-24 on the real M2LB workspace: 429 revisions, 1,752 scenarios). Each test pins one
/// confirmed bottleneck fix and that the result is unchanged: the document identity is not rehashed when the stored fingerprint is that identity;
/// and the review context is not rebuilt when nothing it reads changed.
/// </summary>
public sealed class StartupPerformanceTests
{
    private static string SpecOf(string fr) =>
        $"# Feature Specification: Intake\n\n## User Scenarios & Testing\n\n### User Story 1 - Intake (Priority: P1)\n\n**Acceptance Scenarios**:\n\n1. **Given** a user, **When** they register, **Then** it works.\n\n## Requirements\n\n### Functional Requirements\n\n- **{fr}**: System MUST support intake.\n";

    // ---- Document identity -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("# Spec\n\nBody\n")]
    [InlineData("")]
    public void The_stored_fingerprint_is_the_document_identity_of_text_without_carriage_returns_or_byte_order_mark(string content)
    {
        var revision = new SddArtifactRevision { Content = content, Fingerprint = ArtifactFingerprint.Compute(content) };

        ArtifactExplorerContext.DocumentIdentityOf(revision).Should().Be(MarkdownTokenizer.DocumentFingerprint(content));
    }

    [Theory]
    [InlineData("# Spec\r\n\r\nBody\r\n")]
    [InlineData("\uFEFF# Spec\n\nBody\n")]
    [InlineData("# Spec\rBody")]
    public void Text_whose_canonical_form_differs_is_hashed_as_before(string content)
    {
        var revision = new SddArtifactRevision { Content = content, Fingerprint = ArtifactFingerprint.Compute(content) };

        ArtifactExplorerContext.DocumentIdentityOf(revision).Should().Be(MarkdownTokenizer.DocumentFingerprint(content))
            .And.NotBe(revision.Fingerprint, "the raw fingerprint of this text is not its canonical identity");
    }

    [Fact]
    public void A_revision_without_a_stored_fingerprint_is_hashed()
    {
        var revision = new SddArtifactRevision { Content = "# Spec\n", Fingerprint = "" };

        ArtifactExplorerContext.DocumentIdentityOf(revision).Should().Be(MarkdownTokenizer.DocumentFingerprint("# Spec\n"));
    }

    [Fact]
    public async Task Explorer_document_identity_is_unchanged_for_imported_documents()
    {
        var repository = new WorkspaceArtifactRepository();
        var samples = new MockSampleProjectDocumentResolver();
        var explorers = new ArtifactExplorerContext(repository, samples, samples);
        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, SpecOf("FR-001"), "spec.md", "File"));

        var state = await explorers.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Selected!.DocumentIdentity.Should().Be(MarkdownTokenizer.DocumentFingerprint(SpecOf("FR-001")));
    }

    // ---- Review context rebuild ----------------------------------------------------------------------------------------------------------

    private static (WorkspaceArtifactRepository Repository, ArtifactExplorerContext Explorers, ReviewContextProvider Provider) Workspace()
    {
        var repository = new WorkspaceArtifactRepository();
        var samples = new MockSampleProjectDocumentResolver();
        var explorers = new ArtifactExplorerContext(repository, samples, samples);
        var provider = new ReviewContextProvider(repository, new WorkspaceUpdateCoordinator(), new ConstitutionAnalysisService(), new PlanAnalysisService(),
            new DataModelAnalysisService(), NullLogger<ReviewContextProvider>.Instance, explorers);
        return (repository, explorers, provider);
    }

    [Fact]
    public async Task A_rebuild_with_unchanged_inputs_keeps_the_current_context_and_announces_nothing()
    {
        var (_, explorers, provider) = Workspace();
        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, SpecOf("FR-001"), "spec.md", "File"));
        await provider.RebuildAsync();
        var built = provider.GetCurrent();
        var announced = 0;
        provider.ReviewContextChanged += (_, _) => announced++;

        await provider.RebuildAsync();
        await provider.RebuildAsync();

        provider.GetCurrent().Should().BeSameAs(built, "nothing the context is built from changed");
        announced.Should().Be(0);
        built!.GetRequirements().Select(r => r.Id).Should().Equal("FR-001");
    }

    [Fact]
    public async Task A_changed_document_rebuilds_the_context()
    {
        var (_, explorers, provider) = Workspace();
        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, SpecOf("FR-001"), "spec.md", "File"));
        await provider.RebuildAsync();
        var built = provider.GetCurrent();

        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, SpecOf("FR-002"), "spec.md", "File"));
        await provider.RebuildAsync();

        provider.GetCurrent().Should().NotBeSameAs(built);
        provider.GetCurrent()!.GetRequirements().Select(r => r.Id).Should().Equal("FR-002");
    }

    [Fact]
    public async Task A_document_of_another_role_or_a_new_candidate_rebuilds_the_context()
    {
        var (_, explorers, provider) = Workspace();
        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, SpecOf("FR-001"), "spec.md", "File"));
        await provider.RebuildAsync();
        var first = provider.GetCurrent();

        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Tasks, "# Tasks: Intake\n\n## Phase 1: Setup\n\n- [ ] T001 Create intake\n", "tasks.md", "File"));
        await provider.RebuildAsync();
        var second = provider.GetCurrent();
        second.Should().NotBeSameAs(first);
        second!.GetTasks().Should().ContainSingle();

        // A second specification makes the role ambiguous: the context follows (no requirement is guessed).
        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, SpecOf("FR-009"), "other-spec.md", "File"));
        await provider.RebuildAsync();
        provider.GetCurrent().Should().NotBeSameAs(second);
    }

    [Fact]
    public async Task The_input_signature_changes_with_authority_and_with_loaded_bodies()
    {
        var (repository, explorers, provider) = Workspace();
        explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, SpecOf("FR-001"), "spec.md", "File"));
        var roles = await Roles(explorers);
        var before = provider.InputSignature(roles);

        var revision = repository.SddLifecycle.Revisions.Single();
        revision.Authority = "Baseline";
        provider.InputSignature(await Roles(explorers)).Should().NotBe(before);

        revision.Authority = "Unknown";
        provider.InputSignature(await Roles(explorers)).Should().Be(before);
        revision.Content = "";
        provider.InputSignature(await Roles(explorers)).Should().NotBe(before, "a body that is not loaded is not the same input");
    }

    private static async Task<IReadOnlyList<ArtifactExplorerState>> Roles(ArtifactExplorerContext explorers)
    {
        var roles = new List<ArtifactExplorerState>();
        foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles) roles.Add(await explorers.GetStateAsync(role));
        return roles;
    }
}
