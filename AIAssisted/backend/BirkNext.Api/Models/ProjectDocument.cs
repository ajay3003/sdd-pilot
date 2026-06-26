namespace BirkNext.Api.Models;

public class ProjectDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ProjectDocumentKind DocumentKind { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
