using System.Text.Json.Serialization;
using BirkNext.DatabaseArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.DataModelEvidence;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Shared Data Model Evidence. Source Analysis extracts persistence models once per immutable snapshot (EF Core model + fluent/annotations,
// EF migrations as declarations, static SQL DDL); contract data models come from Contract Evidence and database resources from
// Infrastructure Evidence — never re-parsed. Identities are scoped (store / schema / table), so same-named models in different services
// never collide. Entity ≠ table until mapped; entity ≠ DTO ≠ contract type; mapping confidence is explicit (ToTable / [Table] beats naming
// convention; a name match never confirms); nullability layers stay apart; a migration file is declared, never applied; no row data is read.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataMappingConfidence { Explicit, Strong, Candidate, Ambiguous, Unresolved }

public sealed record DataModelProviderDescriptor(string Key, string Name, string Support, string Proves, string CannotProve);

public static class DataModelProviders
{
    public static IReadOnlyList<DataModelProviderDescriptor> All { get; } =
    [
        new("ef-core-model", "EF Core model (syntax)", "Supported",
            "DbContexts, entities (DbSet / Entity<T>), explicit table names (ToTable / [Table]), keys, required/max length, foreign keys, indexes.",
            "No semantic compilation; owned types, many-to-many join entities and table splitting are reported unresolved, never invented."),
        new("ef-migrations", "EF migrations (declared)", "Supported",
            "Schema operations declared in Up() of each migration, owned by its DbContext.", "A migration file never proves it was applied to any database."),
        new("sql-ddl", "SQL DDL (static)", "Partial", "CREATE/ALTER TABLE and indexes from SQL files, read as tokens.", "Procedures, dynamic SQL and dialect extensions are not assessed; nothing is executed."),
        new("contract-models", "Contract data models (shared Contract Evidence)", "Reused", "OpenAPI/JSON Schema types and XSD elements, reused from Contract Evidence.", "A contract type is not a persistence entity; a field name match is a candidate only."),
        new("infrastructure-datastores", "Database resources (shared Infrastructure Evidence)", "Reused", "Declared database servers/databases in Terraform, reused from Infrastructure Evidence.", "A declared server does not expose its schema."),
        new("runtime-db-metadata", "Runtime database metadata", "Not supported", "—", "No live target database schema is read; no row data is ever read."),
    ];
}

public sealed record DataProvenance(Guid SnapshotId, string Project, string File, int Line, string Extractor, string State);

/// <summary>One field with its separate nullability layers: language (CLR nullable), ORM requiredness and database nullability.</summary>
public sealed record DataField(string Column, string? Property, string? ClrType, string? StoreType, bool? LanguageNullable, bool? OrmRequired, bool? DbNullable, int? MaxLength,
    bool IsKey, bool IsForeignKey, DataProvenance? Provenance)
{
    /// <summary>The layers disagree (e.g. non-nullable CLR type on a nullable column) — needs review, not a defect.</summary>
    public bool NullabilityDiffers => LanguageNullable is { } l && DbNullable is { } d && l != d;
}

/// <summary>A persistence entity and the table it maps to (scoped id), with how strongly source proves the mapping.</summary>
public sealed record DataEntity(string Id, string Store, string Project, string? Entity, string Table, string? Schema, DataMappingConfidence Mapping, string MappingBasis,
    IReadOnlyList<DataField> Fields, IReadOnlyList<string> References, DataProvenance? Provenance);

public sealed record DataLineageLink(string ContractId, string ContractField, string EntityId, string Column, DataMappingConfidence Confidence, string Basis);
public sealed record DataStoreCorrelation(string StoreId, string Store, string? Provider, string ResourceId, string ResourceType, DataMappingConfidence Confidence, string Basis);
public sealed record DataMigration(string Store, string File, string State = "Declared in source — not proven applied");
public sealed record DataModelChange(string Kind, string Object, string Detail, string Classification);

public static class DataModelQueries
{
    private static DataProvenance? P(DatabaseFact f, Guid snapshotId) =>
        f.Evidence.FirstOrDefault() is { } e ? new(snapshotId, e.Project, e.Path, e.Line, e.Extractor, e.State.ToString()) : null;

    /// <summary>Entities/tables of a snapshot's stored database architecture, normalized (scoped id = store + table id).</summary>
    public static IReadOnlyList<DataEntity> GetEntities(DatabaseArchitectureSnapshot? snapshot) =>
        snapshot is null ? [] : [.. snapshot.Databases.SelectMany(db => db.Schemas.SelectMany(s => s.Tables.Select(t =>
        {
            var (mapping, basis) = t.EntityTypeName is null ? (DataMappingConfidence.Strong, "Table declared in DDL or migration (no entity)")
                : t.PhysicalName is not null && t.EvidenceState == DatabaseEvidenceState.Confirmed ? (DataMappingConfidence.Explicit, "Explicit table name (ToTable / [Table])")
                : t.EvidenceState == DatabaseEvidenceState.Unresolved ? (DataMappingConfidence.Unresolved, "Mapping not resolved from source")
                : (DataMappingConfidence.Strong, "EF naming convention (DbSet property) — not an explicit table name");
            return new DataEntity($"{db.Id}/{t.Id}", db.DbContext ?? db.LogicalName, db.SourceProject, t.EntityTypeName, t.PhysicalName ?? t.LogicalName, t.SchemaName ?? s.Name, mapping, basis,
                [.. t.Columns.Select(c => new DataField(c.Name, c.PropertyName, c.ClrType, c.StoreType, c.ClrType is null ? null : c.ClrType.EndsWith('?'),
                    c.Assertions.Any(a => a.Fact.Contains("Required", StringComparison.OrdinalIgnoreCase)) ? true : null, c.Nullable, c.MaxLength, c.IsPrimaryKey, c.IsForeignKey, P(c, snapshot.SourceSnapshotId)))],
                [.. t.ForeignKeys.Concat(t.Relationships).Select(r => r.ToTable).Distinct(StringComparer.Ordinal)], P(t, snapshot.SourceSnapshotId));
        })))];

    /// <summary>Declared migrations (files), per store. Declared ≠ applied.</summary>
    public static IReadOnlyList<DataMigration> Migrations(DatabaseArchitectureSnapshot? snapshot) =>
        snapshot is null ? [] : [.. snapshot.Databases.SelectMany(db => db.Schemas.SelectMany(s => s.Tables).SelectMany(t => t.Evidence.Concat(t.Columns.SelectMany(c => c.Evidence)))
            .Where(e => e.State == DatabaseEvidenceState.ObservedFromMigration).Select(e => new DataMigration(db.DbContext ?? db.LogicalName, e.Path)))
            .DistinctBy(m => $"{m.Store}|{m.File}")];

    /// <summary>Source model changes classified; an entity-model change with no migration evidence for that table is a potential migration
    /// gap (needs review) — never automatically a defect.</summary>
    public static IReadOnlyList<DataModelChange> Changes(DatabaseArchitectureSnapshot previous, DatabaseArchitectureSnapshot current)
    {
        var migratedTables = current.Databases.SelectMany(db => db.Schemas.SelectMany(s => s.Tables))
            .Where(t => t.Evidence.Concat(t.Columns.SelectMany(c => c.Evidence)).Any(e => e.State == DatabaseEvidenceState.ObservedFromMigration))
            .Select(t => t.PhysicalName ?? t.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newMigrations = Migrations(current).Select(m => m.File).Except(Migrations(previous).Select(m => m.File), StringComparer.OrdinalIgnoreCase).Any();
        return [.. DatabaseSchemaComparison.Compare(previous, current).Select(c =>
        {
            var table = c.Object.Split('.')[0];
            var classification = c.Kind.Contains("Removed", StringComparison.Ordinal) ? "Potential breaking data change — review"
                : c.Kind.Contains("Changed", StringComparison.Ordinal) ? "Review required" : "Additive change";
            if (!newMigrations && !migratedTables.Contains(table) && c.Kind is not ("Added index" or "Removed index"))
                classification += " · Potential migration gap (no migration evidence for this change)";
            return new DataModelChange(c.Kind, c.Object, c.Detail, classification);
        })];
    }

    /// <summary>Contract field ↔ entity column candidates by exact name. A name match is a Candidate — never confirmed without explicit
    /// mapping evidence.</summary>
    public static IReadOnlyList<DataLineageLink> ContractLineage(IEnumerable<SourceContract> contracts, IReadOnlyList<DataEntity> entities)
    {
        var columns = entities.SelectMany(e => e.Fields.Select(f => (Entity: e, Field: f))).ToList();
        var links = new List<DataLineageLink>();
        foreach (var contract in contracts)
        {
            var names = contract.Types.SelectMany(t => t.Fields.Select(f => f.Name))
                .Concat((contract.XmlSchema?.Elements ?? []).Select(e => e.Path.Split('/').Last())).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                foreach (var (entity, field) in columns.Where(x => string.Equals(x.Field.Property ?? x.Field.Column, name, StringComparison.OrdinalIgnoreCase)).Take(3))
                    links.Add(new(contract.Id, name, entity.Id, field.Column, DataMappingConfidence.Candidate, "Same field name — candidate lineage; no explicit mapping in source"));
        }
        return links;
    }

    /// <summary>Database stores ↔ declared database resources by engine: a candidate correlation; the resource never exposes a schema.</summary>
    public static IReadOnlyList<DataStoreCorrelation> InfrastructureCorrelation(DatabaseArchitectureSnapshot? snapshot, IEnumerable<InfrastructureResource> resources)
    {
        var databases = resources.Where(r => r.Category == InfrastructureCategory.Database).ToList();
        static string Engine(string? s) => (s ?? "").ToLowerInvariant() switch
        {
            var x when x.Contains("mssql") || x.Contains("sqlserver") || x.Contains("sql_") => "sqlserver",
            var x when x.Contains("postgres") || x.Contains("npgsql") => "postgresql",
            var x => x,
        };
        return snapshot is null ? [] : [.. snapshot.Databases.SelectMany(db =>
        {
            var matches = databases.Where(r => Engine(r.ResourceType) == Engine(db.Provider)).ToList();
            return matches.Select(r => new DataStoreCorrelation(db.Id, db.DbContext ?? db.LogicalName, db.Provider, r.Id, r.ResourceType,
                matches.Count == 1 ? DataMappingConfidence.Candidate : DataMappingConfidence.Ambiguous,
                matches.Count == 1 ? "Same engine; the only declared database resource of that engine" : $"{matches.Count} declared resources of the same engine — ambiguous"));
        })];
    }

    /// <summary>Column constraints Test Data and tests can use (required, max length, key) — structure only, never values.</summary>
    public static IReadOnlyList<string> Constraints(DataEntity entity) =>
        [.. entity.Fields.SelectMany(f => new[]
        {
            f.DbNullable == false || f.OrmRequired == true ? $"{f.Column}: required" : null,
            f.MaxLength is { } max ? $"{f.Column}: max length {max}" : null,
            f.IsKey ? $"{f.Column}: key" : null,
        }.OfType<string>())];

    /// <summary>Tests that name an entity or its table (candidate links — a name is never proof of coverage).</summary>
    public static IReadOnlyList<(string Test, string Entity)> TestsFor(IEnumerable<DataEntity> entities, IEnumerable<string> testNames) =>
        [.. entities.Where(e => (e.Entity ?? e.Table).Length >= 4).SelectMany(e => testNames.Where(t => t.Contains(e.Entity ?? e.Table, StringComparison.OrdinalIgnoreCase)).Select(t => (t, e.Entity ?? e.Table)))];

    /// <summary>A data dictionary from normalized evidence: stores, entities/tables with mapping confidence, columns with layers, migrations.</summary>
    public static string Dictionary(DatabaseArchitectureSnapshot snapshot)
    {
        var lines = new List<string> { "## Data dictionary", "", "Source-derived. It does not prove the deployed database has the same schema; no data was read.", "" };
        foreach (var g in GetEntities(snapshot).GroupBy(e => e.Store))
        {
            lines.Add($"### {g.Key}");
            foreach (var e in g)
            {
                lines.Add($"- **{e.Table}**{(e.Entity is { } en && en != e.Table ? $" (entity {en})" : "")} — {e.Mapping}: {e.MappingBasis}");
                foreach (var f in e.Fields.Take(40))
                    lines.Add($"  - {f.Column}: {f.StoreType ?? f.ClrType ?? "type not stated"}{(f.IsKey ? " · key" : "")}{(f.DbNullable == false ? " · not null" : f.DbNullable == true ? " · nullable" : "")}{(f.MaxLength is { } m ? $" · max {m}" : "")}");
            }
            lines.Add("");
        }
        var migrations = Migrations(snapshot);
        if (migrations.Count > 0) lines.Add($"Migrations declared in source: {migrations.Count} file(s) — not proven applied.");
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    /// <summary>Data implementation evidence for a requirement mentioning an entity/table or field.</summary>
    public static IReadOnlyList<string> ForRequirement(IReadOnlyList<DataEntity> entities, string requirement)
    {
        var text = requirement.ToLowerInvariant();
        return [.. entities.Where(e => (e.Entity ?? e.Table).Length >= 4 && text.Contains((e.Entity ?? e.Table).ToLowerInvariant()))
            .Select(e => $"Implemented by {e.Table} in {e.Store} ({e.Mapping}) — source declaration, runtime database not assessed").Take(10)];
    }
}
