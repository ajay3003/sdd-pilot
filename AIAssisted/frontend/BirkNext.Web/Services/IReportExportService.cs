using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IReportExportService
{
    string ExportQualityReview(QualityReviewReport report, string? projectName);
    string ExportFrontendQualityReview(FrontendQualityReviewReport report, string? projectName);
    string ExportApiQualityReview(ApiQualityReviewReport report, string? projectName);
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
        WasmPerformanceReviewReport? performance);
}
