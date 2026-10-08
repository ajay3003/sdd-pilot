using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>The shared discovery reuses a classification only for identical inputs: never across a path the classifier reads differently.</summary>
public sealed class ArtifactDocumentDiscoveryTests
{
    [Fact]
    public async Task ReusedClassifications_AreIdenticalToClassifyingEachDocument()
    {
        var spec = ProjectImportActivationTests.Spec;
        var candidates = new[]
        {
            new ArtifactDocumentDiscovery.Candidate("a/spec.md", "spec.md", spec),
            new ArtifactDocumentDiscovery.Candidate("b/spec.md", "spec.md", spec.Replace("\n", "\r\n")),
            new ArtifactDocumentDiscovery.Candidate("c/specs/spec.md", "spec.md", spec),
            new ArtifactDocumentDiscovery.Candidate("d/checklists/spec.md", "spec.md", spec),
            new ArtifactDocumentDiscovery.Candidate("e/notes.md", "notes.md", spec),
            new ArtifactDocumentDiscovery.Candidate("f/plan.md", "plan.md", ProjectImportActivationTests.Plan),
        };
        var progress = new List<(int, int)>();

        var documents = await ArtifactDocumentDiscovery.ClassifyAsync(candidates, new SyncProgress(progress.Add), yieldEvery: 2);

        foreach (var document in documents)
        {
            var single = SampleArtifactClassifier.Classify(document.RelativePath, candidates.Single(c => c.RelativePath == document.RelativePath).Text);
            (document.Status, document.Role, document.Confidence).Should().Be((single.Status, single.Role, single.Confidence), document.RelativePath);
            document.Reasons.Should().Equal(single.Reasons, document.RelativePath);
        }
        documents.Single(d => d.RelativePath == "d/checklists/spec.md").Status.Should().Be(ArtifactDiscoveryStatus.Unclassified, "the checklist folder changes the result");
        progress.Should().NotBeEmpty().And.EndWith((6, 6));
    }

    private sealed class SyncProgress(Action<(int, int)> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => report(value);
    }
}
