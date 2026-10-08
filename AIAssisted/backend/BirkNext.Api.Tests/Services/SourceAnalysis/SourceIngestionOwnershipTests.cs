using System.Text.RegularExpressions;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Architecture guards for source ownership: Source Analysis is the only source ingestion (one multipart source upload, one validated archive
/// reader in front of every specialized analyzer), and no review exposes a raw-archive ingestion method. Every allowed exception is listed with
/// its reason, so a new upload, archive reader or archive-taking service fails here until it is justified.
/// </summary>
public sealed class SourceIngestionOwnershipTests
{
    private static readonly string Api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api"));

    private static IEnumerable<(string Rel, string Text)> Sources() => Directory.GetFiles(Api, "*.cs", SearchOption.AllDirectories)
        .Select(f => (Rel: Path.GetRelativePath(Api, f).Replace('\\', '/'), Full: f))
        .Where(f => !f.Rel.StartsWith("obj/") && !f.Rel.StartsWith("bin/"))
        .Select(f => (f.Rel, File.ReadAllText(f.Full)));

    [Fact]
    public void Only_Source_Analysis_accepts_source_uploads()
    {
        var allowed = new Dictionary<string, string>
        {
            ["Controllers/IqrSourceEvidenceController.cs"] = "POST api/source-analysis/snapshots — the source ingestion",
            ["Controllers/ProjectImportController.cs"] = "POST api/project-import/preview — one project ZIP through the same reader and the same Source Analysis snapshot creation (not a second source store)",
            ["Controllers/DependencyReviewController.cs"] = "SBOM / lock-file import: deployed-inventory evidence, not a source repository",
            ["Controllers/TestEvidenceController.cs"] = "one .trx test-result artifact: execution evidence, never source (source tests come from the Source Analysis snapshot)",
        };
        Sources().Where(f => f.Rel.StartsWith("Controllers/") && Regex.IsMatch(f.Text, @"ReadFormAsync|IFormFile|SourceArchiveUpload\.ReadAsync")).Select(f => f.Rel)
            .Should().BeEquivalentTo(allowed.Keys, "a new multipart endpoint must not become a second source upload");
        // The archive upload itself is read in one place for both source endpoints.
        Sources().Where(f => f.Rel.StartsWith("Services/") && f.Text.Contains("ReadFormAsync", StringComparison.Ordinal)).Select(f => f.Rel)
            .Should().BeEquivalentTo(["Services/Integrations/SourceEvidence/SourceArchiveUpload.cs"]);
    }

    [Fact]
    public void Archives_are_opened_only_by_the_canonical_reader_and_justified_specialized_readers()
    {
        var allowed = new Dictionary<string, string>
        {
            ["Services/Integrations/SourceEvidence/IqrSourceArchiveReader.cs"] = "canonical bounded reader: validates every uploaded archive first (paths, symlinks, sizes)",
            ["Services/Integrations/ApplicationMessaging/SourceArchiveReader.cs"] = "Wolverine Roslyn analyzer input; runs inside Source Analysis ingestion on already-validated bytes",
            ["Services/Integrations/Scim/ScimSourceReader.cs"] = "SCIM and Security Classification Roslyn analyzers (incl. docs/specs .md); inside ingestion on validated bytes",
            ["Services/DependencyReview/SourceDependencyEvidenceExtractor.cs"] = "package-manager manifests and Renovate configs for Dependency Review; inside ingestion",
            ["Services/DependencyReview/DependencyReviewService.cs"] = "test-only RunAsync(archives) harness through the same evidence bridge; not exposed over HTTP",
        };
        Sources().Where(f => f.Text.Contains("new ZipArchive(", StringComparison.Ordinal)).Select(f => f.Rel)
            .Should().BeEquivalentTo(allowed.Keys, "source bytes come from Source Analysis; no review opens archives of its own");
    }

    [Fact]
    public void No_review_service_exposes_raw_archive_ingestion()
    {
        var archiveParameter = typeof(IReadOnlyList<(string, byte[])>);
        var offenders = typeof(BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic)
            .SelectMany(t => t.GetMethods().Where(m => m.GetParameters().Any(p => p.ParameterType == archiveParameter)).Select(m => $"{t.Name}.{m.Name}"))
            .ToList();
        offenders.Should().BeEquivalentTo(["IDependencyReviewService.RunAsync"],
            "only Dependency Review keeps its documented test harness; application messaging and SCIM read Source Analysis snapshots");
    }
}
