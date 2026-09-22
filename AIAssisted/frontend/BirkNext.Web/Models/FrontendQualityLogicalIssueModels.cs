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
    /// <summary>
    /// The page this observation was made on, when the source recorded one. It is what makes the SAME rule firing on
    /// five routes one issue on five pages rather than five issues — the page belongs to the occurrence, not to the
    /// problem. Null for a finding that is not page-specific (a missing response header is one fact about the target).
    /// </summary>
    [JsonPropertyName("page")] public string? Page { get; init; }
    /// <summary>The title with any page qualifier removed: what the observation is ABOUT, independent of where it was seen.</summary>
    [JsonPropertyName("subject")] public string? Subject { get; init; }
    /// <summary>Source observation, or a conclusion drawn from observations already reported elsewhere.</summary>
    [JsonPropertyName("origin")] public FrontendQualityFindingOrigin Origin { get; init; } = FrontendQualityFindingOrigin.Source;
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

    /// <summary>
    /// Routes this issue was observed on, in a stable order. Empty for an issue that is not page-specific. A count here
    /// is a count of PLACES, never of problems: "5 affected pages" is one thing to fix in five places.
    /// </summary>
    [JsonPropertyName("affectedPages")] public List<string> AffectedPages { get; init; } = [];

    /// <summary>
    /// Domains this issue also shows up in, beyond <see cref="Category"/>. A missing Content-Security-Policy is one
    /// problem that both Security and Standards care about; duplicating it into both domains would make it read as two.
    /// </summary>
    [JsonPropertyName("relatedCategories")] public List<FrontendQualityCategory> RelatedCategories { get; init; } = [];

    /// <summary>
    /// Every supporting observation is Info: nothing here asks for a change. Kept out of the actionable issue count and
    /// out of the release reasons, and kept in the result — an absent capability is still worth knowing about.
    /// </summary>
    [JsonPropertyName("informational")] public bool Informational { get; init; }

    /// <summary>Conclusions drawn from observations reported elsewhere; never counted as new problems.</summary>
    [JsonPropertyName("derived")] public bool Derived { get; init; }

    /// <summary>How many source observations support this one issue.</summary>
    public int SourceFindingCount => FindingInstances.Count;

    /// <summary>Counted against a release decision: an actionable problem this review actually observed.</summary>
    public bool IsActionable => !Informational && !Derived;

    /// <summary>"5 affected pages · 7 source observations", or just the observation count when no page was recorded.</summary>
    public string ScaleLabel =>
        (AffectedPages.Count > 1 ? $"{AffectedPages.Count} affected pages · " : "")
        + $"{SourceFindingCount} source observation{(SourceFindingCount == 1 ? "" : "s")}";
}
