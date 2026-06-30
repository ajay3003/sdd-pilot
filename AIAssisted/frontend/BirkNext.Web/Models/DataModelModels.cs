namespace BirkNext.Web.Models;

public sealed class DataModelDocument
{
    public string  Title           { get; init; } = "Data Model";
    public string? Overview        { get; init; }
    public string? MigrationNotes  { get; init; }
    public string? RetentionPolicy { get; init; }

    public List<DataEntity>       Entities      { get; init; } = [];
    public List<DataRelationship> Relationships { get; init; } = [];
    public List<DataIndex>        Indexes       { get; init; } = [];
    public List<DataConstraint>   Constraints   { get; init; } = [];
    public List<DataEnum>         Enums         { get; init; } = [];
    public List<DataModelFinding> Findings      { get; init; } = [];

    public int EntityCount       => Entities.Count;
    public int ColumnCount       => Entities.Sum(e => e.Columns.Count);
    public int RelationshipCount => Relationships.Count;
    public int IndexCount        => Indexes.Count;
    public int FindingCount      => Findings.Count;
    public int ErrorCount        => Findings.Count(f => f.Severity is DataModelSeverity.Error or DataModelSeverity.Critical);
}

public sealed class DataEntity
{
    public string  Name        { get; init; } = string.Empty;
    public bool    IsTable     { get; init; }
    public string? Description { get; init; }

    public List<DataColumn> Columns         { get; init; } = [];
    public List<string>     TraceabilityIds { get; init; } = [];
}

public sealed class DataColumn
{
    public string  Name         { get; init; } = string.Empty;
    public string? Type         { get; init; }
    public bool?   Nullable     { get; init; }
    public bool    IsPrimaryKey { get; init; }
    public bool    IsForeignKey { get; init; }
    public bool    IsUnique     { get; init; }
    public string? Description  { get; init; }
}

public sealed class DataRelationship
{
    public string  Source           { get; init; } = string.Empty;
    public string  Target           { get; init; } = string.Empty;
    public string? RelationshipType { get; init; }

    public string SourceEntity => Source.Contains('.') ? Source.Split('.')[0] : Source;
    public string SourceColumn => Source.Contains('.') ? Source.Split('.')[1] : string.Empty;
    public string TargetEntity => Target.Contains('.') ? Target.Split('.')[0] : Target;
    public string TargetColumn => Target.Contains('.') ? Target.Split('.')[1] : string.Empty;
}

public sealed class DataIndex
{
    public string       Name       { get; init; } = string.Empty;
    public string       EntityName { get; init; } = string.Empty;
    public List<string> Columns    { get; init; } = [];
    public bool         IsUnique   { get; init; }
}

public sealed class DataConstraint
{
    public string  Name           { get; init; } = string.Empty;
    public string  EntityName     { get; init; } = string.Empty;
    public string  ConstraintType { get; init; } = string.Empty;
    public string? Definition     { get; init; }
}

public sealed class DataEnum
{
    public string       Name        { get; init; } = string.Empty;
    public List<string> Values      { get; init; } = [];
    public string?      Description { get; init; }
}

public sealed class DataModelFinding
{
    public DataModelSeverity Severity    { get; init; }
    public string            Category   { get; init; } = string.Empty;
    public string            Description { get; init; } = string.Empty;
    public string?           EntityName { get; init; }
}

public enum DataModelSeverity { Info, Warning, Error, Critical }
