using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WcagVersion { Wcag21, Wcag22 }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WcagLevel { A, AA }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WcagAutomation { Automatic, Partial, Manual }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WcagStatus { Pass, Fail, ManualReviewRequired, NotApplicable, NotTested }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WcagConfidence { High, Medium, Low }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WcagEvidenceSource { DOM, BrowserRuntime, Performance, Proxy, CrossPage, Manual }

public sealed record WcagCriterionDefinition(string CriterionId, WcagLevel Level, string Title,
    WcagAutomation AutomationLevel, IReadOnlyList<string> SupportedChecks,
    bool RequiresInteraction = false, bool RequiresCrossPageEvidence = false,
    bool RequiresManualReview = true, string Notes = "", WcagVersion Since = WcagVersion.Wcag21);

public sealed class WcagSettings
{
    public WcagVersion Version { get; set; } = WcagVersion.Wcag22;
    // AA includes A. AAA is deliberately not part of this configuration.
    public WcagLevel Level { get; set; } = WcagLevel.AA;
}

public sealed record WcagManualReview
{
    public string CriterionId { get; init; } = "";
    public WcagVersion Version { get; init; }
    public int Generation { get; init; }
    public WcagStatus Result { get; init; }
    public string Comment { get; init; } = "";
    public string EvidenceNote { get; init; } = "";
    public string ReviewedBy { get; init; } = "";
    public DateTimeOffset ReviewedAt { get; init; }
    // Application-scope evidence depends on every participating page generation.
    public string ScopeGeneration { get; init; } = "";
}

public sealed record WcagCriterionResult
{
    public WcagCriterionDefinition Definition { get; init; } = null!;
    public string Page { get; init; } = "";
    public int Generation { get; init; }
    public WcagStatus Status { get; init; }
    public int Findings { get; init; }
    public WcagConfidence? Confidence { get; init; }
    public WcagEvidenceSource EvidenceSource { get; init; }
    public string AutomatedEvidence { get; init; } = "";
    public DateTimeOffset? LastTested { get; init; }
    public WcagManualReview? ManualReview { get; init; }
    public bool ManualReviewStale { get; init; }
}

public sealed record WcagAssessment
{
    public WcagVersion Version { get; init; } = WcagVersion.Wcag22;
    public WcagLevel Level { get; init; } = WcagLevel.AA;
    public List<WcagCriterionResult> Results { get; init; } = [];
    public string TargetLabel => $"WCAG {(Version == WcagVersion.Wcag21 ? "2.1" : "2.2")} {(Level == WcagLevel.AA ? "A + AA" : "A")}";
    public const string Disclaimer = "WCAG automated assessment. No automated failure detected does not establish WCAG conformance. Human review and complete processes remain necessary.";
}
