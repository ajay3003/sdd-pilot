namespace BirkNext.Web.Models;

public enum ArchElementType
{
    Api,
    Service,
    Persistence,
    Messaging,
    DomainEvent,
    Security,
    ExternalSystem,
    Pattern,
    Risk,
}

public sealed class ArchElement
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public required string Name { get; init; }
    public required ArchElementType ElementType { get; init; }
    public string Description { get; init; } = string.Empty;
    public List<string> SourceSections { get; init; } = [];
    public List<string> RelatedFrIds { get; init; } = [];
    public List<string> RelatedUsIds { get; init; } = [];
    public List<string> UsedBy { get; init; } = [];
    public List<string> DependsOn { get; init; } = [];
}

public sealed class ArchitectureModel
{
    public List<ArchElement> Elements { get; init; } = [];

    public IEnumerable<ArchElement> ByType(ArchElementType t) =>
        Elements.Where(e => e.ElementType == t);

    public bool IsEmpty => Elements.Count == 0;
}
