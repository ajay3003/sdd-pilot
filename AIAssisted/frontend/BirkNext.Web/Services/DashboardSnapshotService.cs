using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IDashboardSnapshotService
{
    ArtifactTraceabilityReport?   TraceabilityReport { get; }
    ConstitutionComplianceReport? ComplianceReport   { get; }
    QaAuditReport?                AuditReport        { get; }
    DeliveryReadinessReport?      DeliveryReport     { get; }
    QAReadinessReport?            ReadinessReport    { get; }

    void Publish(ArtifactTraceabilityReport report);
    void Publish(ConstitutionComplianceReport report);
    void Publish(QaAuditReport report);
    void Publish(DeliveryReadinessReport report);
    void Publish(QAReadinessReport report);
}

public sealed class DashboardSnapshotService : IDashboardSnapshotService
{
    public ArtifactTraceabilityReport?   TraceabilityReport { get; private set; }
    public ConstitutionComplianceReport? ComplianceReport   { get; private set; }
    public QaAuditReport?                AuditReport        { get; private set; }
    public DeliveryReadinessReport?      DeliveryReport     { get; private set; }
    public QAReadinessReport?            ReadinessReport    { get; private set; }

    public void Publish(ArtifactTraceabilityReport report)   => TraceabilityReport = report;
    public void Publish(ConstitutionComplianceReport report) => ComplianceReport   = report;
    public void Publish(QaAuditReport report)                => AuditReport        = report;
    public void Publish(DeliveryReadinessReport report)      => DeliveryReport     = report;
    public void Publish(QAReadinessReport report)            => ReadinessReport    = report;
}
