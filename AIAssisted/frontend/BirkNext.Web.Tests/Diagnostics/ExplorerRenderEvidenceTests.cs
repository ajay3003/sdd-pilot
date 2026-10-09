using BirkNext.MarkdownDiagnostics;
using FluentAssertions;

namespace BirkNext.Web.Tests.Diagnostics;

public sealed class ExplorerRenderEvidenceTests
{
    [Fact]
    public void Compare_uses_projection_identity_and_reports_missing_duplicate_and_unexpected_ids()
    {
        var result = ProjectionRenderVerification.Compare("Specification", "spec-1", "spec-1", "/specification-explorer",
            ["expected-one", "expected-two"], ["expected-one", "expected-one", "unrelated"]);

        result.ExecutionStatus.Should().Be(ProjectionRenderExecutionStatus.Completed);
        result.FoundProjectionIds.Should().ContainSingle("expected-one");
        result.MissingProjectionIds.Should().ContainSingle("expected-two");
        result.DuplicateProjectionIds.Should().ContainSingle("expected-one");
        result.UnexpectedProjectionIds.Should().ContainSingle("unrelated");
    }

    [Fact]
    public void Compare_never_turns_document_mismatch_into_projection_missing()
    {
        var result = ProjectionRenderVerification.Compare("Specification", "doc-a", "doc-b", "/specification-explorer",
            ["expected-projection"], []);

        result.ExecutionStatus.Should().Be(ProjectionRenderExecutionStatus.Unavailable);
        result.UnavailableReason.Should().Be("DocumentMismatch");
        result.ExpectedDocumentId.Should().Be("doc-a");
        result.ActualDocumentId.Should().Be("doc-b");
        result.MissingProjectionIds.Should().BeEmpty();
    }

    [Fact]
    public void Apply_marks_structured_source_blocks_only_when_every_projection_is_found()
    {
        var structured = Block("s-1", CoverageClassification.RepresentedStructurally, ["p-1", "p-2"]);
        var direct = Block("s-2", CoverageClassification.RepresentedDirectly, []);
        var document = Document([structured, direct]);
        var evidence = ProjectionRenderVerification.Compare("Plan", "plan-1", "plan-1", "/plan-explorer", ["p-1", "p-2"], ["p-1"]);

        var actual = ExplorerCoverageRenderEvidence.Apply(document, evidence);

        actual.Blocks[0].RenderEvidence.Should().Be(CoverageEvidenceStatus.Absent);
        actual.Blocks[0].RootCause.Should().Be("RenderProjectionMissing");
        actual.Blocks[1].RenderEvidence.Should().Be(CoverageEvidenceStatus.NotVerified, "Direct blocks have no block-level projection identity here");
        actual.Status.Should().Be(DiagnosticStatus.Fail);
    }

    [Fact]
    public void Apply_marks_structured_source_block_render_verified_when_all_projection_ids_are_found()
    {
        var document = Document([Block("s-1", CoverageClassification.RepresentedStructurally, ["p-1", "p-2"])]);
        var evidence = ProjectionRenderVerification.Compare("Plan", "plan-1", "plan-1", "/plan-explorer", ["p-1", "p-2"], ["p-2", "p-1"]);

        var actual = ExplorerCoverageRenderEvidence.Apply(document, evidence);

        actual.Blocks[0].RenderEvidence.Should().Be(CoverageEvidenceStatus.Present);
        actual.Blocks[0].RootCause.Should().BeNull();
        actual.Status.Should().Be(DiagnosticStatus.Partial, "render evidence must not change source accounting status");
    }

    [Fact]
    public void Apply_keeps_render_not_verified_when_browser_execution_is_unavailable()
    {
        var document = Document([Block("s-1", CoverageClassification.RepresentedStructurally, ["p-1"])]);
        var evidence = ProjectionRenderVerification.Unavailable("Plan", "plan-1", "/plan-explorer", ["p-1"], "Browser launch failed.");

        var actual = ExplorerCoverageRenderEvidence.Apply(document, evidence);

        actual.Blocks[0].RenderEvidence.Should().Be(CoverageEvidenceStatus.NotVerified);
        actual.Blocks[0].RootCause.Should().Be("RenderedComponentEvidenceNotExercised");
        actual.RenderVerifications.Should().ContainSingle().Which.UnavailableReason.Should().Be("Browser launch failed.");
    }

    [Fact]
    public void Apply_keeps_render_unverified_when_evidence_is_for_a_different_document()
    {
        var document = Document([Block("s-1", CoverageClassification.RepresentedStructurally, ["p-1"])]);
        var evidence = ProjectionRenderVerification.Compare("Plan", "other-doc", "other-doc", "/plan-explorer", ["p-1"], ["p-1"]);

        var actual = ExplorerCoverageRenderEvidence.Apply(document, evidence);

        actual.Blocks[0].RenderEvidence.Should().Be(CoverageEvidenceStatus.NotVerified);
        actual.Blocks[0].RootCause.Should().Be("RenderedComponentEvidenceNotExercised");
        actual.RenderVerifications.Should().ContainSingle().Which.UnavailableReason.Should().Be("ExpectedDocumentIdentityMismatch");
    }

    [Fact]
    public void Apply_treats_duplicate_expected_render_identity_as_render_integrity_failure()
    {
        var document = Document([Block("s-1", CoverageClassification.RepresentedStructurally, ["p-1"])]);
        var evidence = ProjectionRenderVerification.Compare("Plan", "plan-1", "plan-1", "/plan-explorer", ["p-1"], ["p-1", "p-1"]);

        var actual = ExplorerCoverageRenderEvidence.Apply(document, evidence);

        actual.Blocks[0].RenderEvidence.Should().Be(CoverageEvidenceStatus.Present);
        actual.Blocks[0].RootCause.Should().Be("DuplicateRenderProjection");
        actual.Status.Should().Be(DiagnosticStatus.Fail);
    }

    private static ExplorerCoverageBlock Block(string id, CoverageClassification classification, IReadOnlyList<string> projectionIds) =>
        new(id, 1, 1, "Paragraph", "fingerprint", "preview", classification, null, "reason",
            RenderEvidence: CoverageEvidenceStatus.NotVerified,
            RootCause: "RenderedComponentEvidenceNotExercised", ProjectionIds: projectionIds);

    private static ExplorerCoverageDocument Document(IReadOnlyList<ExplorerCoverageBlock> blocks) =>
        new("Plan", "plan.md", blocks.Count, blocks.Count(b => b.Classification == CoverageClassification.RepresentedDirectly),
            blocks.Count(b => b.Classification == CoverageClassification.RepresentedStructurally), 0, 0, 0,
            DiagnosticStatus.Partial, blocks, ExpectedDocumentId: "plan-1");
}
