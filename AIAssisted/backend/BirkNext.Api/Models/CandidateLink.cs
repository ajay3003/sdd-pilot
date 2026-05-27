namespace BirkNext.Api.Models;

public class CandidateLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ProjectId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SourceCandidateRef { get; set; } = string.Empty;
    public string TargetCandidateRef { get; set; } = string.Empty;
    public CandidateLinkType LinkType { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
