using BirkNext.TestEvidence;

namespace BirkNext.Web.Models;

/// <summary>An imported test-result artifact (one file). Raw content is not retained; its SHA-256 fingerprint is the dedupe and provenance key.</summary>
public sealed class SddTestResultArtifact
{
    public Guid ArtifactId { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public string Format { get; set; } = "TRX";
    public string Fingerprint { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ProviderId { get; set; } = TestEvidenceProviderIds.Trx;
    public int ProviderVersion { get; set; } = 1;
    public string? BuildReference { get; set; }
    public string BuildBinding { get; set; } = "Unknown";
    public string? CommitReference { get; set; }
    public string CommitBinding { get; set; } = "Unknown";
    public string? EnvironmentReference { get; set; }
    /// <summary>Set only when the user stated the results came from this source snapshot ("Provided"); otherwise the version is unknown.</summary>
    public string? SourceSnapshotId { get; set; }
    public string? SourceFingerprint { get; set; }
    public string SourceBinding { get; set; } = "Unknown";
    /// <summary>Snapshot whose test inventory was used to correlate identities (not a version claim).</summary>
    public string? CorrelationSnapshotId { get; set; }
    public string? RepositoryName { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}

/// <summary>One test run in an artifact. Counts are parsed from the individual results.</summary>
public sealed class SddTestRunEvidence
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public Guid ArtifactId { get; set; }
    public string ProviderId { get; set; } = TestEvidenceProviderIds.Trx;
    public string ProviderRunId { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public double? DurationMs { get; set; }
    public string RunState { get; set; } = "Unknown";
    public string? ProviderOutcome { get; set; }
    public TestResultCounts Counts { get; set; } = new();
    public string? BuildReference { get; set; }
    public string? EnvironmentReference { get; set; }
    public string? SourceSnapshotId { get; set; }
    public string SourceBinding { get; set; } = "Unknown";
    public string? RepositoryName { get; set; }
    public List<string> Messages { get; set; } = [];
}

/// <summary>
/// A source test definition recorded from a Source Analysis snapshot (one record per definition per snapshot). Discovery only. Currentness:
/// Current (in the selected snapshot), Historical (superseded by a later snapshot of the same repository), PotentiallyStale (changed or no longer
/// found there). Executions keep their own immutable results whatever happens here.
/// </summary>
public sealed class SddTestDefinitionEvidence
{
    public SourceTestDefinition Definition { get; set; } = new();
    public string SourceSnapshotId { get; set; } = "";
    public string SourceSnapshotFingerprint { get; set; } = "";
    public string? RepositoryName { get; set; }
    public string DiscoveryProviderId { get; set; } = TestEvidenceProviderIds.DotNetXunitDiscovery;
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Currentness { get; set; } = "Current";
    public string? CurrentnessReason { get; set; }
}

/// <summary>What one import did — the persisted summary shown after import.</summary>
public sealed record SddTestImportSummary(bool AlreadyImported, string Artifact, string Provider, int TestsFound, int Passed, int Failed, int SkippedOrNotExecuted,
    int Other, int UnresolvedDefinitions, int AmbiguousDefinitions, int RequirementLinked, int AcceptanceCriterionLinked, int DuplicatesIgnored,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Limitations, Guid? RunId);

/// <summary>The explainable current result of one test: which execution was chosen and why (not simply the newest timestamp).</summary>
public sealed record SddCurrentTestResult(SddTestExecutionEvidence? Execution, string Result, string Reason, IReadOnlyList<SddTestExecutionEvidence> RunExecutions);

/// <summary>Per requirement: test design, source-discovered tests, executions and result as separate dimensions.</summary>
public sealed record SddRequirementTestEvidenceRow(string RequirementId, string RequirementText, IReadOnlyList<SddTestEvidence> Designed,
    IReadOnlyList<SddTestDefinitionEvidence> SourceTests, IReadOnlyList<SddTestDefinitionEvidence> CandidateSourceTests,
    IReadOnlyList<SddTestExecutionEvidence> Executions, IReadOnlyList<(string TestKey, SddCurrentTestResult Current)> CurrentResults, string Currentness);
