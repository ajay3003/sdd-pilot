using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IDashboardSnapshotService
{
    ArtifactTraceabilityReport?   TraceabilityReport { get; }
    ConstitutionComplianceReport? ComplianceReport   { get; }
    QaAuditReport?                AuditReport        { get; }
    DeliveryReadinessReport?      DeliveryReport     { get; }
    QAReadinessReport?            ReadinessReport    { get; }
    DateTimeOffset?               TraceabilityRunAt  { get; }
    DateTimeOffset?               ComplianceRunAt    { get; }
    DateTimeOffset?               AuditRunAt         { get; }
    DateTimeOffset?               DeliveryRunAt      { get; }
    DateTimeOffset?               ReadinessRunAt     { get; }

    void Publish(ArtifactTraceabilityReport report);
    void Publish(ConstitutionComplianceReport report);
    void Publish(QaAuditReport report);
    void Publish(DeliveryReadinessReport report);
    void Publish(QAReadinessReport report);
    void ClearQualityReview();
    void Clear();
}

public sealed class DashboardSnapshotService : IDashboardSnapshotService
{
    public ArtifactTraceabilityReport?   TraceabilityReport { get; private set; }
    public ConstitutionComplianceReport? ComplianceReport   { get; private set; }
    public QaAuditReport?                AuditReport        { get; private set; }
    public DeliveryReadinessReport?      DeliveryReport     { get; private set; }
    public QAReadinessReport?            ReadinessReport    { get; private set; }
    public DateTimeOffset?               TraceabilityRunAt  { get; private set; }
    public DateTimeOffset?               ComplianceRunAt    { get; private set; }
    public DateTimeOffset?               AuditRunAt         { get; private set; }
    public DateTimeOffset?               DeliveryRunAt      { get; private set; }
    public DateTimeOffset?               ReadinessRunAt     { get; private set; }

    public void Publish(ArtifactTraceabilityReport report)
    {
        TraceabilityReport = report;
        TraceabilityRunAt = DateTimeOffset.UtcNow;
    }

    public void Publish(ConstitutionComplianceReport report)
    {
        ComplianceReport = report;
        ComplianceRunAt = DateTimeOffset.UtcNow;
    }

    public void Publish(QaAuditReport report)
    {
        AuditReport = report;
        AuditRunAt = DateTimeOffset.UtcNow;
    }

    public void Publish(DeliveryReadinessReport report)
    {
        DeliveryReport = report;
        DeliveryRunAt = DateTimeOffset.UtcNow;
    }

    public void Publish(QAReadinessReport report)
    {
        ReadinessReport = report;
        ReadinessRunAt = DateTimeOffset.UtcNow;
    }

    public void ClearQualityReview()
    {
        ComplianceReport = null;
        AuditReport = null;
        DeliveryReport = null;
        ReadinessReport = null;
        ComplianceRunAt = null;
        AuditRunAt = null;
        DeliveryRunAt = null;
        ReadinessRunAt = null;
    }

    public void Clear()
    {
        TraceabilityReport = null;
        TraceabilityRunAt = null;
        ClearQualityReview();
    }
}
