using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BirkNext.Api.Data.SnapshotRegistry;

// Entities of the snapshot_registry schema: metadata, lineage and pointers only. Payloads stay in their owners' stores (Source Analysis, Delivery,
// canonical interpretation, reviews, runs); an entry's storage_reference says where. Enum-valued columns are stored as text.

public sealed class SnapshotEntryRecord
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Project { get; set; }
    public string Module { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EffectiveAt { get; set; }
    public string? SourceRevision { get; set; }
    public string? AnalysisVersion { get; set; }
    public string StorageReference { get; set; } = "";
    public string? ContentFingerprint { get; set; }
    public string Status { get; set; } = "";
    public string Retention { get; set; } = "";
    /// <summary>True when a person set the retention class (sync never overwrites it).</summary>
    public bool RetentionSetByUser { get; set; }
    public bool Frozen { get; set; }
    public int MetadataVersion { get; set; }
    public bool PayloadAvailable { get; set; } = true;
    /// <summary>The owner's compact summary (JSON), computed once; what selectors list instead of loading payloads.</summary>
    public string? SummaryJson { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SnapshotEdgeRecord
{
    public string FromType { get; set; } = "";
    public string FromId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string ToType { get; set; } = "";
    public string ToId { get; set; } = "";
    public string RecordedBy { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class SnapshotPointerRecord
{
    /// <summary>"" for snapshots without a project (Source Analysis is project-independent).</summary>
    public string Project { get; set; } = "";
    public string Type { get; set; } = "";
    public string Module { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public DateTimeOffset SetAt { get; set; }
    public string SetBy { get; set; } = "";
}

/// <summary>
/// The shared Snapshot Registry: its own context and PostgreSQL schema (<see cref="Schema"/>) with its own migration history, like Delivery
/// Reporting and canonical interpretation. It never references another schema by foreign key: owners are read through sync adapters.
/// </summary>
public sealed class SnapshotRegistryDbContext(DbContextOptions<SnapshotRegistryDbContext> options) : DbContext(options)
{
    public const string Schema = "snapshot_registry";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<SnapshotEntryRecord> Entries => Set<SnapshotEntryRecord>();
    public DbSet<SnapshotEdgeRecord> Edges => Set<SnapshotEdgeRecord>();
    public DbSet<SnapshotPointerRecord> Pointers => Set<SnapshotPointerRecord>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<SnapshotEntryRecord>(e =>
        {
            e.ToTable("snapshot_entry");
            e.HasKey(x => new { x.Type, x.Id });
            e.Property(x => x.Type).HasColumnName("snapshot_type").HasMaxLength(40);
            e.Property(x => x.Id).HasColumnName("snapshot_id").HasMaxLength(100);
            e.Property(x => x.Project).HasColumnName("project").HasMaxLength(200);
            e.Property(x => x.Module).HasColumnName("module").HasMaxLength(80);
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(300);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.EffectiveAt).HasColumnName("effective_at");
            e.Property(x => x.SourceRevision).HasColumnName("source_revision").HasMaxLength(100);
            e.Property(x => x.AnalysisVersion).HasColumnName("analysis_version").HasMaxLength(100);
            e.Property(x => x.StorageReference).HasColumnName("storage_reference").HasMaxLength(300);
            e.Property(x => x.ContentFingerprint).HasColumnName("content_fingerprint").HasMaxLength(128);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.Retention).HasColumnName("retention").HasMaxLength(30);
            e.Property(x => x.RetentionSetByUser).HasColumnName("retention_set_by_user");
            e.Property(x => x.Frozen).HasColumnName("frozen");
            e.Property(x => x.MetadataVersion).HasColumnName("metadata_version");
            e.Property(x => x.PayloadAvailable).HasColumnName("payload_available");
            e.Property(x => x.SummaryJson).HasColumnName("summary").HasColumnType("jsonb");
            e.Property(x => x.RegisteredAt).HasColumnName("registered_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.Project, x.Type, x.Module, x.CreatedAt });
            e.HasIndex(x => new { x.Type, x.CreatedAt });
        });

        modelBuilder.Entity<SnapshotEdgeRecord>(e =>
        {
            e.ToTable("snapshot_edge");
            e.HasKey(x => new { x.FromType, x.FromId, x.Kind, x.ToType, x.ToId });
            e.Property(x => x.FromType).HasColumnName("from_type").HasMaxLength(40);
            e.Property(x => x.FromId).HasColumnName("from_id").HasMaxLength(100);
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30);
            e.Property(x => x.ToType).HasColumnName("to_type").HasMaxLength(40);
            e.Property(x => x.ToId).HasColumnName("to_id").HasMaxLength(100);
            e.Property(x => x.RecordedBy).HasColumnName("recorded_by").HasMaxLength(80);
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at");
            e.HasIndex(x => new { x.ToType, x.ToId });
        });

        modelBuilder.Entity<SnapshotPointerRecord>(e =>
        {
            e.ToTable("snapshot_pointer");
            e.HasKey(x => new { x.Project, x.Type, x.Module, x.Kind });
            e.Property(x => x.Project).HasColumnName("project").HasMaxLength(200);
            e.Property(x => x.Type).HasColumnName("snapshot_type").HasMaxLength(40);
            e.Property(x => x.Module).HasColumnName("module").HasMaxLength(80);
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30);
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id").HasMaxLength(100);
            e.Property(x => x.SetAt).HasColumnName("set_at");
            e.Property(x => x.SetBy).HasColumnName("set_by").HasMaxLength(200);
        });
    }
}

public sealed class SnapshotRegistryDbContextFactory : IDesignTimeDbContextFactory<SnapshotRegistryDbContext>
{
    public SnapshotRegistryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SnapshotRegistryDbContext>();
        SnapshotRegistryDbContext.Configure(options, Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=changeme");
        return new SnapshotRegistryDbContext(options.Options);
    }
}
