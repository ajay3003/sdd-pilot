using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Import outcome semantics: separate sections, never one score; partial is not failed; an absent side is neutral.</summary>
public sealed class ProjectImportPresentationTests
{
    private static readonly (string, string)[] Docs = [("spec.md", ProjectImportActivationTests.Spec), ("plan.md", ProjectImportActivationTests.Plan)];

    private static (ProjectImportPreview Preview, ProjectImportArtifactDiscovery Discovery) Make(bool source, params (string, string)[] docs)
    {
        var preview = ProjectImportActivationTests.Preview(new string('a', 64), source, docs);
        return (preview, ProjectImportArtifactDiscovery.From(preview));
    }

    [Fact]
    public void DocumentsOnly_IsImported_WithoutANote()
    {
        var (preview, discovery) = Make(false, Docs);

        ProjectImportPresentation.Outcome(preview, discovery, new ProjectImportSourceResult { State = ProjectImportSourceState.NotDetected })
            .Should().Be(ProjectImportOutcome.Imported);
        ProjectImportPresentation.SourceResult(new ProjectImportSourceResult { State = ProjectImportSourceState.NotDetected }).Tone.Should().Be(ProjectImportTone.Neutral);
    }

    [Fact]
    public void SourceOnly_IsImported()
    {
        var (preview, discovery) = Make(true);

        ProjectImportPresentation.Outcome(preview, discovery, new ProjectImportSourceResult { State = ProjectImportSourceState.Created, SnapshotStatus = SourceAnalysisStatus.Ready })
            .Should().Be(ProjectImportOutcome.Imported);
        ProjectImportPresentation.Roles(discovery).Should().OnlyContain(r => r.Tone == ProjectImportTone.Neutral && r.Status == "Not found");
    }

    [Fact]
    public void NeitherDocumentsNorSource_IsNothingToImport_NotRejected()
    {
        var (preview, discovery) = Make(false, ("README.md", "# Readme"));

        ProjectImportPresentation.HasImportableContent(preview, discovery).Should().BeFalse();
        ProjectImportPresentation.Outcome(preview, discovery, null).Should().Be(ProjectImportOutcome.NothingToImport);
        ProjectImportPresentation.OutcomeTone(ProjectImportOutcome.NothingToImport).Should().Be(ProjectImportTone.Neutral);
    }

    [Theory]
    [InlineData(ProjectImportSourceState.NotCreated, SourceAnalysisStatus.Ready)]
    [InlineData(ProjectImportSourceState.Failed, SourceAnalysisStatus.Ready)]
    [InlineData(ProjectImportSourceState.Created, SourceAnalysisStatus.Partial)]
    [InlineData(ProjectImportSourceState.Created, SourceAnalysisStatus.Failed)]
    public void PartialSource_IsImportedWithNotes_NeverFailedAndNeverComplete(ProjectImportSourceState state, SourceAnalysisStatus status)
    {
        var (preview, discovery) = Make(true, Docs);
        var source = new ProjectImportSourceResult { State = state, SnapshotStatus = status, Message = "Retry available." };

        ProjectImportPresentation.Outcome(preview, discovery, source).Should().Be(ProjectImportOutcome.ImportedWithNotes);
        ProjectImportPresentation.SourceResult(source).Tone.Should().Be(ProjectImportTone.Partial);
        ProjectImportPresentation.OutcomeTone(ProjectImportOutcome.ImportedWithNotes).Should().NotBe(ProjectImportTone.Rejected);
    }

    [Fact]
    public void UnsupportedSourceFiles_AreANote()
    {
        var (preview, discovery) = Make(true, Docs);
        preview = preview with { Source = preview.Source with { UnsupportedSourceFiles = 4 } };

        ProjectImportPresentation.Notes(preview, discovery, new ProjectImportSourceResult { State = ProjectImportSourceState.Created, SnapshotStatus = SourceAnalysisStatus.Partial })
            .Should().Contain(n => n.Contains("4 source files"));
    }

    [Fact]
    public void SourcePreview_NeedsNoTargetEnvironment_AndTheTargetIsNeutralRuntimeContext()
    {
        var detected = new ProjectImportSourceDetection { Detected = true, SourceFiles = 3 };

        ProjectImportPresentation.SourcePreview(detected).Tone.Should().Be(ProjectImportTone.Available);
        ProjectImportPresentation.SourcePreview(detected).Status.Should().NotContain("Target");
        ProjectImportPresentation.SourcePreview(new ProjectImportSourceDetection()).Tone.Should().Be(ProjectImportTone.Neutral);
        ProjectImportPresentation.TargetEnvironment(configured: false).Should().Match<ProjectImportSourceRow>(r =>
            r.Tone == ProjectImportTone.Neutral && r.Status == "Not configured" && r.Detail.Contains("Required only for runtime reviews"));
        ProjectImportPresentation.TargetEnvironment(configured: true).Tone.Should().Be(ProjectImportTone.Neutral);
    }

    [Fact]
    public void AmbiguousResearch_IsKept_WithoutPointingToAnExplorerThatDoesNotExist()
    {
        var (preview, discovery) = Make(false,
            ("a/research.md", "# Research: Cart\n\n## Decision\n\n## Alternatives considered\n\n## Rationale\n"),
            ("b/research.md", "# Research: Checkout\n\n## Decision\n\n## Alternatives considered\n\n## Rationale\n"));

        ProjectImportPresentation.Notes(preview, discovery, null).Should()
            .Contain(n => n.StartsWith("Research: 2 documents were detected and kept")).And.NotContain(n => n.Contains("Research Explorer"));
        ProjectImportPresentation.Roles(discovery).Single(r => r.Role == BirkNext.Web.Services.WorkspaceArtifactType.Research).Detail
            .Should().Be("Imported without a selection");
    }
}
