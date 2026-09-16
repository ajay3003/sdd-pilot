using System.Text.Json.Serialization;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Services;

namespace BirkNext.Web.Models;

/// <summary>Categories of the BirkNext Browser Quality engine (one common finding contract for every sub-result).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserQualityCategory { Accessibility, Performance, Runtime, Dom, Network, Blazor, SecurityObservation }

/// <summary>
/// One Browser Quality finding. Deterministically derived from safe page evidence (companion) plus, where available, proxy network
/// evidence for the same page. Severity is fixed per rule. Evidence lines are sanitized values only: metrics, counts, rule ids,
/// structural selectors, sanitized URLs. Never a credential, raw DOM, body or form value.
/// </summary>
public sealed record BrowserQualityFinding
{
    public WcagLevel? Level { get; init; }
    public string? Element { get; init; }
    public string? Observed { get; init; }
    public string? Expected { get; init; }
    public WcagConfidence? Confidence { get; init; }
    public WcagEvidenceSource? EvidenceSource { get; init; }
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";
    [JsonPropertyName("category")] public BrowserQualityCategory Category { get; init; }
    [JsonPropertyName("severity")] public FrontendQualitySeverity Severity { get; init; }
    [JsonPropertyName("page")] public string Page { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("explanation")] public string Explanation { get; init; } = "";
    [JsonPropertyName("evidence")] public List<string> Evidence { get; init; } = [];
    [JsonPropertyName("recommendation")] public string Recommendation { get; init; } = "";
    /// <summary>"Browser Companion", "Browser Companion + Local HTTPS Proxy" (correlated) or "Local HTTPS Proxy".</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = BrowserQualityRules.CompanionSource;
    [JsonPropertyName("observedAt")] public DateTimeOffset ObservedAt { get; init; }
    [JsonPropertyName("wcag")] public string? Wcag { get; init; }
}

/// <summary>Result of the Browser Quality engine for one review: which pages had evidence and the findings derived from them.</summary>
public sealed record BrowserQualityReviewResult
{
    public WcagAssessment? Wcag { get; init; }
    [JsonPropertyName("companionState")] public BrowserCompanionState CompanionState { get; init; }
    [JsonPropertyName("companionMessage")] public string CompanionMessage { get; init; } = "";
    [JsonPropertyName("proxyEvidenceAvailable")] public bool ProxyEvidenceAvailable { get; init; }
    [JsonPropertyName("pagesWithEvidence")] public int PagesWithEvidence { get; init; }
    [JsonPropertyName("pageIdentities")] public List<string> PageIdentities { get; init; } = [];
    [JsonPropertyName("findings")] public List<BrowserQualityFinding> Findings { get; init; } = [];
    [JsonPropertyName("limitations")] public List<string> Limitations { get; init; } = [];
    [JsonPropertyName("evaluatedAt")] public DateTimeOffset EvaluatedAt { get; init; }
    [JsonPropertyName("browserName")] public string? BrowserName { get; init; }
    public bool Assessed => CompanionState == BrowserCompanionState.Connected && PagesWithEvidence > 0
        || (CompanionState == BrowserCompanionState.Disconnected && PagesWithEvidence > 0);
}
