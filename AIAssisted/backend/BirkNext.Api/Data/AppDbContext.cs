using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<ReviewedCandidate> ReviewedCandidates => Set<ReviewedCandidate>();
    public DbSet<CandidateLink> CandidateLinks => Set<CandidateLink>();
    public DbSet<QaDeltaReview> QaDeltaReviews => Set<QaDeltaReview>();
    public DbSet<TraceLink> TraceLinks => Set<TraceLink>();
    public DbSet<TraceabilitySuggestion> TraceabilitySuggestions => Set<TraceabilitySuggestion>();
    public DbSet<CodeFile> CodeFiles => Set<CodeFile>();
    public DbSet<CodeLink> CodeLinks => Set<CodeLink>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<SavedWorkspace> SavedWorkspaces => Set<SavedWorkspace>();
    public DbSet<SavedWorkspaceArtifact> SavedWorkspaceArtifacts => Set<SavedWorkspaceArtifact>();
    public DbSet<WorkspaceReviewProgress> WorkspaceReviewProgress => Set<WorkspaceReviewProgress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Scenario>(entity =>
        {
            entity.ToTable("scenarios");

            entity.Property(s => s.Id)
                .HasColumnName("id");

            entity.Property(s => s.Title)
                .HasColumnName("title")
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(s => s.Description)
                .HasColumnName("description");

            entity.Property(s => s.Kind)
                .HasColumnName("kind")
                .HasMaxLength(30)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(s => s.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(s => s.CreatedAt)
                .HasColumnName("created_at");

            entity.Property(s => s.DisplayOrder)
                .HasColumnName("display_order")
                .HasDefaultValue(0);

            entity.HasIndex(s => new { s.ProjectId, s.CreatedAt })
                .HasDatabaseName("ix_scenarios_project_id_created_at")
                .IsDescending(false, true);
        });

        modelBuilder.Entity<ReviewedCandidate>(entity =>
        {
            entity.ToTable("reviewed_candidates");

            entity.Property(c => c.Id)
                .HasColumnName("id");

            entity.Property(c => c.CandidateId)
                .HasColumnName("candidate_id");

            entity.Property(c => c.Title)
                .HasColumnName("title")
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(c => c.Classification)
                .HasColumnName("classification")
                .HasMaxLength(30)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(c => c.ReviewStatus)
                .HasColumnName("review_status")
                .HasMaxLength(30)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(c => c.SourceDocument)
                .HasColumnName("source_document");

            entity.Property(c => c.SourceSection)
                .HasColumnName("source_section");

            entity.Property(c => c.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(c => c.SessionId)
                .HasColumnName("session_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(c => c.ReviewedBy)
                .HasColumnName("reviewed_by")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(c => c.CreatedAt)
                .HasColumnName("created_at");

            entity.Property(c => c.ReviewedAt)
                .HasColumnName("reviewed_at");

            entity.HasIndex(c => new { c.ProjectId, c.SessionId })
                .HasDatabaseName("ix_reviewed_candidates_project_session");
        });

        modelBuilder.Entity<CandidateLink>(entity =>
        {
            entity.ToTable("candidate_links");

            entity.Property(l => l.Id)
                .HasColumnName("id");

            entity.Property(l => l.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(l => l.SessionId)
                .HasColumnName("session_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(l => l.SourceCandidateRef)
                .HasColumnName("source_candidate_ref")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(l => l.TargetCandidateRef)
                .HasColumnName("target_candidate_ref")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(l => l.LinkType)
                .HasColumnName("link_type")
                .HasMaxLength(50)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(l => l.CreatedAt)
                .HasColumnName("created_at");

            entity.HasIndex(l => new { l.ProjectId, l.SessionId })
                .HasDatabaseName("ix_candidate_links_project_session");
        });

        modelBuilder.Entity<QaDeltaReview>(entity =>
        {
            entity.ToTable("qa_delta_reviews");

            entity.Property(r => r.Id)
                .HasColumnName("id");

            entity.Property(r => r.Title)
                .HasColumnName("title")
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(r => r.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(r => r.CreatedAt)
                .HasColumnName("created_at");

            entity.Property(r => r.OldSpecFileName)
                .HasColumnName("old_spec_file_name");

            entity.Property(r => r.NewSpecFileName)
                .HasColumnName("new_spec_file_name");

            entity.Property(r => r.OldSpecHash)
                .HasColumnName("old_spec_hash");

            entity.Property(r => r.NewSpecHash)
                .HasColumnName("new_spec_hash");

            entity.Property(r => r.OldSpecSize)
                .HasColumnName("old_spec_size");

            entity.Property(r => r.NewSpecSize)
                .HasColumnName("new_spec_size");

            entity.Property(r => r.AnalysisProfile)
                .HasColumnName("analysis_profile")
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(r => r.SummaryJson)
                .HasColumnName("summary_json")
                .HasColumnType("text")
                .IsRequired();

            entity.Property(r => r.DeltaItemsJson)
                .HasColumnName("delta_items_json")
                .HasColumnType("text")
                .IsRequired();

            entity.HasIndex(r => new { r.ProjectId, r.CreatedAt })
                .HasDatabaseName("ix_qa_delta_reviews_project_id_created_at")
                .IsDescending(false, true);
        });

        modelBuilder.Entity<TraceLink>(entity =>
        {
            entity.ToTable("trace_links");

            entity.Property(t => t.Id)
                .HasColumnName("id");

            entity.Property(t => t.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(t => t.SourceId)
                .HasColumnName("source_id")
                .IsRequired();

            entity.Property(t => t.SourceKind)
                .HasColumnName("source_kind")
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(t => t.TargetId)
                .HasColumnName("target_id")
                .IsRequired();

            entity.Property(t => t.TargetKind)
                .HasColumnName("target_kind")
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(t => t.LinkType)
                .HasColumnName("link_type")
                .HasMaxLength(50)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(t => t.CreatedAt)
                .HasColumnName("created_at");

            entity.Property(t => t.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(200);

            entity.Property(t => t.Notes)
                .HasColumnName("notes");

            entity.HasIndex(t => new { t.ProjectId, t.TargetKind, t.TargetId })
                .HasDatabaseName("ix_trace_links_project_target");

            entity.HasIndex(t => new { t.ProjectId, t.SourceKind, t.SourceId })
                .HasDatabaseName("ix_trace_links_project_source");
        });

        modelBuilder.Entity<TraceabilitySuggestion>(entity =>
        {
            entity.ToTable("traceability_suggestions");

            entity.Property(s => s.Id).HasColumnName("id");

            entity.Property(s => s.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(s => s.SourceId).HasColumnName("source_id").IsRequired();
            entity.Property(s => s.SourceKind).HasColumnName("source_kind").HasMaxLength(50).IsRequired();
            entity.Property(s => s.TargetId).HasColumnName("target_id").IsRequired();
            entity.Property(s => s.TargetKind).HasColumnName("target_kind").HasMaxLength(50).IsRequired();

            entity.Property(s => s.LinkType)
                .HasColumnName("link_type")
                .HasMaxLength(50)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(s => s.Status)
                .HasColumnName("status")
                .HasMaxLength(30)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(s => s.Confidence).HasColumnName("confidence").IsRequired();
            entity.Property(s => s.Reason).HasColumnName("reason").HasColumnType("text").IsRequired();
            entity.Property(s => s.SignalsJson).HasColumnName("signals_json").HasColumnType("text").IsRequired();
            entity.Property(s => s.CreatedAt).HasColumnName("created_at");
            entity.Property(s => s.ConfirmedAt).HasColumnName("confirmed_at");
            entity.Property(s => s.RejectedAt).HasColumnName("rejected_at");

            entity.HasIndex(s => new { s.ProjectId, s.Status })
                .HasDatabaseName("ix_traceability_suggestions_project_status");

            entity.HasIndex(s => new { s.ProjectId, s.SourceId, s.TargetId, s.LinkType })
                .HasDatabaseName("ix_traceability_suggestions_pair")
                .IsUnique();
        });

        modelBuilder.Entity<CodeFile>(entity =>
        {
            entity.ToTable("code_files");

            entity.Property(f => f.Id).HasColumnName("id");

            entity.Property(f => f.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(f => f.FilePath)
                .HasColumnName("file_path")
                .HasMaxLength(1000)
                .IsRequired();

            entity.Property(f => f.FileName)
                .HasColumnName("file_name")
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(f => f.Description)
                .HasColumnName("description")
                .HasColumnType("text");

            entity.Property(f => f.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(f => new { f.ProjectId, f.FilePath })
                .HasDatabaseName("ix_code_files_project_path")
                .IsUnique();

            entity.HasIndex(f => new { f.ProjectId, f.CreatedAt })
                .HasDatabaseName("ix_code_files_project_created");
        });

        modelBuilder.Entity<CodeLink>(entity =>
        {
            entity.ToTable("code_links");

            entity.Property(l => l.Id).HasColumnName("id");

            entity.Property(l => l.ProjectId)
                .HasColumnName("project_id")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(l => l.CodeFileId)
                .HasColumnName("code_file_id")
                .IsRequired();

            entity.Property(l => l.ScenarioId)
                .HasColumnName("scenario_id")
                .IsRequired();

            entity.Property(l => l.ScenarioKind)
                .HasColumnName("scenario_kind")
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(l => l.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(l => new { l.CodeFileId, l.ScenarioId })
                .HasDatabaseName("ix_code_links_file_scenario")
                .IsUnique();

            entity.HasIndex(l => new { l.ProjectId, l.CodeFileId })
                .HasDatabaseName("ix_code_links_project_file");

            entity.HasIndex(l => new { l.ProjectId, l.ScenarioId })
                .HasDatabaseName("ix_code_links_project_scenario");
        });

        modelBuilder.Entity<ProjectDocument>(entity =>
        {
            entity.ToTable("project_documents");

            entity.Property(d => d.Id).HasColumnName("id");

            entity.Property(d => d.DocumentKind)
                .HasColumnName("document_kind")
                .HasMaxLength(50)
                .IsRequired()
                .HasConversion<string>();

            entity.Property(d => d.Content)
                .HasColumnName("content")
                .HasColumnType("text")
                .IsRequired();

            entity.Property(d => d.CreatedUtc).HasColumnName("created_utc");
            entity.Property(d => d.UpdatedUtc).HasColumnName("updated_utc");

            entity.HasIndex(d => d.DocumentKind)
                .HasDatabaseName("ix_project_documents_kind")
                .IsUnique();
        });

        modelBuilder.Entity<SavedWorkspace>(entity =>
        {
            entity.ToTable("saved_workspaces");

            entity.Property(w => w.Id).HasColumnName("id");
            entity.Property(w => w.UserId).HasColumnName("user_id").HasMaxLength(200).IsRequired();
            entity.Property(w => w.Name).HasColumnName("name").HasMaxLength(500).IsRequired();
            entity.Property(w => w.ProjectName).HasColumnName("project_name").HasMaxLength(500).IsRequired();
            entity.Property(w => w.Description).HasColumnName("description").HasColumnType("text");
            entity.Property(w => w.CreatedAt).HasColumnName("created_at");
            entity.Property(w => w.UpdatedAt).HasColumnName("updated_at");
            entity.Property(w => w.LastOpenedAt).HasColumnName("last_opened_at");
            entity.Property(w => w.Version).HasColumnName("version").HasDefaultValue(1);
            entity.Property(w => w.ParserVersion).HasColumnName("parser_version").HasMaxLength(50);
            entity.Property(w => w.ReviewContextVersion).HasColumnName("review_context_version").HasMaxLength(50);
            entity.Property(w => w.ArtifactSetHash).HasColumnName("artifact_set_hash").HasMaxLength(128);
            entity.Property(w => w.AutoSaved).HasColumnName("auto_saved").HasDefaultValue(false);
            entity.Property(w => w.Favorite).HasColumnName("favorite").HasDefaultValue(false);
            entity.Property(w => w.TagsJson).HasColumnName("tags_json").HasColumnType("text");
            entity.Property(w => w.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);

            entity.HasIndex(w => new { w.UserId, w.IsDeleted })
                .HasDatabaseName("ix_saved_workspaces_user_not_deleted");

            entity.HasIndex(w => new { w.UserId, w.UpdatedAt })
                .HasDatabaseName("ix_saved_workspaces_user_updated")
                .IsDescending(false, true);
        });

        modelBuilder.Entity<SavedWorkspaceArtifact>(entity =>
        {
            entity.ToTable("saved_workspace_artifacts");

            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(a => a.ArtifactType).HasColumnName("artifact_type").HasMaxLength(50).IsRequired()
                .HasConversion<string>();
            entity.Property(a => a.FileName).HasColumnName("file_name").HasMaxLength(500).IsRequired();
            entity.Property(a => a.OriginalPath).HasColumnName("original_path").HasMaxLength(1000);
            entity.Property(a => a.Content).HasColumnName("content").HasColumnType("text").IsRequired();
            entity.Property(a => a.ContentHash).HasColumnName("content_hash").HasMaxLength(128);
            entity.Property(a => a.Encoding).HasColumnName("encoding").HasMaxLength(50);
            entity.Property(a => a.LastModified).HasColumnName("last_modified");
            entity.Property(a => a.ParseVersion).HasColumnName("parse_version").HasMaxLength(50);
            entity.Property(a => a.CreatedAt).HasColumnName("created_at");
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(a => a.Workspace)
                .WithMany(w => w.Artifacts)
                .HasForeignKey(a => a.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(a => new { a.WorkspaceId, a.ArtifactType })
                .HasDatabaseName("ix_saved_artifacts_workspace_type");
        });

        modelBuilder.Entity<WorkspaceReviewProgress>(entity =>
        {
            entity.ToTable("workspace_review_progress");

            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(r => r.StepKey).HasColumnName("step_key").HasMaxLength(100).IsRequired();

            // Human review/approval decisions (persisted)
            entity.Property(r => r.ReviewState).HasColumnName("review_state").HasMaxLength(50).IsRequired()
                .HasConversion<string>();
            entity.Property(r => r.ApprovalState).HasColumnName("approval_state").HasMaxLength(50).IsRequired()
                .HasConversion<string>();

            // Review audit trail
            entity.Property(r => r.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(200);
            entity.Property(r => r.ReviewedAt).HasColumnName("reviewed_at");

            // Approval audit trail
            entity.Property(r => r.ApprovedBy).HasColumnName("approved_by").HasMaxLength(200);
            entity.Property(r => r.ApprovedAt).HasColumnName("approved_at");

            // Rejection audit trail
            entity.Property(r => r.RejectedBy).HasColumnName("rejected_by").HasMaxLength(200);
            entity.Property(r => r.RejectedAt).HasColumnName("rejected_at");

            // Optional comment
            entity.Property(r => r.Comment).HasColumnName("comment").HasColumnType("text");

            // Artifact state tracking for invalidation
            entity.Property(r => r.ArtifactSetHashAtReview).HasColumnName("artifact_set_hash_at_review").HasMaxLength(128);
            entity.Property(r => r.ArtifactSetHashAtApproval).HasColumnName("artifact_set_hash_at_approval").HasMaxLength(128);

            // Version tracking for invalidation
            entity.Property(r => r.ReviewContextVersionAtApproval).HasColumnName("review_context_version_at_approval").HasMaxLength(50);
            entity.Property(r => r.WorkspaceVersionAtApproval).HasColumnName("workspace_version_at_approval");

            // User engagement tracking
            entity.Property(r => r.LastOpenedAt).HasColumnName("last_opened_at");

            // Metadata
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(r => r.Workspace)
                .WithMany()
                .HasForeignKey(r => r.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => new { r.WorkspaceId, r.StepKey })
                .HasDatabaseName("ix_workspace_review_progress_workspace_key")
                .IsUnique();

            entity.HasIndex(r => new { r.WorkspaceId, r.ApprovalState })
                .HasDatabaseName("ix_workspace_review_progress_workspace_approval");
        });
    }
}
