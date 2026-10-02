using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IReportExportService
{
    string ExportQualityReview(QualityReviewReport report, string? projectName);
    string ExportFrontendQualityReview(FrontendQualityReviewReport report, string? projectName);
    string ExportApiReview(BirkNext.ApiReview.ApiReviewReport report, string? projectName);
    string ExportIntegrationReview(BirkNext.Integrations.IntegrationReviewResult result, string? projectName);
    string ExportActiveCdcRun(BirkNext.Integrations.ActiveCdcRun run, string? projectName) => throw new NotSupportedException();
    string ExportSourceArchitecture(BirkNext.SourceArchitecture.ArchitectureSnapshot snapshot) => throw new NotSupportedException();
    string ExportDependencyReview(BirkNext.Dependencies.DependencyReviewResult result);
    string ExportDependencyHealth(BirkNext.Dependencies.DependencyHealthRun run);
    string ExportScimCheck(BirkNext.Integrations.ScimEvidenceCheck check);
    string ExportClassificationReview(BirkNext.Integrations.ClassificationReviewResult result);
    string ExportPipelineReview(BirkNext.PipelineReview.PipelineReviewResult result) => throw new NotSupportedException();
    string ExportSecurityReview(WasmSecurityReviewReport report, string? projectName);
    string ExportPerformanceReview(WasmPerformanceReviewReport report, string? projectName);
    string ExportArtifactTraceability(ArtifactTraceabilityReport report, string? projectName);
    string ExportImplementationReview(AlignmentReport report, string? projectName);
    string ExportDataModel(DataModelDocument document, string? projectName);
    string ExportDashboardSummary(
        string? projectName,
        ArtifactTraceabilityReport? traceability,
        ConstitutionComplianceReport? compliance,
        QaAuditReport? audit,
        DeliveryReadinessReport? delivery,
        QAReadinessReport? readiness,
        WasmPerformanceReviewReport? performance,
        BirkNext.Integrations.IntegrationReviewResult? integrationQuality);
}
