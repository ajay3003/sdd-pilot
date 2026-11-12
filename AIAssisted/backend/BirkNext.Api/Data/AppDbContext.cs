using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<IqrSourceSnapshotRecord> IqrSourceSnapshots => Set<IqrSourceSnapshotRecord>();
    public DbSet<SourceSchemaArtifactRecord> SourceSchemaArtifacts => Set<SourceSchemaArtifactRecord>();
    public DbSet<SourceContractDocumentRecord> SourceContractDocuments => Set<SourceContractDocumentRecord>();
    public DbSet<ActiveCdcRunRecord> ActiveCdcRuns => Set<ActiveCdcRunRecord>();
    public DbSet<ActiveEventRunRecord> ActiveEventRuns => Set<ActiveEventRunRecord>();
    public DbSet<ActiveEventSyntheticIdentityRecord> ActiveEventSyntheticIdentities => Set<ActiveEventSyntheticIdentityRecord>();
    public DbSet<IntegrationJourneyRunRecord> IntegrationJourneyRuns => Set<IntegrationJourneyRunRecord>();
    public DbSet<ExecutionRunRow> ExecutionRuns => Set<ExecutionRunRow>();
    public DbSet<ExecutionCallRow> ExecutionCalls => Set<ExecutionCallRow>();
    public DbSet<ExecutionManifestRow> ExecutionManifests => Set<ExecutionManifestRow>();
    public DbSet<SystemGuideRevisionRecord> SystemGuideRevisions => Set<SystemGuideRevisionRecord>();
    public DbSet<ExecutionEvidenceAuditRow> ExecutionEvidenceAudit => Set<ExecutionEvidenceAuditRow>();
    public DbSet<ExecutionRetentionSettingsRow> ExecutionRetentionSettings => Set<ExecutionRetentionSettingsRow>();
    public DbSet<MessageObservationSetRecord> MessageObservationSets => Set<MessageObservationSetRecord>();
    public DbSet<ParquetInspectionRecord> ParquetInspections => Set<ParquetInspectionRecord>();
    public DbSet<ParquetRuleSetRecord> ParquetRuleSets => Set<ParquetRuleSetRecord>();
    public DbSet<BlobObservationRecord> BlobObservations => Set<BlobObservationRecord>();
    public DbSet<ImplementationGapEvaluationRecord> ImplementationGapEvaluations => Set<ImplementationGapEvaluationRecord>();
    public DbSet<IntegrationSuiteRecord> IntegrationSuites => Set<IntegrationSuiteRecord>();
    public DbSet<IntegrationSuiteRunRecord> IntegrationSuiteRuns => Set<IntegrationSuiteRunRecord>();
    public DbSet<CompositeSourceContextRecord> CompositeSourceContexts => Set<CompositeSourceContextRecord>();
    public DbSet<ApiContractRow> ApiContracts => Set<ApiContractRow>();
    public DbSet<ApiContractVersionRow> ApiContractVersions => Set<ApiContractVersionRow>();
    public DbSet<ContractTestRow> ContractTests => Set<ContractTestRow>();
    public DbSet<ContractEndpointBindingRow> ContractEndpointBindings => Set<ContractEndpointBindingRow>();
    public DbSet<ContractReviewDecisionRow> ContractReviewDecisions => Set<ContractReviewDecisionRow>();
    public DbSet<ContractTestRevisionRow> ContractTestRevisions => Set<ContractTestRevisionRow>();
    public DbSet<ContractTestChainRow> ContractTestChains => Set<ContractTestChainRow>();
    public DbSet<ImpactAnalysisRunRecord> ImpactAnalysisRuns => Set<ImpactAnalysisRunRecord>();
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
    public DbSet<IntegrationQualitySnapshotRecord> IntegrationQualitySnapshots => Set<IntegrationQualitySnapshotRecord>();
    public DbSet<IntegrationPlatformRecord> IntegrationPlatforms => Set<IntegrationPlatformRecord>();
    public DbSet<IntegrationDefinitionRecord> IntegrationDefinitions => Set<IntegrationDefinitionRecord>();
    public DbSet<IntegrationEnvironmentStateRecord> IntegrationEnvironmentStates => Set<IntegrationEnvironmentStateRecord>();
    public DbSet<IntegrationReviewRunRecord> IntegrationReviewRuns => Set<IntegrationReviewRunRecord>();
    public DbSet<GraphQlSchemaArtifactRecord> GraphQlSchemaArtifacts => Set<GraphQlSchemaArtifactRecord>();
    public DbSet<IntegrationContractArtifactRecord> IntegrationContractArtifacts => Set<IntegrationContractArtifactRecord>();
    public DbSet<IntegrationMessageFlowRecord> IntegrationMessageFlows => Set<IntegrationMessageFlowRecord>();
    public DbSet<AltinnEnvironmentSettingsRecord> AltinnEnvironmentSettings => Set<AltinnEnvironmentSettingsRecord>();
    public DbSet<ExternalIntegrationRecord> ExternalIntegrations => Set<ExternalIntegrationRecord>();
    public DbSet<SetupLibraryItemRecord> SetupLibraryItems => Set<SetupLibraryItemRecord>();
    public DbSet<SetupLibraryVersionRecord> SetupLibraryVersions => Set<SetupLibraryVersionRecord>();
    public DbSet<IntegrationEvidenceBindingRecord> IntegrationEvidenceBindings => Set<IntegrationEvidenceBindingRecord>();
    public DbSet<ApplicationMessagingEvidenceRecord> ApplicationMessagingEvidence => Set<ApplicationMessagingEvidenceRecord>();
    public DbSet<DependencyReviewRunRecord> DependencyReviewRuns => Set<DependencyReviewRunRecord>();
    public DbSet<AiCodeReviewRunRecord> AiCodeReviewRuns => Set<AiCodeReviewRunRecord>();
    public DbSet<SourceRepositoryAssociationRecord> SourceRepositoryAssociations => Set<SourceRepositoryAssociationRecord>();
    public DbSet<SourceDecisionRecord> SourceDecisions => Set<SourceDecisionRecord>();
    public DbSet<WorkspaceDocumentBodyRecord> WorkspaceDocumentBodies => Set<WorkspaceDocumentBodyRecord>();
    public DbSet<TestCoverageReviewRunRecord> TestCoverageReviewRuns => Set<TestCoverageReviewRunRecord>();
    public DbSet<TestCoverageDecisionRecord> TestCoverageDecisions => Set<TestCoverageDecisionRecord>();
    public DbSet<ManualReviewDecisionRecord> ManualReviewDecisions => Set<ManualReviewDecisionRecord>();
    public DbSet<PerformanceTestDefinitionRecord> PerformanceTestDefinitions => Set<PerformanceTestDefinitionRecord>();
    public DbSet<PerformanceTestRunRecord> PerformanceTestRuns => Set<PerformanceTestRunRecord>();
    public DbSet<PerformanceBaselineRecord> PerformanceBaselines => Set<PerformanceBaselineRecord>();
    public DbSet<PerformanceTestDataProfileRecord> PerformanceTestDataProfiles => Set<PerformanceTestDataProfileRecord>();
    public DbSet<DependencyInventoryRecord> DependencyInventories => Set<DependencyInventoryRecord>();
    public DbSet<DependencyHealthRunRecord> DependencyHealthRuns => Set<DependencyHealthRunRecord>();
    public DbSet<ScimEvidenceRecord> ScimEvidence => Set<ScimEvidenceRecord>();
    public DbSet<SecurityClassificationEvidenceRecord> SecurityClassificationEvidence => Set<SecurityClassificationEvidenceRecord>();
    public DbSet<SecurityExpectationDiscoveryRecord> SecurityExpectationDiscoveries => Set<SecurityExpectationDiscoveryRecord>();
    public DbSet<AzureEnvironmentSnapshotRecord> AzureEnvironmentSnapshots => Set<AzureEnvironmentSnapshotRecord>();
    public DbSet<ProjectActivityRecord> ProjectActivity => Set<ProjectActivityRecord>();
    public DbSet<EndpointDiscoveryRunRecord> EndpointDiscoveryRuns => Set<EndpointDiscoveryRunRecord>();
    public DbSet<ObservedOperationRecord> ObservedOperations => Set<ObservedOperationRecord>();
    public DbSet<WorkspaceTransferOperationRecord> WorkspaceTransferOperations => Set<WorkspaceTransferOperationRecord>();
    public DbSet<ImportedHistoricalRecord> ImportedHistoricalRecords => Set<ImportedHistoricalRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceContractDocumentRecord>(entity =>
        {
            entity.ToTable("source_contract_documents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Path).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Text).HasColumnType("text").IsRequired();
            entity.HasIndex(x => new { x.SnapshotId, x.Path }).IsUnique();
        });

        modelBuilder.Entity<SourceSchemaArtifactRecord>(entity =>
        {
            entity.ToTable("source_schema_artifacts");
            entity.HasKey(x => x.SnapshotId);
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.SnapshotFingerprint).HasColumnName("snapshot_fingerprint").HasMaxLength(128).IsRequired();
            entity.Property(x => x.SourceArchiveName).HasColumnName("source_archive_name").HasMaxLength(500).IsRequired();
            entity.Property(x => x.ProjectImportId).HasColumnName("project_import_id").HasMaxLength(128);
            entity.Property(x => x.CapturedAt).HasColumnName("captured_at");
            entity.Property(x => x.IsComplete).HasColumnName("is_complete");
            entity.Property(x => x.OmittedFiles).HasColumnName("omitted_files");
            entity.Property(x => x.PackageJson).HasColumnName("package_json").HasColumnType("text").IsRequired();
            entity.HasOne<IqrSourceSnapshotRecord>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ImpactAnalysisRunRecord>(entity =>
        {
            entity.ToTable("impact_analysis_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.ProjectId).HasColumnName("project_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.ProjectImportId).HasColumnName("project_import_id").HasMaxLength(200);
            entity.Property(r => r.ProjectDisplayName).HasColumnName("project_display_name").HasMaxLength(500).IsRequired();
            entity.Property(r => r.BaselineSnapshotId).HasColumnName("baseline_snapshot_id");
            entity.Property(r => r.CurrentSnapshotId).HasColumnName("current_snapshot_id");
            entity.Property(r => r.BaselineFingerprint).HasColumnName("baseline_fingerprint").HasMaxLength(128).IsRequired();
            entity.Property(r => r.CurrentFingerprint).HasColumnName("current_fingerprint").HasMaxLength(128).IsRequired();
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.ResultJson).HasColumnName("result_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.ProjectId, r.ProjectImportId, r.CreatedAt });
        });
        modelBuilder.Entity<EndpointDiscoveryRunRecord>(entity =>
        {
            entity.ToTable("endpoint_discovery_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.Host).HasColumnName("host").HasMaxLength(255).IsRequired();
            entity.Property(r => r.StartedAt).HasColumnName("started_at");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
            entity.Property(r => r.Status).HasColumnName("status").HasMaxLength(64).IsRequired();
            entity.Property(r => r.ObservedCount).HasColumnName("observed_count");
            entity.Property(r => r.NewCount).HasColumnName("new_count");
            entity.Property(r => r.ObservedAgainCount).HasColumnName("observed_again_count");
            entity.Property(r => r.NotSeenCount).HasColumnName("not_seen_count");
            entity.HasIndex(r => new { r.EnvironmentId, r.Host, r.StartedAt });
        });
        modelBuilder.Entity<ObservedOperationRecord>(entity =>
        {
            entity.ToTable("observed_operations");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.Host).HasColumnName("host").HasMaxLength(255).IsRequired();
            entity.Property(r => r.Port).HasColumnName("port");
            entity.Property(r => r.Identity).HasColumnName("identity").HasMaxLength(600).IsRequired();
            entity.Property(r => r.Protocol).HasColumnName("protocol").HasMaxLength(16).IsRequired();
            entity.Property(r => r.Method).HasColumnName("method").HasMaxLength(16).IsRequired();
            entity.Property(r => r.Path).HasColumnName("path").HasMaxLength(500).IsRequired();
            entity.Property(r => r.GraphQlOperationType).HasColumnName("graphql_operation_type").HasMaxLength(32);
            entity.Property(r => r.GraphQlOperationName).HasColumnName("graphql_operation_name").HasMaxLength(200);
            entity.Property(r => r.FirstObservedAt).HasColumnName("first_observed_at");
            entity.Property(r => r.LastObservedAt).HasColumnName("last_observed_at");
            entity.Property(r => r.ObservationCount).HasColumnName("observation_count");
            entity.Property(r => r.FirstRunId).HasColumnName("first_run_id").HasMaxLength(64).IsRequired();
            entity.Property(r => r.LastRunId).HasColumnName("last_run_id").HasMaxLength(64).IsRequired();
            entity.Property(r => r.LastStatus).HasColumnName("last_status");
            entity.Property(r => r.AuthObserved).HasColumnName("auth_observed");
            entity.Property(r => r.Confidence).HasColumnName("confidence").HasMaxLength(16).HasDefaultValue("Candidate");
            entity.Property(r => r.Provenance).HasColumnName("provenance").HasMaxLength(32).HasDefaultValue("Unknown");
            entity.HasIndex(r => new { r.EnvironmentId, r.Host, r.Identity }).IsUnique();
        });
        modelBuilder.Entity<ProjectActivityRecord>(entity =>
        {
            entity.ToTable("project_activity");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.ProjectContextId).HasColumnName("project_context_id").HasMaxLength(128).IsRequired();
            entity.Property(r => r.ProjectName).HasColumnName("project_name").HasMaxLength(200);
            entity.Property(r => r.ActivityType).HasColumnName("activity_type").HasMaxLength(64).IsRequired();
            entity.Property(r => r.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            entity.Property(r => r.SourceModule).HasColumnName("source_module").HasMaxLength(100).IsRequired();
            entity.Property(r => r.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
            entity.Property(r => r.Summary).HasColumnName("summary").HasMaxLength(1000);
            entity.Property(r => r.Outcome).HasColumnName("outcome").HasMaxLength(200);
            entity.Property(r => r.RelatedEntityType).HasColumnName("related_entity_type").HasMaxLength(64);
            entity.Property(r => r.RelatedEntityId).HasColumnName("related_entity_id").HasMaxLength(200);
            entity.Property(r => r.PackageId).HasColumnName("package_id").HasMaxLength(128);
            entity.Property(r => r.PackageName).HasColumnName("package_name").HasMaxLength(300);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200);
            entity.Property(r => r.EnvironmentName).HasColumnName("environment_name").HasMaxLength(200);
            entity.Property(r => r.Route).HasColumnName("route").HasMaxLength(500);
            entity.Property(r => r.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200).IsRequired();
            entity.Property(r => r.OccurredAt).HasColumnName("occurred_at");
            entity.HasIndex(r => new { r.ProjectContextId, r.IdempotencyKey }).IsUnique();
            entity.HasIndex(r => new { r.ProjectContextId, r.OccurredAt });
        });
        modelBuilder.Entity<AzureEnvironmentSnapshotRecord>(entity => {
            entity.ToTable("azure_environment_snapshots");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.CapturedAt).HasColumnName("captured_at");
            entity.Property(r => r.Status).HasColumnName("status").HasMaxLength(40).IsRequired();
            entity.Property(r => r.SummaryJson).HasColumnName("summary_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.SnapshotJson).HasColumnName("snapshot_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.CapturedAt });
        });
        modelBuilder.Entity<SecurityExpectationDiscoveryRecord>(entity => {
            entity.ToTable("security_expectation_discoveries");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.SourceSnapshotId).HasColumnName("source_snapshot_id");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.EvidenceJson).HasColumnName("evidence_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.DecisionsJson).HasColumnName("decisions_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.HasIndex(r => new { r.EnvironmentId, r.CreatedAt });
        });
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
            entity.Property(w => w.IsCurrent).HasColumnName("is_current").HasDefaultValue(false);
            entity.Property(w => w.Favorite).HasColumnName("favorite").HasDefaultValue(false);
            entity.Property(w => w.TagsJson).HasColumnName("tags_json").HasColumnType("text");
            entity.Property(w => w.SddLifecycleJson).HasColumnName("sdd_lifecycle_json").HasColumnType("text");
            entity.Property(w => w.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);

            entity.HasIndex(w => new { w.UserId, w.IsDeleted })
                .HasDatabaseName("ix_saved_workspaces_user_not_deleted");

            entity.HasIndex(w => new { w.UserId, w.UpdatedAt })
                .HasDatabaseName("ix_saved_workspaces_user_updated")
                .IsDescending(false, true);

            entity.HasIndex(w => new { w.UserId, w.IsCurrent })
                .HasDatabaseName("ix_saved_workspaces_user_current");
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
            entity.Property(r => r.ArtifactSetHash).HasColumnName("artifact_set_hash").HasMaxLength(128);
            entity.Property(r => r.ArtifactIdentityHash).HasColumnName("artifact_identity_hash").HasMaxLength(128);
            entity.Property(r => r.ArtifactReferences).HasColumnName("artifact_references").HasColumnType("text");

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

            // One decision per step and exact artifact revisions; decisions on earlier revisions remain as history.
            entity.HasIndex(r => new { r.WorkspaceId, r.StepKey, r.ArtifactSetHash })
                .HasDatabaseName("ix_workspace_review_progress_workspace_key_binding")
                .IsUnique();

            entity.HasIndex(r => new { r.WorkspaceId, r.ApprovalState })
                .HasDatabaseName("ix_workspace_review_progress_workspace_approval");
        });

        // ── Integration catalog (Target Environment → Integration Catalog) and Integration Quality Review runs ──
        modelBuilder.Entity<IntegrationPlatformRecord>(entity =>
        {
            entity.ToTable("integration_platforms");
            entity.HasKey(r => new { r.EnvironmentId, r.Id });
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(300);
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.UserModified).HasColumnName("user_modified");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<IntegrationDefinitionRecord>(entity =>
        {
            entity.ToTable("integration_definitions");
            entity.HasKey(r => new { r.EnvironmentId, r.Id });
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(300);
            entity.Property(r => r.PlatformId).HasColumnName("platform_id").HasMaxLength(300);
            entity.Property(r => r.DisplayName).HasColumnName("display_name").HasMaxLength(300).IsRequired();
            entity.Property(r => r.Enabled).HasColumnName("enabled");
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.UserModified).HasColumnName("user_modified");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(r => new { r.EnvironmentId, r.PlatformId }).HasDatabaseName("ix_integration_definitions_environment_platform");
        });

        modelBuilder.Entity<IntegrationEnvironmentStateRecord>(entity =>
        {
            entity.ToTable("integration_environment_states");
            entity.HasKey(r => r.EnvironmentId);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200);
            entity.Property(r => r.SeedVersion).HasColumnName("seed_version");
            entity.Property(r => r.SeedName).HasColumnName("seed_name").HasMaxLength(200);
            entity.Property(r => r.LegacyImportedAt).HasColumnName("legacy_imported_at");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<IntegrationReviewRunRecord>(entity =>
        {
            entity.ToTable("integration_review_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
            entity.Property(r => r.Outcome).HasColumnName("outcome").HasMaxLength(50).IsRequired();
            entity.Property(r => r.ResultJson).HasColumnName("result_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.CompletedAt }).HasDatabaseName("ix_integration_review_runs_environment_completed");
        });

        modelBuilder.Entity<IntegrationContractArtifactRecord>(entity =>
        {
            entity.ToTable("integration_contract_artifacts");
            entity.HasKey(a => new { a.EnvironmentId, a.IntegrationId, a.Role });
            entity.Property(a => a.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200);
            entity.Property(a => a.IntegrationId).HasColumnName("integration_id").HasMaxLength(300);
            entity.Property(a => a.Role).HasColumnName("role").HasMaxLength(20);
            entity.Property(a => a.Content).HasColumnName("content").HasColumnType("text").IsRequired();
            entity.Property(a => a.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsRequired();
            entity.Property(a => a.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(a => a.ImportedAt).HasColumnName("imported_at");
        });

        modelBuilder.Entity<IntegrationEvidenceBindingRecord>(entity =>
        {
            entity.ToTable("integration_evidence_bindings");
            entity.HasKey(r => new { r.EnvironmentId, r.IntegrationId });
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.IntegrationId).HasColumnName("integration_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ExternalIntegrationRecord>(entity =>
        {
            entity.ToTable("external_integrations");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(r => r.EnvironmentId);
        });

        modelBuilder.Entity<SetupLibraryItemRecord>(entity =>
        {
            entity.ToTable("setup_library_items");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(r => r.Status).HasColumnName("status").HasMaxLength(40).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<SetupLibraryVersionRecord>(entity =>
        {
            entity.ToTable("setup_library_versions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.ItemId).HasColumnName("item_id");
            entity.Property(r => r.VersionNumber).HasColumnName("version_number");
            entity.Property(r => r.SchemaVersion).HasColumnName("schema_version");
            entity.Property(r => r.Payload).HasColumnName("payload").HasColumnType("text").IsRequired();
            entity.Property(r => r.ManifestJson).HasColumnName("manifest_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.ChangeSummary).HasColumnName("change_summary").HasMaxLength(500);
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(r => new { r.ItemId, r.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<AltinnEnvironmentSettingsRecord>(entity =>
        {
            entity.ToTable("altinn_environment_settings");
            entity.HasKey(r => r.EnvironmentId);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<IntegrationMessageFlowRecord>(entity =>
        {
            entity.ToTable("integration_message_flows");
            entity.HasKey(r => r.EnvironmentId);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<SecurityClassificationEvidenceRecord>(entity =>
        {
            entity.ToTable("security_classification_evidence");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.Json).HasColumnName("json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.Kind, r.CreatedAt }).HasDatabaseName("ix_security_classification_environment_kind_created");
        });

        modelBuilder.Entity<ScimEvidenceRecord>(entity =>
        {
            entity.ToTable("scim_evidence");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
            entity.Property(r => r.PlatformId).HasColumnName("platform_id").HasMaxLength(200);
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.Json).HasColumnName("json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.Kind, r.CreatedAt }).HasDatabaseName("ix_scim_evidence_environment_kind_created");
        });

        modelBuilder.Entity<PerformanceTestDefinitionRecord>(entity =>
        {
            entity.ToTable("performance_test_definitions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.Version).HasColumnName("version");
            entity.Property(r => r.Archived).HasColumnName("archived");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => r.EnvironmentId).HasDatabaseName("ix_performance_test_definitions_environment");
        });

        modelBuilder.Entity<PerformanceTestRunRecord>(entity =>
        {
            entity.ToTable("performance_test_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.DefinitionId).HasColumnName("definition_id").HasMaxLength(64).IsRequired();
            entity.Property(r => r.State).HasColumnName("state").HasMaxLength(40).IsRequired();
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.CreatedAt }).HasDatabaseName("ix_performance_test_runs_environment_created");
            entity.HasIndex(r => r.DefinitionId).HasDatabaseName("ix_performance_test_runs_definition");
        });

        modelBuilder.Entity<PerformanceBaselineRecord>(entity =>
        {
            entity.ToTable("performance_baselines");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.DefinitionId).HasColumnName("definition_id").HasMaxLength(64).IsRequired();
            entity.Property(r => r.ComparisonFingerprint).HasColumnName("comparison_fingerprint").HasMaxLength(64).IsRequired();
            entity.Property(r => r.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            entity.Property(r => r.Version).HasColumnName("version");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.DefinitionId }).HasDatabaseName("ix_performance_baselines_scope");
        });

        modelBuilder.Entity<PerformanceTestDataProfileRecord>(entity =>
        {
            entity.ToTable("performance_test_data_profiles");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            entity.Property(r => r.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => r.EnvironmentId).HasDatabaseName("ix_performance_test_data_profiles_environment");
        });

        modelBuilder.Entity<TestCoverageReviewRunRecord>(entity =>
        {
            entity.ToTable("test_coverage_review_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
            entity.Property(r => r.CurrentSnapshotId).HasColumnName("current_snapshot_id");
            entity.Property(r => r.BaselineSnapshotId).HasColumnName("baseline_snapshot_id");
            entity.Property(r => r.Label).HasColumnName("label").HasMaxLength(300).IsRequired();
            entity.Property(r => r.ResultJson).HasColumnName("result_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => r.CompletedAt).HasDatabaseName("ix_test_coverage_review_runs_completed");
        });

        modelBuilder.Entity<TestCoverageDecisionRecord>(entity =>
        {
            entity.ToTable("test_coverage_decisions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.RepositoryKey).HasColumnName("repository_key").HasMaxLength(300).IsRequired();
            entity.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
            entity.Property(r => r.SubjectKey).HasColumnName("subject_key").HasMaxLength(600).IsRequired();
            entity.Property(r => r.Value).HasColumnName("value").HasMaxLength(60).IsRequired();
            entity.Property(r => r.Note).HasColumnName("note").HasMaxLength(500);
            entity.Property(r => r.DecidedAt).HasColumnName("decided_at");
            entity.HasIndex(r => new { r.RepositoryKey, r.Kind, r.SubjectKey }).HasDatabaseName("ix_test_coverage_decisions_subject");
        });

        modelBuilder.Entity<ManualReviewDecisionRecord>(entity =>
        {
            entity.ToTable("manual_review_decisions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.Module).HasColumnName("module").HasMaxLength(60).IsRequired();
            entity.Property(r => r.ScopeKey).HasColumnName("scope_key").HasMaxLength(300).IsRequired();
            entity.Property(r => r.SubjectKey).HasColumnName("subject_key").HasMaxLength(300).IsRequired();
            entity.Property(r => r.Reviewer).HasColumnName("reviewer").HasMaxLength(120).IsRequired();
            entity.Property(r => r.Decision).HasColumnName("decision").HasMaxLength(40).IsRequired();
            entity.Property(r => r.Note).HasColumnName("note").HasMaxLength(2000);
            entity.Property(r => r.EvidenceJson).HasColumnName("evidence_json").HasColumnType("text").IsRequired();
            entity.Property(r => r.AppliesTo).HasColumnName("applies_to").HasMaxLength(300);
            entity.Property(r => r.DecidedAt).HasColumnName("decided_at");
            entity.HasIndex(r => new { r.Module, r.ScopeKey, r.SubjectKey, r.DecidedAt }).HasDatabaseName("ix_manual_review_decisions_subject");
        });

        modelBuilder.Entity<SourceRepositoryAssociationRecord>(entity =>
        {
            entity.ToTable("source_repository_associations");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.RepositoryKeyA).HasColumnName("repository_key_a").HasMaxLength(300).IsRequired();
            entity.Property(r => r.RepositoryA).HasColumnName("repository_a").HasMaxLength(300).IsRequired();
            entity.Property(r => r.RepositoryKeyB).HasColumnName("repository_key_b").HasMaxLength(300).IsRequired();
            entity.Property(r => r.RepositoryB).HasColumnName("repository_b").HasMaxLength(300).IsRequired();
            entity.Property(r => r.DeclaredBy).HasColumnName("declared_by").HasMaxLength(200).IsRequired();
            entity.Property(r => r.DeclaredAt).HasColumnName("declared_at");
            entity.Property(r => r.Note).HasColumnName("note").HasMaxLength(1000);
            entity.Property(r => r.RevokedAt).HasColumnName("revoked_at");
            entity.Property(r => r.RevokedBy).HasColumnName("revoked_by").HasMaxLength(200);
            entity.HasIndex(r => new { r.RepositoryKeyA, r.RepositoryKeyB }).HasDatabaseName("ix_source_repository_associations_keys");
        });

        // Content-addressed, immutable document bodies of saved workspaces (see WorkspaceDocumentBodyRecord).
        modelBuilder.Entity<WorkspaceDocumentBodyRecord>(entity =>
        {
            entity.ToTable("workspace_document_bodies");
            entity.HasKey(r => r.Sha256);
            entity.Property(r => r.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(r => r.Content).HasColumnName("content").HasColumnType("text").IsRequired();
            entity.Property(r => r.Length).HasColumnName("length");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
        });

        // Append-only source decisions (analysis baselines, declared branch labels); see SourceDecisionRecord.
        modelBuilder.Entity<SourceDecisionRecord>(entity =>
        {
            entity.ToTable("source_decisions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
            entity.Property(r => r.Subject).HasColumnName("subject").HasMaxLength(300).IsRequired();
            entity.Property(r => r.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(r => r.Value).HasColumnName("value").HasMaxLength(200);
            entity.Property(r => r.SnapshotLabel).HasColumnName("snapshot_label").HasMaxLength(300);
            entity.Property(r => r.ContentFingerprint).HasColumnName("content_fingerprint").HasMaxLength(64);
            entity.Property(r => r.DecidedAt).HasColumnName("decided_at");
            entity.Property(r => r.DecidedBy).HasColumnName("decided_by").HasMaxLength(200).IsRequired();
            entity.Property(r => r.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.HasIndex(r => new { r.Kind, r.Subject, r.DecidedAt }).HasDatabaseName("ix_source_decisions_kind_subject");
        });

        modelBuilder.Entity<AiCodeReviewRunRecord>(entity =>
        {
            entity.ToTable("ai_code_review_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
            entity.Property(r => r.CurrentSnapshotId).HasColumnName("current_snapshot_id");
            entity.Property(r => r.BaselineSnapshotId).HasColumnName("baseline_snapshot_id");
            entity.Property(r => r.Label).HasColumnName("label").HasMaxLength(300).IsRequired();
            entity.Property(r => r.ResultJson).HasColumnName("result_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => r.CompletedAt).HasDatabaseName("ix_ai_code_review_runs_completed");
        });

        modelBuilder.Entity<DependencyReviewRunRecord>(entity =>
        {
            entity.ToTable("dependency_review_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
            entity.Property(r => r.Label).HasColumnName("label").HasMaxLength(300).IsRequired();
            entity.Property(r => r.ResultJson).HasColumnName("result_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => r.CompletedAt).HasDatabaseName("ix_dependency_review_runs_completed");
        });

        modelBuilder.Entity<DependencyInventoryRecord>(entity =>
        {
            entity.ToTable("dependency_inventories");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.RecordedAt).HasColumnName("recorded_at");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
            entity.Property(r => r.SourceType).HasColumnName("source_type").HasMaxLength(40).IsRequired();
            entity.Property(r => r.SnapshotJson).HasColumnName("snapshot_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => r.RecordedAt).HasDatabaseName("ix_dependency_inventories_recorded");
        });

        modelBuilder.Entity<DependencyHealthRunRecord>(entity =>
        {
            entity.ToTable("dependency_health_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
            entity.Property(r => r.Label).HasColumnName("label").HasMaxLength(300).IsRequired();
            entity.Property(r => r.InventoryId).HasColumnName("inventory_id");
            entity.Property(r => r.ResultJson).HasColumnName("result_json").HasColumnType("text").IsRequired();
            entity.HasIndex(r => r.CompletedAt).HasDatabaseName("ix_dependency_health_runs_completed");
        });

        modelBuilder.Entity<ApplicationMessagingEvidenceRecord>(entity =>
        {
            entity.ToTable("application_messaging_evidence");
            entity.HasKey(r => r.EnvironmentId);
            entity.Property(r => r.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200);
            entity.Property(r => r.AnalyzedAt).HasColumnName("analyzed_at");
            entity.Property(r => r.EvidenceJson).HasColumnName("evidence_json").HasColumnType("text").IsRequired();
        });

        modelBuilder.Entity<ActiveCdcRunRecord>(entity =>
        {
            entity.ToTable("active_cdc_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200);
            entity.Property(r => r.IntegrationId).HasMaxLength(200);
            entity.Property(r => r.Status).HasMaxLength(30);
            entity.Property(r => r.ResultJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.IntegrationId, r.StartedAt });
            entity.HasIndex(r => new { r.EnvironmentId, r.SyntheticPersonPk }).IsUnique();
            entity.HasIndex(r => new { r.EnvironmentId, r.SyntheticPersonPkControl }).IsUnique();
        });

        modelBuilder.Entity<ActiveEventRunRecord>(entity =>
        {
            entity.ToTable("active_event_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.IntegrationId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Status).HasMaxLength(40).IsRequired();
            entity.Property(r => r.ResultJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.IntegrationId, r.StartedAt });
        });

        modelBuilder.Entity<ActiveEventSyntheticIdentityRecord>(entity =>
        {
            entity.ToTable("active_event_synthetic_identities");
            entity.HasKey(r => new { r.EnvironmentId, r.Scope, r.Value });
            entity.Property(r => r.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Scope).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ExtensionId).HasMaxLength(200).IsRequired();
            entity.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<IntegrationJourneyRunRecord>(entity =>
        {
            entity.ToTable("integration_journey_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.PackId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.JourneyId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ScenarioId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.State).HasMaxLength(40).IsRequired();
            entity.Property(r => r.ResultJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.PackId, r.JourneyId, r.StartedAt });
        });

        // Execution Evidence: run → logical calls (attempts, redacted captures and assertions in the call document).
        modelBuilder.Entity<ExecutionRunRow>(entity =>
        {
            entity.ToTable("execution_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(300).IsRequired();
            entity.Property(r => r.State).HasMaxLength(40).IsRequired();
            entity.Property(r => r.SummaryJson).HasColumnType("text").IsRequired();
            entity.Property(r => r.Protected).HasDefaultValue(false);
            entity.HasIndex(r => new { r.EnvironmentId, r.StartedAt });
            entity.HasIndex(r => r.StartedAt);
        });
        modelBuilder.Entity<ExecutionManifestRow>(entity =>
        {
            entity.ToTable("execution_manifests");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Owner).HasMaxLength(60).IsRequired();
            entity.Property(r => r.SchemaVersion).HasMaxLength(40).IsRequired();
            entity.Property(r => r.DependencyFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.Owner, r.RunId }).IsUnique();
            entity.HasIndex(r => r.DependencyFingerprint);
        });
        modelBuilder.Entity<SystemGuideRevisionRecord>(entity =>
        {
            entity.ToTable("system_guide_revisions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.GuideKey).HasMaxLength(100).IsRequired();
            entity.Property(r => r.State).HasMaxLength(20).IsRequired();
            entity.Property(r => r.ContentFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(r => r.ContentJson).HasColumnType("text").IsRequired();
            entity.Property(r => r.SourceBasisJson).HasColumnType("text").IsRequired();
            entity.Property(r => r.CreatedBy).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ChangeSummary).HasMaxLength(1000);
            entity.Property(r => r.SubmittedBy).HasMaxLength(200);
            entity.Property(r => r.ApprovedBy).HasMaxLength(200);
            entity.HasIndex(r => new { r.GuideKey, r.Revision }).IsUnique();
            // At most one open revision (Draft/InReview) and one Published revision per guide: concurrent saves or publishes cannot fork it.
            entity.HasIndex(r => r.GuideKey, "IX_system_guide_revisions_open").IsUnique().HasFilter("\"State\" IN ('Draft', 'InReview')");
            entity.HasIndex(r => r.GuideKey, "IX_system_guide_revisions_published").IsUnique().HasFilter("\"State\" = 'Published'");
        });
        modelBuilder.Entity<ParquetInspectionRecord>(entity =>
        {
            entity.ToTable("parquet_inspections");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.FileName).HasMaxLength(300).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.SavedAt });
        });
        modelBuilder.Entity<ParquetRuleSetRecord>(entity =>
        {
            entity.ToTable("parquet_rule_sets");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.ProjectKey).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ContractKey).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ContractVersion).HasMaxLength(100).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(300).IsRequired();
            entity.Property(r => r.RulesJson).HasColumnType("text").IsRequired();
            entity.Property(r => r.ApprovedBy).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Note).HasMaxLength(1000);
            entity.HasIndex(r => new { r.ProjectKey, r.ContractKey, r.ContractVersion, r.Version }).IsUnique();
        });
        modelBuilder.Entity<BlobObservationRecord>(entity =>
        {
            entity.ToTable("blob_observations");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.BlobName).HasMaxLength(400).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.ObservedAt });
        });
        modelBuilder.Entity<IntegrationSuiteRecord>(entity =>
        {
            entity.ToTable("integration_suites");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.SuiteKey).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ScopeId).HasMaxLength(100).IsRequired();
            entity.Property(r => r.EnvironmentId).HasMaxLength(200);
            entity.Property(r => r.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(r => r.DefinitionJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.SuiteKey, r.Revision }).IsUnique();
        });
        modelBuilder.Entity<IntegrationSuiteRunRecord>(entity =>
        {
            entity.ToTable("integration_suite_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.SuiteKey).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Kind).HasMaxLength(50).IsRequired();
            entity.Property(r => r.Initiator).HasMaxLength(120).IsRequired();
            entity.Property(r => r.TestIdsJson).HasColumnType("text").IsRequired();
            entity.Property(r => r.PlanJson).HasColumnType("text");
            entity.HasIndex(r => new { r.SuiteKey, r.StartedAt });
        });
        // Backup & Transfer: operation log (plans, fingerprints, identity mapping, reports) and imported history (never destination runs or decisions).
        modelBuilder.Entity<WorkspaceTransferOperationRecord>(entity =>
        {
            entity.ToTable("workspace_transfer_operations");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Kind).HasMaxLength(40).IsRequired();
            entity.Property(r => r.State).HasMaxLength(40).IsRequired();
            entity.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Scope).HasMaxLength(60).IsRequired();
            entity.Property(r => r.Summary).HasMaxLength(500).IsRequired();
            entity.Property(r => r.PackageSha256).HasMaxLength(64);
            entity.Property(r => r.PlanFingerprint).HasMaxLength(64);
            entity.Property(r => r.DestinationFingerprint).HasMaxLength(64);
            entity.Property(r => r.PlanJson).HasColumnType("text");
            entity.Property(r => r.ResultJson).HasColumnType("text");
            entity.Property(r => r.Error).HasMaxLength(2000);
            entity.Property(r => r.StagedPackage).HasMaxLength(200);
            entity.HasIndex(r => r.CreatedAt);
        });
        modelBuilder.Entity<ImportedHistoricalRecord>(entity =>
        {
            entity.ToTable("imported_historical_records");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Kind).HasMaxLength(60).IsRequired();
            entity.Property(r => r.OriginalId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.OriginInstanceId).HasMaxLength(100).IsRequired();
            entity.Property(r => r.OriginalProject).HasMaxLength(200);
            entity.Property(r => r.OriginalEnvironmentId).HasMaxLength(200);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200);
            entity.Property(r => r.DocumentJson).HasColumnType("text").IsRequired();
            // Re-importing the same package never duplicates history: one row per origin, kind and original id.
            entity.HasIndex(r => new { r.OriginInstanceId, r.Kind, r.OriginalId }).IsUnique();
            entity.HasIndex(r => r.OperationId);
        });
        modelBuilder.Entity<CompositeSourceContextRecord>(entity =>
        {
            entity.ToTable("composite_source_contexts");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.ContextKey).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ScopeId).HasMaxLength(100).IsRequired();
            entity.Property(r => r.OwningProject).HasMaxLength(120).IsRequired();
            entity.Property(r => r.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(r => r.CreatedBy).HasMaxLength(120).IsRequired();
            entity.Property(r => r.DefinitionJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.ContextKey, r.Revision }).IsUnique();
        });
        modelBuilder.Entity<ImplementationGapEvaluationRecord>(entity =>
        {
            entity.ToTable("implementation_gap_evaluations");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.ScopeKey).HasMaxLength(300).IsRequired();
            entity.Property(r => r.ProjectImportId).HasMaxLength(100);
            entity.Property(r => r.InputsJson).HasColumnType("text").IsRequired();
            entity.Property(r => r.ReportJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.ScopeKey, r.EvaluatedAt });
        });
        modelBuilder.Entity<MessageObservationSetRecord>(entity =>
        {
            entity.ToTable("message_observation_sets");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(300).IsRequired();
            entity.Property(r => r.DocumentJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.ImportedAt });
        });
        modelBuilder.Entity<ExecutionEvidenceAuditRow>(entity =>
        {
            entity.ToTable("execution_evidence_audit");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Action).HasMaxLength(40).IsRequired();
            entity.Property(a => a.Scope).HasMaxLength(300).IsRequired();
            entity.Property(a => a.Initiator).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Detail).HasMaxLength(2000);
            entity.HasIndex(a => a.At);
        });
        modelBuilder.Entity<ExecutionRetentionSettingsRow>(entity =>
        {
            entity.ToTable("execution_retention_settings");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Mode).HasMaxLength(20).IsRequired();
            entity.Property(s => s.UpdatedBy).HasMaxLength(200).IsRequired();
        });
        modelBuilder.Entity<ExecutionCallRow>(entity =>
        {
            entity.ToTable("execution_calls");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(300).IsRequired();
            entity.Property(c => c.Protocol).HasMaxLength(20).IsRequired();
            entity.Property(c => c.Method).HasMaxLength(20).IsRequired();
            entity.Property(c => c.State).HasMaxLength(40).IsRequired();
            entity.Property(c => c.DocumentJson).HasColumnType("text").IsRequired();
            entity.HasIndex(c => new { c.RunId, c.Sequence }).IsUnique();
            entity.HasIndex(c => new { c.RunId, c.State });
            entity.Property(c => c.DefinitionRef).HasMaxLength(200);
            entity.HasIndex(c => c.DefinitionRef);
        });

        // Contract registry (immutable versions) and the contract tests generated from it. Results stay in execution_calls.
        modelBuilder.Entity<ApiContractRow>(entity =>
        {
            entity.ToTable("api_contracts");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Key).HasMaxLength(80).IsRequired();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Protocol).HasMaxLength(20).IsRequired();
            entity.HasIndex(c => c.Key).IsUnique();
            entity.Property(c => c.Lifecycle).HasMaxLength(20).IsRequired().HasDefaultValue("Active");
            entity.Property(c => c.ProjectKey).HasMaxLength(200);
            entity.Property(c => c.IdentityState).HasMaxLength(20);
            entity.HasIndex(c => c.ProjectKey);
        });
        modelBuilder.Entity<ApiContractVersionRow>(entity =>
        {
            entity.ToTable("api_contract_versions");
            entity.HasKey(v => v.Id);
            entity.Property(v => v.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(v => v.Source).HasMaxLength(20).IsRequired();
            entity.Property(v => v.SourceLabel).HasMaxLength(500).IsRequired();
            entity.Property(v => v.Format).HasMaxLength(20).IsRequired();
            entity.Property(v => v.DocumentText).HasColumnType("text").IsRequired();
            entity.Property(v => v.ContractJson).HasColumnType("text").IsRequired();
            entity.HasIndex(v => new { v.ContractId, v.Version }).IsUnique();
            entity.Property(v => v.SourcePath).HasMaxLength(500);
            entity.HasIndex(v => new { v.SourceSnapshotId, v.SourcePath });
            entity.Property(v => v.SourceProjectKey).HasMaxLength(200);
            entity.HasIndex(v => new { v.SourceProjectKey, v.SourcePath });
        });
        modelBuilder.Entity<ContractReviewDecisionRow>(entity =>
        {
            entity.ToTable("contract_review_decisions");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Kind).HasMaxLength(40).IsRequired();
            entity.Property(d => d.ProjectKey).HasMaxLength(200);
            entity.Property(d => d.SubjectKey).HasMaxLength(500).IsRequired();
            entity.Property(d => d.Detail).HasColumnType("text").IsRequired();
            entity.Property(d => d.DecidedBy).HasMaxLength(200).IsRequired();
            entity.HasIndex(d => new { d.Kind, d.SubjectKey });
            entity.HasIndex(d => d.ContractId);
        });
        modelBuilder.Entity<ContractTestRow>(entity =>
        {
            entity.ToTable("contract_tests");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Operation).HasMaxLength(500).IsRequired();
            entity.Property(t => t.Name).HasMaxLength(300).IsRequired();
            entity.Property(t => t.Kind).HasMaxLength(40).IsRequired();
            entity.Property(t => t.DocumentJson).HasColumnType("text").IsRequired();
            entity.HasIndex(t => new { t.ContractId, t.Operation });
        });
        modelBuilder.Entity<ContractEndpointBindingRow>(entity =>
        {
            entity.ToTable("contract_endpoint_bindings");
            entity.HasKey(b => b.Id);
            entity.Property(b => b.EnvironmentId).HasMaxLength(200).IsRequired();
            entity.Property(b => b.BasePath).HasMaxLength(300);
            entity.Property(b => b.GraphQlPath).HasMaxLength(300);
            entity.Property(b => b.Authentication).HasMaxLength(40).IsRequired();
            entity.HasIndex(b => new { b.ContractId, b.EnvironmentId }).IsUnique();
        });
        modelBuilder.Entity<ContractTestChainRow>(entity =>
        {
            entity.ToTable("contract_test_chains");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.DocumentJson).HasColumnType("text").IsRequired();
            entity.HasIndex(c => new { c.ChainId, c.Revision }).IsUnique();
        });
        modelBuilder.Entity<ContractTestRevisionRow>(entity =>
        {
            entity.ToTable("contract_test_revisions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.DocumentJson).HasColumnType("text").IsRequired();
            entity.Property(r => r.ChangeNote).HasMaxLength(500);
            entity.HasIndex(r => new { r.TestId, r.Revision }).IsUnique();
        });

        modelBuilder.Entity<IqrSourceSnapshotRecord>(entity =>
        {
            entity.ToTable("iqr_source_snapshots");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.EnvironmentId).HasMaxLength(200);
            entity.Property(r => r.IntegrationId).HasMaxLength(200);
            entity.Property(r => r.EvidenceJson).HasColumnType("text").IsRequired();
            entity.HasIndex(r => new { r.EnvironmentId, r.IntegrationId, r.AnalyzedAt });
        });

        modelBuilder.Entity<GraphQlSchemaArtifactRecord>(entity =>
        {
            entity.ToTable("graphql_schema_artifacts");
            entity.HasKey(a => new { a.EnvironmentId, a.TargetId });
            entity.Property(a => a.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200);
            entity.Property(a => a.TargetId).HasColumnName("target_id").HasMaxLength(300);
            entity.Property(a => a.Id).HasColumnName("id").HasMaxLength(100).IsRequired();
            entity.Property(a => a.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsRequired();
            entity.Property(a => a.Content).HasColumnName("content").HasColumnType("text").IsRequired();
            entity.Property(a => a.DocumentJson).HasColumnName("document_json").HasColumnType("text").IsRequired();
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
