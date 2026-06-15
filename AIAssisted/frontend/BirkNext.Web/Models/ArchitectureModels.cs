namespace BirkNext.Web.Models;

public enum ArchElementType
{
    Api,
    DomainEntity,            // plain domain model entity (Person, Barn, etc.)
    DomainEvent,
    Service,
    InfrastructureComponent, // EF Core, Outbox worker, Hosted Services, Azure deployment
    DataStore,               // database / data-store specific
    Persistence,
    Messaging,
    Security,
    SecurityBoundary,        // security boundary (e.g. Kode 6/7 protected data zone)
    ExternalSystem,
    IntegrationPoint,        // integration point with external systems
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
