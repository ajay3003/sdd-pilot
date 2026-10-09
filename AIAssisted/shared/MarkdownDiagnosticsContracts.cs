namespace BirkNext.MarkdownDiagnostics;

public enum DiagnosticStatus { Pass, Partial, Unsupported, Fail, NotRun }
public enum CoverageClassification { RepresentedDirectly, RepresentedStructurally, IntentionallyIgnored, Unsupported, Missing }
public enum CoverageEvidenceStatus { Present, Absent, NotVerified, NotApplicable }
public enum ProjectionRenderExecutionStatus { Completed, Unavailable }
public sealed record ProjectionRenderVerification(
    string ArtifactRole,
    string ExpectedDocumentId,
    string? ActualDocumentId,
    string Route,
    ProjectionRenderExecutionStatus ExecutionStatus,
    IReadOnlyList<string> ExpectedProjectionIds,
    IReadOnlyList<string> FoundProjectionIds,
    IReadOnlyList<string> MissingProjectionIds,
    IReadOnlyList<string> DuplicateProjectionIds,
    IReadOnlyList<string> UnexpectedProjectionIds,
    string? UnavailableReason = null)
{
    public static ProjectionRenderVerification Compare(string role, string expectedDocumentId, string actualDocumentId, string route,
        IEnumerable<string> expectedProjectionIds, IEnumerable<string> observedProjectionIds)
    {
        if (!IsSafeId(expectedDocumentId))
            return Unavailable(role, expectedDocumentId, route, expectedProjectionIds, "ExpectedDocumentIdentityInvalid");
        if (!IsSafeId(actualDocumentId))
            return Unavailable(role, expectedDocumentId, route, expectedProjectionIds, "DocumentIdentityUnavailable");
        if (!string.Equals(expectedDocumentId, actualDocumentId, StringComparison.Ordinal))
            return Unavailable(role, expectedDocumentId, route, expectedProjectionIds, "DocumentMismatch", actualDocumentId);

        var expected = expectedProjectionIds.Where(IsSafeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var observed = observedProjectionIds.Where(IsSafeId).ToArray();
        var counts = observed.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        return new(role, expectedDocumentId, actualDocumentId, route, ProjectionRenderExecutionStatus.Completed, expected,
            expected.Where(id => counts.ContainsKey(id)).ToArray(),
            expected.Where(id => !counts.ContainsKey(id)).ToArray(),
            counts.Where(pair => pair.Value > 1).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray(),
            counts.Keys.Where(id => !expectedSet.Contains(id)).Order(StringComparer.Ordinal).ToArray());
    }

    public static ProjectionRenderVerification Unavailable(string role, string expectedDocumentId, string route,
        IEnumerable<string> expectedProjectionIds, string reason, string? actualDocumentId = null) =>
        new(role, IsSafeId(expectedDocumentId) ? expectedDocumentId : "", IsSafeId(actualDocumentId) ? actualDocumentId : null,
            route, ProjectionRenderExecutionStatus.Unavailable,
            expectedProjectionIds.Where(IsSafeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            [], [], [], [], string.IsNullOrWhiteSpace(reason) ? "Browser evidence was unavailable." : reason);

    private static bool IsSafeId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
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
    string? RootCause = null,
    IReadOnlyList<string>? ProjectionIds = null);
public sealed record ExplorerCoverageDocument(string ArtifactRole, string DisplayName, int SourceBlockCount,
    int RepresentedDirectlyCount, int RepresentedStructurallyCount, int IntentionallyIgnoredCount,
    int UnsupportedCount, int MissingCount, DiagnosticStatus Status, IReadOnlyList<ExplorerCoverageBlock> Blocks,
    IReadOnlyList<ProjectionRenderVerification>? RenderVerifications = null,
    string? ExpectedDocumentId = null);
public static class ExplorerCoverageRenderEvidence
{
    /// <summary>Attaches browser evidence to existing structured source accounting; direct blocks remain unverified.</summary>
    public static ExplorerCoverageDocument Apply(ExplorerCoverageDocument document, ProjectionRenderVerification verification)
    {
        if (!string.Equals(document.ArtifactRole, verification.ArtifactRole, StringComparison.Ordinal))
            verification = ProjectionRenderVerification.Unavailable(document.ArtifactRole, verification.ExpectedDocumentId,
                verification.Route, verification.ExpectedProjectionIds, "Browser evidence role did not match the coverage document.", verification.ActualDocumentId);
        else if (string.IsNullOrWhiteSpace(document.ExpectedDocumentId) ||
                 !string.Equals(document.ExpectedDocumentId, verification.ExpectedDocumentId, StringComparison.Ordinal))
            verification = ProjectionRenderVerification.Unavailable(document.ArtifactRole, document.ExpectedDocumentId ?? "",
                verification.Route, verification.ExpectedProjectionIds, "ExpectedDocumentIdentityMismatch", verification.ActualDocumentId);

        if (verification.ExecutionStatus != ProjectionRenderExecutionStatus.Completed)
            return document with { RenderVerifications = (document.RenderVerifications ?? []).Append(verification).ToArray() };

        var found = verification.FoundProjectionIds.ToHashSet(StringComparer.Ordinal);
        var duplicates = verification.DuplicateProjectionIds.ToHashSet(StringComparer.Ordinal);
        var blocks = document.Blocks.Select(block =>
        {
            if (block.Classification != CoverageClassification.RepresentedStructurally || block.ProjectionIds is not { Count: > 0 } ids)
                return block;
            var absent = ids.Where(id => !found.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (absent.Length > 0)
                return block with
                {
                    RenderEvidence = CoverageEvidenceStatus.Absent,
                    RootCause = "RenderProjectionMissing"
                };
            if (ids.Any(duplicates.Contains))
                return block with { RenderEvidence = CoverageEvidenceStatus.Present, RootCause = "DuplicateRenderProjection" };
            return block with { RenderEvidence = CoverageEvidenceStatus.Present, RootCause = null };
        }).ToArray();
        var renderIntegrityFailure = blocks.Any(block => block.RenderEvidence == CoverageEvidenceStatus.Absent || block.RootCause == "DuplicateRenderProjection");
        var unexpected = verification.UnexpectedProjectionIds.Count > 0;
        return document with
        {
            Blocks = blocks,
            RenderVerifications = (document.RenderVerifications ?? []).Append(verification).ToArray(),
            Status = renderIntegrityFailure ? DiagnosticStatus.Fail : unexpected ? DiagnosticStatus.Partial : document.Status
        };
    }
}
public sealed record ExplorerCoverageRun(Guid RunId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    DiagnosticStatus OverallStatus, IReadOnlyList<ExplorerCoverageDocument> Documents,
    bool RealProjectFixtureUsed = false, DiagnosticStatus RealFixtureStatus = DiagnosticStatus.NotRun, string? RealFixtureNote = null);
