using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public sealed record FrontendQualityManualReviewItem
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("relatedLogicalId")] public string? RelatedLogicalId { get; init; }
    [JsonPropertyName("severity")] public FrontendQualitySeverity? Severity { get; init; }
}
