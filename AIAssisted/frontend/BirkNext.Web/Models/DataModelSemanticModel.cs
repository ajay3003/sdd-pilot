namespace BirkNext.Web.Models;

/// <summary>
/// Canonical semantic model for Data Models.
/// Single source of truth for all Data Model review pages.
/// </summary>
public sealed class DataModelSemanticModel
{
    // ── Metadata ────────────────────────────────────────────────────────────
    public string Title { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? CreatedDate { get; init; }
    public string? LastUpdated { get; init; }

    // ── Core Elements ───────────────────────────────────────────────────────
    public List<SemanticDataEntity> Entities { get; init; } = [];
    public List<SemanticDataRelationship> Relationships { get; init; } = [];
    public List<SemanticDataEnumeration> Enumerations { get; init; } = [];
    public List<SemanticDataValueObject> ValueObjects { get; init; } = [];
    public List<SemanticDataAggregateRoot> AggregateRoots { get; init; } = [];

    // ── Aggregates ──────────────────────────────────────────────────────────
    public int TotalEntities => Entities.Count;
    public int TotalRelationships => Relationships.Count;
    public int TotalEnumerations => Enumerations.Count;
    public int TotalValueObjects => ValueObjects.Count;
    public int TotalAggregateRoots => AggregateRoots.Count;

    // ── Coverage Metrics ────────────────────────────────────────────────────
    public int EntitiesWithIdentifiers => Entities.Count(e => e.IdentifierFields.Count > 0);
    public int EntitiesWithValidation => Entities.Count(e => e.ValidationRules.Count > 0);
    public int EntitiesWithLifecycle => Entities.Count(e => !string.IsNullOrWhiteSpace(e.Lifecycle));

    // ── Complexity Metrics ──────────────────────────────────────────────────
    public int MaxEntityDepth => DataModelHelpers.CalculateMaxDepth(this);
    public int CircularRelationships => DataModelHelpers.CalculateCircularRelationships(this);
    public int OrphanEntities => DataModelHelpers.CalculateOrphanEntities(this);

    // ── Relationships ───────────────────────────────────────────────────────
    public Dictionary<string, List<string>> EntityToTraceability { get; init; } = [];
}

/// <summary>
/// Domain entity in the data model (semantic model).
/// </summary>
public sealed class SemanticDataEntity
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Stereotype { get; init; }  // e.g., "AggregateRoot", "ValueObject", "Entity"
    public List<SemanticDataAttribute> Attributes { get; init; } = [];
    public List<string> IdentifierFields { get; init; } = [];
    public List<string> ValidationRules { get; init; } = [];
    public List<SemanticDataMethod> Methods { get; init; } = [];
    public string? Lifecycle { get; init; }
    public List<string> RelatedTraceabilityIds { get; init; } = [];
    public List<string> RelationshipIds { get; init; } = [];
}

/// <summary>
/// Attribute of a data entity (semantic model).
/// </summary>
public sealed class SemanticDataAttribute
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public bool IsIdentifier { get; init; }
    public string? Description { get; init; }
    public string? Constraint { get; init; }
}

/// <summary>
/// Method or behavior of an entity (semantic model).
/// </summary>
public sealed class SemanticDataMethod
{
    public string Name { get; init; } = string.Empty;
    public string? ReturnType { get; init; }
    public List<string> Parameters { get; init; } = [];
    public string? Description { get; init; }
}

/// <summary>
/// Relationship between entities (semantic model).
/// </summary>
public sealed class SemanticDataRelationship
{
    public string Id { get; init; } = string.Empty;
    public string SourceEntityId { get; init; } = string.Empty;
    public string TargetEntityId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;  // e.g., "One-to-Many", "Many-to-Many"
    public string? Cardinality { get; init; }  // e.g., "1..*, 0..1"
    public bool IsBidirectional { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Enumeration type in the data model (semantic model).
/// </summary>
public sealed class SemanticDataEnumeration
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<SemanticDataEnumerationValue> Values { get; init; } = [];
    public List<string> UsedByEntityIds { get; init; } = [];
}

/// <summary>
/// Value in an enumeration (semantic model).
/// </summary>
public sealed class SemanticDataEnumerationValue
{
    public string Name { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Value Object in the data model (semantic model).
/// </summary>
public sealed class SemanticDataValueObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> PropertyNames { get; init; } = [];
    public List<string> UsedByEntityIds { get; init; } = [];
}

/// <summary>
/// Aggregate Root entity (semantic model).
/// </summary>
public sealed class SemanticDataAggregateRoot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> MemberEntityIds { get; init; } = [];
    public string? BoundaryId { get; init; }
    public List<string> AggregateInvariants { get; init; } = [];
}

// ── Internal Helpers ──────────────────────────────────────────────────────

internal static class DataModelHelpers
{
    public static int CalculateMaxDepth(this DataModelSemanticModel model)
    {
        // Simplified version - would traverse relationship graph in real implementation
        return model.Relationships.Count == 0 ? 0 : 1;
    }

    public static int CalculateCircularRelationships(this DataModelSemanticModel model)
    {
        // Simplified version - would detect cycles in relationship graph
        return 0;
    }

    public static int CalculateOrphanEntities(this DataModelSemanticModel model)
    {
        var usedEntities = new HashSet<string>();
        foreach (var rel in model.Relationships)
        {
            usedEntities.Add(rel.SourceEntityId);
            usedEntities.Add(rel.TargetEntityId);
        }
        foreach (var vo in model.ValueObjects)
        {
            usedEntities.UnionWith(vo.UsedByEntityIds);
        }
        return model.Entities.Count(e => !usedEntities.Contains(e.Id));
    }
}
