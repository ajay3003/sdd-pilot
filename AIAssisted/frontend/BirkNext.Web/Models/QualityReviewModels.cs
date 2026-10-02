namespace BirkNext.Web.Models;

// ── Pack descriptor (powers the selector UI) ──────────────────────────────────

public sealed record QualityReviewPackDescriptor(
    string PackId,
    string PackGroup,
    string PackName,
    string PackDescription,
    bool   IsDefault);

// ── Per-pack result ───────────────────────────────────────────────────────────

public sealed class QualityReviewPackResult
{
    public string PackId    { get; init; } = string.Empty;
    public string PackName  { get; init; } = string.Empty;
    public string PackGroup { get; init; } = string.Empty;

    /// <summary>0–100 coverage/quality score for this pack. Meaningful only when <see cref="Assessed"/>.</summary>
    public double Score   { get; init; }

    /// <summary>False when the pack had nothing to assess (no applicable rule evaluated, no data model entities): its Score is not a
    /// quality result and is excluded from <see cref="QualityReviewReport.OverallScore"/> (shared ScoreSemantics: no evidence is no score).</summary>
    public bool Assessed { get; init; } = true;
    public string? NotAssessedReason { get; init; }

    // Normalised finding counts for the overall summary.
    public int Critical { get; init; }
    public int High     { get; init; }
    public int Medium   { get; init; }
    public int Low      { get; init; }
    public int Info     { get; init; }

    /// <summary>Non-null when the pack failed or a required artifact is missing.</summary>
    public string? Error { get; init; }

    // Exactly one of the following is set for a successful result:
    public QaAuditReport?                QaAudit          { get; init; }
    public ConstitutionComplianceReport? Compliance       { get; init; }
    public StandardsComplianceReport?    Standards        { get; init; }
    public QAReadinessReport?            QaReadiness      { get; init; }
    public DeliveryReadinessReport?      DeliveryReadiness { get; init; }
    public DataModelDocument?            DataModel         { get; init; }
}

// ── Aggregate report ──────────────────────────────────────────────────────────

public sealed class QualityReviewReport
{
    public List<QualityReviewPackResult> PackResults   { get; init; } = [];
    /// <summary>Average over assessed packs only. Meaningful only when <see cref="AssessedPacks"/> &gt; 0.</summary>
    public double         OverallScore   { get; init; }
    /// <summary>Packs that completed AND had something to assess. 0 = no score for this review.</summary>
    public int            AssessedPacks  { get; init; }
    public int            TotalFindings  { get; init; }
    public int            CriticalCount  { get; init; }
    public int            HighCount      { get; init; }
    public int            MediumCount    { get; init; }
    public int            LowCount       { get; init; }
    public DateTimeOffset RunAt          { get; init; }
}
