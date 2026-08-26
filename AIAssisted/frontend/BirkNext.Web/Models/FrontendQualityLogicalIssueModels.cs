using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public sealed record FrontendQualityFindingInstance
{
    [JsonPropertyName("engineId")] public required FrontendQualityEngineId EngineId { get; init; }
    [JsonPropertyName("sourceSystem")] public required string SourceSystem { get; init; }
    [JsonPropertyName("sourceFindingId")] public required string SourceFindingId { get; init; }
    [JsonPropertyName("sourceRuleId")] public string? SourceRuleId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("severity")] public FrontendQualitySeverity Severity { get; init; }
    [JsonPropertyName("category")] public FrontendQualityCategory Category { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("recommendation")] public required string Recommendation { get; init; }
    [JsonPropertyName("sanitizedEvidence")] public List<string> SanitizedEvidence { get; init; } = [];
    [JsonPropertyName("executionState")] public CheckExecutionStatus ExecutionState { get; init; }
    [JsonPropertyName("evidenceStrength")] public FrontendQualityEvidenceStrength EvidenceStrength { get; init; }
    [JsonPropertyName("reviewDisposition")] public FrontendQualityReviewDisposition ReviewDisposition { get; init; }
}

public sealed record FrontendQualityLogicalIssue
{
    [JsonPropertyName("logicalId")] public required string LogicalId { get; init; }
    [JsonPropertyName("canonicalTitle")] public required string CanonicalTitle { get; init; }
    [JsonPropertyName("primarySeverity")] public FrontendQualitySeverity PrimarySeverity { get; init; }
    [JsonPropertyName("sources")] public List<FrontendQualityEngineId> Sources { get; init; } = [];
    [JsonPropertyName("findingInstances")] public List<FrontendQualityFindingInstance> FindingInstances { get; init; } = [];
    [JsonPropertyName("evidenceStrength")] public FrontendQualityEvidenceStrength EvidenceStrength { get; init; }
    [JsonPropertyName("confidence")] public FrontendQualityEvidenceConfidence? Confidence { get; init; }
    [JsonPropertyName("reviewDisposition")] public FrontendQualityReviewDisposition ReviewDisposition { get; init; }
    [JsonPropertyName("category")] public FrontendQualityCategory Category { get; init; }
    [JsonPropertyName("recommendation")] public required string Recommendation { get; init; }
    [JsonPropertyName("manualVerificationRequired")] public bool ManualVerificationRequired { get; init; }
    [JsonPropertyName("groupingReason")] public string? GroupingReason { get; init; }
}
