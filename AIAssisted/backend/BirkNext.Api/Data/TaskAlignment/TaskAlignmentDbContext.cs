using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BirkNext.Api.Data.TaskAlignment;

/// <summary>
/// One immutable Task Alignment run: fingerprints and provenance of the Specification and Tasks document it was produced from, the summary
/// counts and the per-task results (JSON). Document bodies are never stored. Rows are only inserted — never updated.
/// </summary>
public sealed class TaskAlignmentRunRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary>The project's name across imports (Project Import's archive root name); lets a later import see an earlier run as stale.</summary>
    public string? ProjectName { get; set; }
    public string? Module { get; set; }
    public string? FeatureKey { get; set; }
    public string TasksPath { get; set; } = "";
    public string TasksHash { get; set; } = "";
    public string? SpecPath { get; set; }
    public string SpecHash { get; set; } = "";
    public Guid? SourceSnapshotId { get; set; }
    public string RulesVersion { get; set; } = "";
    public string DesignInputsKey { get; set; } = "";
    public string RecordVersion { get; set; } = "";
    public string Completeness { get; set; } = "";
    public DateTimeOffset AnalyzedAt { get; set; }
    public int TotalTasks { get; set; }
    public int FindingCount { get; set; }
    public string CountsJson { get; set; } = "{}";
    public string TasksJson { get; set; } = "[]";
}

/// <summary>
/// Task Alignment runs (schema task_alignment): its own context on the same database, like the Scope and Snapshot registries. It never
/// references another schema by foreign key.
/// </summary>
public sealed class TaskAlignmentDbContext(DbContextOptions<TaskAlignmentDbContext> options) : DbContext(options)
{
    public const string Schema = "task_alignment";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<TaskAlignmentRunRecord> Runs => Set<TaskAlignmentRunRecord>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<TaskAlignmentRunRecord>(e =>
        {
            e.ToTable("run");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectKey).HasColumnName("project_key").HasMaxLength(300).IsRequired();
            e.Property(x => x.ProjectName).HasColumnName("project_name").HasMaxLength(300);
            e.Property(x => x.Module).HasColumnName("module").HasMaxLength(300);
            e.Property(x => x.FeatureKey).HasColumnName("feature_key").HasMaxLength(300);
            e.Property(x => x.TasksPath).HasColumnName("tasks_path").HasMaxLength(1000).IsRequired();
            e.Property(x => x.TasksHash).HasColumnName("tasks_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.SpecPath).HasColumnName("spec_path").HasMaxLength(1000);
            e.Property(x => x.SpecHash).HasColumnName("spec_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceSnapshotId).HasColumnName("source_snapshot_id");
            e.Property(x => x.RulesVersion).HasColumnName("rules_version").HasMaxLength(100).IsRequired();
            e.Property(x => x.DesignInputsKey).HasColumnName("design_inputs_key").HasMaxLength(200).IsRequired();
            e.Property(x => x.RecordVersion).HasColumnName("record_version").HasMaxLength(40).IsRequired();
            e.Property(x => x.Completeness).HasColumnName("completeness").HasMaxLength(20).IsRequired();
            e.Property(x => x.AnalyzedAt).HasColumnName("analyzed_at");
            e.Property(x => x.TotalTasks).HasColumnName("total_tasks");
            e.Property(x => x.FindingCount).HasColumnName("finding_count");
            e.Property(x => x.CountsJson).HasColumnName("counts_json").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.TasksJson).HasColumnName("tasks_json").HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => new { x.ProjectKey, x.TasksPath, x.AnalyzedAt }).HasDatabaseName("ix_run_project_tasks_analyzed");
        });
    }
}

public sealed class TaskAlignmentDbContextFactory : IDesignTimeDbContextFactory<TaskAlignmentDbContext>
{
    public TaskAlignmentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TaskAlignmentDbContext>();
        TaskAlignmentDbContext.Configure(options, Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=changeme");
        return new TaskAlignmentDbContext(options.Options);
    }
}
