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
public sealed record SourceChangeImpactRequest(string EnvironmentId, string ProjectId, Guid BaselineSnapshotId, Guid TargetSnapshotId);
public sealed record ImpactChange(string Id, ImpactChangeDomain Domain, ImpactChangeKind Kind, string EntityType, string EntityKey, string Name, string Detail, List<string> SourceFiles, string? ContractChangeClass = null);
public sealed record ImpactPathStep(string EntityType, string EntityId, string Label, string Relationship, string EvidenceState, string Basis);
public sealed record TechnicalImpactItem(string Id, string EntityType, string EntityId, string DisplayName, ImpactChangeDomain Domain,
    TechnicalImpactLevel Level, string EvidenceState, string Reason, List<ImpactPathStep> Path, List<string> SourceChangeIds, List<string> SourceFiles);
public sealed record ImpactTraceabilityItem(Guid ScenarioId, string Kind, string Title, string Reason, List<string> Paths);
public sealed record ImpactTestRecommendation(Guid TestId, string Title, string TestKind, string Reason, List<string> Paths);
public sealed record SourceChangeImpactReport(
    Guid BaselineSnapshotId, string BaselineRepository, string BaselineArchiveName, string BaselineFingerprint, DateTimeOffset BaselineAnalyzedAt,
    Guid TargetSnapshotId, string TargetRepository, string TargetArchiveName, string TargetFingerprint, DateTimeOffset TargetAnalyzedAt,
    List<ImpactChange> Changes, List<TechnicalImpactItem> TechnicalImpacts, List<ImpactTraceabilityItem> Requirements,
    List<ImpactTraceabilityItem> Tasks, List<ImpactTestRecommendation> RecommendedTests, List<string> CoverageGaps,
    List<string> Limitations, string RiskStatus);
