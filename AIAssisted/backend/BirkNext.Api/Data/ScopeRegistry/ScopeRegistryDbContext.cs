using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BirkNext.Api.Data.ScopeRegistry;

// Entities of the scope_registry schema: shared scope identity and immutable revisions of typed member references. Members are stored as
// JSON inside the revision (one immutable document per revision); the referenced facts stay in their owners' stores. Enums are text.

public sealed class ScopeRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Purpose { get; set; } = "";
    public string? Owner { get; set; }
    public string State { get; set; } = "";
    public int CurrentRevision { get; set; }
    public int? ApprovedRevision { get; set; }
    /// <summary>Optimistic concurrency: incremented on every change; a save names the version it was based on.</summary>
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ScopeRevisionRecord
{
    public Guid Id { get; set; }
    public Guid ScopeId { get; set; }
    public int Number { get; set; }
    public string State { get; set; } = "";
    public string MembersJson { get; set; } = "[]";
    public string? ChangeSummary { get; set; }
    public string ContentFingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public string? ApprovalRationale { get; set; }
}

/// <summary>
/// The shared Scope Registry (schema scope_registry): one project-scoped scope identity every module can reference. It never references
/// another schema by foreign key; members are typed stable ids of facts owned elsewhere.
/// </summary>
public sealed class ScopeRegistryDbContext(DbContextOptions<ScopeRegistryDbContext> options) : DbContext(options)
{
    public const string Schema = "scope_registry";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<ScopeRecord> Scopes => Set<ScopeRecord>();
    public DbSet<ScopeRevisionRecord> Revisions => Set<ScopeRevisionRecord>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ScopeRecord>(e =>
        {
            e.ToTable("scope");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectKey).HasColumnName("project_key").HasMaxLength(300).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
            e.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(40).IsRequired();
            e.Property(x => x.Owner).HasColumnName("owner").HasMaxLength(200);
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(20).IsRequired();
            e.Property(x => x.CurrentRevision).HasColumnName("current_revision");
            e.Property(x => x.ApprovedRevision).HasColumnName("approved_revision");
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.ProjectKey).HasDatabaseName("ix_scope_project");
            e.HasIndex(x => new { x.ProjectKey, x.Name }).IsUnique().HasDatabaseName("ux_scope_project_name");
        });

        modelBuilder.Entity<ScopeRevisionRecord>(e =>
        {
            e.ToTable("scope_revision");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ScopeId).HasColumnName("scope_id");
            e.Property(x => x.Number).HasColumnName("number");
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(20).IsRequired();
            e.Property(x => x.MembersJson).HasColumnName("members_json").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ChangeSummary).HasColumnName("change_summary").HasMaxLength(2000);
            e.Property(x => x.ContentFingerprint).HasColumnName("content_fingerprint").HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(200).IsRequired();
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasMaxLength(200);
            e.Property(x => x.ApprovalRationale).HasColumnName("approval_rationale").HasMaxLength(2000);
            e.HasIndex(x => new { x.ScopeId, x.Number }).IsUnique().HasDatabaseName("ux_scope_revision_number");
            e.HasOne<ScopeRecord>().WithMany().HasForeignKey(x => x.ScopeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class ScopeRegistryDbContextFactory : IDesignTimeDbContextFactory<ScopeRegistryDbContext>
{
    public ScopeRegistryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ScopeRegistryDbContext>();
        ScopeRegistryDbContext.Configure(options, Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=changeme");
        return new ScopeRegistryDbContext(options.Options);
    }
}
