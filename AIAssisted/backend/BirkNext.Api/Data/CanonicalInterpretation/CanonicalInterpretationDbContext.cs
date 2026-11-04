using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BirkNext.Api.Data.CanonicalInterpretation;

/// <summary>
/// Canonical interpretation persistence: canonical entities, source occurrences, aliases, typed relations, shared review decisions and immutable
/// interpretation snapshots. Like Delivery Reporting it is a separate context bound to its own PostgreSQL schema (<see cref="Schema"/>) with its
/// own migration history table, so its migrations never touch the public schema or another domain's tables. Source payloads are never copied here:
/// occurrences point at source snapshots by id.
/// </summary>
public sealed class CanonicalInterpretationDbContext(DbContextOptions<CanonicalInterpretationDbContext> options) : DbContext(options)
{
    public const string Schema = "canonical_interpretation";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<CanonicalEntityRecord> Entities => Set<CanonicalEntityRecord>();
    public DbSet<CanonicalOccurrenceRecord> Occurrences => Set<CanonicalOccurrenceRecord>();
    public DbSet<CanonicalAliasRecord> Aliases => Set<CanonicalAliasRecord>();
    public DbSet<CanonicalRelationRecord> Relations => Set<CanonicalRelationRecord>();
    public DbSet<ReviewDecisionRecord> Decisions => Set<ReviewDecisionRecord>();
    public DbSet<InterpretationSnapshotRecord> Snapshots => Set<InterpretationSnapshotRecord>();
    public DbSet<InterpretationSnapshotEntityRecord> SnapshotEntities => Set<InterpretationSnapshotEntityRecord>();
    public DbSet<InterpretationSnapshotRelationRecord> SnapshotRelations => Set<InterpretationSnapshotRelationRecord>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<CanonicalEntityRecord>(e =>
        {
            e.ToTable("entity");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
            e.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(40).IsRequired();
            e.Property(x => x.IdentityKey).HasColumnName("identity_key").HasMaxLength(1000).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(500);
            e.Property(x => x.SourceIdentity).HasColumnName("source_identity").HasMaxLength(1000);
            e.Property(x => x.ResolutionMethod).HasColumnName("resolution_method").HasMaxLength(200);
            e.Property(x => x.Version).HasColumnName("version").HasMaxLength(100);
            e.Property(x => x.AttributesJson).HasColumnName("attributes").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.LastInterpretationId).HasColumnName("last_interpretation_id");
            e.HasIndex(x => new { x.Scope, x.EntityType, x.IdentityKey }).IsUnique();
        });

        modelBuilder.Entity<CanonicalOccurrenceRecord>(e =>
        {
            e.ToTable("occurrence");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id").HasMaxLength(64).IsRequired();
            e.Property(x => x.OccurrenceKey).HasColumnName("occurrence_key").HasMaxLength(1000).IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(60);
            e.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(1000);
            e.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
            e.HasOne<CanonicalEntityRecord>().WithMany().HasForeignKey(x => x.EntityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EntityId, x.SourceSnapshotId, x.OccurrenceKey }).IsUnique();
            e.HasIndex(x => new { x.Scope, x.SourceSnapshotId });
        });

        modelBuilder.Entity<CanonicalAliasRecord>(e =>
        {
            e.ToTable("alias");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Alias).HasColumnName("alias").HasMaxLength(300).IsRequired();
            e.Property(x => x.NormalizedAlias).HasColumnName("normalized_alias").HasMaxLength(300).IsRequired();
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(20);
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(500);
            e.Property(x => x.RuleId).HasColumnName("rule_id").HasMaxLength(40);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<CanonicalEntityRecord>().WithMany().HasForeignKey(x => x.EntityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Scope, x.EntityId, x.NormalizedAlias }).IsUnique();
            e.HasIndex(x => new { x.Scope, x.NormalizedAlias });
        });

        modelBuilder.Entity<CanonicalRelationRecord>(e =>
        {
            e.ToTable("relation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
            e.Property(x => x.SourceEntityId).HasColumnName("source_entity_id");
            e.Property(x => x.TargetEntityId).HasColumnName("target_entity_id");
            e.Property(x => x.RelationType).HasColumnName("relation_type").HasMaxLength(40).IsRequired();
            e.Property(x => x.RelationKey).HasColumnName("relation_key").HasMaxLength(2200).IsRequired();
            e.Property(x => x.Resolution).HasColumnName("resolution").HasMaxLength(20);
            e.Property(x => x.Confidence).HasColumnName("confidence").HasMaxLength(20);
            e.Property(x => x.RuleId).HasColumnName("rule_id").HasMaxLength(40);
            e.Property(x => x.RuleSetVersion).HasColumnName("rule_set_version").HasMaxLength(40);
            e.Property(x => x.EvidenceJson).HasColumnName("evidence").HasColumnType("jsonb");
            e.Property(x => x.EvidenceFingerprint).HasColumnName("evidence_fingerprint").HasMaxLength(64);
            e.Property(x => x.Explanation).HasColumnName("explanation").HasMaxLength(2000);
            e.Property(x => x.ReviewState).HasColumnName("review_state").HasMaxLength(30);
            e.Property(x => x.ReviewDecisionId).HasColumnName("review_decision_id");
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<CanonicalEntityRecord>().WithMany().HasForeignKey(x => x.SourceEntityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CanonicalEntityRecord>().WithMany().HasForeignKey(x => x.TargetEntityId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Scope, x.SourceEntityId, x.TargetEntityId, x.RelationType }).IsUnique();
            e.HasIndex(x => new { x.Scope, x.TargetEntityId });
            e.HasIndex(x => new { x.Scope, x.Resolution, x.ReviewState });
        });

        modelBuilder.Entity<ReviewDecisionRecord>(e =>
        {
            e.ToTable("review_decision");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Sequence).HasColumnName("sequence");
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
            e.Property(x => x.TargetKind).HasColumnName("target_kind").HasMaxLength(20);
            e.Property(x => x.TargetKey).HasColumnName("target_key").HasMaxLength(2200).IsRequired();
            e.Property(x => x.TargetRelationId).HasColumnName("target_relation_id");
            e.Property(x => x.TargetAliasId).HasColumnName("target_alias_id");
            e.Property(x => x.ProposedJson).HasColumnName("proposed").HasColumnType("jsonb");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(20);
            e.Property(x => x.Reviewer).HasColumnName("reviewer").HasMaxLength(200);
            e.Property(x => x.DecidedAt).HasColumnName("decided_at");
            e.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);
            e.Property(x => x.EvidenceFingerprint).HasColumnName("evidence_fingerprint").HasMaxLength(64);
            e.Property(x => x.RuleSetVersion).HasColumnName("rule_set_version").HasMaxLength(40);
            e.Property(x => x.InterpretationId).HasColumnName("interpretation_id");
            e.Property(x => x.SupersedesId).HasColumnName("supersedes_id");
            e.Property(x => x.SupersededById).HasColumnName("superseded_by_id");
            e.HasIndex(x => new { x.Scope, x.TargetKey });
            e.HasIndex(x => new { x.Scope, x.Sequence }).IsUnique();
        });

        modelBuilder.Entity<InterpretationSnapshotRecord>(e =>
        {
            e.ToTable("interpretation_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RuleSetVersions).HasColumnName("rule_set_versions").HasMaxLength(200);
            e.Property(x => x.SourceSnapshotsJson).HasColumnName("source_snapshots").HasColumnType("jsonb");
            e.Property(x => x.SourceSetFingerprint).HasColumnName("source_set_fingerprint").HasMaxLength(64);
            e.Property(x => x.DecisionSequence).HasColumnName("decision_sequence");
            e.Property(x => x.EntityCount).HasColumnName("entity_count");
            e.Property(x => x.RelationCount).HasColumnName("relation_count");
            e.Property(x => x.ContentFingerprint).HasColumnName("content_fingerprint").HasMaxLength(64);
            e.Property(x => x.LimitationsJson).HasColumnName("limitations").HasColumnType("jsonb");
            e.Property(x => x.Trigger).HasColumnName("trigger").HasMaxLength(200);
            e.Property(x => x.PreviousId).HasColumnName("previous_id");
            e.HasIndex(x => new { x.Scope, x.CreatedAt });
        });

        modelBuilder.Entity<InterpretationSnapshotEntityRecord>(e =>
        {
            e.ToTable("interpretation_snapshot_entity");
            e.HasKey(x => new { x.SnapshotId, x.EntityId });
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(500);
            e.Property(x => x.Version).HasColumnName("version").HasMaxLength(100);
            e.HasOne<InterpretationSnapshotRecord>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CanonicalEntityRecord>().WithMany().HasForeignKey(x => x.EntityId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InterpretationSnapshotRelationRecord>(e =>
        {
            e.ToTable("interpretation_snapshot_relation");
            e.HasKey(x => new { x.SnapshotId, x.RelationId });
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.RelationId).HasColumnName("relation_id");
            e.Property(x => x.Resolution).HasColumnName("resolution").HasMaxLength(20);
            e.Property(x => x.Confidence).HasColumnName("confidence").HasMaxLength(20);
            e.Property(x => x.RuleId).HasColumnName("rule_id").HasMaxLength(40);
            e.Property(x => x.ReviewState).HasColumnName("review_state").HasMaxLength(30);
            e.Property(x => x.EvidenceFingerprint).HasColumnName("evidence_fingerprint").HasMaxLength(64);
            e.HasOne<InterpretationSnapshotRecord>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CanonicalRelationRecord>().WithMany().HasForeignKey(x => x.RelationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add --context CanonicalInterpretationDbContext</c> (no database connection is opened).</summary>
public sealed class CanonicalInterpretationDbContextFactory : IDesignTimeDbContextFactory<CanonicalInterpretationDbContext>
{
    public CanonicalInterpretationDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<CanonicalInterpretationDbContext>();
        CanonicalInterpretationDbContext.Configure(builder, Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=changeme");
        return new(builder.Options);
    }
}
