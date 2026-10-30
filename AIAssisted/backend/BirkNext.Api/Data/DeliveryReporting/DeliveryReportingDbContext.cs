using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BirkNext.Api.Data.DeliveryReporting;

/// <summary>
/// Delivery Reporting persistence. A separate context on the same database, bound to its own PostgreSQL schema (<see cref="Schema"/>) with its
/// own migration history table in that schema, so its migrations never touch the public schema and the main BirkNext context's model, history,
/// local reset and integrity checks never see these tables. It has no navigation or foreign key into any BirkNext operational table.
/// </summary>
public sealed class DeliveryReportingDbContext(DbContextOptions<DeliveryReportingDbContext> options) : DbContext(options)
{
    public const string Schema = "delivery_reporting";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<DeliveryProjectRecord> Projects => Set<DeliveryProjectRecord>();
    public DbSet<DeliveryReportSourceRecord> ReportSources => Set<DeliveryReportSourceRecord>();
    public DbSet<DeliverySnapshotRecord> Snapshots => Set<DeliverySnapshotRecord>();
    public DbSet<DeliveryFeatureSnapshotRecord> FeatureSnapshots => Set<DeliveryFeatureSnapshotRecord>();
    public DbSet<DeliveryTaskSnapshotRecord> TaskSnapshots => Set<DeliveryTaskSnapshotRecord>();
    public DbSet<DeliveryPhaseSnapshotRecord> PhaseSnapshots => Set<DeliveryPhaseSnapshotRecord>();
    public DbSet<DeliveryUserStorySnapshotRecord> UserStorySnapshots => Set<DeliveryUserStorySnapshotRecord>();
    public DbSet<DeliveryAcceptanceCriterionSnapshotRecord> AcceptanceCriterionSnapshots => Set<DeliveryAcceptanceCriterionSnapshotRecord>();
    public DbSet<DeliveryArtifactSnapshotRecord> ArtifactSnapshots => Set<DeliveryArtifactSnapshotRecord>();
    public DbSet<DeliveryEvidenceReferenceRecord> EvidenceReferences => Set<DeliveryEvidenceReferenceRecord>();
    public DbSet<DeliverySnapshotComparisonRecord> Comparisons => Set<DeliverySnapshotComparisonRecord>();
    public DbSet<DeliveryFeatureDeltaRecord> FeatureDeltas => Set<DeliveryFeatureDeltaRecord>();
    public DbSet<DeliveryStatusEventRecord> StatusEvents => Set<DeliveryStatusEventRecord>();
    public DbSet<DeliveryWeeklyReportRecord> WeeklyReports => Set<DeliveryWeeklyReportRecord>();
    public DbSet<DeliveryWeeklyReportItemRecord> WeeklyReportItems => Set<DeliveryWeeklyReportItemRecord>();

    /// <summary>Npgsql options for this context: the shared connection string, history table inside <see cref="Schema"/>.</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<DeliveryProjectRecord>(e =>
        {
            e.ToTable("project");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
            e.Property(x => x.ExternalReference).HasColumnName("external_reference").HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<DeliveryReportSourceRecord>(e =>
        {
            e.ToTable("report_source");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(40);
            e.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(260);
            e.Property(x => x.ImportedAt).HasColumnName("imported_at");
            e.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            e.Property(x => x.GitCommit).HasColumnName("git_commit").HasMaxLength(64);
            e.Property(x => x.GitBranch).HasColumnName("git_branch").HasMaxLength(200);
            e.Property(x => x.ArchiveBytes).HasColumnName("archive_bytes");
            e.Property(x => x.EntryCount).HasColumnName("entry_count");
            e.Property(x => x.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb");
            e.HasOne<DeliveryProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Fingerprint });
            e.HasIndex(x => new { x.ProjectId, x.GitCommit });
        });

        modelBuilder.Entity<DeliverySnapshotRecord>(e =>
        {
            e.ToTable("report_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.ReportSourceId).HasColumnName("report_source_id");
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(200);
            e.Property(x => x.EffectiveAt).HasColumnName("effective_at");
            e.Property(x => x.ImportedAt).HasColumnName("imported_at");
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(20);
            e.Property(x => x.CoverageStatus).HasColumnName("coverage_status").HasMaxLength(20);
            e.Property(x => x.ParseStatus).HasColumnName("parse_status").HasMaxLength(20);
            e.Property(x => x.WarningsCount).HasColumnName("warnings_count");
            e.Property(x => x.FeatureCount).HasColumnName("feature_count");
            e.Property(x => x.TaskCount).HasColumnName("task_count");
            e.Property(x => x.CompletedTaskCount).HasColumnName("completed_task_count");
            e.Property(x => x.ModulesJson).HasColumnName("modules").HasColumnType("jsonb");
            e.Property(x => x.WarningsJson).HasColumnName("warnings").HasColumnType("jsonb");
            e.HasOne<DeliveryProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DeliveryReportSourceRecord>().WithMany().HasForeignKey(x => x.ReportSourceId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.ProjectId, x.EffectiveAt });
        });

        modelBuilder.Entity<DeliveryFeatureSnapshotRecord>(e =>
        {
            e.ToTable("feature_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.FeatureKey).HasColumnName("feature_key").HasMaxLength(400);
            e.Property(x => x.Module).HasColumnName("module").HasMaxLength(400);
            e.Property(x => x.Folder).HasColumnName("folder").HasMaxLength(200);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(400);
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.SpecPath).HasColumnName("spec_path").HasMaxLength(1000);
            e.Property(x => x.TasksPath).HasColumnName("tasks_path").HasMaxLength(1000);
            e.Property(x => x.PlanPath).HasColumnName("plan_path").HasMaxLength(1000);
            e.Property(x => x.SpecFingerprint).HasColumnName("spec_fingerprint").HasMaxLength(64);
            e.Property(x => x.AcceptanceFingerprint).HasColumnName("acceptance_fingerprint").HasMaxLength(64);
            e.Property(x => x.AcceptanceCriteriaJson).HasColumnName("acceptance_criteria").HasColumnType("jsonb");
            e.Property(x => x.RequirementCount).HasColumnName("requirement_count");
            e.Property(x => x.UserStoryCount).HasColumnName("user_story_count");
            e.Property(x => x.OpenClarifications).HasColumnName("open_clarifications");
            e.Property(x => x.TaskTotal).HasColumnName("task_total");
            e.Property(x => x.TaskCompleted).HasColumnName("task_completed");
            e.Property(x => x.UntrackedTaskCount).HasColumnName("untracked_task_count");
            e.Property(x => x.HierarchyCaptured).HasColumnName("hierarchy_captured").HasDefaultValue(false);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(40);
            e.Property(x => x.StatusReason).HasColumnName("status_reason").HasMaxLength(1000);
            e.HasOne<DeliverySnapshotRecord>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SnapshotId, x.FeatureKey }).IsUnique();
        });

        modelBuilder.Entity<DeliveryTaskSnapshotRecord>(e =>
        {
            e.ToTable("task_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.FeatureSnapshotId).HasColumnName("feature_snapshot_id");
            e.Property(x => x.TaskKey).HasColumnName("task_key").HasMaxLength(200);
            e.Property(x => x.TaskId).HasColumnName("task_id").HasMaxLength(40);
            e.Property(x => x.Text).HasColumnName("text").HasMaxLength(2000);
            e.Property(x => x.Completed).HasColumnName("completed");
            e.Property(x => x.Phase).HasColumnName("phase").HasMaxLength(400);
            e.Property(x => x.UserStory).HasColumnName("user_story").HasMaxLength(40);
            e.Property(x => x.PhaseKey).HasColumnName("phase_key").HasMaxLength(40);
            e.Property(x => x.UserStoryKey).HasColumnName("user_story_key").HasMaxLength(40);
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.SourcePath).HasColumnName("source_path").HasMaxLength(1000);
            e.HasOne<DeliveryFeatureSnapshotRecord>().WithMany().HasForeignKey(x => x.FeatureSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SnapshotId);
            e.HasIndex(x => x.FeatureSnapshotId);
        });

        modelBuilder.Entity<DeliveryPhaseSnapshotRecord>(e =>
        {
            e.ToTable("phase_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.FeatureSnapshotId).HasColumnName("feature_snapshot_id");
            e.Property(x => x.PhaseKey).HasColumnName("phase_key").HasMaxLength(40);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(400);
            e.Property(x => x.Position).HasColumnName("position");
            e.HasOne<DeliveryFeatureSnapshotRecord>().WithMany().HasForeignKey(x => x.FeatureSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SnapshotId);
            e.HasIndex(x => new { x.FeatureSnapshotId, x.PhaseKey }).IsUnique();
        });

        modelBuilder.Entity<DeliveryUserStorySnapshotRecord>(e =>
        {
            e.ToTable("user_story_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.FeatureSnapshotId).HasColumnName("feature_snapshot_id");
            e.Property(x => x.StoryKey).HasColumnName("story_key").HasMaxLength(60);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(300);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(1500);
            e.Property(x => x.Priority).HasColumnName("priority").HasMaxLength(20);
            e.Property(x => x.PhaseKeysJson).HasColumnName("phase_keys_json");
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.SourceLine).HasColumnName("source_line");
            e.HasOne<DeliveryFeatureSnapshotRecord>().WithMany().HasForeignKey(x => x.FeatureSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SnapshotId);
            e.HasIndex(x => new { x.FeatureSnapshotId, x.StoryKey }).IsUnique();
        });

        modelBuilder.Entity<DeliveryAcceptanceCriterionSnapshotRecord>(e =>
        {
            e.ToTable("acceptance_criterion_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.FeatureSnapshotId).HasColumnName("feature_snapshot_id");
            e.Property(x => x.StoryKey).HasColumnName("story_key").HasMaxLength(60);
            e.Property(x => x.CriterionKey).HasColumnName("criterion_key").HasMaxLength(60);
            e.Property(x => x.Text).HasColumnName("text").HasMaxLength(2000);
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.SourceLine).HasColumnName("source_line");
            e.HasOne<DeliveryFeatureSnapshotRecord>().WithMany().HasForeignKey(x => x.FeatureSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SnapshotId);
            e.HasIndex(x => x.FeatureSnapshotId);
        });

        modelBuilder.Entity<DeliveryArtifactSnapshotRecord>(e =>
        {
            e.ToTable("artifact_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.Path).HasColumnName("path").HasMaxLength(4096);
            e.Property(x => x.Module).HasColumnName("module").HasMaxLength(400);
            e.Property(x => x.FeatureKey).HasColumnName("feature_key").HasMaxLength(400);
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40);
            e.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
            e.HasOne<DeliverySnapshotRecord>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SnapshotId);
        });

        modelBuilder.Entity<DeliveryEvidenceReferenceRecord>(e =>
        {
            e.ToTable("evidence_reference");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            e.Property(x => x.FeatureSnapshotId).HasColumnName("feature_snapshot_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40);
            e.Property(x => x.Path).HasColumnName("path").HasMaxLength(1000);
            e.Property(x => x.Line).HasColumnName("line");
            e.Property(x => x.Text).HasColumnName("text").HasMaxLength(1000);
            e.HasOne<DeliveryFeatureSnapshotRecord>().WithMany().HasForeignKey(x => x.FeatureSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SnapshotId);
        });

        modelBuilder.Entity<DeliverySnapshotComparisonRecord>(e =>
        {
            e.ToTable("snapshot_comparison");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.BaselineSnapshotId).HasColumnName("baseline_snapshot_id");
            e.Property(x => x.CurrentSnapshotId).HasColumnName("current_snapshot_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.FeaturesAdded).HasColumnName("features_added");
            e.Property(x => x.FeaturesRemoved).HasColumnName("features_removed");
            e.Property(x => x.FeaturesChanged).HasColumnName("features_changed");
            e.HasOne<DeliveryProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DeliverySnapshotRecord>().WithMany().HasForeignKey(x => x.BaselineSnapshotId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<DeliverySnapshotRecord>().WithMany().HasForeignKey(x => x.CurrentSnapshotId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DeliveryFeatureDeltaRecord>(e =>
        {
            e.ToTable("feature_delta");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ComparisonId).HasColumnName("comparison_id");
            e.Property(x => x.FeatureKey).HasColumnName("feature_key").HasMaxLength(400);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(400);
            e.Property(x => x.Module).HasColumnName("module").HasMaxLength(400);
            e.Property(x => x.BaselineStatus).HasColumnName("baseline_status").HasMaxLength(40);
            e.Property(x => x.CurrentStatus).HasColumnName("current_status").HasMaxLength(40);
            e.Property(x => x.BaselineTaskCompleted).HasColumnName("baseline_task_completed");
            e.Property(x => x.BaselineTaskTotal).HasColumnName("baseline_task_total");
            e.Property(x => x.CurrentTaskCompleted).HasColumnName("current_task_completed");
            e.Property(x => x.CurrentTaskTotal).HasColumnName("current_task_total");
            e.Property(x => x.TaskDelta).HasColumnName("task_delta");
            e.Property(x => x.SpecificationChanged).HasColumnName("specification_changed");
            e.Property(x => x.AcceptanceCriteriaChanged).HasColumnName("acceptance_criteria_changed");
            e.Property(x => x.NewFeature).HasColumnName("new_feature");
            e.Property(x => x.RemovedFeature).HasColumnName("removed_feature");
            e.Property(x => x.DeliveryMovement).HasColumnName("delivery_movement").HasMaxLength(40);
            e.Property(x => x.ReasonsJson).HasColumnName("reasons").HasColumnType("jsonb");
            e.HasOne<DeliverySnapshotComparisonRecord>().WithMany().HasForeignKey(x => x.ComparisonId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ComparisonId);
        });

        modelBuilder.Entity<DeliveryStatusEventRecord>(e =>
        {
            e.ToTable("status_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ComparisonId).HasColumnName("comparison_id");
            e.Property(x => x.FeatureKey).HasColumnName("feature_key").HasMaxLength(400);
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40);
            e.Property(x => x.FromValue).HasColumnName("from_value").HasMaxLength(400);
            e.Property(x => x.ToValue).HasColumnName("to_value").HasMaxLength(400);
            e.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(2000);
            e.HasOne<DeliverySnapshotComparisonRecord>().WithMany().HasForeignKey(x => x.ComparisonId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ComparisonId);
        });

        modelBuilder.Entity<DeliveryWeeklyReportRecord>(e =>
        {
            e.ToTable("weekly_report");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.ComparisonId).HasColumnName("comparison_id");
            e.Property(x => x.BaselineSnapshotId).HasColumnName("baseline_snapshot_id");
            e.Property(x => x.CurrentSnapshotId).HasColumnName("current_snapshot_id");
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(400);
            e.Property(x => x.WeekLabel).HasColumnName("week_label").HasMaxLength(20);
            e.Property(x => x.PeriodStart).HasColumnName("period_start");
            e.Property(x => x.PeriodEnd).HasColumnName("period_end");
            e.Property(x => x.GeneratedAt).HasColumnName("generated_at");
            e.Property(x => x.CoverageStatus).HasColumnName("coverage_status").HasMaxLength(20);
            e.Property(x => x.ContentVersion).HasColumnName("content_version");
            e.Property(x => x.ContentJson).HasColumnName("content").HasColumnType("jsonb");
            e.HasOne<DeliveryProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DeliverySnapshotComparisonRecord>().WithMany().HasForeignKey(x => x.ComparisonId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DeliverySnapshotRecord>().WithMany().HasForeignKey(x => x.BaselineSnapshotId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<DeliverySnapshotRecord>().WithMany().HasForeignKey(x => x.CurrentSnapshotId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.ProjectId, x.GeneratedAt });
        });

        modelBuilder.Entity<DeliveryWeeklyReportItemRecord>(e =>
        {
            e.ToTable("weekly_report_item");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ReportId).HasColumnName("report_id");
            e.Property(x => x.Section).HasColumnName("section").HasMaxLength(20);
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.FeatureKey).HasColumnName("feature_key").HasMaxLength(400);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(400);
            e.Property(x => x.Movement).HasColumnName("movement").HasMaxLength(40);
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40);
            e.Property(x => x.Text).HasColumnName("text").HasMaxLength(2000);
            e.HasOne<DeliveryWeeklyReportRecord>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ReportId);
        });
    }
}

public sealed class DeliveryReportingDbContextFactory : IDesignTimeDbContextFactory<DeliveryReportingDbContext>
{
    public DeliveryReportingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=changeme";
        var options = new DbContextOptionsBuilder<DeliveryReportingDbContext>();
        DeliveryReportingDbContext.Configure(options, connectionString);
        return new DeliveryReportingDbContext(options.Options);
    }
}
