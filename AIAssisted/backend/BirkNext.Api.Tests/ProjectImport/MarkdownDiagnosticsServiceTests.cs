using BirkNext.Api.Services;
using BirkNext.MarkdownDiagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.IO.Compression;

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
        var notes = BirkNext.Web.Services.MarkdownTokenizer.FindUnrepresentedBlocks(markdown, new { });
        first.Should().Be(notes.Single(note => note.StartLine == 2).BlockId);
    }

    [Fact]
    public void SourceNotesUseExplicitRangesInsteadOfSerializedTextContainment()
    {
        const string markdown = "# Example\nrepeat\nrepeat\n";
        var fingerprint = BirkNext.Web.Services.MarkdownTokenizer.DocumentFingerprint(markdown);
        var source = BirkNext.Web.Models.ProjectionProvenance.Source(fingerprint, 2, 2, "Text", "repeat");
        var projection = BirkNext.Web.Models.ProjectionProvenance.Create("Specification", "Example", "example", source);
        var model = new { Provenance = projection, DisplayOnly = "repeat" };

        var notes = BirkNext.Web.Services.MarkdownTokenizer.FindUnrepresentedBlocks(markdown, model);

        notes.Should().ContainSingle(note => note.StartLine == 3 && note.Text == "repeat",
            "the second identical source block is not covered by the projection's explicit line range");
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
        run.Documents.SelectMany(d => d.Blocks).Should().NotContain(b => b.Classification == CoverageClassification.Missing,
            "unprojected source is preserved as complete source-note text when no structured provenance exists");
        run.Documents.SelectMany(d => d.Blocks).Where(b => b.Classification == CoverageClassification.RepresentedDirectly)
            .Should().OnlyContain(b => b.RenderEvidence == CoverageEvidenceStatus.NotVerified,
                "this backend-only diagnostic has no browser evidence");
        run.Documents.SelectMany(d => d.Blocks).Where(b => b.Classification == CoverageClassification.RepresentedStructurally)
            .Should().OnlyContain(b => b.RenderEvidence == CoverageEvidenceStatus.NotVerified && b.RootCause == "RenderedComponentEvidenceNotExercised",
                "source provenance does not prove rendered DOM presence");
        run.Documents.SelectMany(d => d.Blocks).Where(b => b.Classification == CoverageClassification.RepresentedStructurally)
            .Should().OnlyContain(b => b.ExtractorEvidence == CoverageEvidenceStatus.Present && b.PageModelEvidence == CoverageEvidenceStatus.Present,
                "structured coverage is counted only when construction-time provenance exists on the returned Explorer model");
        run.Documents.Should().OnlyContain(d => d.Status != DiagnosticStatus.Fail);
        var missing = run.Documents.SelectMany(d => d.Blocks).Where(b => b.Classification == CoverageClassification.Missing);
        missing.Should().OnlyContain(b => b.ParserEvidence == CoverageEvidenceStatus.Present &&
            b.ExtractorEvidence == CoverageEvidenceStatus.Absent,
            "the production tokenizer sees these source blocks; their missing stage is projection/extractor provenance");
    }

    [Fact]
    public void ExplorerCoverage_DistinguishesArchiveDocumentsFromBuiltInFixtures()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"coverage-origin-{Guid.NewGuid():N}.zip");
        try
        {
            using (var stream = File.Create(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("specs/001-fixture/tasks.md");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("# Tasks\n\n## Phase 1\n\n- [ ] T001 Verify archive ownership\n");
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectCompatibility:AcceptanceArchivePath"] = archivePath
            }).Build();

            var run = new MarkdownDiagnosticsService(config).RunExplorerCoverage();

            run.Documents.Should().HaveCount(6);
            run.Documents.Count(document => document.SourceOrigin == "BuiltInFixture").Should().Be(5);
            run.Documents.Should().ContainSingle(document => document.SourceOrigin == "ConfiguredArchive" && document.DisplayName == "tasks.md");
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    [Fact]
    public void ExplorerCoverage_PreservesUnprojectedCodeAndKeepsProjectedCodeStructured()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"coverage-code-{Guid.NewGuid():N}.zip");
        try
        {
            using (var stream = File.Create(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Add("specs/001-fixture/tasks.md", "# Tasks\n\n## Phase 1\n\n- [ ] T001 Existing\n\n## Parallel Example\n```text\nT001 Existing\n```\n");
                Add("specs/001-fixture/data-model.md", "# Data Model\n\n## State Transitions\n```text\nActive -> Revoked\n```\n\n## Entities\n### Person\n```text\nId: Guid\n```\n");

                void Add(string path, string content)
                {
                    var entry = archive.CreateEntry(path);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }
            }

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectCompatibility:AcceptanceArchivePath"] = archivePath
            }).Build();
            var run = new MarkdownDiagnosticsService(config).RunExplorerCoverage();
            var tasks = run.Documents.Single(d => d.DisplayName == "tasks.md");
            var dataModel = run.Documents.Single(d => d.DisplayName == "data-model.md");

            tasks.Blocks.Should().Contain(b => b.BlockType == "FencedCodeLine" &&
                b.Classification == CoverageClassification.RepresentedDirectly && b.Destination == "Explorer source notes (full source text)");
            dataModel.Blocks.Should().Contain(b => b.StartLine == 5 && b.BlockType == "FencedCodeLine" &&
                b.Classification == CoverageClassification.RepresentedDirectly && b.Destination == "Explorer source notes (full source text)");
            dataModel.Blocks.Should().Contain(b => b.StartLine == 11 && b.BlockType == "FencedCodeLine" &&
                b.Classification == CoverageClassification.RepresentedStructurally,
                "a code block within an explicitly modeled entity is covered by that entity's source range");
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
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
