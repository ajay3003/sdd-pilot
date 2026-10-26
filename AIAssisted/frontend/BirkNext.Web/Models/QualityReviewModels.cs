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

    /// <summary>What this Review Pack reads (its input contract), in tester words.</summary>
    public string? InputContract { get; init; }
    /// <summary>Documents read as context only (never scored), e.g. generated data-model documentation for Data Model Quality.</summary>
    public List<string> ContextDocuments { get; init; } = [];
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

    /// <summary>Authoritative review provenance: every document read (primary, supporting, shared, generated) and the packs that read it.</summary>
    public List<QualityReviewDocumentRead> DocumentsRead { get; init; } = [];
    /// <summary>Documents considered and left out, with the reason (sibling pack, history, generated not requested …).</summary>
    public List<QualityReviewDocumentExcluded> DocumentsExcluded { get; init; } = [];
    /// <summary>Limitations of the document scope (e.g. an import that kept no supporting documents).</summary>
    public List<string> ScopeLimitations { get; init; } = [];
    /// <summary>True when the run knew its document scope (an Artifact Pack inventory); false for text-only runs.</summary>
    public bool HasDocumentScope { get; init; }
    /// <summary>The documentation gate policy the run applied (None: Delivery Readiness is advisory).</summary>
    public BirkNext.Web.Services.DocumentationGatePolicy Policy { get; init; } = BirkNext.Web.Services.DocumentationGatePolicy.None;
}

// ── Review input scope and provenance ─────────────────────────────────────────

/// <summary>
/// What one Document Quality Review run reads: the primary documents' text and, when known, the Artifact Pack's canonical inventory (supporting
/// and generated documents with provenance) and the documentation gate policy in force.
/// </summary>
public sealed class QualityReviewRunInput
{
    public string? ConstitutionText { get; init; }
    public string? SpecText         { get; init; }
    public string? PlanText         { get; init; }
    public string? TaskText         { get; init; }
    public string? DataModelText    { get; init; }
    public BirkNext.Web.Services.Explorers.ArtifactPackInventory? Inventory { get; init; }
    public BirkNext.Web.Services.DocumentationGatePolicy Policy { get; init; } = BirkNext.Web.Services.DocumentationGatePolicy.None;
}

/// <summary>One document a review run read, as it was read: identity, provenance, why it was included and which Review Packs consumed it.</summary>
public sealed class QualityReviewDocumentRead
{
    public string Path        { get; init; } = string.Empty;
    public string Kind        { get; init; } = string.Empty;
    /// <summary>"Primary · pack-local", "Supporting · shared module", "Generated documentation" …</summary>
    public string ClassLabel  { get; init; } = string.Empty;
    public string Category    { get; init; } = string.Empty;
    public string Origin      { get; init; } = string.Empty;
    public string Scope       { get; init; } = string.Empty;
    public string? Fingerprint { get; init; }
    public int?   Revision    { get; init; }
    public string Reason      { get; init; } = string.Empty;
    public List<string> ConsumedBy { get; init; } = [];
}

/// <summary>A document considered and left out of the review, with the reason.</summary>
public sealed record QualityReviewDocumentExcluded(string Path, string Kind, string Reason);
