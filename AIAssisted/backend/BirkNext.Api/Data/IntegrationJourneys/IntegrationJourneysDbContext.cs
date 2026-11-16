using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BirkNext.Api.Data.IntegrationJourneys;

/// <summary>
/// A journey declared for one project: its stable identity (project key + slug, never re-derived), the current display name and revision, and
/// when it was retired (removed). The declared content lives in <see cref="DeclaredJourneyRevisionRecord"/> rows.
/// </summary>
public sealed class DeclaredJourneyRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public string Slug { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>The latest revision number; also the concurrency token (two concurrent edits cannot both append the same next revision).</summary>
    public int CurrentRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public string? RetiredBy { get; set; }
}

/// <summary>One immutable revision of a declared journey. Rows are only inserted — never updated or deleted.</summary>
public sealed class DeclaredJourneyRevisionRecord
{
    public Guid JourneyId { get; set; }
    public int Revision { get; set; }
    public string DisplayName { get; set; } = "";
    public string ComponentsJson { get; set; } = "[]";
    public string? Description { get; set; }
    public string DeclaredBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Origin { get; set; } = "";
}

/// <summary>A requirement linked to a declared journey (content = the shared <c>JourneyRequirementLink</c> as JSON, with its append-only history).</summary>
public sealed class JourneyRequirementLinkRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public Guid JourneyId { get; set; }
    public string RequirementId { get; set; } = "";
    public string SpecificationPath { get; set; } = "";
    public string Status { get; set; } = "";
    public string ContentJson { get; set; } = "{}";
    /// <summary>Concurrency token: two concurrent changes cannot both apply.</summary>
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Project-declared Integration Journeys (schema integration_journeys): its own context on the same database, like the Scope Registry and Task
/// Alignment. It never references another schema by foreign key.
/// </summary>
public sealed class IntegrationJourneysDbContext(DbContextOptions<IntegrationJourneysDbContext> options) : DbContext(options)
{
    public const string Schema = "integration_journeys";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<DeclaredJourneyRecord> Journeys => Set<DeclaredJourneyRecord>();
    public DbSet<DeclaredJourneyRevisionRecord> Revisions => Set<DeclaredJourneyRevisionRecord>();
    public DbSet<JourneyRequirementLinkRecord> RequirementLinks => Set<JourneyRequirementLinkRecord>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<DeclaredJourneyRecord>(e =>
        {
            e.ToTable("journey");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectKey).HasColumnName("project_key").HasMaxLength(150).IsRequired();
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(60).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(120).IsRequired();
            e.Property(x => x.CurrentRevision).HasColumnName("current_revision").IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(120).IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RetiredAt).HasColumnName("retired_at");
            e.Property(x => x.RetiredBy).HasColumnName("retired_by").HasMaxLength(120);
            e.HasIndex(x => new { x.ProjectKey, x.Slug }).IsUnique().HasDatabaseName("ux_journey_project_slug");
        });

        modelBuilder.Entity<DeclaredJourneyRevisionRecord>(e =>
        {
            e.ToTable("journey_revision");
            e.HasKey(x => new { x.JourneyId, x.Revision });
            e.Property(x => x.JourneyId).HasColumnName("journey_id");
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(120).IsRequired();
            e.Property(x => x.ComponentsJson).HasColumnName("components").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
            e.Property(x => x.DeclaredBy).HasColumnName("declared_by").HasMaxLength(120).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Origin).HasColumnName("origin").HasMaxLength(20).IsRequired();
            e.HasOne<DeclaredJourneyRecord>().WithMany().HasForeignKey(x => x.JourneyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JourneyRequirementLinkRecord>(e =>
        {
            e.ToTable("journey_requirement_link");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectKey).HasColumnName("project_key").HasMaxLength(150).IsRequired();
            e.Property(x => x.JourneyId).HasColumnName("journey_id");
            e.Property(x => x.RequirementId).HasColumnName("requirement_id").HasMaxLength(200).IsRequired();
            e.Property(x => x.SpecificationPath).HasColumnName("specification_path").HasMaxLength(500).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            e.Property(x => x.ContentJson).HasColumnName("content").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.ProjectKey, x.JourneyId, x.RequirementId, x.SpecificationPath }).IsUnique().HasDatabaseName("ux_journey_requirement_link");
            e.HasOne<DeclaredJourneyRecord>().WithMany().HasForeignKey(x => x.JourneyId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class IntegrationJourneysDbContextFactory : IDesignTimeDbContextFactory<IntegrationJourneysDbContext>
{
    public IntegrationJourneysDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IntegrationJourneysDbContext>();
        IntegrationJourneysDbContext.Configure(options, Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=changeme");
        return new IntegrationJourneysDbContext(options.Options);
    }
}
