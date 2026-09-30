using System.Text.Json.Serialization;

namespace BirkNext.DatabaseArchitecture;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseEvidenceState { Confirmed, Inferred, ObservedFromMigration, ObservedFromDDL, Unresolved }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseAnalysisStatus { Complete, Partial, NeedsReview, Unsupported }
public sealed record DatabaseEvidence(Guid SourceSnapshotId, string Fingerprint, string Project, string Path,
    string Symbol, int Line, int EndLine, string Extractor, string Type, string Explanation, DatabaseEvidenceState State);
public abstract class DatabaseFact
{
    public List<DatabaseAssertion> Assertions { get; set; } = [];
    public DatabaseEvidenceState EvidenceState { get; set; } = DatabaseEvidenceState.Unresolved;
    public string Confidence { get; set; } = "Not resolved";
    public List<DatabaseEvidence> Evidence { get; set; } = [];
}
public sealed record DatabaseAssertion(string Fact, string Value, DatabaseEvidence Evidence);
public sealed class DatabaseArchitectureSnapshot
{
    public Guid SnapshotId { get; set; } = Guid.NewGuid();
    public Guid SourceSnapshotId { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public DateTimeOffset ExtractedAt { get; set; }
    public int AnalyzerVersion { get; set; } = 1;
    public Dictionary<string, string> ExtractorVersions { get; set; } = [];
    public List<string> TechnologiesDetected { get; set; } = [];
    public DatabaseAnalysisStatus Status { get; set; }
    public List<DatabaseModel> Databases { get; set; } = [];
    public List<DatabaseEvidence> UnresolvedEvidence { get; set; } = [];
    public List<DatabaseConflict> Conflicts { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public const string SourceLimitation = "This diagram is derived from the selected source snapshot. It does not prove that the deployed database has the same schema.";
}
public sealed class DatabaseModel : DatabaseFact
{
    public string Id { get; set; } = "";
    public string LogicalName { get; set; } = "";
    public string? PhysicalName { get; set; }
    public string? ConfiguredDatabaseName { get; set; }
    public string? Provider { get; set; }
    public string? ConnectionReference { get; set; }
    public string? DbContext { get; set; }
    public string SourceProject { get; set; } = "";
    public List<SchemaModel> Schemas { get; set; } = [];
}
public sealed class SchemaModel : DatabaseFact
{
    public string? Name { get; set; }
    public List<TableModel> Tables { get; set; } = [];
}
public sealed class TableModel : DatabaseFact
{
    public string Id { get; set; } = "";
    public string LogicalName { get; set; } = "";
    public string? PhysicalName { get; set; }
    public string? SchemaName { get; set; }
    public string? EntityTypeName { get; set; }
    public string SourceProject { get; set; } = "";
    public List<ColumnModel> Columns { get; set; } = [];
    public KeyModel? PrimaryKey { get; set; }
    public List<RelationshipModel> ForeignKeys { get; set; } = [];
    public List<IndexModel> Indexes { get; set; } = [];
    public List<ConstraintModel> Constraints { get; set; } = [];
    public List<RelationshipModel> Relationships { get; set; } = [];
}
public sealed class ColumnModel : DatabaseFact
{
    public string Name { get; set; } = "";
    public string? PropertyName { get; set; }
    public string? StoreType { get; set; }
    public string? ClrType { get; set; }
    public bool? Nullable { get; set; }
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public bool? IsIdentity { get; set; }
    public bool? IsGeneratedOnAdd { get; set; }
    public bool? IsComputed { get; set; }
    // Reserved for safely classified future evidence. V1 extractors leave these null: source values can contain secrets.
    public string? DefaultValue { get; set; }
    public string? DefaultExpression { get; set; }
}
public sealed class KeyModel : DatabaseFact
{
    public string? Name { get; set; }
    public List<string> Columns { get; set; } = [];
}
public sealed class RelationshipModel : DatabaseFact
{
    public string? Name { get; set; }
    public string Id { get; set; } = "";
    public string FromTable { get; set; } = "";
    public string ToTable { get; set; } = "";
    public string? ToSchema { get; set; }
    public List<string> FromColumns { get; set; } = [];
    public List<string> ToColumns { get; set; } = [];
    public string Cardinality { get; set; } = "Unknown";
    public string? DeleteBehavior { get; set; }
    public bool? IsRequired { get; set; }
}
public sealed class IndexModel : DatabaseFact
{
    public string? Name { get; set; }
    public List<string> Columns { get; set; } = [];
    public bool? Unique { get; set; }
    public string? Filter { get; set; }
}
public sealed class ConstraintModel : DatabaseFact
{
    public string? Name { get; set; }
    public string Type { get; set; } = "";
    public string? Expression { get; set; }
    public List<string> Columns { get; set; } = [];
}
public sealed record DatabaseConflict(string TableId, string? Column, string Fact, string First, string Second, List<DatabaseEvidence> Evidence);
public sealed record DatabaseSchemaChange(string Kind, string Object, string Detail);
public static class DatabaseSchemaComparison
{
    public static List<DatabaseSchemaChange> Compare(DatabaseArchitectureSnapshot previous, DatabaseArchitectureSnapshot current)
    {
        var changes = new List<DatabaseSchemaChange>();
        var a = previous.Databases.SelectMany(d => d.Schemas.SelectMany(s => s.Tables)).ToDictionary(t => t.Id);
        var b = current.Databases.SelectMany(d => d.Schemas.SelectMany(s => s.Tables)).ToDictionary(t => t.Id);
        foreach (var id in a.Keys.Union(b.Keys).Order())
        {
            if (!a.TryGetValue(id, out var old)) { changes.Add(new("Added table", b[id].LogicalName, "Source declaration added")); continue; }
            if (!b.TryGetValue(id, out var next)) { changes.Add(new("Removed table", old.LogicalName, "Source declaration removed")); continue; }
            var before = changes.Count;
            foreach (var name in old.Columns.Select(c => c.Name).Union(next.Columns.Select(c => c.Name)))
            {
                var x = old.Columns.FirstOrDefault(c => c.Name == name); var y = next.Columns.FirstOrDefault(c => c.Name == name);
                if (x is null || y is null) changes.Add(new(x is null ? "Added column" : "Removed column", next.LogicalName + "." + name, "Source declaration changed"));
                else if ((x.StoreType, x.ClrType, x.Nullable) != (y.StoreType, y.ClrType, y.Nullable)) changes.Add(new("Changed column", next.LogicalName + "." + name, "Type or nullability changed in source"));
            }
            string Fk(RelationshipModel r) => $"{r.FromTable}:{string.Join(',', r.FromColumns)}:{r.ToTable}:{string.Join(',', r.ToColumns)}:{r.DeleteBehavior}";
            foreach (var relationship in old.ForeignKeys) if (next.ForeignKeys.FirstOrDefault(r => r.Id == relationship.Id) is { } newer && (relationship.Cardinality, relationship.IsRequired, relationship.DeleteBehavior) != (newer.Cardinality, newer.IsRequired, newer.DeleteBehavior)) changes.Add(new("Changed FK", next.LogicalName, "Cardinality, required state or delete behavior changed in source"));
            foreach (var fk in old.ForeignKeys.Select(Fk).Except(next.ForeignKeys.Select(Fk))) changes.Add(new("Removed FK", next.LogicalName, fk));
            foreach (var fk in next.ForeignKeys.Select(Fk).Except(old.ForeignKeys.Select(Fk))) changes.Add(new("Added FK", next.LogicalName, fk));
            string Ix(IndexModel i) => $"{i.Name}:{string.Join(',', i.Columns)}:{i.Unique}";
            foreach (var ix in old.Indexes.Select(Ix).Except(next.Indexes.Select(Ix))) changes.Add(new("Removed index", next.LogicalName, ix));
            foreach (var ix in next.Indexes.Select(Ix).Except(old.Indexes.Select(Ix))) changes.Add(new("Added index", next.LogicalName, ix));
            if (!(old.PrimaryKey?.Columns ?? []).SequenceEqual(next.PrimaryKey?.Columns ?? [])) changes.Add(new("Changed key", next.LogicalName, "Source primary key column set changed"));
            if (changes.Count > before) changes.Add(new("Changed table", next.LogicalName, "Source structure changed"));
        }
        return changes;
    }
}
