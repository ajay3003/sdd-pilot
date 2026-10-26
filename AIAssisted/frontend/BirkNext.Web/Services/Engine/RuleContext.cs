using BirkNext.Web.Models;

namespace BirkNext.Web.Services.Engine;

/// <summary>
/// All inputs available to every rule pack in a single execution.
/// Includes raw text for keyword-based packs, parsed domain models for
/// structural packs, and pre-computed sub-reports for packs that depend on them.
/// </summary>
/// <summary>One reviewed document as a rule pack reads it: its path (provenance) and its clean searchable text.</summary>
public sealed record RuleDocument(string Path, string Text);

public sealed class RuleContext
{
    // ── Raw artifact text ─────────────────────────────────────────────────────
    // Used by keyword-based rule packs (e.g. Standards: WCAG, OWASP, GDPR, ISO).
    // Pre-concatenated from all non-empty artifact strings.

    public string CombinedText { get; init; } = string.Empty;

    /// <summary>
    /// The reviewed documents one by one (path and clean text), when the review knows them: keyword packs search each and record which
    /// documents were searched and which one matched. Empty for callers that only pass <see cref="CombinedText"/>.
    /// </summary>
    public IReadOnlyList<RuleDocument> Documents { get; init; } = [];

    // ── Parsed domain models ──────────────────────────────────────────────────
    // Used by structural rule packs (Constitution, QA Auditor).
    // Any may be null when the artifact was not provided by the user.

    public ConstitutionDocument? Constitution { get; init; }
    public SpecTree?             Spec         { get; init; }
    public PlanDocument?         Plan         { get; init; }
    public TaskTree?             Tasks        { get; init; }

    // ── Pre-computed sub-reports ──────────────────────────────────────────────
    // Computed once before the engine runs; shared across all rule packs that
    // need them, so no rule pack triggers duplicate sub-analysis.

    public ArtifactTraceabilityReport?   Trace            { get; init; }
    public ConstitutionComplianceReport? ComplianceReport { get; init; }
}
