using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceAnalysisStatus { Ready, Partial, Failed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceConfidence { Confirmed, StrongSourceEvidence, Partial, NotResolved }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeveloperTestLayer { Unit, Integration, Component, Architecture, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceCoverageStatus { DeveloperUnitCovered, DeveloperIntegrationCovered, SourceEvidenceOnly, CrossLayerGap, RuntimeGap, E2EGap, ManualVerification, NotAssessable }

/// <summary>Evidence only. No source text, configuration values, test bodies or execution results.</summary>
public sealed record IqrSourceSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string IntegrationId { get; init; } = "";
    public SourceArchive Archive { get; init; } = new("", "", 0);
    public string Commit { get; init; } = "Unknown";
    public string? Branch { get; init; }
    public int AnalyzerVersion { get; init; } = 1;
    public DateTimeOffset AnalyzedAt { get; init; }
    public SourceAnalysisStatus Status { get; init; }
    public List<SourceProject> Projects { get; init; } = [];
    public List<SourceConfigurationEvidence> Configurations { get; init; } = [];
    public List<ImplementationRule> Rules { get; init; } = [];
    public List<DeveloperTestEvidence> Tests { get; init; } = [];
    public List<SourceCoverage> Coverage { get; init; } = [];
    public List<SourceDataflow> Dataflows { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public string DeploymentCorrelation { get; init; } = "Deployment/source correlation not established";
}

public sealed record SourceProject(string Name, string Path, bool IsTest, string Classification, SourceConfidence Confidence);
public sealed record SourceConfigurationEvidence(string File, List<string> Keys, SourceConfidence Confidence);
public sealed record ImplementationRule
{
    public string Id { get; init; } = "";
    public string Project { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? Table { get; init; }
    public string Field { get; init; } = "";
    public string? TargetType { get; init; }
    public string Requirement { get; init; } = "Not resolved";
    public string Operation { get; init; } = "Not resolved";
    public string NullBehavior { get; init; } = "Not resolved";
    public string InvalidBehavior { get; init; } = "Not resolved";
    public string Fallback { get; init; } = "Not resolved";
    public string Behavior { get; init; } = "";
    public List<string> RelatedSymbols { get; init; } = [];
    public SourceConfidence Confidence { get; init; }
    public SourceLocation Location { get; init; } = new("", 1);
    public string ConditionFingerprint { get; init; } = "";
    public string AssertionIntent { get; init; } = "Not resolved";
    public bool TestCorrelationResolved { get; init; } = true;
}
public sealed record DeveloperTestEvidence
{
    public string Id { get; init; } = "";
    public string Project { get; init; } = "";
    public string Class { get; init; } = "";
    public string Method { get; init; } = "";
    public string Framework { get; init; } = "";
    public List<string> Categories { get; init; } = [];
    public DeveloperTestLayer Layer { get; init; }
    public SourceConfidence Confidence { get; init; }
    public List<string> ProductionSymbols { get; init; } = [];
    public List<string> Fields { get; init; } = [];
    public string InputCondition { get; init; } = "Not resolved";
    public string AssertionIntent { get; init; } = "Not resolved";
    public SourceLocation Location { get; init; } = new("", 1);
    public string ExecutionResult { get; init; } = "Developer test exists; execution result unavailable";
}
public sealed record SourceCoverage(string RuleId, List<string> DeveloperTestIds, List<SourceCoverageStatus> Statuses, string MatchReason, string Action);
public sealed record SourceDataflow(string Field, List<string> Steps, SourceConfidence Confidence, List<SourceLocation> Locations, string Gap,
    List<string>? DeveloperTestIds = null, string GuardSymbol = "Not resolved");

/// <summary>Explicit selection per integration. Uploading B never replaces A in a recorded run.</summary>
public sealed record IqrSourceSelection(string IntegrationId, Guid SnapshotId);
