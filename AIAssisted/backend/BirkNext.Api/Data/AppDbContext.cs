using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<ReviewedCandidate> ReviewedCandidates => Set<ReviewedCandidate>();
    public DbSet<CandidateLink> CandidateLinks => Set<CandidateLink>();

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

            entity.HasIndex(s => new { s.ProjectId, s.CreatedAt })
                .HasDatabaseName("ix_scenarios_project_id_created_at")
                .IsDescending(false, true);
        });

        modelBuilder.Entity<ReviewedCandidate>(entity =>
        {
            entity.ToTable("reviewed_candidates");

            entity.Property(c => c.Id)
                .HasColumnName("id");

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
    }
}
