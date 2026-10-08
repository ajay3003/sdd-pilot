using BirkNext.Api.Services;
using BirkNext.MarkdownDiagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Tests.ProjectImport;

public sealed class MarkdownDiagnosticsServiceTests
{
    [Fact]
    public void ContentIntegrity_ComparesArchiveAndStoredBytes_AndConfirmsParserEof()
    {
        var run = Service().RunContentIntegrity();
        run.Documents.Should().HaveCount(5);
        run.Documents.Should().OnlyContain(d => d.ExactMatch && d.CanonicalMatch && d.ParserReachedEof && !d.PotentialTruncation && d.ArchiveSha256 == d.StoredSha256);
        run.Documents.Should().OnlyContain(d => d.StartMarkerPreserved && d.MiddleMarkerPreserved && d.EndMarkerPreserved);
        run.Documents.Should().OnlyContain(d => d.ArchiveChars > 3_000, "fixtures exercise large documents, not just smoke samples");
        run.Documents.Select(d => d.ArtifactRole).Should().Contain(["Specification", "Constitution", "Plan", "Tasks", "Data Model"]);
    }

    [Fact]
    public void CanonicalFingerprint_NormalizesLineEndingsAndBomWithoutChangingRawComparison()
    {
        var lf = "# Header\nbody\n";
        var crlfWithBom = "\uFEFF# Header\r\nbody\r\n";
        MarkdownDiagnosticsService.CanonicalFingerprint(lf).Should().Be(MarkdownDiagnosticsService.CanonicalFingerprint(crlfWithBom));
        System.Text.Encoding.UTF8.GetBytes(lf).Should().NotEqual(System.Text.Encoding.UTF8.GetBytes(crlfWithBom));
    }

    [Fact]
    public void SourceBlockDomIdentity_IsStableAndRepeatedTextRemainsDistinctByLine()
    {
        const string markdown = "# Title\nrepeated prose\nrepeated prose\n";
        var document = BirkNext.Web.Services.MarkdownTokenizer.DocumentFingerprint(markdown);
        var first = BirkNext.Web.Services.MarkdownTokenizer.CreateSourceBlockId(document, 1, "repeated prose");
        var second = BirkNext.Web.Services.MarkdownTokenizer.CreateSourceBlockId(document, 2, "repeated prose");

        first.Should().NotBe(second);
        var notes = BirkNext.Web.Services.MarkdownTokenizer.FindUnrepresentedBlocks(markdown, new { }, preserveFreeTextForRender: true);
        first.Should().Be(notes.Single(note => note.StartLine == 2).BlockId);
    }

    [Fact]
    public void UnclosedFence_IsAnExplicitParserWarningAndLaterTextIsStillTokenized()
    {
        const string markdown = "# Stress\n```text\n## inside code\n<!-- BIRKNEXT-INTEGRITY-END -->";
        MarkdownDiagnosticsService.ParserWarnings(markdown).Should().ContainSingle().Which.Should().Contain("Unclosed fenced code block");
        var tokens = BirkNext.Web.Services.MarkdownTokenizer.Tokenize(markdown);
        tokens.Last().RawLine.Should().Contain("INTEGRITY-END");
        tokens.Last().Kind.Should().Be(BirkNext.Web.Models.MarkdownTokenKind.FencedCodeLine);
    }

    [Fact]
    public void ExplorerCoverage_AccountsForBlocksAndPreservesUnprojectedTextInSourceNotes()
    {
        var run = Service().RunExplorerCoverage();
        run.Documents.Should().HaveCount(5);
        foreach (var document in run.Documents)
        {
            document.SourceBlockCount.Should().Be(document.RepresentedDirectlyCount + document.RepresentedStructurallyCount +
                document.IntentionallyIgnoredCount + document.UnsupportedCount + document.MissingCount);
            document.Blocks.Should().OnlyContain(b => Enum.IsDefined(b.Classification));
        }
        run.Documents.SelectMany(d => d.Blocks).Should().Contain(b => b.Classification == CoverageClassification.RepresentedDirectly &&
            b.Destination == "Explorer source notes (full source text)",
            "unprojected authored text must remain accessible through the Explorer's full-text source notes");
        run.Documents.SelectMany(d => d.Blocks).Should().Contain(b => b.Classification == CoverageClassification.IntentionallyIgnored &&
            b.RuleId == "markdown.decorative-separator" && !string.IsNullOrWhiteSpace(b.Reason));
        run.Documents.SelectMany(d => d.Blocks).Should().Contain(b => b.StartLine > 1 && b.Preview.Contains("EOF", StringComparison.Ordinal));
        run.Documents.Should().OnlyContain(d => d.MissingCount == 0,
            "generated role fixtures should have every supported block represented; details: {0}",
            string.Join(" | ", run.Documents.Select(d => $"{d.ArtifactRole}: {d.MissingCount} missing ({string.Join(", ", d.Blocks.Where(b => b.Classification == CoverageClassification.Missing).Take(3).Select(b => $"L{b.StartLine} {b.Preview}"))})")));
        run.Documents.SelectMany(d => d.Blocks).Where(b => b.Classification == CoverageClassification.RepresentedDirectly)
            .Should().OnlyContain(b => b.RenderEvidence == CoverageEvidenceStatus.Present,
                "directly represented text is emitted by the shared source-notes render component with stable block hooks");
        run.Documents.SelectMany(d => d.Blocks).Where(b => b.Classification == CoverageClassification.RepresentedStructurally)
            .Should().OnlyContain(b => b.RenderEvidence == CoverageEvidenceStatus.NotVerified && b.RootCause == "RenderedComponentEvidenceNotExercised",
                "serialized parser output does not prove that a role-specific rendered component displayed the projection");
        run.Documents.Should().OnlyContain(d => d.Status == DiagnosticStatus.Partial,
            "the diagnostic must not claim end-to-end Pass while structured render evidence has not been exercised");
    }

    [Fact]
    public void RunsAreIsolatedAndDoNotRetainResults()
    {
        var service = Service();
        var first = service.RunContentIntegrity();
        var second = service.RunExplorerCoverage();
        first.RunId.Should().NotBe(second.RunId);
        first.Documents.Should().HaveCount(5);
        second.Documents.Should().HaveCount(5);
    }

    [Fact]
    public async Task ConcurrentRuns_UseDistinctRunAndResultCollections()
    {
        var service = Service();
        var results = await Task.WhenAll(
            Task.Run(() => service.RunContentIntegrity()),
            Task.Run(() => service.RunContentIntegrity()));

        results[0].RunId.Should().NotBe(results[1].RunId);
        ReferenceEquals(results[0].Documents, results[1].Documents).Should().BeFalse();
        results[0].Documents.Should().HaveCount(5);
        results[1].Documents.Should().HaveCount(5);
    }

    private static MarkdownDiagnosticsService Service() => new(new ConfigurationBuilder().Build());
}
