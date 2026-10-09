using System.Text.Json.Serialization;

namespace BirkNext.SourceImpact;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactChangeDomain { Architecture, Contracts, Configuration, Infrastructure, Database, CiCd, Observability, Dependencies, Unclassified }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactChangeKind { Added, Removed, Changed, NoLongerFound }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TechnicalImpactLevel { Direct, Indirect, Potential, NeedsReview }

public sealed record ImpactSnapshotOption(Guid Id, string Repository, string ArchiveName, string Fingerprint, DateTimeOffset AnalyzedAt, string Status);
public sealed record ImpactSnapshotList(bool SourceAnalysisEnabled, List<ImpactSnapshotOption> Snapshots);
public sealed record SourceChangeImpactRequest(string EnvironmentId, string ProjectId, Guid BaselineSnapshotId, Guid TargetSnapshotId,
    string? ProjectImportId = null, string? ProjectDisplayName = null, int MaxImpactDepth = 2);
public sealed record ImpactChange(string Id, ImpactChangeDomain Domain, ImpactChangeKind Kind, string EntityType, string EntityKey, string Name, string Detail, List<string> SourceFiles, string? ContractChangeClass = null);
public sealed record ImpactPathStep(string EntityType, string EntityId, string Label, string Relationship, string EvidenceState, string Basis);
public sealed record TechnicalImpactItem(string Id, string EntityType, string EntityId, string DisplayName, ImpactChangeDomain Domain,
    TechnicalImpactLevel Level, string EvidenceState, string Reason, List<ImpactPathStep> Path, List<string> SourceChangeIds, List<string> SourceFiles)
{
    public int Depth { get; init; }
}
public sealed record ImpactTraceabilityItem(Guid ScenarioId, string Kind, string Title, string Reason, List<string> Paths);
public sealed record ImpactTestRecommendation(Guid TestId, string Title, string TestKind, string Reason, List<string> Paths);
public sealed record ImpactJourneyStep(string EntityType, string EntityId, string Label, string Relationship, string EvidenceState, List<string> SourceFiles);
public sealed record ImpactJourneyItem(string Id, string DisplayName, string ImpactedSection, string EvidenceState, Guid SourceSnapshotId, List<ImpactJourneyStep> Steps,
    string Limitation);
public sealed record ImpactHistorySummary(Guid RunId, string ProjectDisplayName, string? ProjectImportId, Guid BaselineSnapshotId, Guid CurrentSnapshotId,
    string BaselineFingerprint, string CurrentFingerprint, DateTimeOffset CreatedAt, int ChangeCount, int ImpactCount, int TestCount, int JourneyCount);
public sealed record SourceChangeImpactReport(
    Guid BaselineSnapshotId, string BaselineRepository, string BaselineArchiveName, string BaselineFingerprint, DateTimeOffset BaselineAnalyzedAt,
    Guid TargetSnapshotId, string TargetRepository, string TargetArchiveName, string TargetFingerprint, DateTimeOffset TargetAnalyzedAt,
    List<ImpactChange> Changes, List<TechnicalImpactItem> TechnicalImpacts, List<ImpactTraceabilityItem> Requirements,
    List<ImpactTraceabilityItem> Tasks, List<ImpactTestRecommendation> RecommendedTests, List<string> CoverageGaps,
    List<string> Limitations, string RiskStatus)
{
    public Guid RunId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectImportId { get; init; }
    public string? ProjectDisplayName { get; init; }
    public List<ImpactJourneyItem> Journeys { get; init; } = [];
    public List<string> SecurityAndConfigurationReviews { get; init; } = [];
    public int MaxImpactDepth { get; init; } = 2;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactAnalysisEvidenceStatus { Evaluated, PartiallyEvaluated, NotEvaluated }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactAnalysisFindingKind { Requirement, Source, Component, Contract, Data, Configuration, Dependency, Integration, Journey, Test, Security, Deployment, Documentation, Task }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactAnalysisClassification { SelectedChange, SelectedForReview, DirectRelation, IndirectRelation, PossibleRelation, RelatedOnly, NeedsReview }

public sealed record ImpactRequirementOption(Guid Id, string Title, string? Description);
public sealed record ImpactAnalysisRunRequest(string ProjectId, string? ProjectDisplayName, string? ProjectImportId,
    List<Guid> RequirementIds, Guid? BaselineSnapshotId, Guid? CurrentSnapshotId, int MaxImpactDepth = 1);
public sealed record ImpactAnalysisEvidence(string Kind, string Description, string? SourcePath = null, Guid? SourceSnapshotId = null);
public sealed record ImpactAnalysisFinding(string Id, ImpactAnalysisFindingKind Kind, string DisplayName,
    ImpactAnalysisClassification Classification, string VerificationState, int Depth, string Reason,
    List<ImpactAnalysisEvidence> Evidence)
{
    public string SuggestedQaVerification { get; init; } = "Review the evidence and choose a relevant verification with the project team.";
}
public sealed record ImpactAnalysisDomainAssessment(string Domain, ImpactAnalysisEvidenceStatus Status, string Reason);
public sealed record ImpactAnalysisChangeSet(string ChangeOrigin, List<Guid> SelectedRequirementIds,
    Guid? BaselineSnapshotId, Guid? CurrentSnapshotId, string? BaselineFingerprint, string? CurrentFingerprint);
public sealed record ImpactAnalysisRunReport(Guid RunId, DateTimeOffset CreatedAt, string ProjectId, string? ProjectImportId,
    string ProjectDisplayName, ImpactAnalysisChangeSet ChangeSet, List<ImpactAnalysisFinding> Findings,
    List<ImpactAnalysisDomainAssessment> DomainAssessments, SourceChangeImpactReport? SourceComparison, List<string> Limitations);
public sealed record ImpactAnalysisRunHistoryItem(Guid RunId, string ProjectDisplayName, string? ProjectImportId,
    DateTimeOffset CreatedAt, int FindingCount, int EvaluatedDomains, int NotEvaluatedDomains,
    Guid? BaselineSnapshotId, Guid? CurrentSnapshotId, string? CurrentFingerprint);
