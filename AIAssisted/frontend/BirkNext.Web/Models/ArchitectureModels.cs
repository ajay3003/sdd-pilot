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
    public ArchitectureConfidence Confidence { get; set; } = ArchitectureConfidence.High;
    public string Description { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public List<string> SourceSections { get; init; } = [];
    public List<string> RelatedFrIds { get; init; } = [];
    public List<string> RelatedUsIds { get; init; } = [];
    public List<string> UsedBy { get; init; } = [];
    public List<string> DependsOn { get; init; } = [];
}

public enum ArchitectureConfidence
{
    Low,
    Medium,
    High,
}

public sealed class ArchitectureRelationship
{
    public required string SourceName { get; init; }
    public required string TargetName { get; init; }
    public required string Verb { get; init; }
    public ArchitectureConfidence Confidence { get; init; } = ArchitectureConfidence.Medium;
    public string SourceText { get; init; } = string.Empty;
    public string SourceSection { get; init; } = string.Empty;
    public List<string> RelatedFrIds { get; init; } = [];
}

public sealed class ArchitectureCandidate
{
    public required string Name { get; init; }
    public required string SourceText { get; init; }
    public required ArchElementType SuggestedType { get; init; }
    public ArchitectureConfidence Confidence { get; init; } = ArchitectureConfidence.Medium;
    public required string Reason { get; init; }
    public string SourceSection { get; init; } = string.Empty;
}

public sealed class ArchitectureModel
{
    public List<ArchElement> Elements { get; init; } = [];
    public List<ArchitectureRelationship> Relationships { get; init; } = [];
    public List<ArchitectureCandidate> Candidates { get; init; } = [];

    public IEnumerable<ArchElement> ByType(ArchElementType t) =>
        Elements.Where(e => e.ElementType == t);

    public bool IsEmpty => Elements.Count == 0 && Candidates.Count == 0;
}
