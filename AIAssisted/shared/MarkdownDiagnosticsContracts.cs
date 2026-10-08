namespace BirkNext.MarkdownDiagnostics;

public enum DiagnosticStatus { Pass, Partial, Unsupported, Fail, NotRun }
public enum CoverageClassification { RepresentedDirectly, RepresentedStructurally, IntentionallyIgnored, Unsupported, Missing }
public enum CoverageEvidenceStatus { Present, Absent, NotVerified, NotApplicable }
public sealed record DiagnosticScenario(string Id, string Name, DiagnosticStatus Status, string Summary, string Expected,
    string Observed, long DurationMilliseconds = 0, IReadOnlyList<string>? Details = null);
public sealed record ContentIntegrityDocument(string DisplayName, string ArtifactRole, string SafeRelativePath,
    long ArchiveBytes, int ArchiveChars, int ArchiveLines, string ArchiveSha256, string ArchiveCanonicalSha256,
    long StoredBytes, int StoredChars, int StoredLines, string StoredSha256, string StoredCanonicalSha256,
    bool ExactMatch, bool CanonicalMatch, string RawImportStatus, bool ParserReachedEof, int ParserConsumedChars,
    int ParserConsumedLines, int LastProcessedLine, string? LastProcessedHeading, IReadOnlyList<string> ParserWarnings,
    bool StartMarkerPreserved, bool MiddleMarkerPreserved, bool EndMarkerPreserved, bool PotentialTruncation, DiagnosticStatus Status);
public sealed record ContentIntegrityRun(Guid RunId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    DiagnosticStatus OverallStatus, IReadOnlyList<ContentIntegrityDocument> Documents,
    bool RealProjectFixtureUsed = false, DiagnosticStatus RealFixtureStatus = DiagnosticStatus.NotRun, string? RealFixtureNote = null)
{
    public int MarkdownFilesChecked => Documents.Count;
    public int ExactImportMatches => Documents.Count(d => d.ExactMatch);
    public int CanonicalMatches => Documents.Count(d => d.CanonicalMatch);
    public int ParserEofReached => Documents.Count(d => d.ParserReachedEof);
    public int ParserWarnings => Documents.Sum(d => d.ParserWarnings.Count);
    public int PotentialTruncations => Documents.Count(d => d.PotentialTruncation);
}
public sealed record ExplorerCoverageBlock(string BlockId, int StartLine, int EndLine, string BlockType, string SourceFingerprint,
    string Preview, CoverageClassification Classification, string? Destination, string Reason, string? RuleId = null,
    CoverageEvidenceStatus SourceEvidence = CoverageEvidenceStatus.Present,
    CoverageEvidenceStatus ParserEvidence = CoverageEvidenceStatus.NotVerified,
    CoverageEvidenceStatus ExtractorEvidence = CoverageEvidenceStatus.NotVerified,
    CoverageEvidenceStatus PageModelEvidence = CoverageEvidenceStatus.NotVerified,
    CoverageEvidenceStatus RenderEvidence = CoverageEvidenceStatus.NotVerified,
    string? RootCause = null);
public sealed record ExplorerCoverageDocument(string ArtifactRole, string DisplayName, int SourceBlockCount,
    int RepresentedDirectlyCount, int RepresentedStructurallyCount, int IntentionallyIgnoredCount,
    int UnsupportedCount, int MissingCount, DiagnosticStatus Status, IReadOnlyList<ExplorerCoverageBlock> Blocks);
public sealed record ExplorerCoverageRun(Guid RunId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    DiagnosticStatus OverallStatus, IReadOnlyList<ExplorerCoverageDocument> Documents,
    bool RealProjectFixtureUsed = false, DiagnosticStatus RealFixtureStatus = DiagnosticStatus.NotRun, string? RealFixtureNote = null);
