namespace BirkNext.Api.Models;

public class ReviewedCandidate
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public ScenarioKind Classification { get; set; }
    public CandidateReviewStatus ReviewStatus { get; set; }
    public string? SourceDocument { get; set; }
    public string? SourceSection { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string ReviewedBy { get; set; } = "placeholder";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
}
