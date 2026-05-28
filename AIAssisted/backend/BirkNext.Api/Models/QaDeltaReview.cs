namespace BirkNext.Api.Models;

public class QaDeltaReview
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? OldSpecFileName { get; set; }
    public string? NewSpecFileName { get; set; }
    public string? OldSpecHash { get; set; }
    public string? NewSpecHash { get; set; }
    public int? OldSpecSize { get; set; }
    public int? NewSpecSize { get; set; }
    public string AnalysisProfile { get; set; } = string.Empty;
    public string SummaryJson { get; set; } = string.Empty;
    public string DeltaItemsJson { get; set; } = string.Empty;
}
