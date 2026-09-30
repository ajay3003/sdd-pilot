using BirkNext.DatabaseArchitecture;
using BirkNext.Api.Services.Integrations.SourceEvidence;

namespace BirkNext.Api.Services.DatabaseArchitecture;

public static class DatabaseArchitectureAnalyzer
{
    public const int Version = 2;
    public static async Task<DatabaseArchitectureSnapshot> AnalyzeAsync(Guid sourceId, IqrSourceArchiveReader.Workspace workspace, DateTimeOffset at, CancellationToken ct = default)
    {
        var input = new DatabaseExtractionInput(sourceId, workspace);
        var snapshot = new DatabaseArchitectureSnapshot { SourceSnapshotId = sourceId, SourceFingerprint = workspace.Archive.Sha256, ExtractedAt = at, AnalyzerVersion = Version };
        IDatabaseSchemaExtractor[] extractors = [new EfCoreSchemaExtractor(), new MigrationSchemaExtractor(), new SqlDdlSchemaExtractor()];
        foreach (var extractor in extractors)
        {
            snapshot.ExtractorVersions[extractor.Name] = extractor.Version;
            if (!extractor.CanAnalyze(input)) continue;
            var result = await extractor.AnalyzeAsync(input, ct);
            snapshot.Databases.AddRange(result.Databases); snapshot.UnresolvedEvidence.AddRange(result.Unresolved); snapshot.Diagnostics.AddRange(result.Diagnostics);
        }
        Merge(snapshot);
        foreach (var db in snapshot.Databases)
        {
            if ((db.DbContext is not null || db.Evidence.Any(e => e.Extractor == "EF migration syntax")) && !snapshot.TechnologiesDetected.Contains("EF Core")) snapshot.TechnologiesDetected.Add("EF Core");
            if (db.Provider is not null && !snapshot.TechnologiesDetected.Contains(db.Provider)) snapshot.TechnologiesDetected.Add(db.Provider);
            var tables = db.Schemas.SelectMany(s => s.Tables).ToList();
            // Assign all IDs before resolving edges. Entity candidates and physical assertions have distinct identities.
            foreach (var t in tables)
            {
                t.Id = Extraction.Id(db.Id + ":" + t.SchemaName + ":" + (t.PhysicalName ?? t.LogicalName) + (t.PhysicalName is null ? ":entity:" + t.EntityTypeName : ""));
                if (t.PrimaryKey is { } key) key.Columns = key.Columns.Select(n => t.Columns.FirstOrDefault(c => c.PropertyName == n)?.Name ?? n).ToList();
                t.Assertions = t.Assertions.Select(a => a.Fact == "PrimaryKey" ? a with { Value = string.Join(',', a.Value.Split(',').Select(n => t.Columns.FirstOrDefault(c => c.PropertyName == n)?.Name ?? n)) } : a).ToList();
            }
            foreach (var table in tables)
            {
                string StoreColumn(string name) => table.Columns.FirstOrDefault(c => c.PropertyName == name)?.Name ?? name;
                if (table.PrimaryKey is { } primaryKey) primaryKey.Columns = primaryKey.Columns.Select(StoreColumn).ToList();
                foreach (var index in table.Indexes) index.Columns = index.Columns.Select(StoreColumn).ToList();
                foreach (var assertions in table.Assertions.GroupBy(a => a.Fact))
                {
                    var explicitAssertions = assertions.Where(a => a.Evidence.State != DatabaseEvidenceState.Inferred).ToList(); var values = explicitAssertions.GroupBy(a => a.Value).ToList();
                    if (values.Count > 1) snapshot.Conflicts.Add(new DatabaseConflict(table.Id, null, assertions.Key, values[0].Key, string.Join(" / ", values.Skip(1).Select(g => g.Key)), explicitAssertions.Select(a => a.Evidence).ToList()));
                }
                foreach (var column in table.Columns) foreach (var assertions in column.Assertions.GroupBy(a => a.Fact))
                {
                    var explicitAssertions = assertions.Where(a => a.Evidence.State != DatabaseEvidenceState.Inferred).ToList();
                    var values = explicitAssertions.GroupBy(a => a.Value).ToList();
                    if (values.Count > 1) snapshot.Conflicts.Add(new DatabaseConflict(table.Id, column.Name, assertions.Key, values[0].Key, string.Join(" / ", values.Skip(1).Select(g => g.Key)), explicitAssertions.Select(a => a.Evidence).ToList()));
                }
                foreach (var fk in table.ForeignKeys)
                {
                    fk.FromTable = table.Id;
                    fk.FromColumns = fk.FromColumns.Select(StoreColumn).ToList();
                    var targets = tables.Where(t => (t.PhysicalName == fk.ToTable || t.EntityTypeName == fk.ToTable || t.LogicalName == fk.ToTable) && (fk.ToSchema is null || fk.ToSchema == t.SchemaName)).ToList();
                    if (targets.Count == 1) { fk.ToTable = targets[0].Id; if (fk.ToColumns.Count == 0 && targets[0].PrimaryKey is { EvidenceState: not DatabaseEvidenceState.Inferred } key) fk.ToColumns = key.Columns.ToList(); }
                    else { snapshot.UnresolvedEvidence.AddRange(fk.Evidence.Select(e => e with { State = DatabaseEvidenceState.Unresolved, Explanation = "Relationship target absent or ambiguous in candidate database" })); fk.EvidenceState = DatabaseEvidenceState.Unresolved; }
                    fk.Id = Extraction.Id(fk.FromTable + string.Join(',', fk.FromColumns) + fk.ToTable);
                    if (table.PrimaryKey?.Columns.SequenceEqual(fk.FromColumns) == true || table.Indexes.Any(i => i.Unique == true && i.Columns.SequenceEqual(fk.FromColumns)) || table.Constraints.Any(c => c.Type is "Unique" or "Alternate key" && c.Columns.SequenceEqual(fk.FromColumns))) fk.Cardinality = "1:1";
                }
                table.ForeignKeys = table.ForeignKeys.GroupBy(f => f.Id).Select(g => { var first = g.First(); first.Evidence = g.SelectMany(x => x.Evidence).Distinct().ToList(); return first; }).ToList(); table.Relationships = table.ForeignKeys.ToList();
            }
        }
        if (workspace.Files.Any(f => f.Path.EndsWith(".sql"))) snapshot.TechnologiesDetected.Add("SQL DDL");
        snapshot.Diagnostics.Add(DatabaseArchitectureSnapshot.SourceLimitation);
        snapshot.Diagnostics.Add("No live database assessed. Physical database names and schema defaults remain unknown without explicit evidence. Default values and SQL expressions are excluded for secret safety.");
        snapshot.Status = snapshot.Databases.Count == 0 ? DatabaseAnalysisStatus.Unsupported : snapshot.Conflicts.Count > 0 || snapshot.Diagnostics.Any(d => d.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)) ? DatabaseAnalysisStatus.NeedsReview : snapshot.UnresolvedEvidence.Count > 0 ? DatabaseAnalysisStatus.Partial : DatabaseAnalysisStatus.Complete;
        return snapshot;
    }
    private static void Merge(DatabaseArchitectureSnapshot snapshot)
    {
        // A migration source group only attaches to a context when exactly one context exists in that project.
        // DDL groups and ambiguous multi-context projects remain independent.
        foreach (var migration in snapshot.Databases.Where(d => d.Evidence.Any(e => e.Extractor == "EF migration syntax") && !d.Evidence.Any(e => e.Extractor == "EF Core syntax")).ToList())
        {
            var contexts = snapshot.Databases.Where(d => d.DbContext is not null && d.Evidence.Any(e => e.Extractor == "EF Core syntax") && d.SourceProject == migration.SourceProject && (migration.DbContext is null || d.DbContext == migration.DbContext)).ToList();
            if (contexts.Count != 1) { snapshot.Diagnostics.Add("Migration-to-context ownership ambiguous; migration candidate kept separate: " + migration.SourceProject); continue; }
            var db = contexts.Single(); db.Evidence.AddRange(migration.Evidence);
            foreach (var schema in migration.Schemas) foreach (var observed in schema.Tables)
            {
                var matches = db.Schemas.SelectMany(s => s.Tables).Where(t => t.PhysicalName == observed.PhysicalName && t.SchemaName == observed.SchemaName).ToList();
                if (matches.Count != 1) { var group = db.Schemas.FirstOrDefault(s => s.Name == schema.Name); if (group is null) { group = new SchemaModel { Name = schema.Name }; db.Schemas.Add(group); } group.Tables.Add(observed); continue; }
                var table = matches.Single(); table.Evidence.AddRange(observed.Evidence); table.Assertions.AddRange(observed.Assertions);
                foreach (var incoming in observed.Columns)
                {
                    var column = table.Columns.FirstOrDefault(c => c.Name == incoming.Name);
                    if (column is null) { table.Columns.Add(incoming); continue; }
                    if (!column.Assertions.Any(a => a.Fact == "Nullable" && a.Evidence.State != DatabaseEvidenceState.Inferred) && incoming.Nullable is not null) column.Nullable = incoming.Nullable;
                    column.Assertions.AddRange(incoming.Assertions);
                    foreach (var evidence in incoming.Evidence) Extraction.Add(column, evidence);
                    column.StoreType ??= incoming.StoreType; column.Nullable ??= incoming.Nullable; column.MaxLength ??= incoming.MaxLength;
                    column.IsPrimaryKey |= incoming.IsPrimaryKey; column.IsForeignKey |= incoming.IsForeignKey;
                }
                table.PrimaryKey ??= observed.PrimaryKey; table.ForeignKeys.AddRange(observed.ForeignKeys); table.Indexes.AddRange(observed.Indexes); table.Constraints.AddRange(observed.Constraints);
            }
            snapshot.Databases.Remove(migration);
        }
    }
}
